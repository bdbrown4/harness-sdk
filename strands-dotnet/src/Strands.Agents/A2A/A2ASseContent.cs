using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private sealed class SseContent : HttpContent
    {
        private readonly SseStream _body;
        internal SseContent(EventBuffer events, JsonElement id, int maximumSubscribers, long cursor = 0, JsonElement? initial = null)
        {
            events.Subscribe(maximumSubscribers); _body = new(events, id, cursor, initial);
            Headers.ContentType = new MediaTypeHeaderValue("text/event-stream") { CharSet = "utf-8" };
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _body.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) _body.Dispose(); base.Dispose(disposing); }
    }
    private sealed class SseStream(EventBuffer events, JsonElement id, long cursor, JsonElement? initial) : Stream
    {
        private readonly CancellationTokenSource _stop = new();
        private byte[] _pending = [];
        private int _offset, _disposed, _reading;
        private long _cursor = cursor;
        private JsonElement? _initial = initial;
        private bool _ended;
        public override bool CanRead => _disposed == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(offset));
            if (_disposed != 0) throw new ObjectDisposedException(nameof(SseStream));
            if (count == 0) return 0;
            if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Concurrent SSE reads are not supported.");
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (_offset == _pending.Length)
                {
                    if (_ended) return 0;
                    object envelope;
                    try
                    {
                        var item = _initial ?? await events.ReadAsync(_cursor, linked.Token).ConfigureAwait(false);
                        if (item is null) { _ended = true; return 0; }
                        if (_initial is null) _cursor++; else _initial = null;
                        envelope = new { jsonrpc = "2.0", id, result = item };
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        _ended = true;
                        envelope = new { jsonrpc = "2.0", id, error = new { code = error is A2ARpcException rpc ? rpc.Code : -32603, message = error is A2ARpcException ? error.Message : "Task execution failed." } };
                    }
                    _pending = Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(envelope) + "\n\n"); _offset = 0;
                }
                var copied = Math.Min(count, _pending.Length - _offset); Array.Copy(_pending, _offset, buffer, offset, copied); _offset += copied; return copied;
            }
            finally { Volatile.Write(ref _reading, 0); }
        }
        protected override void Dispose(bool disposing)
        { if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) { _stop.Cancel(); events.Unsubscribe(); } base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
