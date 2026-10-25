using System.Text.Json;
using Microsoft.Extensions.Logging;
using Strands.Agents;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

[CollectionDefinition("KB SDK logging", DisableParallelization = true)]
public sealed class KnowledgeBaseLoggingCollection;

[Collection("KB SDK logging")]
public sealed class KnowledgeBaseCustomWriteDiagnosticsTests
{
    [Fact]
    public async Task ThrowingDiagnosticsDoNotReplaceSuccessfulWritesOrOriginalSdkFailure()
    {
        using var agent = new AgentClient(); var previous = SdkLogging.Logger; var logger = new CallbackLogger(_ => throw new IOException("diagnostic failure"));
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseCustomWriteTests.Options(agent) with { Scope = "tenant" });
        using var metadata = JsonDocument.Parse("{\"namespace\":\"collision\",\"unsupported\":{}}");
        try
        {
            SdkLogging.ConfigureLogging(logger); await store.AddAsync("fact", metadata.RootElement); Assert.Equal(1, agent.IngestCalls);
            var primary = new IOException("original ingestion failure"); agent.OnIngest = (_, _) => Task.FromException<Amazon.BedrockAgent.Model.IngestKnowledgeBaseDocumentsResponse>(primary);
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await store.AddAsync("fact"))); Assert.True(logger.Calls >= 3);
        }
        finally { SdkLogging.ConfigureLogging(previous); }
    }

    [Fact]
    public async Task DroppedMetadataDiagnosticCannotSynchronouslyDisposeItsOwnWrite()
    {
        using var agent = new AgentClient(); var previous = SdkLogging.Logger; Exception? observed = null;
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseCustomWriteTests.Options(agent));
        var logger = new CallbackLogger(_ => observed = Record.Exception(store.Dispose));
        try
        {
            SdkLogging.ConfigureLogging(logger); using var metadata = JsonDocument.Parse("{\"unsupported\":{}}");
            await store.AddAsync("fact", metadata.RootElement); Assert.IsType<InvalidOperationException>(observed); Assert.Equal(1, agent.IngestCalls);
        }
        finally { SdkLogging.ConfigureLogging(previous); }
        await store.DisposeAsync();
    }

    private sealed class CallbackLogger(Action<LogLevel> callback) : ILogger
    {
        internal int Calls;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter) { Calls++; callback(level); }
    }
}
