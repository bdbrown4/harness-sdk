using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClassicManager = Strands.Agents.Conversation.ConversationManager;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelCrossStatefulMinimumTests
{
    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript, false)]
    [InlineData(ModelHistoryBehavior.TypeScript, true)]
    [InlineData(ModelHistoryBehavior.Python, false)]
    [InlineData(ModelHistoryBehavior.Python, true)]
    public async Task IdleReplacementKeepsFullRetainedInputAndTheExplicitClearAuthority(ModelHistoryBehavior behavior, bool initiallyStateful)
    {
        var original = new RecordingModel("original", initiallyStateful);
        var replacement = new RecordingModel("replacement", !initiallyStateful);
        var manager = initiallyStateful ? null : new RecordingManager();
        var seed = State("seed");
        var options = new AgentOptions
        {
            Model = original,
            OwnsModel = true,
            ModelState = seed,
            ModelHistoryBehavior = behavior,
            ConversationManager = manager
        };
        var agent = new Agent(options);
        try
        {
            Assert.Equal("original-1", (await agent.InvokeAsync("first")).Message!.Text);
            Assert.Equal(initiallyStateful ? 0 : 2, agent.Messages.Count);
            agent.SetModel(replacement);
            Assert.Same(replacement, agent.Model);
            Assert.Same(replacement, agent.AuxModel);
            Assert.Equal("replacement-1", (await agent.InvokeAsync("second")).Message!.Text);

            var firstRequest = Assert.Single(replacement.Requests);
            Assert.Equal(initiallyStateful ? new[] { Role.User } : new[] { Role.User, Role.Assistant, Role.User }, firstRequest.Messages.Select(message => message.Role));
            Assert.Equal(initiallyStateful ? new[] { "second" } : new[] { "first", "original-1", "second" }, firstRequest.Messages.Select(message => message.Text));
            Assert.Equal("original-1", firstRequest.ModelState!["responseId"].GetString());
            var clear = behavior == ModelHistoryBehavior.TypeScript ? initiallyStateful : !initiallyStateful;
            Assert.Equal(clear ? 0 : initiallyStateful ? 2 : 4, agent.Messages.Count);

            Assert.Equal("replacement-2", (await agent.InvokeAsync("third")).Message!.Text);
            Assert.Equal(2, replacement.Requests.Count);
            var secondRequest = replacement.Requests[1];
            var expected = clear ? new[] { "third" } : initiallyStateful
                ? new[] { "second", "replacement-1", "third" }
                : new[] { "first", "original-1", "second", "replacement-1", "third" };
            Assert.Equal(expected, secondRequest.Messages.Select(message => message.Text));
            Assert.Equal("replacement-1", secondRequest.ModelState!["responseId"].GetString());
            Assert.Equal("replacement-2", agent.ModelState.Get<string>("responseId"));
            Assert.Equal(clear ? 0 : initiallyStateful ? 4 : 6, agent.Messages.Count);
            Assert.Equal("seed", seed["responseId"].GetString());
            Assert.NotSame(seed, firstRequest.ModelState);
            Assert.Same(original, options.Model);
            Assert.Equal(ModelSelectionTiming.BeforeModelHooks, options.ModelSelectionTiming);
            if (manager is not null)
            {
                Assert.Same(manager, options.ConversationManager);
                Assert.Equal(1, manager.Initializations);
                Assert.Equal(3, manager.BeforeCalls);
                Assert.Equal(3, manager.ManagementCalls);
                Assert.All(manager.Agents, value => Assert.Same(agent, value));
                Assert.All(manager.Instances, value => Assert.Same(manager, value));
            }
            Assert.Equal(1, original.IteratorDisposals);
            Assert.Equal(2, replacement.IteratorDisposals);
        }
        finally { await agent.DisposeAsync(); }
        Assert.Equal(1, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task ToolBoundaryReplacementRetainsTheCorrelatedPendingOutputAndBorrowedModel(ModelHistoryBehavior behavior)
    {
        var original = new RecordingModel("original", true, requestTool: true);
        var replacement = new RecordingModel("replacement", false);
        var effects = 0;
        var tool = new FunctionTool(new("select_model", "Select the next ready model.", Json("{\"type\":\"object\",\"additionalProperties\":false}")),
            (_, context, _) =>
            {
                context.Agent.SetModel(replacement);
                effects++;
                return new ValueTask<string>("selected");
            });
        var agent = new Agent(new()
        {
            Model = original,
            OwnsModel = true,
            Tools = [tool],
            ModelHistoryBehavior = behavior
        });
        try
        {
            var result = await agent.InvokeAsync("choose the next model");
            Assert.Equal("replacement-1", result.Message!.Text);
            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Equal(2, result.Turns);
            Assert.Equal(1, effects);
            Assert.Single(original.Requests);
            var request = Assert.Single(replacement.Requests);
            Assert.Equal(new[] { Role.User, Role.Assistant, Role.User }, request.Messages.Select(message => message.Role));
            Assert.Equal("choose the next model", request.Messages[0].Text);
            var call = Assert.IsType<ToolUseBlock>(Assert.Single(request.Messages[1].Content));
            var output = Assert.IsType<ToolResultBlock>(Assert.Single(request.Messages[2].Content));
            Assert.Equal("cross-mode-call", call.ToolUseId);
            Assert.Equal("select_model", call.Name);
            Assert.Equal(call.ToolUseId, output.ToolUseId);
            Assert.Equal(ToolResultStatus.Success, output.Status);
            Assert.Equal("selected", output.Content);
            Assert.Equal("original-1", request.ModelState!["responseId"].GetString());
            Assert.Equal("replacement-1", agent.ModelState.Get<string>("responseId"));
            Assert.Equal(behavior == ModelHistoryBehavior.TypeScript ? 0 : 4, agent.Messages.Count);
            Assert.Same(replacement, agent.Model);
            Assert.Equal(1, original.IteratorDisposals);
            Assert.Equal(1, replacement.IteratorDisposals);
        }
        finally { await agent.DisposeAsync(); }
        Assert.Equal(1, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeDefaultStillRejectsCapabilityChangesWithoutPublishingOrChangingState(bool initiallyStateful)
    {
        var original = new RecordingModel("original", initiallyStateful);
        var replacement = new RecordingModel("replacement", !initiallyStateful);
        var options = new AgentOptions { Model = original, ModelState = State("seed"), Messages = [Message.User("pending")] };
        await using var agent = new Agent(options);

        Assert.Equal(ModelHistoryBehavior.Native, options.ModelHistoryBehavior);
        Assert.Throws<ArgumentException>(() => agent.SetModel(replacement));
        Assert.Same(original, agent.Model);
        Assert.Equal("pending", Assert.Single(agent.Messages).Text);
        Assert.Equal("seed", agent.ModelState.Get<string>("responseId"));
        Assert.Empty(original.Requests);
        Assert.Empty(replacement.Requests);
        Assert.Equal(0, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
    }

    [Fact]
    public async Task DefaultJsonOmitsTheNewProfileAndSameCapabilityOwnershipIsUnchanged()
    {
        var original = new RecordingModel("original", false);
        var replacement = new RecordingModel("replacement", false);
        var options = new AgentOptions { Model = original, OwnsModel = true };
        Assert.Equal(0, (int)ModelHistoryBehavior.Native);
        var property = typeof(AgentOptions).GetProperty(nameof(AgentOptions.ModelHistoryBehavior))!;
        Assert.Equal(JsonIgnoreCondition.WhenWritingDefault, property.GetCustomAttributes(typeof(JsonIgnoreAttribute), false).Cast<JsonIgnoreAttribute>().Single().Condition);
        Assert.False(JsonSerializer.SerializeToElement(options).TryGetProperty(nameof(AgentOptions.ModelHistoryBehavior), out _));
        var selected = options with { ModelHistoryBehavior = ModelHistoryBehavior.Python };
        Assert.Equal((int)ModelHistoryBehavior.Python, JsonSerializer.SerializeToElement(selected).GetProperty(nameof(AgentOptions.ModelHistoryBehavior)).GetInt32());
        Assert.Equal(ModelHistoryBehavior.Native, options.ModelHistoryBehavior);
        var agent = new Agent(options);
        try
        {
            agent.SetModel(replacement);
            Assert.Same(replacement, agent.Model);
            Assert.Same(original, options.Model);
        }
        finally { await agent.DisposeAsync(); }
        Assert.Equal(1, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static Dictionary<string, JsonElement> State(string value) => new() { ["responseId"] = JsonSerializer.SerializeToElement(value) };

    private sealed class RecordingManager : ClassicManager
    {
        internal int Initializations { get; private set; }
        internal int BeforeCalls { get; private set; }
        internal int ManagementCalls { get; private set; }
        internal List<Agent> Agents { get; } = [];
        internal List<RecordingManager> Instances { get; } = [];
        public override async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            Initializations++;
            await base.InitializeAsync(agent, cancellationToken);
        }
        protected override ValueTask BeforeModelCallAsync(BeforeModelCallEvent value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeCalls++; Agents.Add(value.Agent); Instances.Add(this);
            return default;
        }
        protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManagementCalls++; Agents.Add(agent); Instances.Add(this);
            return default;
        }
        protected override ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The bounded fixture never requests context reduction.");
    }

    private sealed class RecordingModel(string name, bool stateful, bool requestTool = false) : IStatefulModel, IAsyncDisposable
    {
        public bool Stateful => stateful;
        internal List<ModelRequest> Requests { get; } = [];
        internal int DisposeCalls { get; private set; }
        internal int IteratorDisposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request with
            {
                Messages = request.Messages.ToArray(),
                ModelState = request.ModelState?.ToDictionary(value => value.Key, value => value.Value.Clone())
            });
            var call = Requests.Count;
            try
            {
                await Task.CompletedTask;
                yield return new ModelMessageStartEvent();
                if (requestTool)
                {
                    yield return new ModelContentBlockStartEvent(0, new("cross-mode-call", "select_model", Json("{}")));
                    yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{}");
                    yield return new ModelContentBlockStopEvent(0);
                    yield return new ModelMessageStopEvent(StopReasons.ToolUse) { ModelState = State(name + "-" + call) };
                }
                else
                {
                    yield return new ModelContentBlockStartEvent(0);
                    yield return new ModelContentBlockDeltaEvent(0, Text: name + "-" + call);
                    yield return new ModelContentBlockStopEvent(0);
                    yield return new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = State(name + "-" + call) };
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }
}
