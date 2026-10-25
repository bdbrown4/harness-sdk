using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Strands.Agents;
using Strands.Agents.Bidi;
using Xunit.Abstractions;

namespace Strands.Bidi.Audio.Tests;

public sealed class AudioDeviceFeatureTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CapturedPcmUsesResolvedFormatsAndDetachedBytesThroughTheRealRun()
    {
        var backend = new Device();
        await using var host = Host(); await using var model = new Model();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        await using var audio = new BidiAudioIO(model, Options() with { InputDeviceIndex = 3, OutputDeviceIndex = 5, FramesPerBuffer = 4 }, backend);
        var run = agent.RunAsync([audio.Input], [audio.Output]);
        try
        {
            await SignalOrRun(backend.Opened.Task, run);
            var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            backend.Capture!(bytes); Array.Clear(bytes, 0, bytes.Length);
            var input = Assert.IsType<BidiAudioDelta>(await Within(model.Sent.Task));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, input.GetBytes());
            Assert.Equal(model.Audio.Input, backend.CaptureFormat); Assert.Equal(model.Audio.Output, backend.PlaybackFormat);
            Assert.Equal(3, backend.InputDevice); Assert.Equal(5, backend.OutputDevice); Assert.Equal(4, backend.Frames);
            await agent.StopAsync(); await Within(Completion(run));
            Assert.Equal(1, backend.CaptureStream.Closes); Assert.Equal(1, backend.PlaybackStream.Closes);
            Assert.True(model.ReaderExited.Task.IsCompleted); Assert.Equal(0, model.Disposals); Assert.Empty(host.Messages);
        }
        finally { backend.Release(); var stop = agent.StopAsync(); await Join(run); await Join(stop); }
    }

    [Fact]
    public async Task PlaybackPreservesPcmZeroFillsUnderflowAndClearsOnBargeIn()
    {
        var backend = new Device(); var consoleEvents = new List<IAgentStreamEvent>();
        await using var host = Host(); await using var model = new Model();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        await using var audio = new BidiAudioIO(model, Options() with { ConsoleOutput = new BidiOutputStream((value, _) => { consoleEvents.Add(value); return default; }) }, backend);
        await audio.Output.StartAsync(agent);
        try
        {
            var pcm = new byte[] { 1, 2, 3, 4, 5, 6 };
            await audio.Output.WriteAsync(new BidiAudioDeltaEvent(Convert.ToBase64String(pcm), "audio", 24000, 1, "pcm"));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 0, 0 }, backend.Playback!(4));
            Assert.Equal(new byte[4], backend.Playback(2));
            await audio.Output.WriteAsync(new BidiAudioDeltaEvent(Convert.ToBase64String(pcm), "audio", 24000, 1, "pcm"));
            var bargeIn = new BidiBargeInEvent(); await audio.Output.WriteAsync(bargeIn);
            Assert.Equal(new byte[6], backend.Playback(3)); Assert.Contains(bargeIn, consoleEvents);
            await Assert.ThrowsAsync<ArgumentException>(() => audio.Output.WriteAsync(new BidiAudioDeltaEvent("AQI=", "bad", 16000, 1, "pcm")).AsTask());
        }
        finally { await audio.Output.StopAsync(); }
        Assert.Equal(1, backend.PlaybackStream.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndCallerCancellationJoinBothNativeStreamsAndTheIdleRead(bool callerCancellation)
    {
        var backend = new Device { BlockClose = true }; using var cancellation = new CancellationTokenSource();
        await using var host = Host(); await using var model = new Model();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        await using var audio = new BidiAudioIO(model, Options(), backend);
        var run = agent.RunAsync([audio.Input], [audio.Output], cancellationToken: cancellation.Token); Task? stopping = null;
        try
        {
            await SignalOrRun(backend.Opened.Task, run);
            if (callerCancellation) cancellation.Cancel(); else stopping = agent.StopAsync();
            await SignalOrRun(backend.PlaybackStream.Closing.Task, run);
            Assert.False(run.IsCompleted); if (stopping is not null) Assert.False(stopping.IsCompleted);
            backend.PlaybackStream.Release.TrySetResult(true);
            await SignalOrRun(backend.CaptureStream.Closing.Task, run);
            Assert.False(run.IsCompleted);
            backend.CaptureStream.Release.TrySetResult(true);
            if (callerCancellation)
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else { await Within(Completion(run)); await Within(Completion(stopping!)); }
            Assert.Equal(1, backend.CaptureStream.Closes); Assert.Equal(1, backend.PlaybackStream.Closes);
            Assert.True(model.ReaderExited.Task.IsCompleted); Assert.Equal(0, model.Disposals);
        }
        finally { backend.Release(); cancellation.Cancel(); var stop = agent.StopAsync(); await Join(run); await Join(stopping); await Join(stop); }
    }

    [Fact]
    public async Task PartialDeviceStartupRetainsThePlaybackFailureAndCaptureCleanupFailure()
    {
        var failure = new IOException("playback open"); var cleanup = new IOException("capture close");
        var backend = new Device { OpenPlaybackError = failure }; backend.CaptureStream.CloseError = cleanup;
        await using var host = Host(); await using var model = new Model();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        await using var audio = new BidiAudioIO(model, Options(), backend);

        var error = await Assert.ThrowsAsync<IOException>(() => agent.RunAsync([audio.Input], [audio.Output]));

        Assert.Same(failure, error);
        Assert.Contains(cleanup, Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, backend.CaptureStream.Closes); Assert.Equal(0, backend.PlaybackStream.Closes);
        Assert.True(model.ReaderExited.Task.IsCompleted); Assert.Equal(0, model.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceCallbackFailureEndsTheIdleRunAndPhysicallyClosesBothStreams(bool playbackFailure)
    {
        var backend = new Device(); var failure = new IOException("native callback failed");
        await using var host = Host(); await using var model = new Model();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        await using var audio = new BidiAudioIO(model, Options(), backend);
        var run = agent.RunAsync([audio.Input], [audio.Output]);
        try
        {
            await SignalOrRun(backend.Opened.Task, run);
            (playbackFailure ? backend.PlaybackFailed : backend.CaptureFailed)!(failure);
            var error = await Assert.ThrowsAsync<IOException>(() => Within(Completion(run)));
            Assert.Same(failure, error); Assert.True(model.ReaderExited.Task.IsCompleted);
            Assert.Equal(1, backend.CaptureStream.Closes); Assert.Equal(1, backend.PlaybackStream.Closes);
        }
        finally { backend.Release(); var stop = agent.StopAsync(); await Join(run); await Join(stop); }
    }

    [Fact]
    public async Task PlaybackOverflowFailsExplicitlyWithoutDiscardingAlreadyQueuedSpeech()
    {
        var backend = new Device();
        await using var host = Host(); await using var model = new Model();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        await using var audio = new BidiAudioIO(model, Options() with { OutputBufferBytes = 4 }, backend);
        await audio.Output.StartAsync(agent);
        try
        {
            await audio.Output.WriteAsync(new BidiAudioDeltaEvent("AQIDBA==", "first", 24000, 1, "pcm"));
            await Assert.ThrowsAsync<IOException>(() => audio.Output.WriteAsync(new BidiAudioDeltaEvent("BQY=", "overflow", 24000, 1, "pcm")).AsTask());
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, backend.Playback!(2));
            await audio.Output.WriteAsync(new BidiAudioDeltaEvent("AQIDBA==", "fresh", 24000, 1, "pcm"));
            await audio.Output.WriteAsync(new BidiBargeInEvent()); Assert.Equal(new byte[4], backend.Playback(2));
        }
        finally { await audio.Output.StopAsync(); }
    }

    [Fact]
    public void NativeBackendExportsARealVersionWithoutOpeningAnAudioDevice()
    {
        var version = PortAudioDeviceBackend.Version;
        output.WriteLine($"Native version: {version}; PointerBytes: {IntPtr.Size}; Runtime: {RuntimeInformation.FrameworkDescription}; AudioAsset: {typeof(BidiAudioIO).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName}");
        Assert.Contains("PortAudio", version);
    }

    private static BidiAudioOptions Options() => new() { ConsoleOutput = new BidiOutputStream((_, _) => default) };
    private static Agent Host() => new(new() { Model = new NoCallsModel() });
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Within<T>(Task<T> task)
    {
        using var watchdog = new CancellationTokenSource();
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), watchdog.Token)); watchdog.Cancel();
        Assert.Same(task, winner); return await task;
    }
    private static async Task SignalOrRun(Task signal, Task run)
    {
        var first = await Within(Task.WhenAny(signal, run));
        if (ReferenceEquals(first, run)) { await run; Assert.True(signal.IsCompleted, "Run ended before device admission."); }
        await Within(Completion(signal));
    }
    private static async Task<bool> Completion(Task task) { await task; return true; }
    private static async Task Join(Task? task) { if (task is not null) try { await task; } catch { } }

    private sealed class Device : IAudioDeviceBackend
    {
        internal Action<byte[]>? Capture; internal Func<int, byte[]>? Playback;
        internal Action<Exception>? CaptureFailed, PlaybackFailed;
        internal BidiAudioStreamConfig? CaptureFormat; internal BidiAudioStreamConfig? PlaybackFormat;
        internal int? InputDevice; internal int? OutputDevice; internal int Frames;
        internal readonly Stream CaptureStream = new(); internal readonly Stream PlaybackStream = new();
        internal TaskCompletionSource<bool> Opened { get; } = Signal();
        internal bool BlockClose; internal Exception? OpenPlaybackError;
        public IAsyncDisposable OpenCapture(BidiAudioStreamConfig format, int? device, int framesPerBuffer, Action<byte[]> captured, Action<Exception> failed)
        {
            CaptureFormat = format; InputDevice = device; Frames = framesPerBuffer; Capture = captured; CaptureFailed = failed;
            CaptureStream.BlockClose = BlockClose; return CaptureStream;
        }
        public IAsyncDisposable OpenPlayback(BidiAudioStreamConfig format, int? device, int framesPerBuffer, Func<int, byte[]> playback, Action<Exception> failed)
        {
            if (OpenPlaybackError is { } error) throw error;
            PlaybackFormat = format; OutputDevice = device; Playback = playback; PlaybackFailed = failed; PlaybackStream.BlockClose = BlockClose;
            Opened.TrySetResult(true); return PlaybackStream;
        }
        internal void Release() { CaptureStream.Release.TrySetResult(true); PlaybackStream.Release.TrySetResult(true); }
    }
    private sealed class Stream : IAsyncDisposable
    {
        internal int Closes; internal bool BlockClose; internal Exception? CloseError;
        internal TaskCompletionSource<bool> Closing { get; } = Signal();
        internal TaskCompletionSource<bool> Release { get; } = Signal();
        public async ValueTask DisposeAsync()
        {
            Closes++; Closing.TrySetResult(true); if (BlockClose) await Release.Task;
            if (CloseError is { } error) { CloseError = null; throw error; }
        }
    }
    private sealed class NoCallsModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield return new ModelMessageStartEvent(); throw new InvalidOperationException("No chat call is expected."); }
    }
    private sealed class Model : IBidiModel, IAudioBidiModel
    {
        internal TaskCompletionSource<IBidiInput> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReaderExited { get; } = Signal();
        internal int Disposals;
        public BidiAudioConfig Audio => new(new(16000, 1, "pcm"), new(24000, 1, "pcm"));
        public BidiConnectionOptions Connection => new() { AutoRestart = false };
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Sent.TrySetResult(input); return Task.CompletedTask; }
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try { yield return new BidiConnectionStartEvent("audio", "fixture"); await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { ReaderExited.TrySetResult(true); }
        }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
