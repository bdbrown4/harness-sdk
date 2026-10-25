using System.Net.Http;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Testing;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class SubagentInterruptTests
{
    [Fact]
    public async Task ChildApprovalPausesTheParentAndResumesAFreshOwnedChild()
    {
        var bodies = 0; var children = 0; var disposed = 0;
        var approve = new FunctionTool(new("approve", "Approve", BridgeSupport.EmptyInput), (_, context, _) =>
        { var answer = context.Interrupt("approval"); bodies++; return new ValueTask<string>(answer.GetString()!); });
        var tool = Subagent.Create(new()
        {
            Name = "delegate_alias",
            Tools = new Fixed(new[] { "approve" }),
            InheritedTools = ["approve"],
            Builder = (spec, _, _) => { children++; return new(new Agent(new() { Model = BridgeSupport.ToolOrEcho("child-call", "approve", BridgeSupport.EmptyInput), Tools = [approve], Messages = spec.Messages, OwnedResources = [new BridgeSupport.Resource(() => disposed++)] })); },
        });
        await using var parent = new Agent(new() { Model = BridgeSupport.ToolOrEcho("parent/alias:call", "delegate_alias", BridgeSupport.TaskInput), Tools = [approve, tool] });
        var state = new Dictionary<string, object?> { ["tenant"] = "same" };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pause = await parent.InvokeAsync("request", new() { InvocationState = state }, deadline.Token);
        Assert.Equal(StopReasons.Interrupt, pause.StopReason); Assert.Equal(0, bodies); Assert.Equal(1, disposed);
        var question = Assert.Single(pause.Interrupts); Assert.Equal("parent/alias:call", question.ToolUseId); Assert.Equal("approval", question.Name);
        var repeated = await parent.InvokeAsync(null, new() { InvocationState = state }, deadline.Token);
        Assert.Equal(question.Id, Assert.Single(repeated.Interrupts).Id); Assert.Equal(1, children); Assert.Equal(1, disposed);

        var result = await parent.InvokeAsync(null, new() { InvocationState = state, InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement("approved"))] }, deadline.Token);
        Assert.Equal("approved", result.ToString()); Assert.Equal(1, bodies); Assert.Equal(2, children); Assert.Equal(2, disposed);
        Assert.Same(state, result.InvocationState); Assert.Single(state); Assert.Null(parent.InterruptState);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealFileSessionRestoresMultipleChildApprovalsWithoutRepeatingCompletedEffects(bool partialRestart)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-harness-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var store = new FileSessionStore(directory);
        var saved = 0; var approved = 0; var children = 0; var disposed = 0;
        var save = new FunctionTool(new("save", "Save", BridgeSupport.EmptyInput), (_, _, _) => { saved++; return new ValueTask<string>("saved"); });
        var approve = new FunctionTool(new("approve", "Approve", BridgeSupport.EmptyInput), (_, context, _) =>
        { var answer = context.Interrupt("approval", JsonSerializer.SerializeToElement(new { call = context.ToolUse.ToolUseId })); approved++; return new ValueTask<string>(answer.GetString()!); });
        Agent Parent()
        {
            var tool = Subagent.Create(new()
            {
                Name = "delegate_alias",
                InheritedTools = ["save", "approve"],
                Tools = new Fixed(new[] { "save", "approve" }),
                Builder = (spec, _, _) =>
                {
                    children++;
                    return new(new Agent(new()
                    {
                        Model = new BridgeSupport.Events(BridgeSupport.BatchApprovals),
                        Messages = spec.Messages,
                        Tools = [save, approve],
                        OwnedResources = [new BridgeSupport.Resource(() => disposed++)]
                    }));
                },
            });
            return new(new() { Model = BridgeSupport.ToolOrEcho("parent/call:alias", "delegate_alias", BridgeSupport.TaskInput), Tools = [save, approve, tool], SessionStore = store, SessionId = "parent" });
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            IReadOnlyList<Interrupt> questions;
            await using (var first = Parent())
            {
                var paused = await first.InvokeAsync("request", cancellationToken: deadline.Token);
                Assert.Equal(StopReasons.Interrupt, paused.StopReason); questions = paused.Interrupts;
                Assert.Equal(2, questions.Count); Assert.Equal(1, saved); Assert.Equal(0, approved); Assert.Equal(1, disposed);
            }
            if (partialRestart)
                await using (var middle = Parent())
                {
                    var partial = await middle.InvokeAsync(null, new() { InterruptResponses = [new(questions[0].Id, JsonSerializer.SerializeToElement("first"))] }, deadline.Token);
                    Assert.Equal(StopReasons.Interrupt, partial.StopReason); Assert.Equal(1, saved); Assert.Equal(1, approved);
                    Assert.Equal(questions[1].Id, Assert.Single(partial.Interrupts).Id); questions = partial.Interrupts;
                }
            await using var last = Parent();
            var result = await last.InvokeAsync(null, new() { InterruptResponses = questions.Reverse().Select(question => new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("approved"))).ToArray() }, deadline.Token);
            Assert.Equal("finished", result.ToString()); Assert.Equal(1, saved); Assert.Equal(2, approved);
            Assert.Equal(partialRestart ? 3 : 2, children); Assert.Equal(children, disposed); Assert.Null(last.InterruptState);
            var snapshot = await last.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }, deadline.Token);
            Assert.Equal(JsonValueKind.Null, snapshot.Data["interruptState"].ValueKind);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BackgroundDelegationResumesItsOwnDurableChildTurn()
    {
        var approved = 0; var disposed = 0;
        var approve = new FunctionTool(new("approve", "Approve", BridgeSupport.EmptyInput), (_, context, _) =>
        { var answer = context.Interrupt("approval"); approved++; return new ValueTask<string>(answer.GetString()!); });
        var tool = Subagent.Create(new()
        {
            Name = "delegate_alias",
            Tools = new Fixed(new[] { "approve" }),
            InheritedTools = ["approve"],
            Builder = (spec, _, _) => new(new Agent(new()
            {
                Model = BridgeSupport.ToolOrEcho("child", "approve", BridgeSupport.EmptyInput),
                Tools = [approve],
                Messages = spec.Messages,
                OwnedResources = [new BridgeSupport.Resource(() => disposed++)]
            })),
        });
        // An answer-only resume may follow the parent's assistant acknowledgement.
        // Inspect accepted transcript results rather than manufacturing a second same-ID delegation.
        var parentModel = new BridgeSupport.Events(request =>
        {
            var latest = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().LastOrDefault();
            return latest is null ? BridgeSupport.Call("background-call", "delegate_alias", BridgeSupport.TaskInput) : BridgeSupport.Answer(latest.Content);
        });
        await using var parent = new Agent(new() { Model = parentModel, Tools = [approve, tool], BackgroundTasks = new() { Always = ["delegate_alias"] } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pause = await parent.InvokeAsync("request", cancellationToken: deadline.Token);
        Assert.Equal(StopReasons.Interrupt, pause.StopReason); var question = Assert.Single(pause.Interrupts); Assert.StartsWith("background:", question.Id); Assert.Equal(1, disposed);
        var done = await parent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement("approved"))] }, deadline.Token);
        Assert.Contains("approved", done.ToString()); Assert.Equal(1, approved); Assert.Equal(2, disposed); Assert.Empty(done.Interrupts);
    }

    [Fact]
    public async Task DirectCallsKeepHumanRefusalStickyAcrossAnAfterHook()
    {
        var approve = new FunctionTool(new("approve", "Approve", BridgeSupport.EmptyInput), (_, context, _) => new ValueTask<string>(context.Interrupt("approval").GetString()!));
        var tool = Subagent.Create(new()
        {
            Name = "delegate_alias",
            Tools = new Fixed(new[] { "approve" }),
            InheritedTools = ["approve"],
            Builder = (spec, _, _) => new(new Agent(new() { Model = BridgeSupport.ToolOrEcho("child", "approve", BridgeSupport.EmptyInput), Tools = [approve], Messages = spec.Messages })),
        });
        await using var parent = new Agent(new() { Model = BridgeSupport.Text("unused"), Tools = [approve, tool] });
        parent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Result = new(value.ToolUse.ToolUseId, "forged success"); return default; });
        var result = await parent.InvokeToolAsync(new("direct", "delegate_alias", BridgeSupport.TaskInput));
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.DoesNotContain("forged success", result.Content); Assert.Null(parent.InterruptState); Assert.Empty(parent.Messages);
    }

    [Fact]
    public async Task FreshHarnessParentsKeepMcpServerGrantsAndDoNotReplayCompletedRemoteCalls()
    {
        await using var selected = new McpTestServer(); await using var excluded = new McpTestServer();
        using var httpA = new HttpClient(); using var httpB = new HttpClient();
        var providerA = McpProviderHarnessTests.Provider(selected, httpA, "a", "a");
        var providerB = McpProviderHarnessTests.Provider(excluded, httpB, "b", "b");
        var approved = 0;
        var approve = new FunctionTool(new("approve", "Approve", BridgeSupport.EmptyInput), (_, context, _) =>
        { var answer = context.Interrupt("approval"); approved++; return new ValueTask<string>(answer.GetString()!); });
        var directory = Path.Combine(Path.GetTempPath(), "strands-harness-mcp-child-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var store = new FileSessionStore(directory);
        var requested = JsonSerializer.SerializeToElement(new { task = "child", tools = new[] { "approve" }, mcp_servers = new[] { "a" } });
        var childRequests = new List<ModelRequest>();
        var model = new BridgeSupport.Events(request =>
        {
            if (request.Tools!.Any(spec => spec.Name == "subagent"))
            {
                var last = request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().LastOrDefault();
                return last is null ? BridgeSupport.Call("parent/alias", "subagent", requested) : BridgeSupport.Answer(last.Content);
            }
            childRequests.Add(request);
            if (request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().Any() == true) return BridgeSupport.Answer("finished");
            return new ModelStreamEvent[] { new ModelMessageStartEvent(),
                new ModelContentBlockStartEvent(0, new("remote", "a_echo", JsonSerializer.SerializeToElement(new { value = "saved remotely" }))), new ModelContentBlockStopEvent(0),
                new ModelContentBlockStartEvent(1, new("approval", "approve", BridgeSupport.EmptyInput)), new ModelContentBlockStopEvent(1), new ModelMessageStopEvent(StopReasons.ToolUse) };
        });
        Task<Agent> Parent() => Harness.CreateAsync(McpProviderHarnessTests.Options() with
        {
            BuiltinTools = ["subagent"],
            AgentOptions = new() { Model = model, Tools = [approve], ToolProviders = [providerA, providerB], SessionStore = store, SessionId = "parent" }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            Interrupt question;
            await using (var first = await Parent())
            {
                var paused = await first.InvokeAsync("request", cancellationToken: deadline.Token); question = Assert.Single(paused.Interrupts);
                Assert.Equal(StopReasons.Interrupt, paused.StopReason); Assert.Equal(1, selected.Calls); Assert.Equal(0, excluded.Calls);
                Assert.Equal(0, selected.Deletions); Assert.Equal(0, excluded.Deletions);
            }
            Assert.Equal(1, selected.Deletions); Assert.Equal(1, excluded.Deletions);
            await using (var next = await Parent())
            {
                var result = await next.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement("approved"))] }, deadline.Token);
                Assert.Equal("finished", result.ToString()); Assert.Equal(1, selected.Calls); Assert.Equal(0, excluded.Calls); Assert.Equal(1, approved);
                Assert.All(childRequests, request => Assert.Equal(new[] { "a_echo", "approve" }, request.Tools!.Select(spec => spec.Name).OrderBy(name => name, StringComparer.Ordinal)));
                Assert.Equal(2, selected.Initializations); Assert.Equal(2, excluded.Initializations);
            }
            Assert.Equal(2, selected.Deletions); Assert.Equal(2, excluded.Deletions);
        }
        finally { Directory.Delete(directory, true); }
    }

}
