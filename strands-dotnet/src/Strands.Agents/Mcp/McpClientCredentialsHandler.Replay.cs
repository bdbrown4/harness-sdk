namespace Strands.Agents.Mcp;

public sealed partial class McpClientCredentialsHandler
{
    private async Task<RequestSnapshot> CaptureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? body = null;
        if (request.Content is { } content)
        {
            if (content.Headers.ContentLength > _options.MaximumRequestBodyBytes) throw new InvalidDataException("OAuth replay request exceeded its byte limit.");
            using var buffer = new LimitedBuffer(_options.MaximumRequestBodyBytes);
            var copying = content.CopyToAsync(buffer);
            _ = ObserveCopyAsync(copying);
            await TaskCancellation.WaitAsync(copying, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            body = buffer.ToArray();
        }
        return new(request, body);
    }
    private static async Task ObserveCopyAsync(Task copying)
    { try { await copying.ConfigureAwait(false); } catch (Exception) { } }

    private sealed class RequestSnapshot
    {
        private readonly Uri _uri;
        private readonly HttpMethod _method;
        private readonly Version _version;
        private readonly KeyValuePair<string, string[]>[] _headers;
        private readonly KeyValuePair<string, string[]>[] _contentHeaders;
        private readonly byte[]? _body;
#if NET8_0_OR_GREATER
        private readonly HttpVersionPolicy _versionPolicy;
#endif
        internal RequestSnapshot(HttpRequestMessage request, byte[]? body)
        {
            _uri = request.RequestUri!; _method = request.Method; _version = request.Version; _body = body;
            _headers = request.Headers.Select(pair => new KeyValuePair<string, string[]>(pair.Key, pair.Value.ToArray())).ToArray();
            _contentHeaders = request.Content?.Headers.Select(pair => new KeyValuePair<string, string[]>(pair.Key, pair.Value.ToArray())).ToArray() ?? [];
#if NET8_0_OR_GREATER
            _versionPolicy = request.VersionPolicy;
#endif
        }
        internal HttpRequestMessage Create()
        {
            var request = new HttpRequestMessage(_method, _uri) { Version = _version };
#if NET8_0_OR_GREATER
            request.VersionPolicy = _versionPolicy;
#endif
            foreach (var header in _headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (_body is not null)
            {
                request.Content = new ByteArrayContent(_body);
                foreach (var header in _contentHeaders) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return request;
        }
    }

    private sealed class LimitedBuffer(int maximum) : Stream
    {
        private readonly MemoryStream _bytes = new();
        private readonly object _gate = new();
        private bool _closed;
        internal byte[] ToArray() { lock (_gate) return _bytes.ToArray(); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length { get { lock (_gate) return _bytes.Length; } }
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                if (_closed) throw new ObjectDisposedException(nameof(LimitedBuffer));
                if (count > maximum - _bytes.Length) throw new InvalidDataException("OAuth replay request exceeded its byte limit.");
                _bytes.Write(buffer, offset, count);
            }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        protected override void Dispose(bool disposing)
        { if (disposing) { lock (_gate) { _closed = true; _bytes.Dispose(); } } base.Dispose(disposing); }
    }
}
