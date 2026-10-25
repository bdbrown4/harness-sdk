using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Process = Strands.Agents.Processes.OwnedChildProcess;

namespace Strands.Agents.Sandbox;

internal static class SandboxProcess
{
    internal static IAsyncEnumerable<SandboxStreamEvent> Execute(string executable, IReadOnlyList<string> arguments, ExecuteOptions? options, SandboxProcessOptions policy, CancellationToken cancellationToken, string? localWorkingDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        SandboxPolicy.Duration(options.Timeout, nameof(options.Timeout));
        if (options.MaximumOutputBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        var snapshot = options with { Environment = SandboxPolicy.Environment(options.Environment, posix: true), StandardInput = options.StandardInput is null ? null : (byte[])options.StandardInput.Clone() };
        return Stream(executable, arguments.ToArray(), snapshot, policy, cancellationToken, localWorkingDirectory);
    }

    private static async IAsyncEnumerable<SandboxStreamEvent> Stream(string executable, IReadOnlyList<string> arguments, ExecuteOptions options, SandboxProcessOptions policy, [EnumeratorCancellation] CancellationToken cancellationToken, string? localWorkingDirectory)
    {
        using var stop = new CancellationTokenSource();
        using var queue = new EventQueue();
        var operation = PublishAsync();
        var deliveredFailure = false;
        try
        {
            while (true)
            {
                var next = await queue.ReadAsync().ConfigureAwait(false);
                if (next.Error is { } error) { deliveredFailure = true; ExceptionDispatchInfo.Capture(error).Throw(); }
                if (next.Item is null) yield break;
                yield return next.Item;
            }
        }
        finally
        {
            stop.Cancel();
            var error = await operation.ConfigureAwait(false);
            var stoppedNormally = error is OperationCanceledException && !cancellationToken.IsCancellationRequested && error.Data["Strands.CleanupErrors"] is null;
            if (!deliveredFailure && error is not null && !stoppedNormally) ExceptionDispatchInfo.Capture(error).Throw();
        }

        async Task<Exception?> PublishAsync()
        {
            Exception? error = null;
            try
            {
                var outcome = await RunAsync(executable, arguments, options, policy, queue, stop.Token, cancellationToken, localWorkingDirectory).ConfigureAwait(false);
                error = outcome.Error;
                if (error is null) queue.Write(outcome.Result);
            }
            catch (Exception exception) { error = exception; }
            finally { queue.Complete(error); }
            return error;
        }
    }

    private static async Task<ProcessOutcome> RunAsync(string executable, IReadOnlyList<string> arguments, ExecuteOptions options, SandboxProcessOptions policy, EventQueue queue, CancellationToken stopped, CancellationToken caller, string? localWorkingDirectory)
    {
        var timeout = options.Timeout;
        if (policy.MaximumDuration is { } maximum && (timeout is null || timeout > maximum)) timeout = maximum;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, stopped);
        if (timeout is { } deadline) lifetime.CancelAfter(deadline);
        var capture = new Capture(Math.Min(options.MaximumOutputBytes ?? policy.MaximumOutputBytes, policy.MaximumOutputBytes));
        var cleanup = new List<Exception>();
        var process = new Process { StartInfo = StartInfo(executable, arguments, policy, localWorkingDirectory), EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onExit = (_, _) => CompleteExit(process, exited);
        process.Exited += onExit;
        Task pumps = Task.CompletedTask;
        Exception? failure = null;
        var started = false;
        var terminationSent = 0;
        var deferred = false;
        var deadlineCancelled = false;
        var code = -1;
        using var registration = lifetime.Token.Register(() => { if (started) Terminate(); });
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (!process.Start(lifetime.Token)) throw new IOException("The sandbox client process did not start.");
            started = true;
            if (lifetime.IsCancellationRequested) Terminate();
            pumps = Task.WhenAll(PumpAsync(process.StandardOutput.BaseStream, StreamType.Stdout), PumpAsync(process.StandardError.BaseStream, StreamType.Stderr), InputAsync());
            if (process.HasExited) CompleteExit(process, exited);
            var first = await WaitAsync(Task.WhenAny(exited.Task, pumps), lifetime.Token).ConfigureAwait(false);
            if (first == pumps) await pumps.ConfigureAwait(false);
            code = await WaitAsync(exited.Task, lifetime.Token).ConfigureAwait(false);
            if (await Task.WhenAny(pumps, Task.Delay(policy.CleanupTimeout)).ConfigureAwait(false) != pumps)
                throw new SandboxExecutionException("The sandbox client exited while an output pipe remained open beyond CleanupTimeout.");
            await pumps.ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { failure = error; deadlineCancelled = lifetime.IsCancellationRequested; }
        finally
        {
            if (started)
            {
                Terminate();
                lifetime.Cancel();
                if (!pumps.IsCompleted)
                {
                    Attempt(process.StandardInput.Dispose, cleanup);
                    Attempt(process.StandardOutput.Dispose, cleanup);
                    Attempt(process.StandardError.Dispose, cleanup);
                }
                var remaining = Task.WhenAll(exited.Task, pumps);
                if (await Task.WhenAny(remaining, Task.Delay(policy.CleanupTimeout)).ConfigureAwait(false) == remaining)
                {
                    try { await remaining.ConfigureAwait(false); }
                    catch (Exception error) when (failure is not null)
                    {
                        if (error is not (OperationCanceledException or IOException or ObjectDisposedException)) cleanup.Add(error);
                    }
                }
                else
                {
                    failure ??= new SandboxExecutionException("The sandbox client did not finish cleanup within CleanupTimeout.");
                    if (failure is not SandboxExecutionException) failure = new SandboxExecutionException(failure.Message, failure);
                    deferred = true;
                    failure.Data["Strands.CleanupTask"] = FinishLaterAsync(remaining);
                }
            }
            if (!deferred) Release();
        }
        registration.Dispose();
        var result = capture.Result(code);
        if (caller.IsCancellationRequested) failure = Cancellation(new SandboxAbortException(caller, result, failure), failure);
        else if (!stopped.IsCancellationRequested && deadlineCancelled && timeout is { } expired) failure = Cancellation(new SandboxTimeoutException(expired, result with { ExitCode = 124 }, failure), failure);
        foreach (var error in cleanup) { failure ??= error; Attach(failure, error); }
        return new(result, failure);

        async Task PumpAsync(Stream input, StreamType type)
        {
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            var bytes = new byte[8192]; var characters = new char[8192];
            try
            {
                while (true)
                {
                    var count = await input.ReadAsync(bytes, 0, bytes.Length, lifetime.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    var accepted = capture.Admit(count);
                    if (accepted > 0) Emit(decoder.GetChars(bytes, 0, accepted, characters, 0, flush: false));
                }
                if (!capture.Truncated) Emit(decoder.GetChars(Array.Empty<byte>(), 0, 0, characters, 0, flush: true));
            }
            catch (DecoderFallbackException error) { throw new InvalidDataException("The sandbox client returned invalid UTF-8. Use binary filesystem operations for byte content.", error); }
            void Emit(int count)
            {
                if (count == 0) return;
                var text = new string(characters, 0, count);
                capture.Append(text, type); queue.Write(new StreamChunk(text, type));
            }
        }

        async Task InputAsync()
        {
            try
            {
                if (options.StandardInput is { } input) await process.StandardInput.BaseStream.WriteAsync(input, 0, input.Length, lifetime.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
            finally { process.StandardInput.Close(); }
        }
        void Terminate() { if (Interlocked.Exchange(ref terminationSent, 1) == 0) Kill(process, cleanup); }
        void Release() { process.Exited -= onExit; Attempt(process.Dispose, cleanup); }
        async Task<IReadOnlyList<Exception>> FinishLaterAsync(Task remaining)
        {
            var errors = new List<Exception>();
            try { await remaining.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            process.Exited -= onExit;
            Attempt(process.Dispose, errors);
            return errors.AsReadOnly();
        }
    }

    private static Exception Cancellation(Exception replacement, Exception? cause)
    {
        if (cause?.Data["Strands.CleanupTask"] is { } task) replacement.Data["Strands.CleanupTask"] = task;
        return replacement;
    }

    private static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> arguments, SandboxProcessOptions policy, string? localWorkingDirectory)
    {
        var start = new ProcessStartInfo(policy.ExecutablePath ?? executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (localWorkingDirectory is not null) start.WorkingDirectory = localWorkingDirectory;
        foreach (var argument in arguments) if (argument.IndexOf('\0') >= 0) throw new ArgumentException("Client arguments cannot contain NUL.", nameof(arguments));
#if NET8_0_OR_GREATER
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
#else
        if (Path.DirectorySeparatorChar != '\\') throw new PlatformNotSupportedException("Legacy native client execution requires Windows. Use a modern .NET asset for Unix clients.");
        start.Arguments = string.Join(" ", arguments.Select(WindowsArgument));
        if (start.FileName.Length + start.Arguments.Length + 3 > 32767) throw new ArgumentException("The native client arguments exceed the Windows command-line limit.");
#endif
        foreach (var item in policy.Environment) start.EnvironmentVariables[item.Key] = item.Value;
        return start;
    }

#if NETSTANDARD2_0
    private static string WindowsArgument(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
#endif
    private static void CompleteExit(Process process, TaskCompletionSource<int> completion) { try { completion.TrySetResult(process.ExitCode); } catch (Exception error) { completion.TrySetException(error); } }
    private static void Kill(Process process, List<Exception> errors)
    {
        try
        {
            if (process.HasExited) return;
#if NET8_0_OR_GREATER
            process.Kill(entireProcessTree: true);
#else
            process.Kill();
#endif
        }
        catch (InvalidOperationException) { }
        catch (Exception error) { lock (errors) errors.Add(error); }
    }
    private static void Attempt(Action action, List<Exception> errors) { try { action(); } catch (Exception error) { lock (errors) errors.Add(error); } }
    private static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken) { await TaskCancellation.WaitAsync(task, cancellationToken).ConfigureAwait(false); return await task.ConfigureAwait(false); }
    private static void Attach(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = new();
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
    }

    private sealed class Capture(int maximum)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new(), _error = new();
        private int _accepted;
        private bool _truncated;
        internal bool Truncated { get { lock (_gate) return _truncated; } }
        internal int Admit(int count) { lock (_gate) { var accepted = Math.Min(count, maximum - _accepted); _accepted += accepted; _truncated |= accepted < count; return accepted; } }
        internal void Append(string text, StreamType type) { lock (_gate) (type == StreamType.Stdout ? _output : _error).Append(text); }
        internal ExecutionResult Result(int code) { lock (_gate) return new(code, _output.ToString(), _error.ToString()) { OutputTruncated = _truncated }; }
    }

    private sealed record ProcessOutcome(ExecutionResult Result, Exception? Error);
    internal sealed record QueueResult(SandboxStreamEvent? Item, Exception? Error);

    internal sealed class EventQueue : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<SandboxStreamEvent> _items = new();
        private readonly SemaphoreSlim _available = new(0);
        private bool _completed;
        private Exception? _error;
        internal void Write(SandboxStreamEvent item) { lock (_gate) _items.Enqueue(item); _available.Release(); }
        internal void Complete(Exception? error) { lock (_gate) { _completed = true; _error = error; } _available.Release(); }
        internal async ValueTask<QueueResult> ReadAsync()
        {
            await _available.WaitAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_items.Count > 0) return new(_items.Dequeue(), null);
                if (_error is not null) return new(null, _error);
                return _completed ? new(null, null) : throw new InvalidOperationException("Sandbox event queue lost an update.");
            }
        }
        public void Dispose() => _available.Dispose();
    }
}
