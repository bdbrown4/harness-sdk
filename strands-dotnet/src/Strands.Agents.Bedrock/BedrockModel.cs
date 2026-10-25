using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace Strands.Agents.Bedrock;

/// <summary>A Bedrock Converse provider using the official AWS SDK and a common streaming event contract.</summary>
public sealed partial class BedrockModel : IModel, IModelMetadataProvider, IConfigurableModel<BedrockModelOptions>, IModelTokenCounter, IDisposable
{
    /// <inheritdoc />
    public ModelMetadata Metadata { get { var options = Volatile.Read(ref _options); return new(options.ModelId, MaxOutputTokens: options.MaxTokens); } }
    private readonly Lazy<IAmazonBedrockRuntime> _client;
    private BedrockModelOptions _options;
    private readonly string _region;
    private readonly bool _ownsClient;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    /// <summary>The configured model or inference-profile identifier.</summary>
    public string ModelId => Volatile.Read(ref _options).ModelId;
    /// <summary>The convenience constructor's configured region; supplied clients retain their own configuration.</summary>
    public string Region => _region;

    /// <summary>Creates an owned AWS client lazily on the first request, using the AWS credential chain.</summary>
    /// <param name="options">The model, inference, and region settings, or the core SDK defaults.</param>
    public BedrockModel(BedrockModelOptions? options = null) : this(options ?? new(), null, true) { }
    /// <summary>Creates a model using a caller-owned AWS client.</summary>
    /// <param name="client">The AWS client, including its credentials, endpoint, and retry configuration.</param>
    /// <param name="options">The model and inference settings.</param>
    public BedrockModel(IAmazonBedrockRuntime client, BedrockModelOptions options) : this(options, client ?? throw new ArgumentNullException(nameof(client)), false) { }

