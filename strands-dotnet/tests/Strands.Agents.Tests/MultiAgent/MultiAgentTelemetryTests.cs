using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.MultiAgent;

[Collection("Telemetry listeners")]
public sealed class MultiAgentTelemetryTests
{
    [Fact]
    public void UnknownProfilesAreRejectedBeforeAnyNodeOrModelCall()
    {
        var options = new MultiAgentTelemetryOptions { Profile = (MultiAgentTelemetryProfile)999 };
        var node = new FunctionNode("node", (_, _, _) => throw new InvalidOperationException("No body."));
        Assert.Throws<ArgumentException>(() => new Graph(new() { Nodes = [node], Telemetry = options }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerFailuresNeverReplaceAnOrchestrationResultOrCallerContext(bool onStop)
    {
        using var capture = new MultiAgentTelemetryCapture(onStarted: onStop ? null : _ => throw new IOException("listener start"),
            onStopped: onStop ? _ => throw new IOException("listener stop") : null);
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())] });

        var result = await graph.InvokeAsync("task");

        Assert.Equal(Status.Completed, result.Status);
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Theory]
    [InlineData(ActivitySamplingResult.None)]
    [InlineData(ActivitySamplingResult.PropagationData)]
    public async Task NonrecordingListenersDoNotChangeExecutionOrAmbientContext(ActivitySamplingResult sampling)
    {
        using var capture = new MultiAgentTelemetryCapture(sampling: sampling);
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())] });
        Assert.Equal(Status.Completed, (await graph.InvokeAsync("task")).Status);
        Assert.Same(capture.Ambient, Activity.Current);
        Assert.All(capture.Stopped, value => Assert.False(value.IsAllDataRequested));
    }

    [Fact]
    public async Task PythonLatestInputUsesTheExistingCanonicalRedactionAndAttributesOnlyPolicy()
    {
        using var capture = new MultiAgentTelemetryCapture("gen_ai_latest_experimental,gen_ai_span_attributes_only,gen_ai_unredacted_attributes=gen_ai.output.messages");
        var graph = new Graph(GraphOptions.Python with { Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())] });
        await graph.InvokeAsync("private input");
        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph");
        Assert.Equal("[REDACTED]", span.GetTagItem("gen_ai.input.messages"));
        Assert.Empty(span.Events);
        Assert.Null(span.GetTagItem("gen_ai.agent.input"));
    }

    [Fact]
    public async Task TypeScriptInputAndCustomAttributesFollowTheSourceMergeOrderAndCopyCallerArrays()
    {
        using var capture = new MultiAgentTelemetryCapture("gen_ai_unredacted_attributes=gen_ai.output.messages");
        var tags = new[] { "original" };
        var attributes = new Dictionary<string, object?> { ["labels"] = tags, ["gen_ai.agent.name"] = "caller name" };
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())],
            Telemetry = new() { TraceAttributes = attributes }
        });
        tags[0] = "changed";
        attributes["gen_ai.agent.name"] = "changed";

        await graph.InvokeAsync(new MultiAgentInput([new TextBlock("private input")]));

        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph graph");
        Assert.Equal("caller name", span.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("original", Assert.Single(Assert.IsType<string[]>(span.GetTagItem("labels"))));
        using var json = JsonDocument.Parse(Assert.IsType<string>(span.GetTagItem("gen_ai.agent.input")));
        Assert.Equal("private input", json.RootElement[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task InspectingCopiedGraphOptionsCannotMutateThePrivateTracingConfiguration()
    {
        using var capture = new MultiAgentTelemetryCapture();
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())],
            Telemetry = new() { TraceAttributes = new Dictionary<string, object?> { ["labels"] = new[] { "original" } } }
        });
        Assert.IsType<string[]>(graph.Options.Telemetry.TraceAttributes!["labels"])[0] = "changed through options";
        await graph.InvokeAsync("task");
        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph graph");
        Assert.Equal("original", Assert.Single(Assert.IsType<string[]>(span.GetTagItem("labels"))));
    }

    [Fact]
    public async Task ThrowingCurrentChangedObserversCannotEscapeProducerContextRestoration()
    {
        using var capture = new MultiAgentTelemetryCapture();
        var graph = new Graph(GraphOptions.Python with { Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())] });
        EventHandler<ActivityChangedEventArgs> observer = (_, _) => throw new IOException("ambient listener");
        Activity.CurrentChanged += observer;
        try
        {
            Assert.Equal(Status.Completed, (await graph.InvokeAsync("task")).Status);
            Assert.Same(capture.Ambient, Activity.Current);
        }
        finally { Activity.CurrentChanged -= observer; }
    }

    [Theory]
    [InlineData("location")]
    [InlineData("cache")]
    [InlineData("guard")]
    public async Task TypeScriptRichInputPreservesThePinnedSerializedDataShape(string kind)
    {
        using var capture = new MultiAgentTelemetryCapture();
        ContentBlock block = kind switch
        {
            "location" => new ImageBlock("image/png", new S3Location("s3://bucket/object", "123456789012")),
            "cache" => new CachePointBlock("1h"),
            _ => new GuardContentBlock(new ImageBlock("image/png", "AQID"))
        };
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())] });
        await graph.InvokeAsync(new MultiAgentInput([block]));
        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph graph");
        using var input = JsonDocument.Parse(Assert.IsType<string>(span.GetTagItem("gen_ai.agent.input")));
        var actual = input.RootElement[0];
        if (kind == "location") Assert.Equal("s3", actual.GetProperty("image").GetProperty("source").GetProperty("location").GetProperty("type").GetString());
        else if (kind == "cache") Assert.Equal("default", actual.GetProperty("cachePoint").GetProperty("cacheType").GetString());
        else Assert.Equal("AQID", actual.GetProperty("guardContent").GetProperty("image").GetProperty("source").GetProperty("bytes").GetString());
    }

    [Fact]
    public async Task PythonMediaBytesUseTheSourceEncoderPlaceholderInsteadOfExportingNativeBase64()
    {
        using var capture = new MultiAgentTelemetryCapture("gen_ai_latest_experimental");
        var graph = new Graph(GraphOptions.Python with { Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())] });
        await graph.InvokeAsync(new MultiAgentInput([new ImageBlock("image/png", "AQID")]));
        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph");
        var payload = Assert.IsType<string>(Assert.Single(span.Events).Tags.Single(value => value.Key == "gen_ai.input.messages").Value);
        using var input = JsonDocument.Parse(payload);
        Assert.Equal("<replaced>", input.RootElement[0].GetProperty("parts")[0].GetProperty("content").GetProperty("source").GetProperty("bytes").GetString());
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("redacted")]
    [InlineData("citations")]
    [InlineData("cache")]
    public async Task PythonInputUsesItsOwnReasoningCitationAndCacheDataContracts(string kind)
    {
        using var capture = new MultiAgentTelemetryCapture("gen_ai_latest_experimental");
        ContentBlock block = kind switch
        {
            "reasoning" => new ReasoningBlock("thought", "signature"),
            "redacted" => new RedactedReasoningBlock("AQID"),
            "citations" => new CitationsBlock([new Citation(new DocumentCharCitationLocation(1, 2, 3), title: "source")], [new CitationText("quoted")]),
            _ => new CachePointBlock("1h")
        };
        var graph = new Graph(GraphOptions.Python with { Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())] });
        await graph.InvokeAsync(new MultiAgentInput([block]));
        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph");
        using var input = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(span.Events).Tags.Single(value => value.Key == "gen_ai.input.messages").Value));
        var part = input.RootElement[0].GetProperty("parts")[0];
        Assert.Equal(kind == "citations" ? "citationsContent" : kind == "cache" ? "cachePoint" : "reasoningContent", part.GetProperty("type").GetString());
        var data = part.GetProperty("content");
        if (kind == "reasoning") Assert.Equal("signature", data.GetProperty("reasoningText").GetProperty("signature").GetString());
        else if (kind == "redacted") Assert.Equal("<replaced>", data.GetProperty("redactedContent").GetString());
        else if (kind == "citations") Assert.Equal(1, data.GetProperty("citations")[0].GetProperty("location").GetProperty("documentChar").GetProperty("documentIndex").GetInt32());
        else Assert.Equal("default", data.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatedAfterCallbackFailureClosesTheVisitAndPreservesItsOriginalError(bool swarm)
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var agent = new Agent(new() { Id = "node", Model = new MultiAgentTelemetryCapture.TextModel() });
        var failure = new IOException("after gated visit");
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        hooks.Register<AfterNodeCallEvent>((_, _) => throw failure);
        IMultiAgent orchestrator = swarm ? new Swarm(new() { Nodes = [new(agent)], Hooks = hooks })
            : new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => throw new InvalidOperationException("Not admitted."))], Hooks = hooks });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => orchestrator.InvokeAsync(new("task"))));

        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node node");
        Assert.Equal(ActivityStatusCode.Error, node.Status);
        Assert.Equal(failure.Message, node.StatusDescription);
        Assert.Null(node.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Equal(capture.Started.Count, capture.Stopped.Count);
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingAtTheGatedAfterEventCompletesTheStillOpenVisitExactlyOnce(bool swarm)
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var agent = new Agent(new() { Id = "node", Model = new MultiAgentTelemetryCapture.TextModel() });
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        IMultiAgent orchestrator = swarm ? new Swarm(new() { Nodes = [new(agent)], Hooks = hooks })
            : new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => throw new InvalidOperationException("Not admitted."))], Hooks = hooks });
        var reader = orchestrator.StreamAsync(new("task")).GetAsyncEnumerator();
        while (await reader.MoveNextAsync()) if (reader.Current is AfterNodeCallEvent) break;
        Assert.DoesNotContain(capture.Stopped, value => value.OperationName == "node node");

        await reader.DisposeAsync();

        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node node");
        Assert.Equal(ActivityStatusCode.Ok, node.Status);
        Assert.Equal(0d, node.GetTagItem("gen_ai.agent.execution_time"));
        Assert.Null(node.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Equal(capture.Started.Count, capture.Stopped.Count);
        Assert.Same(capture.Ambient, Activity.Current);
    }
}

