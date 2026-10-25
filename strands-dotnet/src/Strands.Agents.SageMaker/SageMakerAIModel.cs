using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams.Internal;
using Amazon.SageMakerRuntime;
using Amazon.SageMakerRuntime.Model;
using Strands.Agents.Models;

namespace Strands.Agents.SageMaker;

/// <summary>A native SageMaker Runtime adapter using the official AWS client.</summary>
public sealed partial class SageMakerAIModel : IModel, IModelMetadataProvider, IConfigurableModel<SageMakerModelOptions>, IDisposable
{
    private readonly Lazy<IAmazonSageMakerRuntime> _client;
    private readonly bool _ownsClient;
    private readonly string _region;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private SageMakerModelOptions _options;
    private bool _disposed;
    /// <summary>Creates a lazily owned AWS client using its credential chain.</summary>
    public SageMakerAIModel(SageMakerModelOptions options) : this(options, null, true) { }
    /// <summary>Creates an adapter over a caller-owned AWS client.</summary>
    public SageMakerAIModel(IAmazonSageMakerRuntime client, SageMakerModelOptions options) : this(options, client ?? throw new ArgumentNullException(nameof(client)), false) { }
    internal SageMakerAIModel(SageMakerModelOptions options, IAmazonSageMakerRuntime? client, bool ownsClient)
    {
        _options = Snapshot(options); _region = _options.Region!; _ownsClient = ownsClient;
        _client = new(() => client ?? new AmazonSageMakerRuntimeClient(new AmazonSageMakerRuntimeConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(_region), MaxErrorRetry = 0 }));
    }
    /// <inheritdoc/>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = Capture();
        var invocation = new SageMakerInvocation(options, SageMakerRequestFormatter.Payload(request, options));
        using var body = new MemoryStream(invocation.Body);
        using var operation = Operation(cancellationToken);
        if (!invocation.Stream)
        {
            using var response = await ReadCompleteAsync(invocation.Request(body), options, cancellationToken, operation.Token).ConfigureAwait(false);
            var state = new SageMakerChunkState(options.MaximumResponseBytes);
            var events = state.Process(response.RootElement, complete: true);
            foreach (var item in events.Where(item => item is not ModelMetadataEvent)) yield return item;
            foreach (var item in state.Complete()) yield return item;
            foreach (var item in events.OfType<ModelMetadataEvent>()) yield return item;
            yield break;
        }
        InvokeEndpointWithResponseStreamResponse received;
        try { received = await Client().InvokeEndpointWithResponseStreamAsync(invocation.StreamingRequest(body), operation.Token).ConfigureAwait(false); }
        catch (Exception error) { throw Translate(error, cancellationToken, operation.Token); }
        var output = received?.Body ?? throw new ModelException("SageMaker returned no response event stream.");
        var reader = new SageMakerResponseReader(output, operation.Token, error => Translate(error, cancellationToken, operation.Token));
        await using var readerScope = reader.ConfigureAwait(false);
        var parser = new SageMakerPayloadParser(options.MaximumResponseBytes, options.MalformedEventPolicy);
        var chunks = new SageMakerChunkState(options.MaximumResponseBytes);
        var buffer = new byte[8192];
        while (await reader.MoveNextAsync().ConfigureAwait(false))
        {
            MemoryStream bytes;
            try
            {
                if (reader.Current is InitialResponseEvent) continue;
                if (reader.Current is not PayloadPart { Bytes: { } payload }) throw new ModelException("SageMaker returned an unsupported or empty event-stream frame.");
                bytes = payload;
                bytes.Position = 0;
            }
            catch (Exception error) { reader.PrimaryError = error; throw; }
            while (true)
            {
                IReadOnlyList<ModelStreamEvent> events;
                try
                {
                    operation.Token.ThrowIfCancellationRequested();
                    var count = bytes.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    events = parser.Append(buffer, length: count).SelectMany(value => chunks.Process(value)).ToArray();
                }
                catch (Exception error) { reader.PrimaryError = Translate(error, cancellationToken, operation.Token); ExceptionDispatchInfo.Capture(reader.PrimaryError).Throw(); throw; }
                foreach (var item in events) yield return item;
            }
        }
        IReadOnlyList<ModelStreamEvent> final;
        try { final = parser.Append([], final: true).SelectMany(value => chunks.Process(value)).Concat(chunks.Complete()).ToArray(); }
        catch (Exception error) { reader.PrimaryError = error; throw; }
        foreach (var item in final) yield return item;
    }

    /// <summary>Uses InvokeEndpoint with strict container JSON-schema formatting and common schema validation/deserialization.</summary>
    public async Task<T> InvokeStructuredAsync<T>(ModelRequest request, StructuredOutputOptions<T> options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = Capture();
        var validator = new StructuredOutputTool<T>(options);
        var payload = SageMakerRequestFormatter.Payload(request with { Tools = null, ToolChoice = null }, config);
        payload["stream"] = false;
        payload["response_format"] = new { type = "json_schema", json_schema = new { name = typeof(T).Name, schema = options.Schema, strict = true } };
        var invocation = new SageMakerInvocation(config, payload, payload["response_format"]);
        using var body = new MemoryStream(invocation.Body);
        using var operation = Operation(cancellationToken);
        using var response = await ReadCompleteAsync(invocation.Request(body), config, cancellationToken, operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1 || choices[0].ValueKind != JsonValueKind.Object) throw new StructuredOutputException("SageMaker structured output requires exactly one choice.");
            if (!choices[0].TryGetProperty("finish_reason", out var finish) || finish.ValueKind != JsonValueKind.String || finish.GetString() != "stop") throw new StructuredOutputException("SageMaker structured output requires a completed, unfiltered response.");
            var state = new SageMakerChunkState(config.MaximumResponseBytes);
            _ = state.Process(root, complete: true);
            _ = state.Complete();
            var message = ProviderHttp.RequiredObject(choices[0], "message");
            if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null && (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 0)) throw new StructuredOutputException("SageMaker structured output returned unexpected tool calls.");
            var text = ProviderHttp.OptionalString(message, "content");
            if (string.IsNullOrWhiteSpace(text)) throw new StructuredOutputException("SageMaker structured output requires JSON content.");
            using var parsed = JsonDocument.Parse(text!);
            return validator.ParseOutput(parsed.RootElement, operation.Token);
        }
        catch (JsonException error) { throw new StructuredOutputException("SageMaker returned invalid structured JSON.", error); }
        catch (ModelException error) { throw new StructuredOutputException("SageMaker returned invalid structured completion metadata.", error); }
        catch (OperationCanceledException error) when (operation.IsCancellationRequested) { throw Translate(error, cancellationToken, operation.Token); }
    }
    private async Task<JsonDocument> ReadCompleteAsync(InvokeEndpointRequest request, SageMakerModelOptions options, CancellationToken caller, CancellationToken operation)
    {
        InvokeEndpointResponse response;
        try { response = await Client().InvokeEndpointAsync(request, operation).ConfigureAwait(false); }
        catch (Exception error) { throw Translate(error, caller, operation); }
        var stream = response?.Body ?? throw new ModelException("SageMaker returned no response body.");
        Exception? primary = null;
        try { return await ProviderHttp.ReadJsonAsync(stream, "SageMaker", options.MaximumResponseBytes, operation).ConfigureAwait(false); }
        catch (Exception error) { primary = Translate(error, caller, operation); ExceptionDispatchInfo.Capture(primary).Throw(); throw; }
        finally { try { stream.Dispose(); } catch (Exception cleanup) { if (primary is null) throw; AttachCleanup(primary, cleanup); } }
    }
    private SageMakerModelOptions Capture() { lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(SageMakerAIModel)); return Volatile.Read(ref _options); } }
    private IAmazonSageMakerRuntime Client() { lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(SageMakerAIModel)); return _client.Value; } }
    private CancellationTokenSource Operation(CancellationToken caller) { lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(SageMakerAIModel)); return CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token); } }
    private Exception Translate(Exception error, CancellationToken caller, CancellationToken operation)
    {
        if (caller.IsCancellationRequested) return new OperationCanceledException("The SageMaker request was cancelled.", error, caller);
        if (_lifetime.IsCancellationRequested) return new OperationCanceledException("The SageMaker model was disposed.", error, operation);
        var cause = error is SageMakerRuntimeEventStreamException { InnerException: { } inner } ? inner : error;
        if (cause is ModelNotReadyException || cause is AmazonServiceException service && ((int)service.StatusCode == 429 || service.ErrorCode == "ThrottlingException")) return new ModelThrottledException(cause.Message, error);
        return error is ModelException ? error : new ModelException("SageMaker request failed: " + error.Message, error);
    }
    /// <summary>Cancels active requests and disposes only a client created by this model.</summary>
    public void Dispose()
    {
        IAmazonSageMakerRuntime? owned;
        lock (_gate) { if (_disposed) return; _disposed = true; owned = _ownsClient && _client.IsValueCreated ? _client.Value : null; }
        Exception? primary = null;
        Attempt(_lifetime.Cancel); Attempt(() => owned?.Dispose()); Attempt(_lifetime.Dispose);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        void Attempt(Action action) { try { action(); } catch (Exception error) { if (primary is null) primary = error; else AttachCleanup(primary, error); } }
    }
    internal static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
    }
}
