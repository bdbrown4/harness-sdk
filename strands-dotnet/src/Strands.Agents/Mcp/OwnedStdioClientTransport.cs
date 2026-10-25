using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Process = Strands.Agents.Processes.OwnedChildProcess;

namespace Strands.Agents.Mcp;

// Owns native process lifetime; JSON-RPC framing and dispatch remain in the official client.
internal sealed class OwnedStdioClientTransport : IClientTransport, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly StdioClientTransportOptions _options;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private WindowsStdioJob? _job;
    private ITransport? _session;
    private Task _stderrPump = Task.CompletedTask;
    private Task? _disposal;
    private bool _connected, _closing;
    private int? _pid, _exitCode;
    private readonly string _releaseName = "__STRANDS_MCP_RELEASE_" + Guid.NewGuid().ToString("N");
    private static bool Windows => Path.DirectorySeparatorChar == '\\';

    internal OwnedStdioClientTransport(StdioClientTransportOptions options)
    {
        Guard.NotNull(options, nameof(options)); Guard.NotNullOrWhiteSpace(options.Command, nameof(options.Command));
        if (options.ShutdownTimeout < TimeSpan.Zero || options.ShutdownTimeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(options), "ShutdownTimeout must be nonnegative and bounded by Int32.MaxValue milliseconds.");
        var arguments = options.Arguments?.ToArray() ?? Array.Empty<string>();
        foreach (var value in new[] { options.Command }.Concat(arguments))
        {
            if (value is null || value.IndexOf('\0') >= 0) throw new ArgumentException("MCP command and arguments cannot contain null or NUL.", nameof(options));
            if (Windows && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)) throw new ArgumentException("Windows MCP command and arguments cannot contain CR or LF.", nameof(options));
        }
        var extension = Path.GetExtension(options.Command);
        if (Windows && arguments.Any(value => value.IndexOf('"') >= 0) &&
            !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Literal quote arguments require an explicit .exe or .com command. CMD batch forwarding and extensionless PATHEXT lookup cannot guarantee literal quote preservation.", nameof(options));
        var environment = options.EnvironmentVariables?.ToDictionary(pair => pair.Key, pair => pair.Value, Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _options = new()
        {
            Command = options.Command,
            Arguments = arguments,
            Name = options.Name,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
            InheritEnvironmentVariables = options.InheritEnvironmentVariables,
            EnvironmentVariables = environment,
            ShutdownTimeout = options.ShutdownTimeout,
            StandardErrorLines = options.StandardErrorLines
        };
        Name = options.Name ?? "stdio-" + Regex.Replace(Path.GetFileName(options.Command), @"[\s.]+", "-");
#if NETSTANDARD2_0
        if (!Windows) throw new PlatformNotSupportedException("Owned legacy MCP process execution requires Windows. Use a modern .NET asset for Unix stdio.");
#endif
    }
    public string Name { get; }
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposal is not null) throw new ObjectDisposedException(nameof(OwnedStdioClientTransport));
            if (_connected) throw new InvalidOperationException("An owned MCP transport creates one process connection.");
            _connected = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = StartInfo(), EnableRaisingEvents = true };
        process.Exited += (_, _) => { try { _exitCode = process.ExitCode; _exited.TrySetResult(process.ExitCode); } catch (Exception error) { _exited.TrySetException(error); } };
        _process = process;
        try
        {
            if (Windows) _job = new();
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start(cancellationToken)) throw new IOException("The MCP server process did not start.");
            _pid = process.Id;
            _stderrPump = Task.Run(() => DrainErrorsAsync(process.StandardError));
            if (Windows)
            {
                _job!.Assign(process.BorrowedProcessHandle);
                cancellationToken.ThrowIfCancellationRequested();
                var release = Encoding.ASCII.GetBytes("1\r\n");
                await process.StandardInput.BaseStream.WriteAsync(release, 0, release.Length, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await ReadReleaseAcknowledgementAsync(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
            }
            _session = await new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream).ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new Session(this, _session);
        }
        catch (Exception error)
        {
            try { await DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { Attach(error, cleanup); }
            ExceptionDispatchInfo.Capture(error).Throw(); throw;
        }
    }
    private ProcessStartInfo StartInfo()
    {
        string? windowsCommand = null;
        var start = new ProcessStartInfo(Windows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : _options.Command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = _options.WorkingDirectory
        };
        if (Windows)
        {
            windowsCommand = EscapeCommand(string.Join(" ", new[] { _options.Command }.Concat(_options.Arguments!).Select(Quote)));
            // set /p returns failure on EOF: no server command is released unless the job was assigned.
            // Clear the private variable before executing any application command.
            start.Arguments = "/d /v:off /c set /p " + _releaseName + "=&&set " + _releaseName + "=&&echo " + _releaseName + "&&%" + _releaseName + "%";
            if (windowsCommand.Length + start.Arguments.Length > 8191) throw new ArgumentException("MCP command exceeds CMD's 8191-character expanded command-line limit.");
        }
#if NET8_0_OR_GREATER
        else foreach (var argument in _options.Arguments!) start.ArgumentList.Add(argument);
        start.StandardInputEncoding = new UTF8Encoding(false);
#endif
        if (!_options.InheritEnvironmentVariables) start.EnvironmentVariables.Clear();
        if (_options.EnvironmentVariables is { } environment) foreach (var pair in environment) start.EnvironmentVariables[pair.Key] = pair.Value;
        if (windowsCommand is not null)
        {
            // A variable is expanded once: percent characters inside its value are not recursively expanded.
            // The fixed script clears this same private value before releasing application execution.
            start.EnvironmentVariables[_releaseName] = windowsCommand;
        }
        return start;
    }
    private static string EscapeCommand(string value)
    {
        var result = new StringBuilder();
        foreach (var character in value)
        {
            if (character is '^' or '&' or '<' or '>' or '|' or '(' or ')' or '"' or '%' or '!') result.Append('^');
            result.Append(character);
        }
        return result.ToString();
    }
    private async Task ReadReleaseAcknowledgementAsync(Stream output, CancellationToken cancellationToken)
    {
        // CMD's set /p can read ahead: no MCP bytes may be sent until it acknowledges release.
        // Read one byte at a time so a buffered reader cannot consume application protocol output.
        var expected = Encoding.ASCII.GetBytes(_releaseName + "\r\n");
        var actual = new byte[1];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            foreach (var value in expected)
            {
                var reading = output.ReadAsync(actual, 0, 1, deadline.Token);
                try { await TaskCancellation.WaitAsync(reading, deadline.Token).ConfigureAwait(false); }
                catch { _ = ObserveReadAsync(reading); throw; }
                if (await reading.ConfigureAwait(false) != 1 || actual[0] != value) throw new IOException("The owned MCP process gate did not acknowledge release.");
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("The owned MCP process gate was cancelled.", error, cancellationToken); }
        catch (OperationCanceledException error) { throw new TimeoutException("The owned MCP process gate did not acknowledge release within one minute.", error); }
    }
    private static async Task ObserveReadAsync(Task reading)
    {
        try { await reading.ConfigureAwait(false); }
        catch { /* The startup owner closes the process and observes a read abandoned after cancellation. */ }
    }
    private static string Quote(string value)
    {
        // Quotes survive a batch file's %* forwarding; single-use caret escaping does not.
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character is '"' or '&' or '^' or '>' or '<' or '|')) return value;
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private async Task DrainErrorsAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (_gate) { if (_stderr.Count == 10) _stderr.Dequeue(); _stderr.Enqueue(line); }
            try { _options.StandardErrorLines?.Invoke(line); } catch { /* A borrowed diagnostic callback cannot terminate the transport. */ }
        }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; _closing = true;
        }
        _ = CloseAsync(completion); return new(completion.Task);
    }
    private async Task CloseAsync(TaskCompletionSource<bool> completion)
    {
        var errors = new List<Exception>();
        var process = _process;
        if (process is null) { _job?.Dispose(); completion.TrySetResult(true); return; }
        try
        {
            try { process.StandardInput.BaseStream.Dispose(); } catch (InvalidOperationException) { } catch (Exception error) { errors.Add(error); }
            if (_pid is not null && !_exited.Task.IsCompleted && _options.ShutdownTimeout > TimeSpan.Zero)
                await Task.WhenAny(_exited.Task, Task.Delay(_options.ShutdownTimeout)).ConfigureAwait(false);
            var terminate = _job is not null ? _job.TerminateAndWaitAsync() : TerminateUnixAsync(process);
            var cleanup = FinishAsync(terminate, errors);
            var budget = _options.ShutdownTimeout == TimeSpan.Zero ? TimeSpan.FromSeconds(5) : _options.ShutdownTimeout;
            if (await Task.WhenAny(cleanup, Task.Delay(budget)).ConfigureAwait(false) != cleanup)
            {
                var timeout = new CleanupFailure("MCP physical process or pipe cleanup exceeded ShutdownTimeout.");
                timeout.Data["Strands.CleanupTask"] = cleanup;
                completion.TrySetException(timeout); return;
            }
            var completed = await cleanup.ConfigureAwait(false);
            if (completed.Count == 0) completion.TrySetResult(true);
            else { var primary = completed[0]; foreach (var secondary in completed.Skip(1)) Attach(primary, secondary); completion.TrySetException(primary); }
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private async Task<IReadOnlyList<Exception>> FinishAsync(Task terminate, List<Exception> errors)
    {
        try
        {
            try { await terminate.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            if (_pid is not null) try { await _exited.Task.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            if (!Windows && _pid is not null) { try { _process!.StandardOutput.BaseStream.Dispose(); } catch (Exception error) { errors.Add(error); } }
            if (_session is not null) try { await _session.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { await _stderrPump.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        }
        finally
        {
            try { _job?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { _process?.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        return Array.AsReadOnly(errors.ToArray());
    }
    private Task TerminateUnixAsync(Process process)
    {
        try
        {
#if NET8_0_OR_GREATER
            if (_pid is not null && !process.HasExited) process.Kill(entireProcessTree: true);
#endif
            return Task.CompletedTask;
        }
        catch (InvalidOperationException) { return Task.CompletedTask; }
        catch (Exception error) { return Task.FromException(error); }
    }
    private ClientCompletionDetails Details(Exception? failure, bool expected)
    {
        lock (_gate) return new StdioClientCompletionDetails { ProcessId = _pid, ExitCode = _exitCode, StandardErrorTail = _stderr.ToArray(), Exception = failure ?? (!expected ? new IOException("MCP server process exited unexpectedly.") : null) };
    }
    private static void Attach(Exception primary, Exception secondary)
    {
        if (ReferenceEquals(primary, secondary)) return;
        var errors = primary.Data["Strands.CleanupErrors"] is IEnumerable<Exception> previous ? previous.ToList() : [];
        if (!errors.Any(error => ReferenceEquals(error, secondary))) errors.Add(secondary);
        primary.Data["Strands.CleanupErrors"] = errors;
    }
    private sealed class CleanupFailure(string message) : IOException(message)
    {
        public override System.Collections.IDictionary Data { get; } = new System.Collections.Hashtable();
    }
    private sealed class Session(OwnedStdioClientTransport owner, ITransport inner) : ITransport
    {
        public string? SessionId => inner.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader { get; } = new Reader(owner, inner.MessageReader);
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => inner.SendMessageAsync(message, cancellationToken);
        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }
    private sealed class Reader : ChannelReader<JsonRpcMessage>
    {
        private readonly ChannelReader<JsonRpcMessage> _inner;
        internal Reader(OwnedStdioClientTransport owner, ChannelReader<JsonRpcMessage> inner) { _inner = inner; Completion = CompleteAsync(owner, inner); }
        public override Task Completion { get; }
        public override bool TryRead(out JsonRpcMessage item) => _inner.TryRead(out item!);
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => _inner.WaitToReadAsync(cancellationToken);
        private static async Task CompleteAsync(OwnedStdioClientTransport owner, ChannelReader<JsonRpcMessage> inner)
        {
            Exception? failure = null;
            try { await inner.Completion.ConfigureAwait(false); } catch (Exception error) { failure = error; }
            bool expected;
            lock (owner._gate) expected = owner._closing;
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { if (failure is null) failure = cleanup; else Attach(failure, cleanup); }
            throw new ClientTransportClosedException(owner.Details(failure, expected));
        }
    }
}
