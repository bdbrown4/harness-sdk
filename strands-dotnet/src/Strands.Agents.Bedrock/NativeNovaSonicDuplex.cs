using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Amazon.Runtime.EventStreams.Internal;
using Strands.Agents.Bidi;

namespace Strands.Agents.Bedrock;

internal sealed class NativeNovaSonicDuplex : INovaSonicDuplex
{
    private readonly ImmutableCredentials _credentials;
    private readonly RegionEndpoint _region;
    private readonly Uri _endpoint;
    private readonly string _path;
    private readonly string _caFile;
    private readonly object _gate = new object();
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly TaskCompletionSource<State> _initialized = new TaskCompletionSource<State>(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _opened;
    private Task? _stopping;

    internal NativeNovaSonicDuplex(ImmutableCredentials credentials, RegionEndpoint region, string modelId)
        : this(credentials, region, modelId, null, "") { }

    // Local TLS fixtures use an explicit private endpoint and CA; public construction always resolves the AWS region.
    internal NativeNovaSonicDuplex(ImmutableCredentials credentials, RegionEndpoint region, string modelId, Uri? endpoint, string caFile)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _region = region ?? throw new ArgumentNullException(nameof(region));
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("A Nova Sonic model ID is required.", nameof(modelId));
        _endpoint = endpoint ?? new Uri("https://bedrock-runtime." + region.SystemName + "." + region.PartitionDnsSuffix);
        if (_endpoint.Scheme != Uri.UriSchemeHttps || _endpoint.AbsolutePath != "/" || _endpoint.Query.Length != 0 || _endpoint.UserInfo.Length != 0)
            throw new ArgumentException("Nova Sonic requires an HTTPS service root.", nameof(endpoint));
        _path = "/model/" + Uri.EscapeDataString(modelId) + "/invoke-with-bidirectional-stream";
        _caFile = caFile ?? throw new ArgumentNullException(nameof(caFile));
    }

