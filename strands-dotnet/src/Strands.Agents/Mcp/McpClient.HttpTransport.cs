using System.Net;
using System.Runtime.ExceptionServices;
using ModelContextProtocol.Client;

namespace Strands.Agents.Mcp;

public sealed partial class McpClient
{
    private HttpClientBridge? _httpBridge;

    private static Task<McpClient> CreateHttpCoreAsync(McpClientSettings settings, HttpClient borrowed,
        HttpClientTransportOptions transportOptions, McpClientOptions? clientOptions, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0
        return CreateBridgedHttpAsync(settings, borrowed, transportOptions, clientOptions, cancellationToken);
#else
        return CreateAsync(settings with { OwnsTransport = true }, new HttpClientTransport(transportOptions, borrowed, ownsHttpClient: false), clientOptions, cancellationToken);
#endif
    }

    private static async Task<McpClient> CreateBridgedHttpAsync(McpClientSettings settings, HttpClient borrowed,
        HttpClientTransportOptions transportOptions, McpClientOptions? clientOptions, CancellationToken cancellationToken)
    {
        var bridge = new HttpClientBridge(borrowed);
        HttpClientTransport transport;
        try { transport = new HttpClientTransport(transportOptions, bridge, ownsHttpClient: true); }
        catch { bridge.Dispose(); throw; }
        var client = await CreateAsync(settings with { OwnsTransport = true }, transport, clientOptions, cancellationToken).ConfigureAwait(false);
        client._httpBridge = bridge;
        return client;
    }

    // Framework HTTP response streams can ignore read cancellation. The owned forwarding client
    // changes only that response-body boundary; the application's HTTP client remains borrowed.
    internal sealed class HttpClientBridge : HttpClient
    {
        private static readonly AsyncLocal<OperationScope?> Current = new();
        internal HttpClientBridge(HttpClient borrowed) : this(new ForwardingHandler(borrowed), borrowed) { }
        private HttpClientBridge(ForwardingHandler handler, HttpClient borrowed) : base(handler)
        { handler.Bridge = this; BaseAddress = borrowed.BaseAddress; Timeout = System.Threading.Timeout.InfiniteTimeSpan; }

