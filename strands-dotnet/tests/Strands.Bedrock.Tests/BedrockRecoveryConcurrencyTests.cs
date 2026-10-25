using System.Text.Json;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Logging;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;
using static Strands.Bedrock.Tests.BedrockRecoveryConformanceTests;
using Reply = Strands.Bedrock.Tests.BedrockRecoveryWireFixture.Reply;

namespace Strands.Bedrock.Tests;

public sealed class BedrockRecoveryConcurrencyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigUpdateDuringFirstResponseDoesNotChangeTheAdmittedRecoverySnapshot(bool streaming)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError), new Reply(), new Reply())
        {
            BeforeReply = async index => { if (index == 0) { entered.TrySetResult(true); await release.Task.ConfigureAwait(false); } }
        };
        using var client = wire.CreateClient();
        var stops = new List<string> { "original-stop" };
        using var document = JsonDocument.Parse("""{"thinking":{"type":"adaptive"},"admitted":"雪"}""");
        using var model = new BedrockModel(client, Options(streaming) with { StopSequences = stops, AdditionalModelRequestFields = document.RootElement });
        document.Dispose();
        var pending = CollectAsync(model, Plain());
        Exception? primary = null;
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5))));
            stops[0] = "mutated";
            model.UpdateConfig(value => value with
            {
                ModelId = "replacement",
                RecoverStaleThinkingBlocks = false,
                AdditionalModelRequestFields = JsonSerializer.Deserialize<JsonElement>("""{"thinking":{"type":"disabled"}}""")
            });
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            try { await pending; }
            catch (Exception cleanup) when (primary is not null) { primary.Data["Fixture.PendingJoinFailure"] = cleanup; }
        }
        Assert.Equal("summary", await pending);
        Assert.Equal("summary", await CollectAsync(model, Plain()));
        await wire.Completion;
        Assert.All(wire.Requests.Take(2), value => Assert.Contains("fixture-model", value.Target, StringComparison.Ordinal));
        Assert.Contains("replacement", wire.Requests[2].Target, StringComparison.Ordinal);
        Assert.True(HasDropBlock(wire.Requests[1].Body));
        Assert.False(HasDropBlock(wire.Requests[2].Body));
        Assert.Equal("雪", wire.Requests[1].Body.GetProperty("additionalModelRequestFields").GetProperty("admitted").GetString());
        Assert.All(wire.Requests, value => Assert.Equal("original-stop", value.Body.GetProperty("inferenceConfig").GetProperty("stopSequences")[0].GetString()));
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "recovery-snapshot-wire", Requests = wire.Requests }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BorrowedClientDoesNotShareLearnedStateBetweenModelInstances(bool streaming, bool turn)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: turn ? BedrockRecoveryWireFixture.TurnError : BedrockRecoveryWireFixture.BindingError), new Reply(), new Reply());
        using var client = wire.CreateClient();
        using (var first = new BedrockModel(client, Options(streaming)))
            Assert.Equal("summary", await CollectAsync(first, turn ? Turns() : Plain()));
        using (var second = new BedrockModel(client, Options(streaming)))
            Assert.Equal("summary", await CollectAsync(second, turn ? Turns() : Plain()));
        await wire.Completion;
        Assert.False(HasDropBlock(wire.Requests[2].Body));
        Assert.Equal(turn ? 4 : 1, wire.Requests[2].Body.GetProperty("messages").GetArrayLength());
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "recovery-isolation-wire", Turn = turn, Requests = wire.Requests }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ARepeatedEligibleErrorStopsAtTheSecondAcquisition(bool streaming, bool turn)
    {
        var message = turn ? BedrockRecoveryWireFixture.TurnError : BedrockRecoveryWireFixture.BindingError;
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: message), new Reply(Error: message));
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        var failure = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, turn ? Turns() : Plain()));
        Assert.Equal(message, Assert.IsType<ValidationException>(failure.InnerException).Message);
        await wire.Completion;
        Assert.Equal(2, wire.Requests.Length);
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "recovery-bounded-wire", Turn = turn, Requests = wire.Requests }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedRecoveryNeverExceedsFourAcquisitionsAndKeepsTheLastFailure(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError),
            new Reply(Error: BedrockRecoveryWireFixture.TurnError), new Reply(Error: BedrockRecoveryWireFixture.BindingError), new Reply(Error: BedrockRecoveryWireFixture.TurnError));
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        var failure = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, Turns()));
        Assert.Equal(BedrockRecoveryWireFixture.TurnError, Assert.IsType<ValidationException>(failure.InnerException).Message);
        await wire.Completion;
        Assert.Equal(4, wire.Requests.Length);
        Assert.Equal(new[] { false, true, false, true }, wire.Requests.Select(value => HasDropBlock(value.Body)));
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "recovery-four-bound-wire", Requests = wire.Requests }));
    }
}

[Collection("Bedrock logging")]
public sealed class BedrockRecoveryCancellationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CallerCancellationFromTheRetryWarningPreservesTokenAndPreventsAnotherWireSend(bool streaming, bool turn)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: turn ? BedrockRecoveryWireFixture.TurnError : BedrockRecoveryWireFixture.BindingError), new Reply());
        using var client = wire.CreateClient();
        using var caller = new CancellationTokenSource();
        using var model = new BedrockModel(client, Options(streaming));
        var saved = SdkLogging.Logger;
        var logger = new CancelLogger(caller);
        try
        {
            SdkLogging.ConfigureLogging(logger);
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(model, turn ? Turns() : Plain(), caller.Token));
            Assert.Equal(caller.Token, failure.CancellationToken);
            Assert.Equal(1, logger.Calls);
            Assert.Single(wire.Requests);
        }
        finally { SdkLogging.ConfigureLogging(saved); }
        // Cancellation is local to this call; both the borrowed client and model remain usable.
        Assert.Equal("summary", await CollectAsync(model, turn ? Turns() : Plain()));
        await wire.Completion;
        Assert.Equal(2, wire.Requests.Length);
        Assert.Equal(!turn, HasDropBlock(wire.Requests[1].Body));
        Assert.Equal(turn ? 4 : 1, wire.Requests[1].Body.GetProperty("messages").GetArrayLength());
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "recovery-cancel-between-wire", Streaming = streaming, Turn = turn, ExactCallerToken = true, Requests = wire.Requests }));
    }

    private sealed class CancelLogger(CancellationTokenSource caller) : ILogger
    {
        internal int Calls { get; private set; }
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning && formatter(state, error).IndexOf("retrying once", StringComparison.Ordinal) >= 0)
            { Calls++; caller.Cancel(); }
        }
    }
}
