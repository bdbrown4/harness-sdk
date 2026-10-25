using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

/// <summary>A native Gemini Developer API or Vertex AI Live WebSocket connection.</summary>
public sealed partial class GoogleGeminiLiveModel : IRestartableBidiModel, IAudioBidiModel, IModelMetadataProvider, IConfigurableModel<GoogleGeminiLiveModelOptions>
{
    private GoogleGeminiLiveModelOptions _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private LiveConnection? _connection;
    private Dictionary<string, string> _pendingTools = new(StringComparer.Ordinal);
    private bool _disposed;
    private Task? _disposal;
    /// <summary>Creates a reusable model without connecting to the service.</summary>
    public GoogleGeminiLiveModel(GoogleGeminiLiveModelOptions options) => _options = Snapshot(options);
    /// <inheritdoc/>
    public ModelMetadata Metadata => new(Volatile.Read(ref _options).ModelId);
    /// <inheritdoc/>
    public BidiConnectionOptions Connection => Volatile.Read(ref _options).Connection;
    /// <inheritdoc/>
    public BidiAudioConfig Audio
    {
        get { lock (_gate) return new(new((_connection?.Options ?? Volatile.Read(ref _options)).InputSampleRate, 1, "pcm"), new(24000, 1, "pcm")); }
    }
    /// <inheritdoc/>
    public GoogleGeminiLiveModelOptions GetConfig() => Snapshot(Volatile.Read(ref _options));
    /// <inheritdoc/>
    public void UpdateConfig(Func<GoogleGeminiLiveModelOptions, GoogleGeminiLiveModelOptions> update)
    {
        if (update is null) throw new ArgumentNullException(nameof(update));
        lock (_gate) if (_disposed) throw new ObjectDisposedException(nameof(GoogleGeminiLiveModel));
        var next = Snapshot(update(GetConfig()));
        lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(GoogleGeminiLiveModel)); Volatile.Write(ref _options, next); }
    }
    /// <inheritdoc/>
    public async Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
    {
        RejectAuthenticationReentry();
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = Prepare(session, null);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StartWithAuthenticationAsync(prepared, cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
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
            var (value, completed) = Input(connection, input);
            await SendEventAsync(connection, value, operation.Token).ConfigureAwait(false);
            foreach (var id in completed) connection.State.ToolNames.Remove(id);
        }
        catch (Exception error) { ExceptionDispatchInfo.Capture(Translate(error, cancellationToken, connection)).Throw(); throw; }
        finally { if (entered) connection.StateGate.Release(); }
    }
    /// <inheritdoc/>
    public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = Current();
        if (Interlocked.CompareExchange(ref connection.ReaderActive, 1, 0) != 0) throw new InvalidOperationException("A Live connection supports one receiver.");
        try
        {
            yield return new BidiConnectionStartEvent(connection.Id, connection.Options.ModelId);
            while (connection.InitialEvents.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested(); connection.Token.ThrowIfCancellationRequested();
                yield return connection.InitialEvents.Dequeue();
            }
            while (true)
            {
                IReadOnlyList<IBidiOutputEvent>? values;
                try
                {
                    var native = await ReadEventAsync(connection, cancellationToken).ConfigureAwait(false);
                    if (native is null) yield break;
                    using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Token);
                    await connection.StateGate.WaitAsync(operation.Token).ConfigureAwait(false);
                    try
                    {
                        var accepted = connection.State.Copy();
                        values = accepted.Process(native.Value);
                        connection.State = accepted;
                    }
                    finally { connection.StateGate.Release(); }
                }
                catch (Exception error) { ExceptionDispatchInfo.Capture(Translate(error, cancellationToken, connection)).Throw(); throw; }
                foreach (var value in values)
                {
                    cancellationToken.ThrowIfCancellationRequested(); connection.Token.ThrowIfCancellationRequested();
                    yield return value;
                }
            }
        }
        finally { Interlocked.Exchange(ref connection.ReaderActive, 0); }
    }
    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RejectAuthenticationReentry();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _pendingTools.Clear(); _lifecycle.Release(); }
    }
    /// <inheritdoc/>
    public async Task RestartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
    {
        RejectAuthenticationReentry();
        cancellationToken.ThrowIfCancellationRequested();
        var fresh = Prepare(session, null);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? handle = null;
            LiveConnection? previous;
            lock (_gate) previous = _connection;
            if (previous is not null)
            {
                await previous.StateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { handle = previous.State.ResumeHandle; }
                finally { previous.StateGate.Release(); }
            }
            var resumed = handle is null ? null : Prepare(session, handle);
            await StartWithAuthenticationAsync(resumed ?? fresh, cancellationToken, replacing: true, fallback: resumed is null ? null : fresh).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        RejectAuthenticationReentry();
        TaskCompletionSource<bool>? completion = null;
        Task disposal;
        lock (_gate)
        {
            _disposed = true;
            if (_disposal is null) _disposal = (completion = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            disposal = _disposal;
        }
        if (completion is not null) _ = CompleteDisposalAsync(completion);
        return new(disposal);
    }
    private async Task CompleteDisposalAsync(TaskCompletionSource<bool> completion)
    {
        try { await DisposeCoreAsync().ConfigureAwait(false); completion.TrySetResult(true); }
        catch (Exception error) { completion.TrySetException(error); }
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
    private async Task StartCoreAsync(PreparedSession prepared, CancellationToken caller, CancellationToken operation, string? accessToken, IReadOnlyDictionary<string, string>? pending)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GoogleGeminiLiveModel));
            if (_connection is not null) throw new InvalidOperationException("The Live model is already started.");
        }
        var connection = new LiveConnection(prepared.Options, _lifetime.Token, pending);
        try
        {
            var endpoint = new UriBuilder(prepared.Options.Endpoint ?? new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"));
            var query = endpoint.Query.TrimStart('?').Split('&').Where(value => value.Length > 0 && Uri.UnescapeDataString(value.Split('=')[0]) != "key");
            endpoint.Query = string.Join("&", prepared.Options.UseVertexAi ? query : query.Concat(new[] { "key=" + Uri.EscapeDataString(prepared.Options.ApiKey!) }));
            if (accessToken is not null) connection.Socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
            operation.ThrowIfCancellationRequested();
            await connection.Socket.ConnectAsync(endpoint.Uri, operation).ConfigureAwait(false);
            await SendEventAsync(connection, new { setup = prepared.Configuration }, operation).ConfigureAwait(false);
            var acknowledgement = await ReadEventAsync(connection, operation).ConfigureAwait(false);
            if (acknowledgement is null) throw new ModelException("Gemini Live disconnected before setupComplete.");
            CheckError(acknowledgement.Value);
            if (!acknowledgement.Value.TryGetProperty("setupComplete", out var setup) || setup.ValueKind != JsonValueKind.Object) throw new ModelException("Gemini Live requires setupComplete before content.");
            foreach (var value in connection.State.Process(acknowledgement.Value)) connection.InitialEvents.Enqueue(value);
            if (prepared.History is { } history) await SendEventAsync(connection, history, operation).ConfigureAwait(false);
            operation.ThrowIfCancellationRequested();
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(GoogleGeminiLiveModel)); _connection = connection; }
        }
        catch (Exception error)
        {
            var primary = Translate(error, caller, connection);
            try { connection.Dispose(); } catch (Exception cleanup) { Cleanup(primary, cleanup); }
            ExceptionDispatchInfo.Capture(primary).Throw(); throw;
        }
    }
    private async Task StopCoreAsync(CancellationToken caller)
    {
        LiveConnection? connection;
        lock (_gate) { connection = _connection; _connection = null; }
        if (connection is null) return;
        Exception? primary = null;
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(caller); closing.CancelAfter(connection.Options.CloseTimeout);
        try
        {
            await connection.SendGate.WaitAsync(closing.Token).ConfigureAwait(false);
            try { if (connection.Socket.State == WebSocketState.Open) await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token).ConfigureAwait(false); }
            finally { connection.SendGate.Release(); }
        }
        catch (Exception error) { primary = caller.IsCancellationRequested ? new OperationCanceledException("Stopping Gemini Live was cancelled.", error, caller) : error; }
        finally { try { connection.Dispose(); } catch (Exception cleanup) { if (primary is null) primary = cleanup; else Cleanup(primary, cleanup); } }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private LiveConnection Current()
    {
        lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(GoogleGeminiLiveModel)); return _connection ?? throw new InvalidOperationException("The Live model is not started."); }
    }
    private static Exception Translate(Exception error, CancellationToken caller, LiveConnection connection)
    {
        if (caller.IsCancellationRequested) return new OperationCanceledException("Gemini Live was cancelled.", error, caller);
        if (connection.Token.IsCancellationRequested) return new OperationCanceledException("The Gemini Live connection stopped.", error, connection.Token);
        return error is WebSocketException or IOException or JsonException or System.Text.DecoderFallbackException ? new ModelException("Gemini Live transport or protocol failed.", error) : error;
    }
    private static void Cleanup(Exception primary, Exception secondary)
    {
        if (ReferenceEquals(primary, secondary)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(secondary);
    }
    private sealed record PreparedSession(GoogleGeminiLiveModelOptions Options, JsonElement Configuration, object? History);
    private sealed class LiveConnection : IDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private int _disposed;
        internal LiveConnection(GoogleGeminiLiveModelOptions options, CancellationToken lifetime, IReadOnlyDictionary<string, string>? pending)
        {
            Options = options; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = _lifetime.Token; State = new();
            if (pending is not null) foreach (var pair in pending) State.ToolNames.Add(pair.Key, pair.Value);
        }
        internal string Id { get; } = Guid.NewGuid().ToString();
        internal ClientWebSocket Socket { get; } = new();
        internal GoogleGeminiLiveModelOptions Options { get; }
        internal GoogleLiveState State { get; set; }
        internal Queue<IBidiOutputEvent> InitialEvents { get; } = new();
        internal SemaphoreSlim SendGate { get; } = new(1, 1);
        internal SemaphoreSlim StateGate { get; } = new(1, 1);
        internal CancellationToken Token { get; }
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