        internal void RefuseLifecycleReentry()
        {
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope.Receipt.Bridge, this) && scope.Receipt.Active)
                    throw new InvalidOperationException("An owned MCP HTTP callback cannot wait on its own client lifecycle.");
        }

        private sealed class Receipt(HttpClientBridge? bridge)
        {
            internal HttpClientBridge? Bridge { get; } = bridge;
            internal volatile bool Active = true;
            internal Exception? Primary;
            internal void RecordFailure(Exception failure) => Interlocked.CompareExchange(ref Primary, failure, null);
        }

        private sealed class OperationScope : IDisposable
        {
            internal OperationScope(Receipt receipt)
            { Receipt = receipt; Previous = Current.Value; Current.Value = this; }
            internal Receipt Receipt { get; }
            internal OperationScope? Previous { get; }
            public void Dispose() => Current.Value = Previous;
        }

        private sealed class ForwardingHandler(HttpClient borrowed) : HttpMessageHandler
        {
            internal HttpClientBridge? Bridge { get; set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // HttpClient cannot send the same request twice. Only this forwarding envelope is
                // owned here; disposing this clone never adds disposal of the original content.
                // Framework's outer HttpClient retains its normal automatic content disposal.
                using var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
#if !NETSTANDARD2_0
                clone.VersionPolicy = request.VersionPolicy;
#endif
                foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
#pragma warning disable CS0618 // Properties is the portable representation of the same options bag.
                foreach (var property in request.Properties) clone.Properties[property.Key] = property.Value;
#pragma warning restore CS0618
                if (request.Content is { } content) clone.Content = new BorrowedContent(content);
                var receipt = new Receipt(Bridge);
                using var scope = new OperationScope(receipt);
                HttpResponseMessage? response = null;
                try
                {
                    response = await borrowed.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (ReferenceEquals(response.RequestMessage, clone) && clone.Method == request.Method && clone.RequestUri == request.RequestUri)
                        response.RequestMessage = request;
                    if (response.Content is { } responseContent)
                        response.Content = new ResponseContent(responseContent, receipt);
                    else receipt.Active = false;
                    return response;
                }
                catch (Exception failure)
                {
                    try { response?.Dispose(); }
                    catch (Exception cleanup) { AttachDisposalError(failure, cleanup); }
                    finally { receipt.Active = false; }
                    throw;
                }
            }
        }

        private sealed class BorrowedContent : HttpContent
        {
            private readonly HttpContent _borrowed;
            internal BorrowedContent(HttpContent borrowed)
            { _borrowed = borrowed; foreach (var header in borrowed.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _borrowed.CopyToAsync(stream);
            protected override bool TryComputeLength(out long length)
            { length = _borrowed.Headers.ContentLength.GetValueOrDefault(); return _borrowed.Headers.ContentLength.HasValue; }
        }

        private sealed class ResponseContent : HttpContent
        {
            private readonly HttpContent _owned;
            private readonly Receipt _receipt;
            private int _disposed;
            internal ResponseContent(HttpContent owned, Receipt receipt)
            { _owned = owned; _receipt = receipt; foreach (var header in owned.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
            protected override async Task<Stream> CreateContentReadStreamAsync()
            {
                using var scope = new OperationScope(_receipt);
                Stream stream;
                try { stream = await _owned.ReadAsStreamAsync().ConfigureAwait(false); }
                catch (Exception error) { _receipt.RecordFailure(error); throw; }
                if (Volatile.Read(ref _disposed) != 0)
                {
                    var lateReceipt = new Receipt(_receipt.Bridge);
                    using var lateScope = new OperationScope(lateReceipt);
                    var failure = new ObjectDisposedException(nameof(ResponseContent));
                    try { stream.Dispose(); }
                    catch (Exception cleanup) { AttachDisposalError(failure, cleanup); }
                    finally { lateReceipt.Active = false; }
                    _receipt.RecordFailure(failure);
                    throw failure;
                }
                return new ResponseStream(stream, _receipt);
            }
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                using var scope = new OperationScope(_receipt);
                try { await _owned.CopyToAsync(stream).ConfigureAwait(false); }
                catch (Exception error) { _receipt.RecordFailure(error); throw; }
            }
            protected override bool TryComputeLength(out long length)
            { length = _owned.Headers.ContentLength.GetValueOrDefault(); return _owned.Headers.ContentLength.HasValue; }
            protected override void Dispose(bool disposing)
            {
                if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0) return;
                using var scope = new OperationScope(_receipt);
                var failures = new List<Exception>();
                try { base.Dispose(true); } catch (Exception error) { failures.Add(error); }
                try { _owned.Dispose(); }
                catch (Exception error) { failures.Add(error); }
                finally { _receipt.Active = false; }
                if (failures.Count != 0)
                {
                    if (_receipt.Primary is { } primary)
                        foreach (var cleanup in failures) AttachDisposalError(primary, cleanup);
                    else
                    {
                        var failure = failures[0];
                        foreach (var cleanup in failures.Skip(1)) AttachDisposalError(failure, cleanup);
                        ExceptionDispatchInfo.Capture(failure).Throw();
                    }
                }
            }
        }

        private sealed class ResponseStream(Stream owned, Receipt receipt) : Stream
        {
            private int _disposed;
            public override bool CanRead => owned.CanRead;
            public override bool CanSeek => owned.CanSeek;
            public override bool CanWrite => owned.CanWrite;
            public override long Length => owned.Length;
            public override long Position { get => owned.Position; set => owned.Position = value; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                using var scope = new OperationScope(receipt);
                try { return owned.Read(buffer, offset, count); }
                catch (Exception failure) { receipt.RecordFailure(failure); throw; }
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                using var scope = new OperationScope(receipt);
                try { cancellationToken.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException cancellationFailure) { receipt.RecordFailure(cancellationFailure); throw; }
                var cancellation = new ReadCancellation(this);
                using var registration = cancellationToken.Register(value => ((ReadCancellation)value!).Close(), cancellation);
                Exception? failure = null;
                var read = 0;
                try { read = await owned.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { failure = error; }
                registration.Dispose();
                if (cancellationToken.IsCancellationRequested && failure is null or OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException)
                    failure = failure is OperationCanceledException canceled && canceled.CancellationToken == cancellationToken
                        ? canceled : new OperationCanceledException("The MCP HTTP response read was cancelled.", failure, cancellationToken);
                if (cancellation.Failure is { } cleanup)
                { if (failure is null) failure = cleanup; else AttachDisposalError(failure, cleanup); }
                if (failure is not null)
                { receipt.RecordFailure(failure); ExceptionDispatchInfo.Capture(failure).Throw(); }
                return read;
            }
            public override void Flush() => owned.Flush();
            public override long Seek(long offset, SeekOrigin origin) => owned.Seek(offset, origin);
            public override void SetLength(long value) => owned.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => owned.Write(buffer, offset, count);
            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                { using var scope = new OperationScope(receipt); owned.Dispose(); }
                base.Dispose(disposing);
            }

            private sealed class ReadCancellation(ResponseStream stream)
            {
                internal Exception? Failure { get; private set; }
                internal void Close()
                { try { stream.Dispose(); } catch (Exception error) { Failure = error; } }
            }
        }
    }
}
