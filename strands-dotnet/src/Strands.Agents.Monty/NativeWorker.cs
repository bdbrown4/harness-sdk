using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Process = Strands.Agents.Processes.OwnedChildProcess;

namespace Strands.Agents.Monty;

internal static class NativeWorker
{
    private const int FrameLimit = 32 * 1024 * 1024;
    internal delegate Task<JsonElement> HostFunction(JsonElement positional, JsonElement keywords, CancellationToken cancellationToken);
    internal static async Task RunAsync(string code, IReadOnlyDictionary<string, HostFunction> functions, ProgrammaticToolCallerOptions options, Output output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var worker = new Process { StartInfo = new() { FileName = ResolvePath(options.WorkerPath), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory } };
        var hostCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var slots = new SemaphoreSlim(10);
        var pending = new Dictionary<uint, Task<HostResult>>();
        var cleanupErrors = new ConcurrentQueue<Exception>();
        Task<string>? stderr = null;
        Exception? primary = null;
        var started = false;
        var deferred = false;
        CancellationTokenRegistration registration = default;
        try
        {
            if (!worker.Start(cancellationToken)) throw new IOException("The native Monty worker did not start.");
            started = true;
            stderr = DrainErrorAsync(worker.StandardError);
            registration = cancellationToken.Register(() => Kill(worker, cleanupErrors));
            options.ProcessStarted?.Invoke(worker.Id);
            var ready = await ReadAsync(worker.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
            if (ready.GetProperty("kind").GetString() != "ready" || ready.GetProperty("abi").GetInt32() != 1 || ready.GetProperty("engine").GetString() != "0.0.23")
                throw new InvalidDataException("The native worker does not implement Monty 0.0.23 ABI 1.");
            var bits = ready.GetProperty("pointer_bits").GetInt32();
            if (bits is not (32 or 64) || options.WorkerPath is null && bits != IntPtr.Size * 8) throw new InvalidDataException("The native Monty worker has the wrong process architecture.");
            options.WorkerReady?.Invoke(worker.Id, bits);
            await WriteAsync(worker.StandardInput.BaseStream, new
            {
                code,
                functions = functions.Keys.ToArray(),
                max_duration_ms = (long)Math.Ceiling(options.MaxDuration.TotalMilliseconds),
                max_memory_bytes = options.MaxMemoryBytes,
                max_suspensions = options.MaxSuspensions,
                max_recursion_depth = options.MaxRecursionDepth,
            }, cancellationToken).ConfigureAwait(false);
            var complete = false;
            while (!complete)
            {
                JsonElement message;
                try { message = await ReadAsync(worker.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false); }
                catch (EndOfStreamException error)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await WaitForExitAsync(worker, TimeSpan.FromSeconds(5)).ConfigureAwait(false) && worker.ExitCode == 65)
                        throw new MontyCodeException("MemoryError: native worker exceeded its memory budget.", false, error);
                    throw new IOException("Native Monty worker ended before a complete response (exit " + (worker.HasExited ? worker.ExitCode.ToString() : "pending") + ").", error);
                }
                var kind = message.GetProperty("kind").GetString();
                switch (kind)
                {
                    case "print": output.Write(message.GetProperty("text").GetString()!); break;
                    case "call":
                        var id = message.GetProperty("id").GetUInt32();
                        var name = message.GetProperty("name").GetString()!;
                        if (!functions.TryGetValue(name, out var function) || pending.ContainsKey(id)) throw new InvalidDataException("Native worker requested an unknown tool or reused a call identifier.");
                        var positional = message.GetProperty("args").Clone();
                        var keywords = message.GetProperty("kwargs").Clone();
                        if (positional.ValueKind != JsonValueKind.Array || keywords.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Native worker supplied malformed tool arguments.");
                        pending.Add(id, CallAsync(id, function, positional, keywords, slots, hostCancellation.Token));
                        break;
                    case "resolve":
                        var ids = message.GetProperty("ids").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
                        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length || ids.Any(value => !pending.ContainsKey(value))) throw new InvalidDataException("Native worker requested invalid pending futures.");
                        await WaitAsync(Task.WhenAny(ids.Select(value => pending[value])), cancellationToken).ConfigureAwait(false);
                        var completed = ids.Where(value => pending[value].IsCompleted).ToArray();
                        var results = new List<object>();
                        foreach (var completedId in completed)
                        {
                            var result = await pending[completedId].ConfigureAwait(false);
                            results.Add(result.Error is null ? (object)new { id = result.Id, value = result.Value } : new { id = result.Id, error = result.Error, type = result.Type });
                            pending.Remove(completedId);
                        }
                        await WriteAsync(worker.StandardInput.BaseStream, new { results }, cancellationToken).ConfigureAwait(false);
                        break;
                    case "complete":
                        if (!await WaitForExitAsync(worker, TimeSpan.FromSeconds(5)).ConfigureAwait(false)) throw new IOException("Native Monty worker did not exit after completion.");
                        if (worker.ExitCode != 0) throw new IOException("Native Monty worker failed after completion, exit " + worker.ExitCode + ".");
                        complete = true;
                        break;
                    case "error": throw new MontyCodeException(message.GetProperty("text").GetString()!, message.GetProperty("syntax").GetBoolean());
                    case "worker_error": throw new InvalidDataException("Native Monty protocol failed: " + message.GetProperty("text").GetString());
                    default: throw new InvalidDataException("Unknown native Monty event: " + kind);
                }
            }
        }
        catch (Exception error)
        {
            primary = cancellationToken.IsCancellationRequested ? new MontyCancellationException(error, cancellationToken) : error;
            if (primary is IOException && started && await WaitForExitAsync(worker, TimeSpan.FromSeconds(1)).ConfigureAwait(false) && worker.ExitCode == 65)
                primary = new MontyCodeException("MemoryError: native worker exceeded its memory budget.", false, error);
        }
        finally
        {
            registration.Dispose();
            try { hostCancellation.Cancel(); } catch (Exception error) { cleanupErrors.Enqueue(error); }
            if (started)
            {
                Kill(worker, cleanupErrors);
                try { if (!await WaitForExitAsync(worker, TimeSpan.FromSeconds(5)).ConfigureAwait(false)) cleanupErrors.Enqueue(new IOException("Native Monty worker could not be reaped.")); }
                catch (Exception error) { cleanupErrors.Enqueue(error); }
            }
            if (stderr is not null)
            {
                try
                {
                    if (await BoundedAsync(stderr, TimeSpan.FromSeconds(2)).ConfigureAwait(false)) _ = await stderr.ConfigureAwait(false);
                    else { worker.StandardError.Dispose(); cleanupErrors.Enqueue(new IOException("Native Monty error pipe did not close.")); _ = ObserveAsync(stderr); }
                }
                catch (Exception error) { cleanupErrors.Enqueue(error); }
            }
            if (pending.Count > 0)
            {
                var calls = Task.WhenAll(pending.Values);
                if (!await BoundedAsync(calls, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                {
                    primary ??= new TimeoutException("An unawaited host tool did not stop within the cleanup deadline.");
                    primary.Data["Strands.CleanupTask"] = ObserveHostCleanupAsync(calls, slots, hostCancellation);
                    deferred = true;
                }
                else await calls.ConfigureAwait(false); // Host tasks always return a result, including errors.
            }
            if (!cleanupErrors.IsEmpty)
            {
                primary ??= new IOException("Native Monty cleanup failed.");
                primary.Data["Strands.CleanupErrors"] = cleanupErrors.ToList();
            }
            if (!deferred) { slots.Dispose(); hostCancellation.Dispose(); }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static async Task<HostResult> CallAsync(uint id, HostFunction function, JsonElement positional, JsonElement keywords, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        try
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = await Task.Run(() => function(positional, keywords, cancellationToken), cancellationToken).ConfigureAwait(false);
                return new(id, value.Clone(), null, null);
            }
            finally { slots.Release(); }
        }
        catch (Exception error) { return new(id, default, error.Message, error is MontyTypeException ? "TypeError" : "RuntimeError"); }
    }
    private sealed record HostResult(uint Id, JsonElement Value, string? Error, string? Type);
    private static void Kill(Process process, ConcurrentQueue<Exception> errors)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { /* Exited between the observation and termination. */ }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { /* Windows can report access denied after natural process exit. */ }
        catch (Exception error) { errors.Enqueue(error); }
    }
    private static Task<bool> WaitForExitAsync(Process process, TimeSpan timeout) => Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds));
    private static async Task<bool> BoundedAsync(Task task, TimeSpan timeout) => ReferenceEquals(await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false), task);
    private static async Task WaitAsync(Task task, CancellationToken token)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => cancelled.TrySetResult(true));
        if (!ReferenceEquals(await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false), task)) token.ThrowIfCancellationRequested();
        await task.ConfigureAwait(false);
    }
    private static async Task<IReadOnlyList<Exception>> ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); return Array.Empty<Exception>(); }
        catch (Exception error) { return new[] { error }; }
    }
    private static async Task<IReadOnlyList<Exception>> ObserveHostCleanupAsync(Task task, SemaphoreSlim slots, CancellationTokenSource cancellation)
    {
        var errors = (await ObserveAsync(task).ConfigureAwait(false)).ToList();
        try { slots.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { cancellation.Dispose(); } catch (Exception error) { errors.Add(error); }
        return errors.AsReadOnly();
    }
    private static async Task<string> DrainErrorAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        while (true) { var count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false); if (count == 0) break; var keep = Math.Min(count, 8192 - text.Length); if (keep > 0) text.Append(buffer, 0, keep); }
        return text.ToString();
    }
    private static async Task WriteAsync(Stream stream, object value, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length == 0 || data.Length > FrameLimit) throw new InvalidDataException("Monty frame exceeds the 32 MiB transport limit.");
        var size = BitConverter.GetBytes(data.Length);
        if (!BitConverter.IsLittleEndian) Array.Reverse(size);
        await stream.WriteAsync(size, 0, size.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    private static async Task<JsonElement> ReadAsync(Stream stream, CancellationToken token)
    {
        var size = new byte[4];
        await ReadExactlyAsync(stream, size, token).ConfigureAwait(false);
        if (!BitConverter.IsLittleEndian) Array.Reverse(size);
        var count = BitConverter.ToInt32(size, 0);
        if (count <= 0 || count > FrameLimit) throw new InvalidDataException("Native Monty frame has an invalid length.");
        var data = new byte[count];
        await ReadExactlyAsync(stream, data, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(data);
        return document.RootElement.Clone();
    }
    private static async Task ReadExactlyAsync(Stream stream, byte[] data, CancellationToken token)
    {
        for (var offset = 0; offset < data.Length;)
        {
            var count = await stream.ReadAsync(data, offset, data.Length - offset, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
    }
    private static string ResolvePath(string? explicitPath)
    {
        if (explicitPath is not null) return Path.GetFullPath(explicitPath);
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var architecture = RuntimeInformation.ProcessArchitecture;
        var rid = windows && architecture == Architecture.X86 ? "win-x86" : windows && architecture == Architecture.X64 ? "win-x64" : mac && architecture == Architecture.Arm64 ? "osx-arm64" : mac && architecture == Architecture.X64 ? "osx-x64" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && architecture == Architecture.X64 ? "linux-x64" : throw new PlatformNotSupportedException("No Monty worker asset exists for this platform and architecture.");
        var filename = "strands_monty_worker" + (windows ? ".exe" : string.Empty);
        var assemblyDirectory = Path.GetDirectoryName(typeof(NativeWorker).Assembly.Location) ?? AppContext.BaseDirectory;
        var roots = new[] { assemblyDirectory, AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "bin") };
        return roots.SelectMany(root => new[] { Path.Combine(root, "runtimes", rid, "native", filename), Path.Combine(root, filename) }).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Monty native worker is missing for " + rid + ". Restore the complete Strands.Agents.Monty package; no host interpreter is substituted.");
    }
}

internal sealed class MontyCodeException(string message, bool syntax, Exception? cause = null) : Exception(message, cause) { internal bool Syntax { get; } = syntax; }
internal sealed class MontyTypeException(string message) : Exception(message);
internal sealed class MontyCancellationException(Exception cause, CancellationToken token) : OperationCanceledException("Monty execution was cancelled.", cause, token)
{
    private readonly IDictionary _data = new Hashtable();
    public override IDictionary Data => _data;
}
