using Amazon;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.S3;
using Strands.Agents.Memory;

namespace Strands.Agents.BedrockKnowledgeBase;

/// <summary>Retrieval and optional CUSTOM or S3 ingestion backed by an existing Amazon Bedrock Knowledge Base.</summary>
public sealed partial class BedrockKnowledgeBaseStore : MemoryStore, IWritableMemoryStore, IDisposable, IAsyncDisposable
{
    private readonly BedrockKnowledgeBaseStoreOptions _options;
    private readonly RetrievalFilter? _filter;
    private readonly Lazy<IAmazonBedrockAgentRuntime> _runtime;
    private readonly Lazy<IAmazonBedrockAgent> _agent;
    private readonly bool _ownsRuntime;
    private readonly bool _ownsAgent;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private string? _type;
    private int _typeKnown;

    /// <summary>Creates a store with detached options and lazy owned or explicit borrowed official clients.</summary>
    public BedrockKnowledgeBaseStore(BedrockKnowledgeBaseStoreOptions options) : this(options, null, null) { }

    internal BedrockKnowledgeBaseStore(BedrockKnowledgeBaseStoreOptions options, Func<IAmazonBedrockAgentRuntime>? runtimeFactory, Func<IAmazonBedrockAgent>? agentFactory, Func<IAmazonS3>? s3Factory = null)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.Name, nameof(options.Name));
        Guard.NotNullOrWhiteSpace(options.KnowledgeBaseId, nameof(options.KnowledgeBaseId));
        if (options.MaxSearchResults is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxSearchResults must be positive.");
        if (options.MinScore is not null && options.MaxScore is not null) throw new ArgumentException("MinScore and MaxScore are mutually exclusive.", nameof(options));
        if (!Enum.IsDefined(typeof(BedrockKnowledgeBaseProfile), options.Profile)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown knowledge-base profile.");
        if (options.Region is not null) Guard.NotNullOrWhiteSpace(options.Region, nameof(options.Region));
        ValidateWriteOptions(options);
        _accessControlList = CopyAcl(options.AccessControlList);
        _filter = KnowledgeBaseFilter.Copy(options.Filter);
        _type = options.KnownKnowledgeBaseType?.Value;
        if (options.KnownKnowledgeBaseType is not null && _type is null) throw new ArgumentException("A known knowledge-base type needs a value.", nameof(options));
        _typeKnown = options.KnownKnowledgeBaseType is null ? 0 : 1;
        _s3Options = options.S3 is null ? null : options.S3 with { Client = null };
        _options = options with { S3 = null, Filter = null, KnownKnowledgeBaseType = null, AccessControlList = null, DataSourceType = options.DataSourceType is null ? null : new(options.DataSourceType.Value) };
        _ownsRuntime = options.RuntimeClient is null; _ownsAgent = options.AgentClient is null; _ownsS3 = options.S3?.Client is null;
        var region = options.Region is null ? null : RegionEndpoint.GetBySystemName(options.Region);
        _runtime = new(() => options.RuntimeClient ?? runtimeFactory?.Invoke() ?? new AmazonBedrockAgentRuntimeClient(region is null ? new AmazonBedrockAgentRuntimeConfig() : new() { RegionEndpoint = region }));
        _agent = new(() => options.AgentClient ?? agentFactory?.Invoke() ?? new AmazonBedrockAgentClient(region is null ? new AmazonBedrockAgentConfig() : new() { RegionEndpoint = region }));
        var borrowedS3 = options.S3?.Client;
        _s3 = new(() => borrowedS3 ?? s3Factory?.Invoke() ?? new AmazonS3Client(region is null ? new AmazonS3Config() : new() { RegionEndpoint = region }));
    }
    /// <inheritdoc />
    public override string Name => _options.Name;
    /// <inheritdoc />
    public override string? Description => _options.Description;
    /// <inheritdoc />
    public override int? MaxSearchResults => _options.MaxSearchResults;

    /// <inheritdoc />
    public override bool Writable => _options.Writable;
    /// <inheritdoc />
    public override MemoryExtractionOptions? Extraction => _options.Extraction;

    /// <summary>Detects and caches one valid type; failed or canceled detection can be retried.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        try { await EnsureTypeAsync(operation.Token).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); }
        catch (Exception error)
        {
            var failure = NormalizeCancellation(error, cancellationToken);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }
    /// <inheritdoc />
    public override ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        return InitializeAsync(cancellationToken);
    }
    /// <summary>Searches with the configured limit, otherwise ten entries.</summary>
    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, CancellationToken cancellationToken = default) => SearchAsync(query, MaxSearchResults ?? 10, cancellationToken);
    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(query, nameof(query));
        if (maxSearchResults <= 0) throw new ArgumentOutOfRangeException(nameof(maxSearchResults));
        var filter = ResolveFilter();
        using var operation = EnterOperation(cancellationToken);
        try
        {
            await EnsureTypeAsync(operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            var config = string.Equals(_type, "MANAGED", StringComparison.Ordinal)
                ? new KnowledgeBaseRetrievalConfiguration { ManagedSearchConfiguration = new() { NumberOfResults = maxSearchResults, Filter = filter } }
                : new KnowledgeBaseRetrievalConfiguration { VectorSearchConfiguration = new() { NumberOfResults = maxSearchResults, Filter = filter } };
            var response = await InvokeClient(() => _runtime.Value.RetrieveAsync(new RetrieveRequest
            {
                KnowledgeBaseId = _options.KnowledgeBaseId,
                RetrievalQuery = new() { Text = query },
                RetrievalConfiguration = config
            }, operation.Token)).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            var entries = KnowledgeBaseProjection.Entries(response?.RetrievalResults, _options.Profile, _options.MinScore, _options.MaxScore);
            cancellationToken.ThrowIfCancellationRequested();
            return entries;
        }
        catch (Exception error)
        {
            Report(error, "Knowledge base retrieval failed for store {Store}, knowledge base {KnowledgeBase}.", Name, _options.KnowledgeBaseId);
            var failure = NormalizeCancellation(error, cancellationToken);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }

    private async Task EnsureTypeAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _typeKnown) != 0) return;
        await _initialization.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _typeKnown) != 0) return;
            token.ThrowIfCancellationRequested();
            var response = await InvokeClient(() => _agent.Value.GetKnowledgeBaseAsync(new GetKnowledgeBaseRequest { KnowledgeBaseId = _options.KnowledgeBaseId }, token)).ConfigureAwait(false);
            var type = response?.KnowledgeBase?.KnowledgeBaseConfiguration?.Type?.Value;
            if (type is null) throw new InvalidDataException("Knowledge-base detection returned no type.");
            token.ThrowIfCancellationRequested();
            _type = type;
            Volatile.Write(ref _typeKnown, 1);
        }
        catch (Exception error) { Report(error, "Knowledge base type detection failed for store {Store}, knowledge base {KnowledgeBase}.", Name, _options.KnowledgeBaseId); throw; }
        finally { _initialization.Release(); }
    }

    private RetrievalFilter? ResolveFilter()
    {
        if (_filter is not null && (_options.Profile == BedrockKnowledgeBaseProfile.TypeScript || !KnowledgeBaseFilter.Empty(_filter))) return KnowledgeBaseFilter.Copy(_filter);
        if (string.IsNullOrEmpty(_options.Scope)) return null;
        var key = _options.ScopeMetadataKey;
        if (key is null || _options.Profile == BedrockKnowledgeBaseProfile.Python && key.Length == 0) key = "namespace";
        return new() { Equals = new() { Key = key, Value = Amazon.Runtime.Documents.Document.FromObject(_options.Scope) } };
    }
    private void Report(Exception error, string template, params object?[] values) => InvokeClient(() => { SdkLogging.Error(error, template, values); return 0; });

    private static Exception NormalizeCancellation(Exception error, CancellationToken caller, string operation = "retrieval") =>
        error is OperationCanceledException canceled && caller.IsCancellationRequested && canceled.CancellationToken != caller
            ? new OperationCanceledException("Knowledge-base " + operation + " was canceled by its caller.", error, caller) : error;
}
