using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Methods = Strands.Agents.Bedrock.NativeNovaSonicNativeMethods;

namespace Strands.Agents.Bedrock;

internal sealed class NativeNovaSonicHttpConnection
{
    private readonly object _gate = new object();
    private readonly object _readGate = new object();
    private readonly Queue<byte[]> _received = new Queue<byte[]>();
    private readonly TaskCompletionSource<int> _ready = NewSource<int>();
    private readonly TaskCompletionSource<int> _headers = NewSource<int>();
    private TaskCompletionSource<bool> _changed = NewSource<bool>();
    private readonly Methods.Signal _signal;
    private readonly Methods.Body _body;
    private static readonly Methods.Written WrittenCallback = OnWritten;
    private IntPtr _handle;
    private Task<int>? _stopped;
    private int _offset;
    private int _buffered;
    private int? _completion;

    internal NativeNovaSonicHttpConnection(string host, ushort port, string path, string trustedCaFile, Dictionary<string, string>? signedHeaders = null)
    {
        if (Methods.AbiVersion() != 2) throw new IOException("The native Nova transport ABI version does not match this managed adapter.");
        _signal = OnSignal;
        _body = OnBody;
        var headers = new StringBuilder();
        if (signedHeaders is not null) foreach (var header in signedHeaders)
        {
            if (header.Key.IndexOfAny(new[] { '\0', '\r', '\n', ':' }) >= 0 || header.Value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new ArgumentException("A request header contains an invalid delimiter.", nameof(signedHeaders));
            headers.Append(header.Key).Append(':').Append(header.Value).Append('\n');
        }
        _handle = Methods.Open(Utf8(host), port, Utf8(path), Utf8(trustedCaFile), Utf8(headers.ToString()), _signal, _body, IntPtr.Zero);
        if (_handle == IntPtr.Zero) throw new IOException("AWS CRT could not allocate the connection.");
    }

    internal Task<int> Ready => _ready.Task;
    internal Task<int> Headers => _headers.Task;
    internal int PendingWrites { get { lock (_gate) return _stopped is null ? Methods.Pending(_handle) : 0; } }

    internal async Task WriteAsync(byte[] bytes, bool endStream, CancellationToken cancellationToken = default)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        cancellationToken.ThrowIfCancellationRequested();
        using (cancellationToken.Register(Cancel))
        {
            try { await Ready.ConfigureAwait(false); }
            catch (Exception readyError) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Native write was canceled.", readyError, cancellationToken); }
        }
        var completion = NewSource<int>();
        var root = GCHandle.Alloc(completion);
        int error;
        lock (_gate)
        {
            if (_stopped is not null) { root.Free(); throw new ObjectDisposedException(nameof(NativeNovaSonicHttpConnection)); }
            error = Methods.Write(_handle, bytes, (uint)bytes.Length, endStream ? 1 : 0, WrittenCallback, GCHandle.ToIntPtr(root));
        }
        if (error != 0) { root.Free(); cancellationToken.ThrowIfCancellationRequested(); throw Failure(error); }
        using (cancellationToken.Register(Cancel))
        {
            // Cancellation requests native shutdown, then waits for the actual write callback.
            error = await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (error != 0) throw Failure(error);
        }
    }

    internal async Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken = default)
    {
        if (buffer is null || buffer.Length == 0) throw new ArgumentException("A nonempty read buffer is required.", nameof(buffer));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            int count = 0;
            lock (_readGate)
            {
                if (_received.Count != 0)
                {
                    var bytes = _received.Peek();
                    count = Math.Min(buffer.Length, bytes.Length - _offset);
                    Buffer.BlockCopy(bytes, _offset, buffer, 0, count);
                    _offset += count;
                    _buffered -= count;
                    if (_offset == bytes.Length) { _received.Dequeue(); _offset = 0; }
                }
                else if (_completion.HasValue)
                {
                    if (_completion.Value != 0) throw Failure(_completion.Value);
                    return 0;
                }
                changed = _changed.Task;
            }
            if (count != 0)
            {
                lock (_gate) if (_stopped is null) Methods.UpdateWindow(_handle, (uint)count);
                return count;
            }
            using (cancellationToken.Register(Cancel))
            {
                await changed.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    internal void Cancel()
    {
        lock (_gate) if (_stopped is null && _handle != IntPtr.Zero) Methods.Cancel(_handle);
    }

    internal Task<int> StopAsync()
    {
        lock (_gate)
        {
            if (_stopped is not null) return _stopped;
            Methods.Cancel(_handle);
            _stopped = Task.Run(() =>
            {
                var error = Methods.JoinAndDestroy(_handle, out var flags);
                // Delegates stay rooted through native stream destruction and event-loop join.
                GC.KeepAlive(_signal);
                GC.KeepAlive(_body);
                if (error != 0) throw Failure(error);
                return flags;
            });
            return _stopped;
        }
    }

    private void OnSignal(IntPtr context, int kind, int value)
    {
        if (kind == 1) _ready.TrySetResult(value);
        if (kind == 2) _headers.TrySetResult(value);
        if (kind != 3) return;
        var error = value == 0 ? new IOException("The connection ended before response headers.") : Failure(value);
        _ready.TrySetException(error);
        _headers.TrySetException(error);
        lock (_readGate) { _completion = value; Pulse(); }
    }

    private int OnBody(IntPtr context, IntPtr bytes, uint length)
    {
        try
        {
            var copy = new byte[checked((int)length)];
            Marshal.Copy(bytes, copy, 0, copy.Length);
            lock (_readGate)
            {
                if (_buffered + copy.Length > 65535) return 1;
                if (copy.Length != 0) { _received.Enqueue(copy); _buffered += copy.Length; Pulse(); }
            }
            return 0;
        }
        catch { return 1; } // An exception cannot cross the native callback boundary.
    }

    private void Pulse() { var previous = _changed; _changed = NewSource<bool>(); previous.TrySetResult(true); }
    private static void OnWritten(IntPtr context, int error)
    {
        var root = GCHandle.FromIntPtr(context);
        var completion = (TaskCompletionSource<int>)root.Target!;
        root.Free();
        completion.TrySetResult(error);
    }
    private static TaskCompletionSource<T> NewSource<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IOException Failure(int error) => new IOException("AWS CRT: " + Marshal.PtrToStringAnsi(Methods.ErrorName(error)));
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

}
