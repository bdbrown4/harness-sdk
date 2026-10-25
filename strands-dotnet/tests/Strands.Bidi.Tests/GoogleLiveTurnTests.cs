using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;

namespace Strands.Bidi.Tests;

public sealed class GoogleLiveTurnTests
{
    [Fact]
    public void BargeInCompletesThePriorUtteranceWithoutClosingANewerUserTranscript()
    {
        var state = new GoogleLiveState();
        var first = Process(state, """{"voiceActivity":{"voiceActivityType":"ACTIVITY_START"}}""");
        var oldId = Assert.IsType<BidiTranscriptStartEvent>(Assert.Single(first)).ContentId;
        Process(state, """{"serverContent":{"inputTranscription":{"text":"old"},"modelTurn":{"parts":[{"text":"answer"},{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AAA="}}]}}}""");
        var activity = Process(state, """{"voiceActivity":{"voiceActivityType":"ACTIVITY_START"}}"""); var newId = Assert.IsType<BidiTranscriptStartEvent>(Assert.Single(activity)).ContentId;
        var interrupted = Process(state, """{"serverContent":{"interrupted":true,"inputTranscription":{"text":"new"}}}"""); Assert.Single(interrupted.OfType<BidiBargeInEvent>()); Assert.Single(interrupted.OfType<BidiAudioStopEvent>());
        var complete = Process(state, """{"serverContent":{"turnComplete":true}}""");
        Assert.Equal(oldId, Assert.Single(complete.OfType<BidiTranscriptStopEvent>()).ContentId); Assert.Single(complete.OfType<BidiResponseStopEvent>());
        var next = Process(state, """{"serverContent":{"modelTurn":{"parts":[{"text":"new answer"}]},"turnComplete":true}}""");
        Assert.Equal(newId, Assert.Single(next.OfType<BidiTranscriptStopEvent>()).ContentId);
    }

    [Fact]
    public void FinishedTranscriptionAndGenerationCompletionHaveSeparateBoundaries()
    {
        var state = new GoogleLiveState();
        var first = Process(state, """{"serverContent":{"inputTranscription":{"text":"question","finished":true},"outputTranscription":{"text":"answer"},"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm","data":"AAA="}}]}}}""");
        Assert.IsType<BidiTranscriptStartEvent>(first[0]); Assert.Single(first.OfType<BidiTranscriptStopEvent>()); Assert.Single(first.OfType<BidiResponseStartEvent>());
        var generation = Process(state, """{"serverContent":{"generationComplete":true}}"""); Assert.IsType<BidiAudioStopEvent>(Assert.Single(generation));
        var complete = Process(state, """{"serverContent":{"turnComplete":true}}""");
        Assert.Equal(Role.Assistant, Assert.Single(complete.OfType<BidiTranscriptStopEvent>()).Role); Assert.Single(complete.OfType<BidiResponseStopEvent>());
        Assert.Empty(Process(state, """{"serverContent":{"turnComplete":true}}"""));
    }

    [Fact]
    public void UsageCategoriesCanAccompanyNonContentMessagesAndReasoningIsSeparateFromText()
    {
        var state = new GoogleLiveState();
        var values = Process(state, """{"serverContent":{"modelTurn":{"parts":[{"text":"think","thought":true},{"text":"answer"}]}},"usageMetadata":{"promptTokenCount":8,"responseTokenCount":5,"totalTokenCount":13,"cachedContentTokenCount":2,"thoughtsTokenCount":3,"promptTokensDetails":[{"modality":"AUDIO","tokenCount":7}],"responseTokensDetails":[{"modality":"TEXT","tokenCount":2}]}}""");
        Assert.Single(values.OfType<BidiReasoningStartEvent>()); Assert.Single(values.OfType<BidiReasoningStopEvent>()); Assert.Single(values.OfType<BidiTextStartEvent>());
        var usage = Assert.Single(values.OfType<BidiUsageEvent>()); Assert.Equal(7, usage.InputDetails!["audio"]); Assert.Equal(2, usage.InputDetails["cache_read"]); Assert.Equal(3, usage.OutputDetails!["reasoning"]);
    }

    [Fact]
    public void CancelledToolIdentifiersCannotLaterReceiveAnUnsolicitedFunctionResponse()
    {
        var state = new GoogleLiveState(); Process(state, """{"toolCall":{"functionCalls":[{"id":"a","name":"n","args":{}}]}}""");
        var values = Process(state, """{"toolCallCancellation":{"ids":["a"]}}""");
        Assert.Single(values.OfType<BidiNativeEvent>()); Assert.Empty(state.ToolNames);
    }
    private static IReadOnlyList<IBidiOutputEvent> Process(GoogleLiveState state, string value) { using var document = JsonDocument.Parse(value); return state.Process(document.RootElement); }
}
