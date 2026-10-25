using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Microsoft.Extensions.Logging;
using Strands.Agents.Memory;

namespace Strands.Agents.BedrockKnowledgeBase;

/// <summary>The identifier accepted by one document ingestion request.</summary>
public sealed record BedrockKnowledgeBaseAddResult(string DocumentId);

public sealed partial class BedrockKnowledgeBaseStore
{
    private readonly IReadOnlyList<DocumentAccessControlEntry>? _accessControlList;

    /// <summary>Uploads and ingests one S3 document or ingests one CUSTOM inline document, returning its identifier.</summary>
    public async ValueTask<BedrockKnowledgeBaseAddResult> AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
    {
        if (!Writable) throw new InvalidOperationException("This knowledge-base store is not writable. Set Writable=true to enable AddAsync.");
        Guard.NotNull(content, nameof(content));
        if (KnowledgeBaseWriteDocument.Blank(content, _options.Profile)) throw new ArgumentException("Knowledge-base content must not be empty.", nameof(content));
        if (metadata is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Memory metadata must be a JSON object.", nameof(metadata));
        var detached = metadata?.Clone();
        using var operation = EnterOperation(cancellationToken);
        var id = KnowledgeBaseWriteDocument.NewId(_options.Profile);
        KnowledgeBaseDocument document;
        if (string.Equals(_options.DataSourceType?.Value, "S3", StringComparison.Ordinal))
        {
            (id, document) = await PrepareS3DocumentAsync(id, content, detached, operation.Token, cancellationToken).ConfigureAwait(false);
        }
        else document = KnowledgeBaseWriteDocument.Custom(id, content, detached, _options.Scope, ScopeKey(), _accessControlList, _options.Profile, ReportDroppedMetadata);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            await InvokeClient(() => _agent.Value.IngestKnowledgeBaseDocumentsAsync(new IngestKnowledgeBaseDocumentsRequest
            {
                KnowledgeBaseId = _options.KnowledgeBaseId,
                DataSourceId = _options.DataSourceId,
                Documents = [document]
            }, operation.Token)).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            return new(id);
        }
        catch (Exception error)
        {
            Report(error, "Knowledge base document ingestion failed for store {Store}, knowledge base {KnowledgeBase}, data source {DataSource}.", Name, _options.KnowledgeBaseId, _options.DataSourceId);
            if (_accessControlList is null && error is ValidationException serviceError && string.Equals(serviceError.ErrorCode, "ValidationException", StringComparison.Ordinal) && serviceError.Message.IndexOf("accesscontrollist", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("Knowledge-base ingestion was rejected because the data source has ACL awareness enabled but this store has no AccessControlList configured. Set AccessControlList to write to an ACL-enabled data source.", error);
            var failure = NormalizeCancellation(error, cancellationToken, "document ingestion");
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }

    async ValueTask IWritableMemoryStore.AddAsync(string content, JsonElement? metadata, CancellationToken cancellationToken) => await AddAsync(content, metadata, cancellationToken).ConfigureAwait(false);

    private void ReportDroppedMetadata(string key, bool collision) => InvokeClient(() =>
    {
        if (collision) SdkLogging.Warning("Dropping knowledge-base metadata key {Key} that collides with scope metadata for store {Store}.", key, Name);
        else
        {
            try { var logger = SdkLogging.Logger; if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Dropping unsupported knowledge-base metadata key {Key} for store {Store}.", key, Name); }
            catch { /* Application diagnostics are observational. */ }
        }
        return 0;
    });

    private string ScopeKey() => _options.ScopeMetadataKey is null || _options.Profile == BedrockKnowledgeBaseProfile.Python && _options.ScopeMetadataKey.Length == 0 ? "namespace" : _options.ScopeMetadataKey;
    private static void ValidateWriteOptions(BedrockKnowledgeBaseStoreOptions options)
    {
        if (!options.Writable) return;
        if (!string.Equals(options.DataSourceType?.Value, "CUSTOM", StringComparison.Ordinal) && !string.Equals(options.DataSourceType?.Value, "S3", StringComparison.Ordinal)) throw new ArgumentException("Writable knowledge-base stores require the CUSTOM or S3 data source type.", nameof(options));
        if (string.Equals(options.DataSourceType?.Value, "S3", StringComparison.Ordinal))
        {
            if (options.S3 is null) throw new ArgumentException("S3 configuration is required for S3 writes.", nameof(options));
            Guard.NotNullOrWhiteSpace(options.S3.Bucket, nameof(options.S3.Bucket));
            Guard.NotNull(options.S3.Prefix, nameof(options.S3.Prefix));
        }
        if (string.IsNullOrEmpty(options.DataSourceId)) throw new ArgumentException("DataSourceId is required for knowledge-base writes.", nameof(options));
    }
    private static IReadOnlyList<DocumentAccessControlEntry>? CopyAcl(IReadOnlyList<DocumentAccessControlEntry>? entries) => entries?.Select(entry => entry is null
        ? throw new ArgumentException("ACL entries cannot be null.", nameof(entries))
        : new DocumentAccessControlEntry { Name = entry.Name, Type = entry.Type is null ? null : new(entry.Type.Value), Access = entry.Access is null ? null : new(entry.Access.Value) }).ToArray();
}
