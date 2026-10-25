using Microsoft.Extensions.Logging;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Storage;

namespace Strands.Bidi.Tests;

[CollectionDefinition("Bidi logging", DisableParallelization = true)]
public sealed class BidiLoggingCollection { }

[Collection("Bidi logging")]
public sealed class BidiLoggingTests : IDisposable
{
    private readonly ILogger _saved = SdkLogging.Logger;
    public void Dispose() => SdkLogging.ConfigureLogging(_saved);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSnapshotTriggerWarningKeepsItsCauseAndRequiredLatestWrite(bool loggerFails)
    {
        var logger = new HostLogger(loggerFails); SdkLogging.ConfigureLogging(logger);
        var failure = new IOException("trigger failed");
        var storage = new InMemoryStorage();
        var manager = new BidiSnapshotSessionManager(storage, saveLatestOn: BidiSessionSaveStrategy.Trigger, snapshotTrigger: _ => throw failure);
        await using var host = new Agent(new() { Model = new BidiAgentTests.NoCallsModel() });
        await using var model = new BidiAgentTests.ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager });
        await agent.StartAsync(); await agent.StopAsync();
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("Snapshot trigger failed; immutable checkpoint skipped: trigger failed", entry.Message);
        Assert.Same(failure, entry.Error);
        Assert.NotNull(await storage.ReadAsync("session/default-session/scopes/agent/default/snapshots/snapshot_latest.json"));
        Assert.Empty(await manager.ListSnapshotIdsAsync(agent));
    }
    private sealed record Entry(string Message, Exception? Error);
    private sealed class HostLogger(bool fails) : ILogger
    {
        internal List<Entry> Entries { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { Entries.Add(new(formatter(state, error), error)); if (fails) throw new IOException("host logger"); }
    }
}
