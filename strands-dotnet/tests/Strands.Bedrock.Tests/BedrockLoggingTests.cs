using System.Text.Json;
using Microsoft.Extensions.Logging;
using Strands.Agents;

namespace Strands.Bedrock.Tests;

[CollectionDefinition("Bedrock logging", DisableParallelization = true)]
public sealed class BedrockLoggingCollection { }

[Collection("Bedrock logging")]
public sealed class BedrockLoggingTests : IDisposable
{
    private readonly ILogger _saved = SdkLogging.Logger;
    public void Dispose() => SdkLogging.ConfigureLogging(_saved);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRequestSchemaWarningUsesTheBorrowedSdkLoggerAndCannotBreakTheRequest(bool loggerFails)
    {
        var logger = new HostLogger(loggerFails); SdkLogging.ConfigureLogging(logger);
        var spec = new ToolSpec("lookup", "Lookup", JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"n":{"type":"integer","minimum":1}}}"""));
        var mapped = await BedrockStrictSchemaTests.MapAsync(spec, true);
        Assert.Equal("lookup", mapped.Name);
        Assert.Equal("Bedrock strict tool 'lookup' retains unsupported or unresolved schema keywords: minimum.", Assert.Single(logger.Messages));
    }
    private sealed class HostLogger(bool fails) : ILogger
    {
        internal List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, error)); if (fails) throw new IOException("host logger"); }
    }
}
