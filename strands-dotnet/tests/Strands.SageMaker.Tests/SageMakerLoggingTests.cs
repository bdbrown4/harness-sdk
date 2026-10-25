using System.Text;
using Microsoft.Extensions.Logging;
using Strands.Agents;
using Strands.Agents.Models;
using Strands.Agents.SageMaker;

namespace Strands.SageMaker.Tests;

[CollectionDefinition("SageMaker logging", DisableParallelization = true)]
public sealed class SageMakerLoggingCollection { }

[Collection("SageMaker logging")]
public sealed class SageMakerLoggingTests : IDisposable
{
    private readonly ILogger _saved = SdkLogging.Logger;
    public void Dispose() => SdkLogging.ConfigureLogging(_saved);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAwsRequestWarningsUseTheHostAndRetainTheSuccessfulResult(bool loggerFails)
    {
        var logger = new HostLogger(loggerFails); SdkLogging.ConfigureLogging(logger);
        using var body = new SageMakerModelTests.TrackingStream(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        using var client = new SageMakerModelTests.StubClient(body);
        using var model = new SageMakerAIModel(client, new() { EndpointName = "fixture", CacheConfig = new(), Payload = new() { Stream = false } });
        var events = await SageMakerModelTests.CollectAsync(model, new([new(Role.User, [new TextBlock("input"), new ReasoningBlock("private", "signature")])]) { ToolChoice = new(ToolChoiceMode.Any) });
        Assert.Equal(StopReasons.EndTurn, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
        Assert.Equal(new[] { "SageMaker ignores requested tool choice, matching the pinned provider.", "SageMaker ignores prompt cache controls.", "SageMaker skips ReasoningBlock in chat history, matching the pinned provider." }, logger.Messages);
        Assert.False(client.Disposed);
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
