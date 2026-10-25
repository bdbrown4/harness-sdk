using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiAgentTests
{
    [Fact]
    public async Task StartAndSendUseTheRealToolHostsConfigurationAndCopyUserHistory()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "host prompt" });
        await using var model = new ScriptedModel();
        var messages = new List<Message> { Message.User("prior") };
        await using var agent = new BidiAgent(new() { ToolAgent = host, Model = model, Messages = messages });
        messages.Clear();
        await agent.StartAsync();
        Assert.Equal("host prompt", model.Session!.SystemPrompt);
        Assert.Equal("prior", Assert.Single(model.Session.Messages).Text);
        await agent.SendAsync(new BidiMessage([new TextBlock("current")]));
        Assert.Equal("current", Assert.IsType<TextBlock>(Assert.Single(Assert.IsType<BidiMessage>(Assert.Single(model.Inputs)).Content)).Text);
        Assert.Equal(new[] { "prior", "current" }, agent.Messages.Select(message => message.Text));
        await agent.StopAsync(); Assert.Equal(1, model.Stops);
    }

    [Fact]
    public async Task RealToolExecutionPreservesStateIdentityHooksSchemaAndProviderCorrelation()
    {
        var state = new Dictionary<string, object?> { ["user"] = 7 };
        var seen = new List<string>();
        var tool = new FunctionTool(new("lookup", "Look up a value", JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "key" }, properties = new { key = new { type = "integer" } } })),
            (input, context, token) => { Assert.Same(state, context.InvocationState); seen.Add("body"); return new ValueTask<string>((input.GetProperty("key").GetInt32() + 1).ToString()); });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        host.Hooks.Register<BeforeToolCallEvent>((_, _) => { seen.Add("before"); return default; });
        host.Hooks.Register<AfterToolCallEvent>((_, _) => { seen.Add("after"); return default; });
        await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([new("call", "lookup", JsonSerializer.SerializeToElement(new { key = 6 }))]));
        await using var agent = new BidiAgent(new() { ToolAgent = host, Model = model });
        await agent.StartAsync(state);
        await using var reader = agent.ReceiveAsync().GetAsyncEnumerator();
        do { Assert.True(await Within(NextAsync())); } while (reader.Current is not ToolResultEvent);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single((await Within(model.Results.Task)).Content));
        Assert.Equal("call", result.ToolUseId); Assert.Equal("7", result.Content);
        Assert.Equal(new[] { "before", "body", "after" }, seen);
        Assert.Single(state); Assert.Empty(host.Messages);
        Assert.Equal("call", Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>()).ToolUseId);
        Assert.Equal("call", Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).ToolUseId);
        await agent.StopAsync();
        async Task<bool> NextAsync() => await reader.MoveNextAsync();
    }

    [Fact]
    public async Task AnInvalidSchemaProducesAnErrorWithoutExecutingTheToolBody()
    {
        var calls = 0;
        var tool = new FunctionTool(new("lookup", "Look up a value", JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "key" } })), (_, _, _) => { calls++; return new ValueTask<string>("unexpected"); });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([new("call", "lookup", JsonSerializer.SerializeToElement(new { }))]));
        await using var agent = new BidiAgent(new() { ToolAgent = host, Model = model });
        await agent.StartAsync();
        await using var reader = agent.ReceiveAsync().GetAsyncEnumerator();
        do { Assert.True(await Within(NextAsync())); } while (reader.Current is not ToolResultEvent);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single((await Within(model.Results.Task)).Content));
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Equal(0, calls);
        await agent.StopAsync();
        async Task<bool> NextAsync() => await reader.MoveNextAsync();
    }

    internal sealed class NoCallsModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield return new ModelMessageStartEvent(); throw new InvalidOperationException("The tool host must never invoke its chat model."); }
    }

    internal sealed class ScriptedModel(params IBidiOutputEvent[] events) : IBidiModel
    {
        internal BidiSessionOptions? Session;
        internal List<IBidiInput> Inputs { get; } = [];
        internal TaskCompletionSource<BidiMessage> Results { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Stops;
        internal int Starts;
        internal int Disposals;
        internal Exception? StopError;
        internal Exception? DisposeError;
        internal Action<CancellationToken>? OnReceive;
        internal Func<int, Task>? BeforeEvent;
        internal Task? StartGate;
        internal bool EndAfterEvents;
        internal TaskCompletionSource<bool> StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        public BidiConnectionOptions Connection => new() { AutoRestart = false };
        public async Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        { Starts++; Session = session; StartEntered.TrySetResult(true); if (StartGate is { } gate) await gate; }
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
        {
            lock (Inputs) Inputs.Add(input);
            if (input is BidiMessage message && message.Content[0] is ToolResultBlock) Results.TrySetResult(message);
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            OnReceive?.Invoke(linked.Token);
            yield return new BidiConnectionStartEvent("connection", "fixture");
            for (var index = 0; index < events.Length; index++)
            {
                if (BeforeEvent is { } before) await before(index);
                yield return events[index];
            }
            if (EndAfterEvents) yield break;
            await Task.Delay(Timeout.Infinite, linked.Token);
        }
        public Task StopAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Stops++; _stop.Cancel(); if (StopError is { } error) throw error; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; _stop.Dispose(); if (DisposeError is { } error) throw error; return default; }
    }
}
