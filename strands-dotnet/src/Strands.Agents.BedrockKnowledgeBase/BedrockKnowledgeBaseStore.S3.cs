using System.Text;
using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.S3;
using Amazon.S3.Model;

namespace Strands.Agents.BedrockKnowledgeBase;

public sealed partial class BedrockKnowledgeBaseStore
{
    private readonly BedrockKnowledgeBaseS3Options? _s3Options;
    private readonly Lazy<IAmazonS3> _s3;
    private readonly bool _ownsS3;

    private async Task<(string Id, KnowledgeBaseDocument Document)> PrepareS3DocumentAsync(string id, string content, JsonElement? metadata, CancellationToken token, CancellationToken caller)
    {
        var options = _s3Options!;
        var prefix = options.Prefix.EndsWith("/", StringComparison.Ordinal) ? options.Prefix : options.Prefix + "/";
        var key = prefix + id + ".txt";
        try
        {
            token.ThrowIfCancellationRequested();
            var client = InvokeClient(() => _s3.Value);
            var route = InvokeClient(() => client.Config);
            // These vendor path-style/custom routes drop an initial key slash instead of preserving the object identity.
            if (key.StartsWith("/", StringComparison.Ordinal) && route is AmazonS3Config config && (config.ForcePathStyle || !string.IsNullOrEmpty(config.ServiceURL)))
                throw new ArgumentException("Leading-slash S3 keys, including an empty Prefix, are unsupported with ForcePathStyle or a custom ServiceURL because the vendor transport changes the uploaded key.", nameof(_options.S3));
            var uri = "s3://" + options.Bucket + "/" + key;
            await PutS3ObjectAsync(client, options.Bucket, key, Encoding.UTF8.GetBytes(content), "text/plain; charset=utf-8", token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var sidecar = S3Sidecar(metadata);
            string? sidecarUri = null;
            if (sidecar is not null)
            {
                await PutS3ObjectAsync(client, options.Bucket, key + ".metadata.json", sidecar, "application/json", token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                sidecarUri = uri + ".metadata.json";
            }
            return (uri, new KnowledgeBaseDocument
            {
                Content = new() { DataSourceType = ContentDataSourceType.S3, S3 = new() { S3Location = new() { Uri = uri } } },
                Metadata = sidecarUri is null ? null : new DocumentMetadata { Type = MetadataSourceType.S3_LOCATION, S3Location = new() { Uri = sidecarUri } }
            });
        }
        catch (Exception error)
        {
            Report(error, "Knowledge base S3 upload failed for store {Store}, bucket {Bucket}.", Name, options.Bucket);
            var failure = NormalizeCancellation(error, caller, "S3 upload");
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }

    private async Task PutS3ObjectAsync(IAmazonS3 client, string bucket, string key, byte[] bytes, string contentType, CancellationToken token)
    {
        using var body = new MemoryStream(bytes, writable: false);
        token.ThrowIfCancellationRequested();
        await InvokeClient(() => client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = body, AutoCloseStream = false, ContentType = contentType }, token)).ConfigureAwait(false);
    }

    private byte[]? S3Sidecar(JsonElement? metadata)
    {
        var attributes = KnowledgeBaseWriteDocument.Attributes(metadata, _options.Scope, ScopeKey(), ReportDroppedMetadata);
        if (attributes.Count == 0 && (_accessControlList is null || _accessControlList.Count == 0)) return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            var value = attribute.Value; var typed = new Dictionary<string, object?> { ["type"] = value.Type.Value };
            switch (value.Type.Value)
            {
                case "STRING": typed["stringValue"] = value.StringValue; break;
                case "BOOLEAN": typed["booleanValue"] = value.BooleanValue; break;
                case "NUMBER": typed["numberValue"] = value.NumberValue; break;
                case "STRING_LIST": typed["stringListValue"] = value.StringListValue; break;
            }
            values[attribute.Key] = new { value = typed, includeForEmbedding = false };
        }
        var sidecar = new Dictionary<string, object?> { ["metadataAttributes"] = values };
        if (_accessControlList is { Count: > 0 }) sidecar["accessControlList"] = _accessControlList.Select(entry => new Dictionary<string, object?> { ["Name"] = entry.Name, ["Type"] = entry.Type?.Value, ["Access"] = entry.Access?.Value }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(sidecar);
    }
}
