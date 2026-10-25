using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.MultiAgent;
using Strands.Agents.VendedTools;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class SwarmToolMinimumTests
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    [Fact]
    public void DefaultSchemaAndConfiguredAuthorityAreDetached()
    {
        var tool = SwarmTool.Create();
        Assert.IsAssignableFrom<IStreamingTool>(tool);
        Assert.Equal("swarm", tool.Spec.Name);
        Assert.Equal(SwarmTool.DefaultDescription, tool.Spec.Description);
        var schema = tool.Spec.InputSchema;
        Assert.Equal(new[] { "task", "agents" }, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var agents = schema.GetProperty("properties").GetProperty("agents");
        Assert.Equal(1, agents.GetProperty("minItems").GetInt32());
        Assert.Equal(20, agents.GetProperty("maxItems").GetInt32());
        Assert.Equal(new[] { "name", "instructions" }, agents.GetProperty("items").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var axes = agents.GetProperty("items").GetProperty("properties");
        Assert.False(axes.TryGetProperty("tools", out _));
        Assert.False(axes.TryGetProperty("model", out _));
        Assert.False(agents.GetProperty("items").GetProperty("additionalProperties").GetBoolean());

        var roles = new Dictionary<string, Preset> { ["writer"] = new() { Instructions = "Write.", Description = "Writes" } };
        var fixedTools = new List<string> { "read" };
        var configured = SwarmTool.Create(new() { Name = "team", Presets = roles, Tools = new Fixed(fixedTools), MaxAgents = 3 });
        var before = configured.Spec.InputSchema.GetRawText();
        roles.Add("late", new()); fixedTools.Add("late");
        Assert.Equal(before, configured.Spec.InputSchema.GetRawText());
        var item = configured.Spec.InputSchema.GetProperty("properties").GetProperty("agents").GetProperty("items");
        Assert.Equal(new[] { "name" }, item.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("writer", Assert.Single(item.GetProperty("properties").GetProperty("agent_type").GetProperty("enum").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task DefaultBuilderRunsARealHandoffWithFreshStateAndNamedProgress()
    {
        var recordCalls = 0;
        var record = new FunctionTool(new("record", "Observe child", ObjectSchema()), (_, context, _) =>
        {
            recordCalls++;
            Assert.Empty(context.Agent.State.GetAll());
            Assert.Empty(context.Agent.ModelState.GetAll());
            Assert.DoesNotContain(context.Agent.Messages, message => message.Text.Contains("parent-history"));
            return new("recorded");
        });
        var model = new Events(request => request.SystemPrompt == "A."
            ? HasResult(request) ? Answer("A done") : Calls(("record-call", "record", Empty), ("handoff", "handoff_to_agent", JsonSerializer.SerializeToElement(new { agent_name = "b", message = "Continue." })))
            : Answer("B done"));
        var tool = SwarmTool.Create();
        await using var parent = new Agent(new() { Model = model, Tools = [record, tool], Messages = [Message.User("parent-history")] });
        parent.State.Set("parent-only", "secret"); parent.ModelState.Set("parent-only", "secret");
        await parent.InitializeAsync();
        var items = new List<IToolStreamEvent>();
        var input = Team(("a", "A."), ("b", "B."));
        await foreach (var item in ((IStreamingTool)tool).StreamAsync(input, Context(parent, tool, input))) items.Add(item);
        var result = Assert.IsType<ToolStreamResultEvent>(items.Last()).Result;
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("a: A done\nb: B done", result.Content);
        Assert.Equal(1, recordCalls);
        Assert.Contains(items.OfType<ToolStreamEvent>(), item => item.Data is MultiAgentHandoffEvent);
        Assert.DoesNotContain(items.OfType<ToolStreamEvent>(), item => item.Data is MultiAgentResultEvent);
        Assert.Equal(result.Content, Assert.IsType<string>(items.OfType<ToolStreamEvent>().Last().Data));
        Assert.Single(parent.Messages); Assert.Equal("secret", parent.State.Get("parent-only")!.Value.GetString());
    }

    [Fact]
    public async Task PresetResolutionAndIdentityFilteringReachTheBuiltChild()
    {
        var read = Tool("read"); var ordinarySwarm = Tool("swarm"); var reserved = Tool("handoff_to_agent");
        var another = SwarmTool.Create(new() { Name = "another_team" });
        SwarmAgentSpec? observed = null; Agent? constructed = null;
        var childModel = new Events(_ => Answer("kept"));
        var tool = SwarmTool.Create(new()
        {
            Name = "team",
            Instructions = new Fixed("axis fallback"),
            Tools = new Choice([new Option("inspect", "read"), new Option("ordinary", "swarm")], multiple: true),
            Presets = new Dictionary<string, Preset> { ["role"] = new() { Instructions = "preset", Tools = ["read", "not-owned"], Model = childModel } },
            Builder = (spec, _, _) =>
            {
                observed = spec;
                constructed = new(new() { Model = childModel, Tools = [read, reserved, another, ordinarySwarm], SystemPrompt = spec.Instructions });
                return new(constructed);
            }
        });
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("parent")), Tools = [read, ordinarySwarm, another, tool] });
        var input = JsonSerializer.SerializeToElement(new { task = "", agents = new[] { new { name = " a ", tools = new[] { "inspect", "unknown", "ordinary" }, instructions = "hidden override" } } });
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal("a: kept", result.Content);
        Assert.Equal("a", observed!.Name); Assert.Equal("role", observed.AgentType); Assert.Equal("preset", observed.Instructions);
        Assert.Equal(new[] { "read", "swarm" }, observed.Tools); Assert.Same(childModel, observed.Model);
        Assert.NotNull(constructed);
        Assert.Equal(new[] { "read", "swarm" }, constructed.Tools.Select(value => value.Spec.Name));
        Assert.Equal(4, parent.Tools.Count);
    }

    [Fact]
    public async Task InvalidTeamAndEntryRejectBeforeAnyBuilderOrModel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SwarmTool.Create(new() { MaxAgents = 0 }));
        var builds = 0; var models = 0;
        var tool = SwarmTool.Create(new()
        {
            MaxAgents = 2,
            Presets = new Dictionary<string, Preset> { ["known"] = new() },
            Builder = (_, _, _) => { builds++; return new(new Agent(new() { Model = new Events(_ => { models++; return Answer("must not run"); }) })); }
        });
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("unused")) });
        object[] inputs =
        [
            new { task = "", agents = Array.Empty<object>() },
            new { task = "", agents = new[] { new { name = "a" }, new { name = "b" }, new { name = "c" } } },
            new { task = "", agents = new[] { new { name = "a" }, new { name = " a " } } },
            new { task = "", agents = new[] { new { name = " " } } },
            new { task = "", agents = new[] { new { name = "a", agent_type = "unknown" } } },
            new { task = "", agents = new[] { new { name = "a" } }, entry_point = "missing" }
        ];
        foreach (var value in inputs)
        {
            var input = JsonSerializer.SerializeToElement(value);
            var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
            Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Equal("team-call", result.ToolUseId);
        }
        Assert.Equal(0, builds); Assert.Equal(0, models);
    }

    [Fact]
    public async Task ChildStopUsesCopiedInvocationStateWithoutStoppingParent()
    {
        var userValue = new object(); var caller = new Dictionary<string, object?> { ["request_state"] = new object(), ["user"] = userValue };
        IReadOnlyDictionary<string, object?>? childState = null;
        var stop = Stop.Create();
        var childModel = new Events(_ => Calls(("child-stop", "stop", JsonSerializer.SerializeToElement(new { message = "child done" }))));
        var tool = SwarmTool.Create(new()
        {
            Builder = (spec, _, _) =>
            {
                var hooks = new HookRegistry();
                hooks.Register<BeforeInvocationEvent>((value, _) => { childState = value.InvocationState; return default; });
                return new(new Agent(new() { Model = childModel, Hooks = hooks, Tools = [stop], Name = spec.Name }));
            }
        });
        var input = Team(("a", "Do."));
        await using var parent = new Agent(new() { Model = new Events(request => HasResult(request) ? Answer("parent final") : Calls(("outer", "swarm", input))), Tools = [tool, stop] });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await parent.InvokeAsync("start", new() { InvocationState = caller }, deadline.Token);
        Assert.Equal(StopReasons.EndTurn, result.StopReason); Assert.Equal("parent final", result.ToString());
        Assert.NotNull(childState); Assert.NotSame(caller, childState); Assert.False(childState.ContainsKey("request_state")); Assert.Same(userValue, childState["user"]);
        Assert.Equal(2, caller.Count); Assert.Same(caller, result.InvocationState);
    }

    [Fact]
    public async Task TwoHumanAnswersResumeTheSameTeamWithoutRepeatingAcceptedEffects()
    {
        var builds = 0; var saved = 0; var answered = 0; var disposed = 0;
        var save = new FunctionTool(new("save", "Save", ObjectSchema()), (_, _, _) => { saved++; return new("saved"); });
        var approve = new FunctionTool(new("approve", "Approve", ObjectSchema()), (input, context, _) =>
        { var answer = context.Interrupt("approval", input.GetProperty("question")); answered++; return new(answer.GetString()!); });
        var childModel = new Events(request => HasResult(request) ? Answer("child done") : Calls(("save", "save", Empty),
            ("approval-a", "approve", JsonSerializer.SerializeToElement(new { question = "A" })), ("approval-b", "approve", JsonSerializer.SerializeToElement(new { question = "B" }))));
        var tool = SwarmTool.Create(new()
        {
            Builder = (spec, _, _) => { builds++; return new(new Agent(new() { Name = spec.Name, Model = childModel, Tools = [save, approve], OwnedResources = [new Resource(() => disposed++)] })); }
        });
        var team = Team(("a", "Do."));
        await using var parent = new Agent(new() { Model = new Events(request => HasResult(request) ? Answer("parent done") : Calls(("outer", "swarm", team))), Tools = [tool, save, approve] });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await parent.InvokeAsync("start", cancellationToken: deadline.Token);
        Assert.Equal(StopReasons.Interrupt, first.StopReason);
        var a = Assert.Single(first.Interrupts); Assert.Equal("A", a.Reason!.Value.GetProperty("reason").GetString());
        Assert.Equal(1, builds); Assert.Equal(1, saved); Assert.Equal(0, answered); Assert.Equal(0, disposed);
        var second = await parent.InvokeAsync(null, new() { InterruptResponses = [new(a.Id, JsonSerializer.SerializeToElement("yes A"))] }, deadline.Token);
        Assert.Equal(StopReasons.Interrupt, second.StopReason);
        var b = Assert.Single(second.Interrupts); Assert.Equal("B", b.Reason!.Value.GetProperty("reason").GetString());
        Assert.Equal(1, builds); Assert.Equal(1, saved); Assert.Equal(0, answered); Assert.Equal(0, disposed);
        var final = await parent.InvokeAsync(null, new() { InterruptResponses = [new(b.Id, JsonSerializer.SerializeToElement("yes B"))] }, deadline.Token);
        Assert.Equal("parent done", final.ToString()); Assert.Equal(1, builds); Assert.Equal(1, saved); Assert.Equal(2, answered); Assert.Equal(1, disposed); Assert.Null(parent.InterruptState);
    }

    [Fact]
    public async Task FailedParticipantReturnsCorrelatedNamedPartialOutputAndOriginalCause()
    {
        var failure = new IOException("participant failed"); var builds = 0;
        var tool = SwarmTool.Create(new()
        {
            Builder = (spec, _, _) =>
            {
                builds++;
                IModel model = spec.Name == "a" ? new Events(request => HasResult(request) ? Answer("partial α")
                    : Calls(("handoff", "handoff_to_agent", JsonSerializer.SerializeToElement(new { agent_name = "b", message = "continue" })))) : new Events(_ => throw failure);
                return new(new Agent(new() { Name = spec.Name, Model = model }));
            }
        });
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("unused")) });
        var input = Team(("a", "A."), ("b", "B."));
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Equal("team-call", result.ToolUseId);
        Assert.Contains("a: partial α", result.Content); Assert.Contains("failed", result.Content); Assert.Equal(2, builds);
        Assert.True(ReferenceEquals(failure, result.Error) || ReferenceEquals(failure, result.Error?.InnerException));
    }

    [Fact]
    public async Task CancellationAndConstructionFailurePhysicallyJoinOwnedChildrenOnly()
    {
        var model = new GatedModel(); var disposed = 0;
        var tool = SwarmTool.Create(new() { Builder = (spec, _, _) => new(new Agent(new() { Name = spec.Name, Model = model, OwnedResources = [new Resource(() => { Assert.True(model.Exited); disposed++; })] })) });
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("parent reusable")) });
        var input = Team(("a", "A.")); using var caller = new CancellationTokenSource();
        var active = tool.InvokeResultAsync(input, Context(parent, tool, input), caller.Token).AsTask();
        try
        {
            await Task.WhenAny(model.Entered.Task, active, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(model.Entered.Task.IsCompleted, "The accepted child body must start before cancellation is tested.");
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            Assert.Equal(caller.Token, error.CancellationToken); Assert.True(model.Exited); Assert.Equal(1, disposed);
        }
        finally
        {
            caller.Cancel();
            model.Release.TrySetResult(true);
            try { await active; } catch (Exception) { /* The main assertion retains the operation outcome; always join. */ }
        }

        var construction = new IOException("builder failed"); var closed = 0; var called = 0;
        var partial = SwarmTool.Create(new()
        {
            Builder = (spec, _, _) => spec.Name == "b" ? throw construction : new(new Agent(new()
            { Model = new Events(_ => { called++; return Answer("must not run"); }), OwnedResources = [new Resource(() => closed++)] }))
        });
        var both = Team(("a", "A."), ("b", "B."));
        var rejected = await partial.InvokeResultAsync(both, Context(parent, partial, both));
        Assert.Equal(ToolResultStatus.Error, rejected.Status); Assert.Equal(1, closed); Assert.Equal(0, called);
        Assert.True(ReferenceEquals(construction, rejected.Error) || ReferenceEquals(construction, rejected.Error?.InnerException));
        Assert.Equal("parent reusable", (await parent.InvokeAsync("still usable")).ToString());
    }

    private static bool HasResult(ModelRequest request) => request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().Any() == true;
    private static JsonElement ObjectSchema() => JsonSerializer.SerializeToElement(new { type = "object" });
    private static ITool Tool(string name) => new FunctionTool(new(name, "Helper", ObjectSchema()), (_, _, _) => new("ok"));
    private static JsonElement Team(params (string Name, string Instructions)[] agents) => JsonSerializer.SerializeToElement(new { task = "work", agents = agents.Select(agent => new { name = agent.Name, instructions = agent.Instructions }).ToArray() });
    private static ToolContext Context(Agent parent, ITool tool, JsonElement input) => new(parent, new("team-call", tool.Spec.Name, input), new Dictionary<string, object?>());
    private static ModelStreamEvent[] Answer(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private static IReadOnlyList<ModelStreamEvent> Calls(params (string Id, string Name, JsonElement Input)[] calls)
    {
        var result = new List<ModelStreamEvent> { new ModelMessageStartEvent() };
        for (var index = 0; index < calls.Length; index++) { var call = calls[index]; result.Add(new ModelContentBlockStartEvent(index, new(call.Id, call.Name, call.Input))); result.Add(new ModelContentBlockStopEvent(index)); }
        result.Add(new ModelMessageStopEvent(StopReasons.ToolUse)); return result;
    }
    private sealed class Events(Func<ModelRequest, IReadOnlyList<ModelStreamEvent>> events) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { foreach (var item in events(request)) { cancellationToken.ThrowIfCancellationRequested(); yield return item; } await Task.CompletedTask; }
    }
    private sealed class Resource(Action dispose) : IAsyncDisposable
    { public ValueTask DisposeAsync() { dispose(); return default; } }
    private sealed class GatedModel : IModel
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Exited { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            using var registration = cancellationToken.Register(() => Release.TrySetCanceled(cancellationToken));
            try { await Release.Task.ConfigureAwait(false); foreach (var item in Answer("released")) yield return item; }
            finally { Exited = true; }
        }
    }
}
