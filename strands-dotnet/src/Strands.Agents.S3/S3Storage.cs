using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Strands.Agents.Storage;

namespace Strands.Agents.S3;

/// <summary>A persistent byte store using the official Amazon S3 client.</summary>
public sealed partial class S3Storage : ISearchableStorage, IStorageCharacteristics, IDisposable, IAsyncDisposable
{
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly S3MissingKeyPolicy _missingKeyPolicy;
    private readonly StorageSearchStrategy _search;
    private readonly Lazy<IAmazonS3> _client;
    private readonly bool _ownsClient;

    /// <summary>Creates a store with a lazy owned AWS client.</summary>
    public S3Storage(string bucket, S3StorageOptions? options = null) : this(null, bucket, options, true) { }
    /// <summary>Creates a store over a borrowed AWS client.</summary>
    public S3Storage(IAmazonS3 client, string bucket, S3StorageOptions? options = null) : this(client, bucket, options, false) { Guard.NotNull(client, nameof(client)); }

    internal S3Storage(string bucket, S3StorageOptions? options, Func<IAmazonS3> clientFactory) : this(null, bucket, options, true, clientFactory) { Guard.NotNull(clientFactory, nameof(clientFactory)); }

    private S3Storage(IAmazonS3? client, string bucket, S3StorageOptions? options, bool ownsClient, Func<IAmazonS3>? clientFactory = null)
    {
        Guard.NotNullOrWhiteSpace(bucket, nameof(bucket));
        options ??= new();
        if (!Enum.IsDefined(typeof(S3MissingKeyPolicy), options.MissingKeyPolicy)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown missing-key policy.");
        if (options.Region is not null) Guard.NotNullOrWhiteSpace(options.Region, nameof(options));
        if (client is not null && options.Region is not null) throw new ArgumentException("A borrowed client cannot be combined with a region override.", nameof(options));
        _bucket = bucket;
        var prefix = StorageKey.Prefix(options.Prefix).TrimEnd('/');
        _prefix = prefix.Length == 0 ? "" : prefix + "/";
        _missingKeyPolicy = options.MissingKeyPolicy;
        _search = options.SearchStrategy ?? new KeywordSearchStrategy();
        _ownsClient = ownsClient;
        var region = options.Region;
        _client = new(() => client ?? clientFactory?.Invoke() ?? new AmazonS3Client(region is null ? new AmazonS3Config() : new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) }));
    }
    /// <inheritdoc />
    public bool IsEphemeral => false;
    /// <inheritdoc />
    public bool IsNamespaced => false;
    /// <inheritdoc />
    public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(data, nameof(data));
        var normalized = StorageKey.Normalize(key);
        var copy = (byte[])data.Clone();
        using var operation = EnterOperation(cancellationToken);
        try
        {
            using var input = new MemoryStream(copy, false);
            await _client.Value.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = _prefix + normalized, InputStream = input, AutoCloseStream = false }, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            var failure = Failure("write", normalized, error, cancellationToken);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
        try { await _search.IndexAsync(this, normalized, (byte[])copy.Clone(), operation.Token).ConfigureAwait(false); operation.Token.ThrowIfCancellationRequested(); }
        catch (Exception error)
        {
            var failure = Failure("index already written", normalized, error, cancellationToken);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }
    /// <inheritdoc />
    public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.Normalize(key);
        using var operation = EnterOperation(cancellationToken);
        GetObjectResponse? response = null;
        Exception? failure = null;
        byte[]? value = null;
        try
        {
            response = await _client.Value.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = _prefix + normalized }, operation.Token).ConfigureAwait(false);
            if (response?.ResponseStream is not { } stream) throw new InvalidDataException("S3 returned no response body.");
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, 81920, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            value = output.ToArray();
        }
        catch (AmazonS3Exception error) when (!operation.Token.IsCancellationRequested && IsMissing(error)) { }
        catch (Exception error)
        {
            failure = Failure("read", normalized, error, cancellationToken);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
        finally
        {
            try { response?.Dispose(); }
            catch (Exception cleanup)
            {
                if (failure is null)
                {
                    var classified = Failure("clean up read", normalized, cleanup, cancellationToken);
                    if (ReferenceEquals(classified, cleanup)) throw;
                    throw classified;
                }
                AttachCleanup(failure, cleanup);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        operation.Token.ThrowIfCancellationRequested();
        return value;
    }
    /// <inheritdoc />
    public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.Normalize(key);
        using var operation = EnterOperation(cancellationToken);
        try
        {
            await _client.Value.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = _prefix + normalized }, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            var failure = Failure("delete", normalized, error, cancellationToken);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.Prefix(prefix);
        using var operation = EnterOperation(cancellationToken);
        try
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            string? token = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var response = await _client.Value.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = _prefix + normalized, MaxKeys = 1000, ContinuationToken = token }, operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                if (response is null) throw new InvalidDataException("S3 returned no listing response.");
                foreach (var value in response.S3Objects ?? [])
                {
                    if (value?.Key is not { } key) continue;
                    if (!key.StartsWith(_prefix + normalized, StringComparison.Ordinal)) throw new InvalidDataException("S3 returned a key outside the requested prefix.");
                    var relative = key.Substring(_prefix.Length);
                    string canonical;
                    try { canonical = StorageKey.Normalize(relative); }
                    catch (StorageException error) { throw new InvalidDataException("S3 returned a key that cannot be represented by native storage.", error); }
                    if (!string.Equals(relative, canonical, StringComparison.Ordinal)) throw new InvalidDataException("S3 returned a noncanonical native storage key.");
                    keys.Add(relative);
                }
                if (response.IsTruncated != true) break;
                token = response.NextContinuationToken;
                if (string.IsNullOrEmpty(token) || !seen.Add(token)) throw new InvalidDataException("S3 returned a missing or repeated continuation token.");
            } while (true);
            return keys.ToArray();
        }
        catch (Exception error)
        {
            var failure = Failure("list", normalized, error, cancellationToken);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(query, nameof(query));
        using var operation = EnterOperation(cancellationToken);
        try
        {
            var results = await _search.SearchAsync(this, query, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            return results;
        }
        catch (Exception error)
        {
            var failure = Failure("search", query, error, cancellationToken);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }
    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(BeginDispose());
    private bool IsMissing(AmazonS3Exception error) => error.ErrorCode == "NoSuchKey" || (_missingKeyPolicy == S3MissingKeyPolicy.NoSuchKeyOrNotFound && error.ErrorCode == "NotFound");
    private static Exception Failure(string operation, string key, Exception error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return error is OperationCanceledException cancelled && cancelled.CancellationToken == cancellationToken ? error : new OperationCanceledException("The S3 operation was canceled.", error, cancellationToken);
        return error is OperationCanceledException ? error : new StorageException($"Failed to {operation} S3 storage key '{key}'.", error);
    }
}
