using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Strands.Agents.Context;

namespace Strands.Agents.Tests.Logging;

[Collection("SDK logging")]
public sealed class RemainingLoggingTests : IDisposable
{
    private readonly ILogger _saved = SdkLogging.Logger;
    public void Dispose() => SdkLogging.ConfigureLogging(_saved);

    [Fact]
    public void ActualOffloadFilterWarningUsesTheConfiguredHostWithoutChangingItsPolicy()
    {
        var host = new HostLogger(); SdkLogging.ConfigureLogging(host);
        var target = OffloadTarget.ForTools("read", "!write");
        Assert.NotNull(target);
        Assert.Equal("An offload tool filter contains includes and excludes; exclusions are ignored.", Assert.Single(host.Entries).Message);
    }

    [Fact]
    public void ActualLlamaRequestWarningKeepsThePinnedToolChoiceOmission()
    {
        var host = new HostLogger(); SdkLogging.ConfigureLogging(host);
        var body = global::Strands.Agents.Models.LlamaApiFormatting.Format(new([Message.User("input")]) { ToolChoice = new(ToolChoiceMode.Any) }, new("model", "key"));
        Assert.False(body.ContainsKey("tool_choice"));
        Assert.Equal("ToolChoice was provided to this provider but is not supported by the pinned Llama API adapter; set HonorToolChoice to enable native wire support.", Assert.Single(host.Entries).Message);
    }

    [Fact]
    public async Task ActualSummarizerFailureRetainsItsTolerantOutcomeAndOriginalCauseInTheHost()
    {
        var host = new HostLogger(); SdkLogging.ConfigureLogging(host);
        var failure = new IOException("summary backend");
        var result = await ContextReductionMethods.SummarizeAsync([new TextBlock("source")], new FailureModel(failure));
        Assert.Null(result);
        var entry = Assert.Single(host.Entries);
        Assert.Equal("Context summarization failed: summary backend", entry.Message);
        Assert.Same(failure, entry.Error);
    }

    [Fact]
    public async Task ActualContinuationCallbackFailureIsObservedWithoutChangingSettlement()
    {
        var host = new HostLogger(); SdkLogging.ConfigureLogging(host);
        await using var agent = new Agent(new() { Model = new CallModel() });
        var input = new ContinuationInputs(agent);
        var boundary = new AfterInvocationEvent(agent, null, null);
        var failure = new IOException("settlement callback");
        input.Add(boundary, [Message.User("accepted")], onAppended: () => throw failure);
        Assert.Single((await input.PrepareAsync(boundary, StopReasons.EndTurn))!);
        await input.MarkAppendedAsync(boundary);
        await input.MarkAppendedAsync(boundary);
        var entry = Assert.Single(host.Entries);
        Assert.Equal("Continuation settlement callback failed: settlement callback", entry.Message);
        Assert.Same(failure, entry.Error);
    }

    [Fact]
    public async Task ActualNestedAgentPauseWarningKeepsItsCorrelationAndBorrowedLogger()
    {
        var host = new HostLogger(); SdkLogging.ConfigureLogging(host);
        var approval = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, context, _) => new ValueTask<string>(context.Interrupt("approval").GetRawText()));
        await using var child = new Agent(new() { Model = new CallModel("approve"), Tools = [approval], Name = "specialist" });
        var tool = child.AsTool(new AgentToolOptions { Name = "specialist", PreserveContext = true });
        await using var parent = new Agent(new() { Model = new CallModel("specialist"), Tools = [tool] });
        var result = await parent.InvokeAsync("request approval");
        Assert.Equal(StopReasons.Interrupt, result.StopReason);
        Assert.Single(result.Interrupts);
        Assert.Equal("Agent 'specialist' interrupted with PreserveContext=true and no session store; it cannot recover its interrupted turn after restart.", Assert.Single(host.Entries).Message);
    }

    private sealed record Entry(string Message, Exception? Error);
    private sealed class HostLogger : ILogger
    {
        internal List<Entry> Entries { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Entries.Add(new(formatter(state, error), error));
    }
    private sealed class FailureModel(Exception failure) : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw failure;
    }
    private sealed class CallModel(string? tool = null) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (tool is not null) { yield return new ModelContentBlockStartEvent(0, new("call", tool, tool == "specialist" ? JsonSerializer.SerializeToElement(new { input = "request approval" }) : JsonSerializer.SerializeToElement(new { }))); yield return new ModelContentBlockStopEvent(0); }
            yield return new ModelMessageStopEvent(tool is null ? StopReasons.EndTurn : StopReasons.ToolUse);
        }
    }
}
