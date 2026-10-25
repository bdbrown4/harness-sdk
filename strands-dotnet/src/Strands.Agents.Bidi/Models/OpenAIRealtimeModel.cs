using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

/// <summary>A native persistent OpenAI Realtime WebSocket connection.</summary>
public sealed partial class OpenAIRealtimeModel : IRestartableBidiModel, IAudioBidiModel, IModelMetadataProvider, IConfigurableModel<OpenAIRealtimeModelOptions>
{
    private OpenAIRealtimeModelOptions _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private RealtimeConnection? _connection;
    private string[] _restartPendingTools = [];
    private bool _disposed;
    private Task? _disposal;
    /// <summary>Creates a reusable model adapter; no connection is opened during construction.</summary>
    public OpenAIRealtimeModel(OpenAIRealtimeModelOptions options) => _options = Snapshot(options);
    /// <inheritdoc/>
    public ModelMetadata Metadata => new(Volatile.Read(ref _options).ModelId);
    /// <inheritdoc/>
    public BidiConnectionOptions Connection => Volatile.Read(ref _options).Connection;
    /// <inheritdoc/>
    public BidiAudioConfig Audio => new(new(24000, 1, "pcm"), new(24000, 1, "pcm"));
    /// <inheritdoc/>
    public OpenAIRealtimeModelOptions GetConfig() => Snapshot(Volatile.Read(ref _options));
    /// <inheritdoc/>
    public void UpdateConfig(Func<OpenAIRealtimeModelOptions, OpenAIRealtimeModelOptions> update)
    {
        if (update is null) throw new ArgumentNullException(nameof(update));
        lock (_gate) if (_disposed) throw new ObjectDisposedException(nameof(OpenAIRealtimeModel));
        var options = Snapshot(update(GetConfig()));
        lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(OpenAIRealtimeModel)); Volatile.Write(ref _options, options); }
    }
    /// <inheritdoc/>
    public async Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detached = SessionSnapshot(session);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StartCoreAsync(Prepare(detached), cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }
    private PreparedSession Prepare(BidiSessionOptions session)
    {
        OpenAIRealtimeModelOptions options;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpenAIRealtimeModel));
            options = Volatile.Read(ref _options);
        }
        var configuration = SessionConfiguration(options, session);
        var history = History(session.Messages);
        var initialization = new[] { EventBytes(new { type = "session.update", session = configuration }, options.MaximumEventBytes) }
            .Concat(history.Select(item => EventBytes(new { type = "conversation.item.create", item }, options.MaximumEventBytes))).ToArray();
        return new(options, configuration, initialization);
    }
    private async Task StartCoreAsync(PreparedSession prepared, CancellationToken cancellationToken, IReadOnlyList<string>? pending = null)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpenAIRealtimeModel));
            if (_connection is not null) throw new InvalidOperationException("The model is already started. Stop it before starting again.");
        }
        var options = prepared.Options;
        var connection = new RealtimeConnection(options, _lifetime.Token, prepared.Configuration);
        connection.State.RestorePending(pending ?? []);
        try
        {
            connection.Socket.Options.SetRequestHeader("Authorization", "Bearer " + options.ApiKey);
            if (!string.IsNullOrEmpty(options.Organization)) connection.Socket.Options.SetRequestHeader("OpenAI-Organization", options.Organization);
            if (!string.IsNullOrEmpty(options.Project)) connection.Socket.Options.SetRequestHeader("OpenAI-Project", options.Project);
            var endpoint = new UriBuilder(options.Endpoint ?? new Uri("wss://api.openai.com/v1/realtime"));
            var query = endpoint.Query.TrimStart('?').Split('&').Where(value => value.Length > 0 && Uri.UnescapeDataString(value.Split('=')[0]) != "model");
            endpoint.Query = string.Join("&", query.Concat(new[] { "model=" + Uri.EscapeDataString(options.ModelId) }));
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Token);
            await connection.Socket.ConnectAsync(endpoint.Uri, operation.Token).ConfigureAwait(false);
            foreach (var item in prepared.Initialization) await SendBytesAsync(connection, item, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(OpenAIRealtimeModel)); _connection = connection; }
            _restartPendingTools = [];
        }
        catch (Exception error)
        {
            var primary = Translate(error, cancellationToken, connection);
            try { connection.Dispose(); } catch (Exception cleanup) { Cleanup(primary, cleanup); }
            ExceptionDispatchInfo.Capture(primary).Throw(); throw;
        }
    }
    /// <inheritdoc/>
    public async Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        cancellationToken.ThrowIfCancellationRequested();
        var connection = Current();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Token);
        var entered = false;
        try
        {
            await connection.StateGate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
            if (input is BidiAudioDelta audio)
                await SendEventAsync(connection, new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(audio.GetBytes()) }, operation.Token).ConfigureAwait(false);
            else if (input is BidiMessage message)
                await SendMessageAsync(connection, message, operation.Token).ConfigureAwait(false);
            else throw new ArgumentException("Unsupported bidirectional input.", nameof(input));
        }
        catch (Exception error) { ExceptionDispatchInfo.Capture(Translate(error, cancellationToken, connection)).Throw(); throw; }
        finally { if (entered) connection.StateGate.Release(); }
    }
    /// <inheritdoc/>
    public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = Current();
        if (Interlocked.CompareExchange(ref connection.ReaderActive, 1, 0) != 0) throw new InvalidOperationException("A bidirectional connection supports one active receiver.");
        try
        {
            yield return new BidiConnectionStartEvent(connection.Id, connection.Options.ModelId);
            while (true)
            {
                JsonElement? native;
                try { native = await ReadEventAsync(connection, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { ExceptionDispatchInfo.Capture(Translate(error, cancellationToken, connection)).Throw(); throw; }
                if (native is null) yield break;
                var output = await ProcessEventAsync(connection, native.Value, cancellationToken).ConfigureAwait(false);
                foreach (var item in output) yield return item;
            }
        }
        finally { Interlocked.Exchange(ref connection.ReaderActive, 0); }
    }
    private static async Task<IReadOnlyList<IBidiOutputEvent>> ProcessEventAsync(RealtimeConnection connection, JsonElement value, CancellationToken caller)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(caller, connection.Token);
        var entered = false;
        try
        {
            await connection.StateGate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
            var output = connection.State.Process(value);
            if (RealtimeJson.Text(value, "type") == "response.done") await FlushResponseAsync(connection, operation.Token).ConfigureAwait(false);
            return output;
        }
        catch (Exception error) { ExceptionDispatchInfo.Capture(Translate(error, caller, connection)).Throw(); throw; }
        finally { if (entered) connection.StateGate.Release(); }
    }
    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _restartPendingTools = []; _lifecycle.Release(); }
    }
    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        RealtimeConnection? connection;
        lock (_gate) { connection = _connection; _connection = null; }
        if (connection is null) return;
        Exception? primary = null;
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closing.CancelAfter(connection.Options.CloseTimeout);
        try
        {
            await connection.SendGate.WaitAsync(closing.Token).ConfigureAwait(false);
            try { if (connection.Socket.State == WebSocketState.Open) await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token).ConfigureAwait(false); }
            finally { connection.SendGate.Release(); }
        }
        catch (Exception error)
        {
            primary = cancellationToken.IsCancellationRequested
                ? new OperationCanceledException("Stopping Realtime was cancelled.", error, cancellationToken)
                : error is OperationCanceledException && closing.IsCancellationRequested
                    ? new TimeoutException("OpenAI Realtime shutdown exceeded CloseTimeout.", error)
                    : error;
        }
        finally { try { connection.Dispose(); } catch (Exception cleanup) { if (primary is null) primary = cleanup; else Cleanup(primary, cleanup); } }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
    /// <inheritdoc/>
    public async Task RestartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detached = SessionSnapshot(session);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prepared = Prepare(detached);
            RealtimeConnection? previous;
            lock (_gate) previous = _connection;
            var pending = _restartPendingTools;
            if (previous is not null)
            {
                await previous.StateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    pending = previous.State.PendingTools.ToArray();
                    _restartPendingTools = pending;
                    await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                }
                finally { previous.StateGate.Release(); }
            }
            await StartCoreAsync(prepared, cancellationToken, pending).ConfigureAwait(false);
            var current = Current();
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Token);
            try { await SendEventAsync(current, new { type = "conversation.item.create", item = new { type = "message", role = "system", content = new[] { new { type = "input_text", text = "The connection was re-established mid-conversation. Continue seamlessly from the prior context: do not greet or re-introduce yourself, and keep replying in the language already in use." } } } }, operation.Token).ConfigureAwait(false); }
            catch (Exception error) when (!operation.IsCancellationRequested)
            {
                try
                {
                    var observerFailure = SdkLogging.WarningFailure(error, "Realtime restart re-anchor failed: {Error}", error.Message);
                    if (observerFailure is not null) ExceptionDispatchInfo.Capture(observerFailure).Throw();
                }
                catch (Exception diagnostic) { error.Data["Strands.DiagnosticErrors"] = new List<Exception> { diagnostic }; }
            }
            catch (Exception error) { ExceptionDispatchInfo.Capture(Translate(error, cancellationToken, current)).Throw(); throw; }
        }
        finally { _lifecycle.Release(); }
    }
    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; return new(_disposal ??= DisposeCoreAsync()); }
    }
    private async Task DisposeCoreAsync()
    {
        Exception? primary = null;
        try { _lifetime.Cancel(); } catch (Exception error) { primary = error; }
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync(default).ConfigureAwait(false); }
        catch (Exception error) { if (primary is null) primary = error; else Cleanup(primary, error); }
        finally { _lifecycle.Release(); _lifetime.Dispose(); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private RealtimeConnection Current()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpenAIRealtimeModel));
            return _connection ?? throw new InvalidOperationException("The model is not started.");
        }
    }
    private static Exception Translate(Exception error, CancellationToken caller, RealtimeConnection connection)
    {
        if (caller.IsCancellationRequested) return new OperationCanceledException("Realtime was cancelled.", error, caller);
        if (connection.Token.IsCancellationRequested) return new OperationCanceledException("The Realtime connection stopped.", error, connection.Token);
        return error is WebSocketException or IOException or JsonException or DecoderFallbackException ? new ModelException("OpenAI Realtime transport or protocol failed.", error) : error;
    }
    private static void Cleanup(Exception primary, Exception secondary)
    {
        if (ReferenceEquals(primary, secondary)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(secondary);
    }
    private sealed record PreparedSession(OpenAIRealtimeModelOptions Options, JsonElement Configuration, byte[][] Initialization);
    private sealed class RealtimeConnection : IDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private int _disposed;
        internal RealtimeConnection(OpenAIRealtimeModelOptions options, CancellationToken lifetime, JsonElement configuration)
        {
            Options = options; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = _lifetime.Token;
            State = new(RealtimeJson.Object(configuration.GetProperty("audio"), "input").TryGetProperty("transcription", out var transcription) && transcription.ValueKind != JsonValueKind.Null);
        }
        internal string Id { get; } = Guid.NewGuid().ToString();
        internal ClientWebSocket Socket { get; } = new();
        internal OpenAIRealtimeModelOptions Options { get; }
        internal RealtimeState State { get; }
        internal SemaphoreSlim SendGate { get; } = new(1, 1);
        internal SemaphoreSlim StateGate { get; } = new(1, 1);
        internal CancellationToken Token { get; }
        internal Stopwatch Clock { get; } = Stopwatch.StartNew();
        internal int ReaderActive;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Exception? primary = null;
            try { _lifetime.Cancel(); } catch (Exception error) { primary = error; }
            try { Socket.Abort(); } catch (Exception error) { if (primary is null) primary = error; else Cleanup(primary, error); }
            try { Socket.Dispose(); } catch (Exception error) { if (primary is null) primary = error; else Cleanup(primary, error); }
            _lifetime.Dispose();
            if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }
}
