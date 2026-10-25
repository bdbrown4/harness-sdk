using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Strands.Agents.Processes;

// Three binary protocols share this lifetime: sandbox clients, the native Monty worker, and MCP stdio.
// The attached legacy Process supplies process identity/lifetime, never its text pipes.
internal sealed class OwnedChildProcess : IDisposable
{
    private Process? _process;
    private InputPipe? _input;
    private StreamReader? _output, _error;
    private bool _disposed, _started;

    internal ProcessStartInfo StartInfo { get; set; } = new();
    internal bool EnableRaisingEvents { get; set; }
    internal event EventHandler? Exited;
    internal InputPipe StandardInput => _input ?? throw new InvalidOperationException("The child process has not started.");
    internal StreamReader StandardOutput => _output ?? throw new InvalidOperationException("The child process has not started.");
    internal StreamReader StandardError => _error ?? throw new InvalidOperationException("The child process has not started.");
    internal int Id => RequiredProcess.Id;
    internal int ExitCode => RequiredProcess.ExitCode;
    internal bool HasExited => RequiredProcess.HasExited;
    // Borrowed only: the facade retains ownership until its own disposal.
    internal SafeProcessHandle BorrowedProcessHandle => RequiredProcess.SafeHandle;
    private Process RequiredProcess => _process ?? throw new InvalidOperationException("The child process has not started.");

    internal bool Start(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OwnedChildProcess));
        if (_started) throw new InvalidOperationException("The child process has already started.");
        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        if (StartInfo.UseShellExecute || !StartInfo.RedirectStandardInput || !StartInfo.RedirectStandardOutput || !StartInfo.RedirectStandardError)
            throw new InvalidOperationException("An owned binary child requires three redirected pipes and UseShellExecute=false.");
#if NETSTANDARD2_0
        try { WindowsChildProcess.Start(StartInfo, Attach, cancellationToken); }
        catch
        {
            // The starter owns every resource until successful release of the suspended thread.
            // Do not dispose its already-cleaned resources again while unwinding the caller's primary failure.
            _process = null; _input = null; _output = _error = null;
            throw;
        }
#else
        StartInfo.StandardInputEncoding = new UTF8Encoding(false);
        var process = new Process { StartInfo = StartInfo };
        _process = process;
        process.Exited += OnExited;
        process.EnableRaisingEvents = EnableRaisingEvents;
        if (!process.Start()) return false;
        _input = new(process.StandardInput.BaseStream);
        _output = process.StandardOutput;
        _error = process.StandardError;
#endif
        return true;
    }

#if NETSTANDARD2_0
    private void Attach(Process process, Stream input, Stream output, Stream error)
    {
        _process = process;
        _input = new(input);
        _output = new StreamReader(output, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
        _error = new StreamReader(error, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
        process.Exited += OnExited;
        process.EnableRaisingEvents = EnableRaisingEvents;
    }
#endif
    private void OnExited(object? sender, EventArgs arguments) => Exited?.Invoke(this, arguments);
    internal bool WaitForExit(int milliseconds) => RequiredProcess.WaitForExit(milliseconds);
    internal void Kill() => RequiredProcess.Kill();
    internal sealed class InputPipe(Stream stream) : IDisposable
    {
        private int _closed;
        internal Stream BaseStream { get; } = stream;
        internal void Close() => Dispose();
        public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) == 0) BaseStream.Dispose(); }
    }
#if NET8_0_OR_GREATER
    internal void Kill(bool entireProcessTree) => RequiredProcess.Kill(entireProcessTree);
#endif
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures = null;
        Attempt(_input);
        Attempt(_output);
        Attempt(_error);
        if (_process is { } process) { process.Exited -= OnExited; Attempt(process); }
        if (failures is { Count: > 0 })
        {
            var primary = failures[0];
            if (failures.Count > 1) primary.Data["Strands.CleanupErrors"] = failures.Skip(1).ToList();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
        void Attempt(IDisposable? owned)
        {
            try { owned?.Dispose(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
    }
}
