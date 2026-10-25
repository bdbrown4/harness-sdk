using System.Runtime.CompilerServices;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;

namespace Strands.Bidi.Tests;

public sealed class BidiLifecycleCallbackTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task ModelLifecycleCallbacksCannotAwaitTheirOwnAgentLifecycle(string phase)
    {
        using var safety = new CancellationTokenSource();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        var model = new CallbackModel(phase); var agent = new BidiAgent(new() { ToolAgent = host, Model = model, OwnsModel = true });
        model.Callback = () => agent.StopAsync(safety.Token);
        try
        {
            if (phase != "start") await agent.StartAsync();
            var operation = phase switch { "start" => agent.StartAsync(), "stop" => agent.StopAsync(), _ => agent.DisposeAsync().AsTask() };
            if (await Task.WhenAny(operation, Task.Delay(250)) != operation) safety.Cancel();
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        }
        finally
        {
            safety.Cancel();
            try { await agent.DisposeAsync(); } catch { } // Observe the cached lifecycle failure while preserving the assertion above.
        }
    }

    [Fact]
    public async Task PluginInitializationCannotAwaitTheEnclosingBidirectionalStop()
    {
        using var safety = new CancellationTokenSource();
        BidiAgent? agent = null;
        var plugin = new CallbackPlugin(() => agent!.StopAsync(safety.Token));
        await using var host = new Agent(new() { Model = new NoCallsModel(), Plugins = [plugin] });
        await using var model = new ScriptedModel();
        await using (agent = new BidiAgent(new() { ToolAgent = host, Model = model }))
        {
            var operation = agent.StartAsync();
            if (await Task.WhenAny(operation, Task.Delay(250)) != operation) safety.Cancel();
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.Equal(0, model.Starts);
        }
    }

    private sealed class CallbackPlugin(Func<Task> callback) : IPlugin
    {
        public string Name => "test:lifecycle-callback";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => new(callback());
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class CallbackModel(string phase) : IBidiModel
    {
        internal Func<Task> Callback = () => Task.CompletedTask;
        public BidiConnectionOptions Connection => new() { AutoRestart = false };
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default) => phase == "start" ? Callback() : Task.CompletedTask;
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { yield return new BidiConnectionStartEvent("callback", "fixture"); await Task.Delay(Timeout.Infinite, cancellationToken); }
        public Task StopAsync(CancellationToken cancellationToken = default) => phase == "stop" ? Callback() : Task.CompletedTask;
        public ValueTask DisposeAsync() => phase == "dispose" ? new(Callback()) : default;
    }
}