    internal BedrockModel(BedrockModelOptions options, IAmazonBedrockRuntime? client, bool ownsClient)
    {
        _options = CopyValidatedOptions(options);
        _region = _options.Region;
        _ownsClient = ownsClient;
        _client = new(() => client ?? new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(_region), MaxErrorRetry = 0 }));
    }

    /// <inheritdoc/>
    public BedrockModelOptions GetConfig()
    {
        var options = Volatile.Read(ref _options);
        return CopyOptions(options);
    }

    /// <summary>Atomically publishes validated inference options for future requests. The AWS client and its region remain construction-time transport settings.</summary>
    public void UpdateConfig(Func<BedrockModelOptions, BedrockModelOptions> update)
    {
        if (update is null) throw new ArgumentNullException(nameof(update));
        lock (_gate) if (_disposed) throw new ObjectDisposedException(nameof(BedrockModel));
        var replacement = CopyValidatedOptions(update(GetConfig()));
        if (!string.Equals(replacement.Region, _region, StringComparison.Ordinal)) throw new ArgumentException("Region is a construction-time transport setting; create a new Bedrock model to change it.", nameof(update));
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BedrockModel));
            Volatile.Write(ref _options, replacement);
        }
    }

    private static BedrockModelOptions CopyValidatedOptions(BedrockModelOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ModelId)) throw new ArgumentException("A Bedrock model ID is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Region)) throw new ArgumentException("A Bedrock region is required.", nameof(options));
        if (options.MaxTokens is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxTokens must be positive.");
        if (options.Temperature is { } temperature && (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature is < 0 or > 1)) throw new ArgumentOutOfRangeException(nameof(options), "Temperature must be between zero and one.");
        if (options.TopP is { } topP && (double.IsNaN(topP) || double.IsInfinity(topP) || topP is < 0 or > 1)) throw new ArgumentOutOfRangeException(nameof(options), "TopP must be between zero and one.");
        if (options.RequestTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)) throw new ArgumentOutOfRangeException(nameof(options), "RequestTimeout must be positive, at most Int32.MaxValue milliseconds, or null.");
        if (options.MaximumBufferedContentBytes < 1 || options.MaximumContentBlocks < 1) throw new ArgumentOutOfRangeException(nameof(options), "Response bounds must be positive.");
        if (!Enum.IsDefined(typeof(NativeTokenCountFailurePolicy), options.TokenCountFailurePolicy)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown native token-count failure policy.");
        if (options.Cache is { } cache && !Enum.IsDefined(typeof(BedrockCacheStrategy), cache.Strategy)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown Bedrock cache strategy.");
        if (options.AdditionalModelRequestFields is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Additional model request fields must be a JSON object.", nameof(options));
        if (options.StopSequences?.Any(value => value is null) == true) throw new ArgumentException("Stop sequences cannot contain null.", nameof(options));
        if (options.AdditionalModelResponseFieldPaths?.Any(value => value is null) == true) throw new ArgumentException("Response field paths cannot contain null.", nameof(options));
        if (options.RequestMetadata?.Any(pair => pair.Key is null || pair.Value is null) == true || options.PromptVariables?.Any(pair => pair.Key is null || pair.Value is null) == true)
            throw new ArgumentException("Request metadata and prompt variables cannot contain null keys or values.", nameof(options));
        if (options.PerformanceLatency is { Length: 0 } || options.ServiceTier is { Length: 0 }) throw new ArgumentException("Configured latency and tier cannot be empty.", nameof(options));
        if (options.Guardrail is { } guardrail)
        {
            if (string.IsNullOrWhiteSpace(guardrail.Identifier) || string.IsNullOrWhiteSpace(guardrail.Version)) throw new ArgumentException("A guardrail identifier and version are required.", nameof(options));
            if (guardrail.Trace is not ("enabled" or "disabled" or "enabled_full")) throw new ArgumentException("Unknown guardrail trace mode.", nameof(options));
            if (guardrail.StreamProcessingMode is not (null or "sync" or "async")) throw new ArgumentException("Unknown guardrail processing mode.", nameof(options));
            if (guardrail.InputReplacement is null || guardrail.OutputReplacement is null) throw new ArgumentException("Guardrail replacement text cannot be null.", nameof(options));
        }
        return CopyOptions(options);
    }

    private static BedrockModelOptions CopyOptions(BedrockModelOptions options) => options with
    {
        AdditionalModelRequestFields = options.AdditionalModelRequestFields?.Clone(),
        StopSequences = options.StopSequences is null ? null : Array.AsReadOnly(options.StopSequences.ToArray()),
        AdditionalModelResponseFieldPaths = options.AdditionalModelResponseFieldPaths is null ? null : Array.AsReadOnly(options.AdditionalModelResponseFieldPaths.ToArray()),
        RequestMetadata = CopyDictionary(options.RequestMetadata),
        PromptVariables = CopyDictionary(options.PromptVariables),
        Guardrail = options.Guardrail is null ? null : options.Guardrail with { },
        Cache = options.Cache is null ? null : options.Cache with { }
    };

    private static IReadOnlyDictionary<string, string>? CopyDictionary(IReadOnlyDictionary<string, string>? values) => values is null ? null
        : new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    /// <inheritdoc/>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));
        lock (_gate) if (_disposed) throw new ObjectDisposedException(nameof(BedrockModel));
        var options = Volatile.Read(ref _options);
        using var mapper = new BedrockRequestMapper();
        var native = ApplyRememberedThinking(ApplyRememberedToolResultTurns(mapper.Map(request, options), options), options);
        using var deadline = new CancellationTokenSource();
        if (options.RequestTimeout is { } timeout) deadline.CancelAfter(timeout);
        using var operation = CreateRequestCancellation(cancellationToken, deadline.Token);
        if (!options.Streaming)
        {
            await foreach (var item in StreamConverseAsync(native, options, cancellationToken, operation.Token, deadline.Token).ConfigureAwait(false)) yield return item;
            yield break;
        }
        var response = await GetResponseAsync(native, options, cancellationToken, operation.Token, deadline.Token).ConfigureAwait(false);
        var reader = new BedrockResponseReader(response.Stream ?? throw new ModelException("Bedrock returned no response stream."), operation.Token,
            error => Translate(error, cancellationToken, operation.Token, deadline.Token));
        await using var readerScope = reader.ConfigureAwait(false);
        using var parser = new BedrockStreamParser(options);
        while (await reader.MoveNextAsync().ConfigureAwait(false))
        {
            IReadOnlyList<ModelStreamEvent> events;
            try { events = parser.Map(reader.Current); }
            catch (Exception error) { reader.PrimaryError = error; throw; }
            foreach (var item in events) yield return item;
        }
        IReadOnlyList<ModelStreamEvent> finalEvents;
        try { finalEvents = parser.Complete(); }
        catch (Exception error) { reader.PrimaryError = error; throw; }
        foreach (var item in finalEvents) yield return item;
    }

    private CancellationTokenSource CreateRequestCancellation(CancellationToken caller, CancellationToken deadline)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BedrockModel));
            return CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token, deadline);
        }
    }

    private async Task<ConverseStreamResponse> GetResponseAsync(ConverseStreamRequest request, BedrockModelOptions options, CancellationToken caller, CancellationToken operation, CancellationToken deadline)
    {
        try
        {
            operation.ThrowIfCancellationRequested();
            IAmazonBedrockRuntime client;
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(BedrockModel)); client = _client.Value; }
            return await SendWithRecoveryAsync(request, options, input => client.ConverseStreamAsync(input, operation), operation).ConfigureAwait(false);
        }
        catch (Exception error) { throw Translate(error, caller, operation, deadline); }
    }

    private Exception Translate(Exception error, CancellationToken caller, CancellationToken operation, CancellationToken deadline)
    {
        if (caller.IsCancellationRequested) return new OperationCanceledException("The Bedrock request was cancelled.", error, caller);
        if (_lifetime.IsCancellationRequested) return new OperationCanceledException("The Bedrock model was disposed.", error, operation);
        if (deadline.IsCancellationRequested) return new ModelException("The Bedrock request timed out.", new TimeoutException("The configured Bedrock request deadline elapsed.", error));
        var native = error is BedrockRuntimeEventStreamException { InnerException: { } cause } ? cause : error;
        if (native is ThrottlingException || native is Amazon.Runtime.AmazonServiceException service && (service.ErrorCode == "ThrottlingException" || (int)service.StatusCode == 429))
            return new ModelThrottledException(native.Message, error);
        if (native is ValidationException && new[] { "Input is too long for requested model", "input length and `max_tokens` exceed context limit", "too many total text bytes", "prompt is too long", "This model's maximum context length is" }.Any(message => native.Message.IndexOf(message, StringComparison.OrdinalIgnoreCase) >= 0))
            return new ContextWindowOverflowException(native.Message, error);
        return error is ModelException ? error : new ModelException("Bedrock request failed: " + error.Message, error);
    }

    /// <summary>Disposes only a lazily created client owned by this model.</summary>
    public void Dispose()
    {
        IAmazonBedrockRuntime? owned;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            owned = _ownsClient && _client.IsValueCreated ? _client.Value : null;
        }
        Exception? primary = null;
        Attempt(_lifetime.Cancel);
        Attempt(() => owned?.Dispose());
        Attempt(_lifetime.Dispose);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();

        void Attempt(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception error)
            {
                if (primary is null) primary = error;
                else
                {
                    const string key = "Strands.CleanupErrors";
                    if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
                    errors.Add(error);
                }
            }
        }
    }
}
