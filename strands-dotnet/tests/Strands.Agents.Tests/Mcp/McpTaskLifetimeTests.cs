using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpTaskLifetimeTests
{
    [Fact]
    public async Task RealStdioTaskCompletesAndItsOwnedProcessExitsOnDisposal()
    {
        await using var client = await Strands.Agents.Mcp.McpClient.CreateStdioAsync(new McpClientSettings { TasksConfig = new() }, StdioOptions(),
            new McpClientOptions { ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(20) });
        using var process = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo.Version, System.Globalization.CultureInfo.InvariantCulture));

        var result = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "genuine stdio Café 雪" }));

        Assert.Equal("genuine stdio Café 雪", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        await client.DisposeAsync();
        Assert.True(await Task.Run(() => process.WaitForExit(10000)), "The owned stdio process must exit after closing its protocol connection.");
    }

    [Fact]
    public async Task CancellingARealStdioTaskRetainsItsTokenAndDisposalReapsTheBlockedServer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands mcp task " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var marker = Path.Combine(directory, "task.pid");
            await using var client = await Strands.Agents.Mcp.McpClient.CreateStdioAsync(new McpClientSettings { TasksConfig = new() }, StdioOptions("--block-task", "--task-marker=" + marker),
                new McpClientOptions { ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(20) });
            using var process = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo.Version, System.Globalization.CultureInfo.InvariantCulture));
            using var cancellation = new CancellationTokenSource();
            var call = client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "pending" }), cancellationToken: cancellation.Token).AsTask();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(marker))
            {
                if (call.IsCompleted) await call;
                await Task.Delay(25, startup.Token);
            }
            Assert.Equal(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(marker));

            cancellation.Cancel();

            Assert.Same(call, await Task.WhenAny(call, Task.Delay(5000)));
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call)).CancellationToken);
            await client.DisposeAsync();
            Assert.True(await Task.Run(() => process.WaitForExit(10000)), "The actual blocked stdio process must exit after disposal.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static StdioClientTransportOptions StdioOptions(params string[] arguments)
    {
#if NETFRAMEWORK
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
        Assert.True(File.Exists(fixture), "Build must stage the native Framework stdio executable.");
        return new() { Command = fixture, Arguments = new[] { "--tasks" }.Concat(arguments).ToArray() };
#else
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture), "Build must stage the stdio fixture and runtime metadata.");
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return new() { Command = Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), Arguments = new[] { fixture, "--tasks" }.Concat(arguments).ToArray() };
#endif
    }

    [Fact]
    public async Task ActiveProgressCannotExtendTheOverallDeadline()
    {
        var progress = new CountingProgress();
        ProgressStream? stream = null;
        using var server = new McpTaskClientTests.TaskServer();
        server.Respond = (request, _) =>
        {
            if (request.Method != "tasks/get") return Task.FromResult<HttpResponseMessage?>(null);
            stream = new ProgressStream(request.Parameters.GetProperty("_meta").GetProperty("progressToken").Clone());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http, new() { RequestTimeout = TimeSpan.FromSeconds(10), PollTimeout = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromMilliseconds(10) });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), progress).AsTask();

        Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(10_000)));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => invocation);

        Assert.Contains("overall", error.Message);
        Assert.NotNull(stream);
        Assert.Same(stream.Disposed.Task, await Task.WhenAny(stream.Disposed.Task, Task.Delay(5000)));
        Assert.True(progress.Count >= 3, "The overall deadline must stop an otherwise active progress stream.");
        Assert.Single(server.Requests, request => request.Method == "tasks/cancel");
    }

    [Fact]
    public async Task ObservedProgressKeepsARequestAliveBeyondItsOriginalInactivityInterval()
    {
        var progress = new CountingProgress();
        using var server = new McpTaskClientTests.TaskServer();
        server.Respond = (request, _) =>
        {
            if (request.Method != "tasks/get") return Task.FromResult<HttpResponseMessage?>(null);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new ProgressStream(request.Parameters.GetProperty("_meta").GetProperty("progressToken").Clone())) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http, new() { RequestTimeout = TimeSpan.FromSeconds(5), PollTimeout = TimeSpan.FromSeconds(30), PollInterval = TimeSpan.FromMilliseconds(10) });
        using var cancellation = new CancellationTokenSource();
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), progress, cancellationToken: cancellation.Token).AsTask();
        try
        {
            Assert.Same(progress.ThreeReports.Task, await Task.WhenAny(progress.ThreeReports.Task, invocation, Task.Delay(10_000)));
            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.False(invocation.IsCompleted, "Progress must reset inactivity beyond the original five-second request interval.");
        }
        finally { cancellation.Cancel(); }

        Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(5000)));
        Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation)).CancellationToken);
        Assert.Single(server.Requests, request => request.Method == "tasks/cancel");
    }

    [Fact]
    public async Task ExplicitLifecycleRequestsKeepTheirFixedBudgetDespiteProgress()
    {
        using var server = new McpTaskClientTests.TaskServer();
        server.Respond = (request, _) =>
        {
            if (request.Method != "tasks/get") return Task.FromResult<HttpResponseMessage?>(null);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new ProgressStream(request.Parameters.GetProperty("_meta").GetProperty("progressToken").Clone())) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http, new() { RequestTimeout = TimeSpan.FromMilliseconds(500) });
        using var cancellation = new CancellationTokenSource();
        var invocation = client.GetTaskAsync(server.TaskId, cancellation.Token).AsTask();
        try
        {
            Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(2000)));
            await Assert.ThrowsAsync<TimeoutException>(() => invocation);
        }
        finally
        {
            cancellation.Cancel();
            try { await invocation; } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CancellingSubmissionPreservesTheOriginalCallerToken()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new McpTaskClientTests.TaskServer
        {
            Respond = async (request, token) =>
            {
                if (request.Method != "tools/call") return null;
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return null;
            }
        };
        using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http);
        using var cancellation = new CancellationTokenSource();
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), cancellationToken: cancellation.Token).AsTask();
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task AnUncooperativeInputHandlerCannotExtendTheOverallDeadlineOrSendALateUpdate()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new McpTaskClientTests.TaskServer();
        server.Respond = (request, _) => Task.FromResult(request.Method == "tasks/get"
            ? McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), McpTaskClientTests.InputState(server.TaskId, new Dictionary<string, object>
            {
                ["question"] = new { method = "elicitation/create", @params = new { message = "approve", requestedSchema = new { type = "object", properties = new { } } } }
            })) : null);
        using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http, new() { PollTimeout = TimeSpan.FromMilliseconds(500) }, new()
        {
            ElicitationHandler = async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                returned.TrySetResult(true);
                return new ElicitResult { Action = "accept" };
            }
        });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));
            Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(2000)));
            await Assert.ThrowsAsync<TimeoutException>(() => invocation);
        }
        finally
        {
            release.TrySetResult(true);
            try { await invocation; } catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
        }
        Assert.Same(returned.Task, await Task.WhenAny(returned.Task, Task.Delay(5000)));
        Assert.DoesNotContain(server.Requests, request => request.Method == "tasks/update");
    }

    private sealed class CountingProgress : IProgress<ProgressNotificationValue>
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        internal TaskCompletionSource<bool> ThreeReports { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(ProgressNotificationValue value)
        {
            if (Interlocked.Increment(ref _count) >= 3) ThreeReports.TrySetResult(true);
        }
    }

    private sealed class ProgressStream(JsonElement token) : Stream
    {
        private int _index;
        private byte[] _pending = Array.Empty<byte>();
        private int _offset;
        internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_offset == _pending.Length)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/progress", @params = new { progressToken = token, progress = Interlocked.Increment(ref _index) } });
                _pending = Encoding.UTF8.GetBytes("event: message\ndata: " + json + "\n\n");
                _offset = 0;
            }
            var length = Math.Min(count, _pending.Length - _offset);
            Array.Copy(_pending, _offset, buffer, offset, length);
            _offset += length;
            return length;
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed.TrySetResult(true); base.Dispose(disposing); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