    public async Task OpenAsync(Func<CancellationToken, Task<byte[]?>> readInput, CancellationToken cancellationToken)
    {
        if (readInput is null) throw new ArgumentNullException(nameof(readInput));
        lock (_gate)
        {
            if (_opened) throw new InvalidOperationException("The native Nova Sonic connection has already opened.");
            if (_stopping is not null) throw new ObjectDisposedException(nameof(NativeNovaSonicDuplex));
            _opened = true;
        }
        State state;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signed = NativeNovaSonicRequestSigner.Sign(_credentials.AccessKey, _credentials.SecretKey, _credentials.Token ?? "",
                _region.SystemName, _endpoint.Authority, _path, DateTimeOffset.UtcNow);
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            NativeNovaSonicHttpConnection connection;
            try { connection = new NativeNovaSonicHttpConnection(_endpoint.IdnHost, checked((ushort)_endpoint.Port), _path, _caFile, signed.Headers); }
            catch { lifetime.Dispose(); throw; }
            state = new State(connection, lifetime);
            state.Publishing = PublishAsync(state, readInput, Hex(signed.Signature));
            _initialized.TrySetResult(state);
        }
        catch (Exception error) { _initialized.TrySetException(error); throw; }
        try
        {
            using (state.Lifetime.Token.Register(state.Connection.Cancel))
            {
                // Publishing started above, so initial events can reach the service before headers exist.
                int status;
                try { status = await state.Connection.Headers.ConfigureAwait(false); }
                catch { if (state.Publishing.IsFaulted) await state.Publishing.ConfigureAwait(false); throw; }
                if (status < 200 || status >= 300) throw new IOException("Nova Sonic returned HTTP " + status + ".");
            }
        }
        catch (Exception error)
        {
            var primary = cancellationToken.IsCancellationRequested ? new OperationCanceledException("Nova Sonic connection opening was canceled.", error, cancellationToken) : error;
            try { await StopAsync().ConfigureAwait(false); } catch (Exception cleanup) { AddCleanup(primary, cleanup); }
            ExceptionDispatchInfo.Capture(primary).Throw();
            throw;
        }
    }

    private async Task PublishAsync(State state, Func<CancellationToken, Task<byte[]?>> readInput, string previous)
    {
        try
        {
            while (true)
            {
                var json = await readInput(state.Lifetime.Token).ConfigureAwait(false);
                if (json is null) { await state.Connection.WriteAsync(Array.Empty<byte>(), true, state.Lifetime.Token).ConfigureAwait(false); return; }
                state.Lifetime.Token.ThrowIfCancellationRequested();
                var eventType = new EventStreamHeader(":event-type"); eventType.SetString("chunk");
                var messageType = new EventStreamHeader(":message-type"); messageType.SetString("event");
                var contentType = new EventStreamHeader(":content-type"); contentType.SetString("application/json");
                var inner = new EventStreamMessage(new List<IEventStreamHeader> { messageType, eventType, contentType },
                    JsonSerializer.SerializeToUtf8Bytes(new { bytes = Convert.ToBase64String(json) })).ToByteArray();
                var timestamp = DateTimeOffset.UtcNow;
                var signature = NativeNovaSonicEventSigner.Sign(_credentials.SecretKey, _region.SystemName, previous, timestamp, inner);
                var date = new EventStreamHeader(":date"); date.SetTimestamp(timestamp.UtcDateTime);
                var signed = new EventStreamHeader(":chunk-signature"); signed.SetByteBuf(signature);
                var wire = new EventStreamMessage(new List<IEventStreamHeader> { date, signed }, inner).ToByteArray();
                await state.Connection.WriteAsync(wire, false, state.Lifetime.Token).ConfigureAwait(false);
                previous = Hex(signature);
            }
        }
        catch { state.Connection.Cancel(); throw; }
    }

    public async IAsyncEnumerable<byte[]> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = await _initialized.Task.ConfigureAwait(false);
        var frames = new Queue<EventStreamMessage>();
        var decoder = new EventStreamDecoder();
        decoder.MessageReceived += (_, args) => frames.Enqueue(args.Message);
        var buffer = new byte[16384];
        var boundary = new FrameBoundary();
        try
        {
            while (true)
            {
                int count;
                try { count = await state.Connection.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
                catch { if (state.Publishing.IsFaulted) await state.Publishing.ConfigureAwait(false); throw; }
                if (count == 0)
                {
                    boundary.End();
                    if (state.Publishing.IsFaulted) await state.Publishing.ConfigureAwait(false);
                    yield break;
                }
                boundary.Advance(buffer, count);
                decoder.ProcessData(buffer, 0, count); // Official AWS decoder validates CRCs and header values.
                while (frames.Count != 0)
                {
                    var message = frames.Dequeue();
                    if (message.Headers.TryGetValue(":message-type", out var kind) && kind.AsString() == "exception") throw ServiceError(message);
                    if (!message.Headers.TryGetValue(":event-type", out var type) || type.AsString() != "chunk") throw new IOException("Nova Sonic returned an unsupported event-stream message.");
                    using (var json = JsonDocument.Parse(message.Payload))
                    {
                        if (!json.RootElement.TryGetProperty("bytes", out var bytes) || bytes.ValueKind != JsonValueKind.String)
                            throw new IOException("A Nova Sonic output chunk did not contain bytes.");
                        yield return Convert.FromBase64String(bytes.GetString()!);
                    }
                }
            }
        }
        finally
        {
            // A remote terminal or abandoned reader ends input before callers can publish another EOF/write.
            try { state.Lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception error) { state.CancellationFailure = error; }
            state.Connection.Cancel();
        }
    }

    public Task StopAsync()
    {
        TaskCompletionSource<bool>? owner = null;
        bool opened;
        lock (_gate)
        {
            opened = _opened;
            if (_stopping is null) { owner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); _stopping = owner.Task; }
        }
        if (owner is not null) _ = StopCoreAsync(owner, opened);
        return _stopping;
    }

    private async Task StopCoreAsync(TaskCompletionSource<bool> completion, bool opened)
    {
        Exception? primary = null;
        try
        {
            try { _stop.Cancel(); } catch (Exception error) { Add(error); }
            State? state = null;
            if (opened) try { state = await _initialized.Task.ConfigureAwait(false); } catch { /* Open reports allocation/signing failure; no native state exists. */ }
            if (state is not null)
            {
                try { state.Connection.Cancel(); } catch (Exception error) { Add(error); }
                try { await state.Publishing.ConfigureAwait(false); }
                catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested) { }
                catch (Exception error) { Add(error); }
                try { await state.Connection.StopAsync().ConfigureAwait(false); } catch (Exception error) { Add(error); }
                if (state.CancellationFailure is not null) Add(state.CancellationFailure);
                state.Lifetime.Dispose();
            }
            _stop.Dispose();
            if (primary is null) completion.TrySetResult(true); else completion.TrySetException(primary);
        }
        catch (Exception error) { completion.TrySetException(error); }
        void Add(Exception error) { if (primary is null) primary = error; else AddCleanup(primary, error); }
    }

    private static Exception ServiceError(EventStreamMessage frame)
    {
        var code = frame.Headers.TryGetValue(":exception-type", out var type) ? type.AsString() : "Unknown";
        var message = "Nova Sonic returned " + code + ".";
        try { using var json = JsonDocument.Parse(frame.Payload); if (json.RootElement.TryGetProperty("message", out var value)) message = value.GetString() ?? message; }
        catch (JsonException) { }
        if (code == "modelTimeoutException" || (code == "validationException" && message.IndexOf("InternalErrorCode=531", StringComparison.Ordinal) >= 0))
        {
            var timeout = new BidiConnectionTimeoutException("Nova Sonic reached its connection deadline.");
            timeout.Data["Strands.ProviderError"] = code == "modelTimeoutException" ? (Exception)new ModelTimeoutException(message) : new ValidationException(message);
            return timeout;
        }
        return code switch
        {
            "validationException" => new ValidationException(message),
            "throttlingException" => new ThrottlingException(message),
            "serviceUnavailableException" => new ServiceUnavailableException(message),
            "internalServerException" => new InternalServerException(message),
            "modelStreamErrorException" => new ModelStreamErrorException(message),
            _ => new IOException(message)
        };
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        if (!(primary.Data["Strands.CleanupErrors"] is List<Exception> errors)) primary.Data["Strands.CleanupErrors"] = errors = new List<Exception>();
        errors.Add(cleanup);
    }
    private sealed class State
    {
        internal readonly NativeNovaSonicHttpConnection Connection;
        internal readonly CancellationTokenSource Lifetime;
        internal Task Publishing = Task.CompletedTask;
        internal Exception? CancellationFailure;
        internal State(NativeNovaSonicHttpConnection connection, CancellationTokenSource lifetime) { Connection = connection; Lifetime = lifetime; }
    }
    private sealed class FrameBoundary
    {
        private uint _length;
        private int _prefix;
        private uint _remaining;
        internal void Advance(byte[] bytes, int count)
        {
            for (var offset = 0; offset < count;)
            {
                if (_remaining != 0) { var step = Math.Min((uint)(count - offset), _remaining); offset += (int)step; _remaining -= step; continue; }
                _length = (_length << 8) | bytes[offset++];
                if (++_prefix != 4) continue;
                if (_length < 16 || _length > 24 * 1024 * 1024) throw new IOException("A Nova Sonic event-stream frame exceeds its supported bounds.");
                _remaining = _length - 4; _length = 0; _prefix = 0;
            }
        }
        internal void End() { if (_prefix != 0 || _remaining != 0) throw new EndOfStreamException("Nova Sonic ended inside an event-stream frame."); }
    }
}
