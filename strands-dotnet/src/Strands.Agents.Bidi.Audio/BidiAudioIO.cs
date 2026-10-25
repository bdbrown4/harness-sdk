using System.Runtime.ExceptionServices;

namespace Strands.Agents.Bidi;

/// <summary>Owns run-scoped device audio while borrowing the model and console sink.</summary>
public sealed class BidiAudioIO : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IAudioBidiModel _model;
    private readonly BidiAudioOptions _options;
    private readonly IAudioDeviceBackend _backend;
    private readonly CaptureStream _input;
    private readonly PlaybackStream _output;
    private ManagedAudioProcessor? _processor;
    private BidiAudioConfig? _format;
    private bool _disposed;

    /// <summary>Creates optional device I/O without opening hardware.</summary>
    /// <param name="model">The audio capability used by the associated BidiAgent.</param>
    /// <param name="options">Optional device and processing settings.</param>
    public BidiAudioIO(IAudioBidiModel model, BidiAudioOptions? options = null) : this(model, options, new PortAudioDeviceBackend()) { }
    internal BidiAudioIO(IAudioBidiModel model, BidiAudioOptions? options, IAudioDeviceBackend backend)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = options ?? new();
        if (_options.FramesPerBuffer is < 1 or > 65536 || _options.InputBufferCapacity is < 1 or > 65536 || _options.OutputBufferBytes is < 2 or > 64 * 1024 * 1024 || _options.OutputBufferBytes % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Audio buffering must be positive, bounded, and aligned to PCM16 samples.");
        if (_options.InputDeviceIndex < 0 || _options.OutputDeviceIndex < 0) throw new ArgumentOutOfRangeException(nameof(options));
        _input = new(this); _output = new(this, _options.ConsoleOutput ?? BidiConsole.Output());
    }
    /// <summary>Microphone input, started and physically stopped by the run.</summary>
    public IBidiInputStream Input => _input;
    /// <summary>Speaker and console output, started and physically stopped by the run.</summary>
    public IBidiOutputStream Output => _output;
    /// <summary>Stops owned devices and processing without disposing the borrowed model or console.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        Exception? primary = null;
        try { await _output.StopAsync().ConfigureAwait(false); } catch (Exception error) { Add(ref primary, error); }
        try { await _input.StopAsync().ConfigureAwait(false); } catch (Exception error) { Add(ref primary, error); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private BidiAudioConfig Prepare()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BidiAudioIO));
        if (_format is not null) return _format;
        var format = _model.Audio ?? throw new ArgumentException("The model must resolve audio formats before device startup.");
        Validate(format.Input); Validate(format.Output);
        _processor = _options.Processing is { } processing ? new(format.Input.SampleRate, format.Output.SampleRate, processing) : null;
        return _format = format;
    }
    private void ReleaseFormat()
    {
        if (_input.Current is null && _output.Current is null) { _format = null; _processor = null; }
    }
    private void Failed(object source, Exception error)
    {
        TaskCompletionSource<bool>? ready;
        lock (_gate)
        {
            if (!ReferenceEquals(_input.Current, source) && !ReferenceEquals(_output.Current, source)) return;
            if (_input.Current is { } input) input.Error ??= error;
            if (_output.Current is { } output) output.Error ??= error;
            ready = _input.Current?.Ready;
        }
        ready?.TrySetResult(true);
    }
    private static void Validate(BidiAudioStreamConfig format)
    {
        if (format is null || format.SampleRate is < 8000 or > 192000 || format.Channels != 1 || !string.Equals(format.Format, "pcm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Device audio requires mono, little-endian PCM16 at 8000–192000 Hz.");
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitAsync(Task task, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var canceled = Signal();
        using (token.Register(() => canceled.TrySetResult(true)))
        {
            if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task) token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested(); await task.ConfigureAwait(false);
        }
    }
    private static void Add(ref Exception? primary, Exception error)
    {
        if (primary is null) { primary = error; return; }
        if (ReferenceEquals(primary, error)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(error);
    }

    private sealed class CaptureState(ManagedAudioProcessor? processor)
    {
        internal readonly Queue<byte[]> Pending = [];
        internal TaskCompletionSource<bool> Ready = Signal();
        internal IAsyncDisposable? Device;
        internal readonly ManagedAudioProcessor? Processor = processor;
        internal bool Stopped;
        internal Exception? Error;
    }
    private sealed class CaptureStream(BidiAudioIO owner) : IBidiInputStream
    {
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        internal CaptureState? Current;
        public async ValueTask StartAsync(BidiAgent agent, CancellationToken cancellationToken = default)
        {
            if (agent is null) throw new ArgumentNullException(nameof(agent));
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CaptureState state; BidiAudioConfig format;
                lock (owner._gate)
                {
                    if (Current is not null) throw new InvalidOperationException("Audio capture is already in use.");
                    format = owner.Prepare(); Current = state = new(owner._processor);
                }
                state.Device = owner._backend.OpenCapture(format.Input, owner._options.InputDeviceIndex, owner._options.FramesPerBuffer, bytes => Captured(state, bytes), error => owner.Failed(state, error));
            }
            finally { _lifecycle.Release(); }
        }
        private void Captured(CaptureState state, byte[] bytes)
        {
            if (bytes.Length % 2 != 0) throw new ArgumentException("Capture returned an incomplete PCM16 sample.");
            if (bytes.Length == 0) return;
            TaskCompletionSource<bool> ready;
            lock (owner._gate)
            {
                if (!ReferenceEquals(Current, state) || state.Stopped) return;
                if (state.Pending.Count == owner._options.InputBufferCapacity) state.Pending.Dequeue();
                state.Pending.Enqueue(bytes.ToArray()); ready = state.Ready;
            }
            ready.TrySetResult(true);
        }
        public async ValueTask<IBidiInput> ReadAsync(CancellationToken cancellationToken = default)
        {
            CaptureState state;
            lock (owner._gate) state = Current ?? throw new InvalidOperationException("Start audio capture before reading.");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested(); byte[]? bytes = null; Task ready;
                lock (owner._gate)
                {
                    if (state.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
                    if (state.Stopped) throw new OperationCanceledException(cancellationToken);
                    if (state.Pending.Count > 0) bytes = state.Pending.Dequeue();
                    else if (state.Ready.Task.IsCompleted) state.Ready = Signal();
                    ready = state.Ready.Task;
                }
                if (bytes is not null) return new BidiAudioDelta(state.Processor?.Process(bytes) ?? bytes);
                await WaitAsync(ready, cancellationToken).ConfigureAwait(false);
            }
        }
        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CaptureState? state;
                lock (owner._gate)
                {
                    state = Current; if (state is null) return;
                    state.Stopped = true; state.Pending.Clear();
                }
                state.Ready.TrySetResult(true);
                try { if (state.Device is not null) await state.Device.DisposeAsync().ConfigureAwait(false); }
                finally { lock (owner._gate) { Current = null; owner.ReleaseFormat(); } }
            }
            finally { _lifecycle.Release(); }
        }
    }

    private sealed class PlaybackState(BidiAudioStreamConfig format, ManagedAudioProcessor? processor)
    {
        internal readonly BidiAudioStreamConfig Format = format;
        internal readonly ManagedAudioProcessor? Processor = processor;
        internal readonly Queue<byte[]> Pending = [];
        internal IAsyncDisposable? Device;
        internal int Offset, Bytes;
        internal bool Stopped, ConsoleEntered;
        internal Exception? Error;
    }
    private sealed class PlaybackStream(BidiAudioIO owner, IBidiOutputStream console) : IBidiOutputStream
    {
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        internal PlaybackState? Current;
        public async ValueTask StartAsync(BidiAgent agent, CancellationToken cancellationToken = default)
        {
            if (agent is null) throw new ArgumentNullException(nameof(agent));
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                PlaybackState state;
                lock (owner._gate)
                {
                    if (Current is not null) throw new InvalidOperationException("Audio playback is already in use.");
                    var format = owner.Prepare(); Current = state = new(format.Output, owner._processor); state.ConsoleEntered = true;
                }
                await console.StartAsync(agent, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                state.Device = owner._backend.OpenPlayback(state.Format, owner._options.OutputDeviceIndex, owner._options.FramesPerBuffer, frames => Pull(state, frames), error => owner.Failed(state, error));
            }
            finally { _lifecycle.Release(); }
        }
        public async ValueTask WriteAsync(IAgentStreamEvent output, CancellationToken cancellationToken = default)
        {
            if (output is null) throw new ArgumentNullException(nameof(output));
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                PlaybackState state;
                lock (owner._gate)
                {
                    state = Current is { Stopped: false } active ? active : throw new InvalidOperationException("Start audio playback before writing.");
                    if (state.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
                }
                if (output is BidiAudioDeltaEvent audio)
                {
                    if (audio.SampleRate != state.Format.SampleRate || audio.Channels != state.Format.Channels || !string.Equals(audio.Format, state.Format.Format, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Audio output must match the resolved device format.", nameof(output));
                    var bytes = Convert.FromBase64String(audio.Audio);
                    if (bytes.Length % 2 != 0) throw new ArgumentException("Audio output requires complete PCM16 samples.", nameof(output));
                    lock (owner._gate)
                    {
                        if (bytes.Length > owner._options.OutputBufferBytes - state.Bytes)
                            throw new IOException("The audio playback buffer is full. Increase OutputBufferBytes for responses produced faster than playback; no queued speech was discarded.");
                        state.Pending.Enqueue(bytes); state.Bytes += bytes.Length;
                    }
                }
                else if (output is BidiBargeInEvent)
                    lock (owner._gate) { state.Pending.Clear(); state.Offset = state.Bytes = 0; state.Processor?.ClearPlayback(); }
                await console.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            }
            finally { _lifecycle.Release(); }
        }
        private byte[] Pull(PlaybackState state, int frames)
        {
            var bytes = new byte[checked(frames * state.Format.Channels * 2)];
            lock (owner._gate)
            {
                if (state.Stopped || !ReferenceEquals(Current, state)) return bytes;
                var position = 0;
                while (position < bytes.Length && state.Pending.Count > 0)
                {
                    var chunk = state.Pending.Peek(); var count = Math.Min(bytes.Length - position, chunk.Length - state.Offset);
                    Buffer.BlockCopy(chunk, state.Offset, bytes, position, count); position += count; Discard(state, count);
                }
                state.Processor?.AddPlayback(bytes);
            }
            return bytes;
        }
        private static void Discard(PlaybackState state, int count)
        {
            while (state.Pending.Count > 0 && (count > 0 || state.Pending.Peek().Length == state.Offset))
            {
                var take = Math.Min(count, state.Pending.Peek().Length - state.Offset);
                state.Offset += take; state.Bytes -= take; count -= take;
                if (state.Offset == state.Pending.Peek().Length) { state.Pending.Dequeue(); state.Offset = 0; }
            }
        }
        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                PlaybackState? state; lock (owner._gate) { state = Current; if (state is null) return; state.Stopped = true; state.Pending.Clear(); state.Bytes = state.Offset = 0; }
                Exception? primary = null;
                try { if (state.Device is not null) await state.Device.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Add(ref primary, error); }
                try { if (state.ConsoleEntered) await console.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { Add(ref primary, error); }
                finally { lock (owner._gate) { state.Processor?.ClearPlayback(); Current = null; owner.ReleaseFormat(); } }
                if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
            }
            finally { _lifecycle.Release(); }
        }
    }
}

internal interface IAudioDeviceBackend
{
    IAsyncDisposable OpenCapture(BidiAudioStreamConfig format, int? device, int framesPerBuffer, Action<byte[]> captured, Action<Exception> failed);
    IAsyncDisposable OpenPlayback(BidiAudioStreamConfig format, int? device, int framesPerBuffer, Func<int, byte[]> playback, Action<Exception> failed);
}
