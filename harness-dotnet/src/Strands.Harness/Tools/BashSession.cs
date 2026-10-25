using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using Strands.Agents;
using Strands.Agents.Processes;
using Strands.Harness.Internal;

namespace Strands.Harness.Tools;

// One association owns admission, serialized commands and transitions; it never retains its Agent or ToolContext.
internal sealed class BashSession(BashConfig config, TimeSpan defaultTimeout, Action<BashSession> onClosed)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly HashSet<Epoch> _epochs = [];
    private Epoch _epoch = new();
    private Generation? _generation;
    private Task _transition = Task.CompletedTask;
    private Task? _close;
    private bool _closed;
    private TimeSpan _defaultTimeout = defaultTimeout;
    private WeakReference<IDisposable>? _registration;
    internal bool IsClosed { get { lock (_gate) return _closed; } }

    internal void SetRegistration(IDisposable registration)
    {
        bool closed;
        lock (_gate) { _registration = new(registration); closed = _closed; }
        if (closed) registration.Dispose();
    }

    internal async Task<(string Output, string Error)> ExecuteAsync(string command, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Epoch epoch; Task transition; TimeSpan duration;
        lock (_gate)
        {
            RefuseClosed(); epoch = _epoch; transition = _transition; duration = timeout ?? _defaultTimeout;
            if (epoch.Count++ == 0) { epoch.Drain = NewSignal(); _epochs.Add(epoch); }
        }
        var acquired = false;
        try
        {
            await TaskCancellation.WaitAsync(transition, cancellationToken).ConfigureAwait(false);
            await _commands.WaitAsync(cancellationToken).ConfigureAwait(false); acquired = true;
            Generation generation;
            lock (_gate)
            {
                RefuseClosed();
                if (!ReferenceEquals(epoch, _epoch)) throw new BashSessionException("The queued command belongs to a restarted Bash session.");
                generation = _generation ??= new(config);
            }
            if (generation.IsTerminal)
            {
                // An interpreter which died while idle must be joined before lazily replacing it.
                try { await generation.StopAsync().ConfigureAwait(false); }
                catch { lock (_gate) _closed = true; throw; }
                lock (_gate)
                {
                    RefuseClosed();
                    if (!ReferenceEquals(epoch, _epoch)) throw new BashSessionException("The queued command belongs to a restarted Bash session.");
                    if (ReferenceEquals(_generation, generation)) _generation = null;
                    generation = _generation ??= new(config);
                }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.StopToken);
            deadline.CancelAfter(duration);
            try { return await generation.RunAsync(command, deadline.Token).ConfigureAwait(false); }
            catch (Exception error)
            {
                Exception primary = error;
                if (error is OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) primary = new OperationCanceledException(error.Message, error, cancellationToken);
                    else if (generation.StopToken.IsCancellationRequested) primary = new BashSessionException("The Bash session was stopped before its command completed.", error);
                    else primary = new BashTimeoutException("The Bash command exceeded its execution deadline.", error);
                }
                else if (error is not BashSessionException) primary = new BashSessionException("The Bash interpreter or its binary transport failed.", error);
                CopyPartialDiagnostics(error, primary);
                try { await generation.StopAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { AttachCleanup(primary, cleanup); lock (_gate) _closed = true; }
                lock (_gate) if (ReferenceEquals(_generation, generation)) _generation = null;
                ExceptionDispatchInfo.Capture(primary).Throw(); throw;
            }
        }
        finally
        {
            if (acquired) _commands.Release();
            TaskCompletionSource<bool>? drained = null;
            lock (_gate) if (--epoch.Count == 0) { drained = epoch.Drain; _epochs.Remove(epoch); }
            drained?.TrySetResult(true);
        }
    }

    internal async Task RestartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task transition; TaskCompletionSource<bool>? completion = null; Generation? oldGeneration = null; Task oldDrain = Task.CompletedTask;
        lock (_gate)
        {
            RefuseClosed();
            if (!_transition.IsCompleted) transition = _transition;
            else
            {
                oldGeneration = _generation; _generation = null; oldDrain = _epoch.Drain?.Task ?? Task.CompletedTask;
                _epoch = new(); _defaultTimeout = TimeSpan.FromSeconds(120);
                completion = NewSignal(); transition = _transition = completion.Task;
            }
        }
        if (completion is not null) _ = CompleteRestartAsync(completion, oldGeneration, oldDrain);
        // An admitted restart always joins its transition; cancellation cannot abandon the old interpreter.
        await transition.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task CompleteRestartAsync(TaskCompletionSource<bool> completion, Generation? generation, Task drain)
    {
        Exception? failure = null;
        try
        {
            try { if (generation is not null) await generation.StopAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; lock (_gate) _closed = true; }
            await drain.ConfigureAwait(false);
            if (failure is null) completion.TrySetResult(true); else completion.TrySetException(failure);
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal Task CloseAsync()
    {
        TaskCompletionSource<bool> completion; Generation? generation; Task transition; Task[] drains;
        lock (_gate)
        {
            if (_close is not null) return _close;
            _closed = true; completion = NewSignal(); _close = completion.Task;
            generation = _generation; _generation = null; transition = _transition;
            drains = _epochs.Select(epoch => epoch.Drain!.Task).ToArray();
        }
        _ = CompleteCloseAsync(completion, generation, transition, drains);
        return completion.Task;
    }

    private async Task CompleteCloseAsync(TaskCompletionSource<bool> completion, Generation? generation, Task transition, Task[] drains)
    {
        Exception? primary = null;
        try
        {
            try { if (generation is not null) await generation.StopAsync().ConfigureAwait(false); }
            catch (Exception error) { primary = error; }
            try { await transition.ConfigureAwait(false); }
            catch (Exception error) { if (primary is null) primary = error; else AttachCleanup(primary, error); }
            await Task.WhenAll(drains).ConfigureAwait(false);
            _commands.Dispose();
            if (_registration?.TryGetTarget(out var registration) == true) registration.Dispose();
            if (primary is null) { onClosed(this); completion.TrySetResult(true); }
            else completion.TrySetException(primary);
        }
        catch (Exception error) { if (primary is not null) { AttachCleanup(primary, error); completion.TrySetException(primary); } else completion.TrySetException(error); }
    }

    private void RefuseClosed() { if (_closed) throw new ObjectDisposedException(nameof(BashSession), "This Agent's Bash association is closed."); }
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Epoch { internal int Count; internal TaskCompletionSource<bool>? Drain; }
    internal static void CopyPartialDiagnostics(Exception source, Exception target)
    {
        if (ReferenceEquals(source, target)) return;
        if (source.Data["Strands.Bash.StandardOutput"] is string output) target.Data["Strands.Bash.StandardOutput"] = output;
        if (source.Data["Strands.Bash.StandardError"] is string error) target.Data["Strands.Bash.StandardError"] = error;
    }
    internal static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup) || ReferenceEquals(primary.InnerException, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        var errors = primary.Data[key] as List<Exception> ?? [];
        if (errors.Any(error => ReferenceEquals(error, cleanup))) return;
        errors.Add(cleanup); primary.Data[key] = errors;
    }

    // One interpreter generation owns its binary pipes, two readers and a memoized physical stop.
    internal sealed class Generation
    {
        private readonly BashConfig policy;
        private readonly object _gate = new();
        private readonly OwnedChildProcess _process = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<bool> _startup = NewSignal(), _exited = NewSignal();
        private Task _pumps = Task.CompletedTask;
        private Task? _stop;
        private Command? _command;
        private Exception? _failure;
        private bool _started, _startAttempted;
#if NETSTANDARD2_0
        private LegacyWindowsJob? _job;
#endif
        internal Generation(BashConfig policy) { this.policy = policy; StopToken = _lifetime.Token; }
        internal CancellationToken StopToken { get; }
        private CancellationToken LifetimeToken => StopToken;
        internal bool IsTerminal
        {
            get
            {
                lock (_gate)
                {
                    if (_failure is not null || _stop is not null) return true;
                    if (!_started) return false;
                }
                // Process exit callbacks acquire this gate while holding their own process monitor.
                bool exited;
                try { exited = _process.HasExited; }
                catch (InvalidOperationException)
                {
                    lock (_gate) if (_stop is not null) return true;
                    throw;
                }
                lock (_gate) return _failure is not null || _stop is not null || exited;
            }
        }

        internal async Task<(string Output, string Error)> RunAsync(string text, CancellationToken cancellationToken)
        {
            // Session serialization is the sole caller; stop may race startup from another owner.
            bool start;
            lock (_gate)
            {
                if (_stop is not null) throw new BashSessionException("The Bash interpreter generation has stopped.");
                start = !_startAttempted;
                if (start) _startAttempted = true;
            }
            if (start) Start(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var command = new Command(policy.MaximumOutputBytes);
            lock (_gate)
            {
                if (_failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                if (_stop is not null) throw new BashSessionException("The Bash interpreter generation has stopped.");
                _command = command;
            }
            try
            {
                var quoted = "'" + text.Replace("'", "'\\''") + "'";
                var script = "builtin eval -- " + quoted + "\nbuiltin printf '\\036%s\\037' '" + command.OutputMarker + "'\nbuiltin printf '\\036%s\\037' '" + command.ErrorMarker + "' >&2\n";
                var bytes = new UTF8Encoding(false).GetBytes(script);
                var input = _process.StandardInput.BaseStream;
                await input.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                await TaskCancellation.WaitAsync(command.Done.Task, cancellationToken).ConfigureAwait(false);
                return command.Result();
            }
            catch (Exception failure) { command.RetainPartial(failure); throw; }
            finally { lock (_gate) if (ReferenceEquals(_command, command)) _command = null; }
        }

        private void Start(CancellationToken cancellationToken)
        {
            try
            {
#if NETSTANDARD2_0
                if (Path.DirectorySeparatorChar != '\\') throw new PlatformNotSupportedException("The .NET Standard Bash adapter requires Windows job objects; use a modern asset on Linux.");
                _job = new();
#endif
                var start = new ProcessStartInfo
                {
                    FileName = policy.ExecutablePath ?? "bash",
                    WorkingDirectory = policy.WorkingDirectory!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                foreach (var pair in policy.Environment) start.Environment[pair.Key] = pair.Value;
                start.Environment["PS1"] = ""; start.Environment["PS2"] = "";
                _process.StartInfo = start; _process.EnableRaisingEvents = true; _process.Exited += OnExited;
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, LifetimeToken);
                if (!_process.Start(startup.Token)) throw new BashSessionException("The Bash interpreter did not start.");
                lock (_gate) _started = true;
#if NETSTANDARD2_0
                _job.Assign(_process.BorrowedProcessHandle);
#endif
                _pumps = Task.WhenAll(PumpAsync(_process.StandardOutput.BaseStream, false), PumpAsync(_process.StandardError.BaseStream, true));
                if (_process.HasExited) OnExited(null, EventArgs.Empty);
            }
            finally { _startup.TrySetResult(true); }
        }

        private void OnExited(object? sender, EventArgs args)
        {
            _exited.TrySetResult(true);
            Fail(new BashSessionException("The Bash interpreter exited before the command completed."));
        }
        private async Task PumpAsync(Stream stream, bool error)
        {
            var bytes = new byte[4096];
            try
            {
                while (true)
                {
                    var count = await stream.ReadAsync(bytes, 0, bytes.Length, LifetimeToken).ConfigureAwait(false);
                    if (count == 0) { if (!_lifetime.IsCancellationRequested) Fail(new BashSessionException("A Bash output pipe ended unexpectedly.")); return; }
                    Command? command; lock (_gate) command = _command;
                    if (command?.Feed(bytes, count, error) is { } failure) Fail(failure);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception failure) { if (!_lifetime.IsCancellationRequested) Fail(new BashSessionException("Reading a Bash output pipe failed.", failure)); }
        }
        private void Fail(Exception failure)
        {
            Command? command;
            lock (_gate) { _failure ??= failure; failure = _failure; command = _command; }
            command?.Fail(failure);
        }

        internal Task StopAsync()
        {
            TaskCompletionSource<bool> completion;
            bool neverClaimedStartup;
            lock (_gate)
            {
                if (_stop is not null) return _stop;
                completion = NewSignal(); _stop = completion.Task;
                neverClaimedStartup = !_startAttempted;
            }
            // An unclaimed generation cannot start after stop admission. Claimed startup settles in Start's finally.
            if (neverClaimedStartup) _startup.TrySetResult(true);
            _ = CompleteStopAsync(completion);
            return completion.Task;
        }
        private async Task CompleteStopAsync(TaskCompletionSource<bool> completion)
        {
            Exception? primary = null;
            try
            {
                _lifetime.Cancel();
                await _startup.Task.ConfigureAwait(false);
                if (_started)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
#if NET8_0_OR_GREATER
                            _process.Kill(entireProcessTree: true);
#else
                            _process.Kill();
#endif
                        }
                    }
                    catch (InvalidOperationException) when (_process.HasExited) { }
#if NETSTANDARD2_0
                    catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 5 && _process.HasExited) { }
#endif
                    catch (Exception error) { primary = error; }
#if NETSTANDARD2_0
                    try { _job?.Dispose(); } catch (Exception error) { if (primary is null) primary = error; else AttachCleanup(primary, error); }
#endif
                    using var limit = new CancellationTokenSource(policy.CleanupTimeout);
                    try
                    {
                        await TaskCancellation.WaitAsync(Task.WhenAll(_pumps, _exited.Task), limit.Token).ConfigureAwait(false);
                        // Exited may observe an available exit code before the retained process handle is signaled.
                        while (!_process.WaitForExit(0))
                            await Task.Delay(10, limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException error) when (limit.IsCancellationRequested)
                    {
                        var failure = new BashSessionException("Bash process/pipe cleanup did not physically finish within CleanupTimeout.", error);
                        if (primary is null) primary = failure; else AttachCleanup(primary, failure);
                    }
                }
            }
            catch (Exception error) { if (primary is null) primary = error; else AttachCleanup(primary, error); }
            finally
            {
                try { _process.Exited -= OnExited; _process.Dispose(); }
                catch (Exception error) { if (primary is null) primary = error; else AttachCleanup(primary, error); }
#if NETSTANDARD2_0
                try { _job?.Dispose(); } catch (Exception error) { if (primary is null) primary = error; else AttachCleanup(primary, error); }
#endif
                _lifetime.Dispose();
            }
            if (primary is null) completion.TrySetResult(true); else completion.TrySetException(primary);
        }
    }

    // Byte-bounded collectors preserve UTF8 sequences split across reads and recognize independent stream fences.
    private sealed class Command(int limit) : IDisposable
    {
        private readonly object _gate = new();
        internal string OutputMarker { get; } = Guid.NewGuid().ToString("N") + ":out";
        internal string ErrorMarker { get; } = Guid.NewGuid().ToString("N") + ":err";
        internal TaskCompletionSource<bool> Done { get; } = NewSignal();
        private readonly Sink _output = new(), _error = new();
        private int _count;
        private bool _disposed;
        private Exception? _failure;
        internal void Fail(Exception error)
        {
            lock (_gate)
            {
                if (_disposed || Done.Task.IsCompleted) return;
                _failure = error; Done.TrySetResult(true);
            }
        }
        internal Exception? Feed(byte[] bytes, int count, bool error)
        {
            lock (_gate)
            {
                if (_disposed || Done.Task.IsCompleted) return null;
                var sink = error ? _error : _output; var marker = Encoding.ASCII.GetBytes("\u001e" + (error ? ErrorMarker : OutputMarker) + "\u001f");
                for (var i = 0; i < count && !sink.Finished; i++)
                {
                    sink.Tail.Add(bytes[i]);
                    while (!Prefix(sink.Tail, marker))
                    {
                        if (++_count > limit) { var failure = new BashSessionException("The Bash command exceeded MaximumOutputBytes."); _failure = failure; Done.TrySetResult(true); return failure; }
                        sink.Bytes.WriteByte(sink.Tail[0]); sink.Tail.RemoveAt(0);
                    }
                    if (sink.Tail.Count == marker.Length) { sink.Finished = true; sink.Tail.Clear(); }
                }
                if (_output.Finished && _error.Finished) Done.TrySetResult(true);
                return null;
            }
        }
        internal (string Output, string Error) Result()
        {
            lock (_gate)
            {
                if (_failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                return (Encoding.UTF8.GetString(_output.Bytes.ToArray()), Encoding.UTF8.GetString(_error.Bytes.ToArray()));
            }
        }
        internal void RetainPartial(Exception failure)
        {
            (string Output, string Error) partial;
            lock (_gate) partial = (Encoding.UTF8.GetString(_output.Bytes.ToArray()), Encoding.UTF8.GetString(_error.Bytes.ToArray()));
            failure.Data["Strands.Bash.StandardOutput"] = partial.Output;
            failure.Data["Strands.Bash.StandardError"] = partial.Error;
        }
        private static bool Prefix(List<byte> candidate, byte[] marker)
        { if (candidate.Count > marker.Length) return false; for (var i = 0; i < candidate.Count; i++) if (candidate[i] != marker[i]) return false; return true; }
        public void Dispose() { lock (_gate) { _disposed = true; _output.Bytes.Dispose(); _error.Bytes.Dispose(); } }
        private sealed class Sink { internal List<byte> Tail { get; } = []; internal MemoryStream Bytes { get; } = new(); internal bool Finished; }
    }
}