internal sealed class MultiAgentTelemetryCapture : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly Dictionary<string, string?> _environment = new(StringComparer.Ordinal);
    internal ConcurrentQueue<Activity> Stopped { get; } = new();
    internal ConcurrentQueue<Activity> Started { get; } = new();
    internal Activity Ambient { get; }
    internal MultiAgentTelemetryCapture(string? tokens = null, ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded,
        Action<Activity>? onStarted = null, Action<Activity>? onStopped = null)
    {
        foreach (var name in new[] { "OTEL_SEMCONV_STABILITY_OPT_IN", "OTEL_SERVICE_NAME", "LANGFUSE_BASE_URL", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT" })
        {
            _environment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, name == "OTEL_SEMCONV_STABILITY_OPT_IN" ? tokens : null);
        }
        _listener = new()
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
            ActivityStarted = activity => { Started.Enqueue(activity); onStarted?.Invoke(activity); },
            ActivityStopped = activity => { Stopped.Enqueue(activity); onStopped?.Invoke(activity); }
        };
        ActivitySource.AddActivityListener(_listener);
        Ambient = new Activity("orchestration caller").SetIdFormat(ActivityIdFormat.W3C).Start();
    }
    public void Dispose()
    {
        Ambient.Dispose();
        _listener.Dispose();
        foreach (var entry in _environment) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
    }
    internal static async IAsyncEnumerable<INodeStreamEvent> Output(Usage? usage = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new NodeResultUpdateEvent([new TextBlock("done")], usage);
        await Task.Yield();
    }
    internal sealed class HandoffModel(params string[] decisions) : IModel
    {
        private int _turn;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var turn = Interlocked.Increment(ref _turn);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new("call-" + turn, Assert.Single(request.Tools!).Name, JsonSerializer.SerializeToElement(new { })));
            yield return new ModelContentBlockDeltaEvent(0, ToolInput: decisions[turn - 1]);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            yield return new ModelMetadataEvent(new(2, 3));
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
    internal sealed class TextModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.Yield();
        }
    }
}
