using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Strands.Agents.Tests.Mcp;
using Client = Strands.Agents.Mcp.McpClient;
using TaskServer = Strands.Agents.Tests.Mcp.McpTaskClientTests.TaskServer;

namespace Strands.Agents.Tests.Logging;

[Collection("SDK logging")]
public sealed class McpLoggingClosureTests : IDisposable
{
    private readonly ILogger _saved = SdkLogging.Logger;
    public void Dispose() => SdkLogging.ConfigureLogging(_saved);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOverlongDiscoveredNameUsesTheConfiguredSdkLoggerAndKeepsTheValidTool(bool throwingLogger)
    {
        var logger = new Host(throwingLogger); SdkLogging.ConfigureLogging(logger);
        var longName = new string('x', 60);
        using var server = new TaskServer
        {
            Respond = (request, _) => Task.FromResult(request.Method == "tools/list"
            ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new { tools = new[] { new { name = longName, inputSchema = new { type = "object" } }, new { name = "keep", inputSchema = new { type = "object" } } } }) : null)
        };
        using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http);
        Assert.Equal("remote_keep", Assert.Single(await client.ListToolsAsync(new() { Prefix = "remote", ContinueOnError = true })).Spec.Name);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal($"MCP tool '{longName}' has prefixed name 'remote_{longName}' of length 67, exceeding 64 characters; skipping because ContinueOnError is enabled.", entry.Message);
        Assert.Null(entry.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProgressObserverFailureIsLoggedWithItsCauseWithoutReplacingTheCompletedResult(bool throwingLogger)
    {
        var logger = new Host(throwingLogger); SdkLogging.ConfigureLogging(logger);
        var failure = new IOException("application progress failed");
        var observer = new ThrowingProgress(failure);
        using var server = new TaskServer();
        server.Respond = (request, _) =>
        {
            if (request.Method != "tasks/get") return Task.FromResult<HttpResponseMessage?>(null);
            var progress = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/progress", @params = new { progressToken = request.Parameters.GetProperty("_meta").GetProperty("progressToken"), progress = 1 } });
            var final = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = request.Envelope.GetProperty("id"),
                result = new
                {
                    resultType = "complete",
                    taskId = server.TaskId,
                    status = "completed",
                    createdAt = "2026-10-20T10:00:00Z",
                    lastUpdatedAt = "2026-10-20T10:00:01Z",
                    ttlMs = (int?)null,
                    result = new { content = new[] { new { type = "text", text = "complete despite observer" } } }
                }
            });
            var content = new StreamContent(new ProgressStream("event: message\ndata: " + progress + "\n\n", "event: message\ndata: " + final + "\n\n", observer.Observed.Task));
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = content });
        };
        using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http);
        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), observer);
        Assert.Equal("complete despite observer", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(1, observer.Calls);
        await Task.WhenAny(logger.Logged.Task, Task.Delay(1000));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("MCP progress observer failed: IOException", entry.Message);
        Assert.Same(failure, entry.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCooperativeTaskCancellationKeepsTheCallerTokenAndLogsTheCleanupCause(bool throwingLogger)
    {
        var logger = new Host(throwingLogger); SdkLogging.ConfigureLogging(logger);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new TaskServer();
        server.Respond = async (request, token) =>
        {
            if (request.Method == "tasks/cancel") return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.Envelope.GetProperty("id"), error = new { code = -32603, message = "cooperative cleanup unavailable" } }), Encoding.UTF8, "application/json") };
            if (request.Method == "tasks/get") { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); }
            return null;
        };
        using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http);
        using var caller = new CancellationTokenSource();
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), cancellationToken: caller.Token).AsTask();
        try { Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000))); }
        finally { caller.Cancel(); }
        var primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Equal(caller.Token, primary.CancellationToken);
        var entry = Assert.Single(logger.Entries);
        Assert.StartsWith("MCP task cancellation failed: ", entry.Message);
        Assert.NotNull(entry.Error);
        Assert.Contains("cooperative cleanup unavailable", entry.Error!.Message);
        Assert.NotSame(primary, entry.Error);
    }

    [Fact]
    public async Task RepeatedDiscoveryWarningsAreNotDeduplicatedAndReplacementDoesNotDisposeBorrowedLoggers()
    {
        var first = new Host(); var second = new Host();
        using var server = ToolServer(); using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http);
        SdkLogging.ConfigureLogging(first);
        await client.ListToolsAsync(new() { Prefix = "remote", ContinueOnError = true });
        await client.ListToolsAsync(new() { Prefix = "remote", ContinueOnError = true });
        SdkLogging.ConfigureLogging(second);
        await client.ListToolsAsync(new() { Prefix = "remote", ContinueOnError = true });
        Assert.Equal(2, first.Entries.Count); Assert.Single(second.Entries);
        Assert.False(first.Disposed); Assert.False(second.Disposed);
    }

    [Fact]
    public async Task OverlongNamesStillFailClosedWithoutAWarningWhenContinueOnErrorIsFalse()
    {
        var logger = new Host(); SdkLogging.ConfigureLogging(logger);
        using var server = ToolServer(); using var http = new HttpClient(server);
        await using var client = await McpTaskClientTests.ConnectAsync(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListToolsAsync(new() { Prefix = "remote", ContinueOnError = false }));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task TheDefaultLoggerStillWritesMcpWarningsToTheTraceBridge()
    {
        SdkLogging.ConfigureLogging(new Strands.Agents.Logging.LogDispatcher().Logger);
        using var listener = new WarningListener(); System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            using var server = ToolServer(); using var http = new HttpClient(server);
            await using var client = await McpTaskClientTests.ConnectAsync(http);
            await client.ListToolsAsync(new() { Prefix = "remote", ContinueOnError = true });
            Assert.Contains(listener.Messages, message => message.Contains("exceeding 64 characters"));
        }
        finally { System.Diagnostics.Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task LegacyProgressWarningsRemainOncePerConnectionWhenTheLoggerFailsAndIsReplaced()
    {
        var first = new Host(throwing: true); var replacement = new Host();
        var progress = new ThrowingProgress(new IOException("legacy progress is not delivered"));
        using var server = McpLegacyTaskTests.Server(); using var http = new HttpClient(server);
        await using var client = await McpLegacyTaskTests.ConnectAsync(http);
        SdkLogging.ConfigureLogging(first);
        var initial = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), progress);
        SdkLogging.ConfigureLogging(replacement);
        var repeated = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), progress);
        Assert.Equal("legacy result Café 雪", Assert.IsType<TextContentBlock>(Assert.Single(initial.Content)).Text);
        Assert.Equal("legacy result Café 雪", Assert.IsType<TextContentBlock>(Assert.Single(repeated.Content)).Text);
        Assert.Equal("MCP legacy task calls do not deliver application progress callbacks.", Assert.Single(first.Entries).Message);
        Assert.Empty(replacement.Entries); Assert.Equal(0, progress.Calls); Assert.False(first.Disposed);
    }

    private static TaskServer ToolServer() => new()
    {
        Respond = (request, _) => Task.FromResult(request.Method == "tools/list"
        ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new { tools = new[] { new { name = new string('x', 60), inputSchema = new { type = "object" } } } }) : null)
    };

    internal sealed record Entry(LogLevel Level, string Message, Exception? Error);
    internal sealed class Host(bool throwing = false) : ILogger, IDisposable
    {
        internal List<Entry> Entries { get; } = [];
        internal TaskCompletionSource<bool> Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) { Entries.Add(new(level, formatter(state, error), error)); Logged.TrySetResult(true); if (throwing) throw new IOException("logger failure"); }
        public void Dispose() => Disposed = true;
    }

    private sealed class WarningListener : System.Diagnostics.TraceListener
    {
        internal List<string> Messages { get; } = [];
        public override void Write(string? message) { if (message is not null) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private sealed class ThrowingProgress(Exception error) : IProgress<ProgressNotificationValue>
    {
        internal int Calls;
        internal TaskCompletionSource<bool> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(ProgressNotificationValue value) { Calls++; Observed.TrySetResult(true); throw error; }
    }

    private sealed class ProgressStream(string first, string final, Task observed) : Stream
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(first), _final = Encoding.UTF8.GetBytes(final);
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_position >= _first.Length)
            {
                var winner = await Task.WhenAny(observed, Task.Delay(10_000, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                if (winner != observed) throw new TimeoutException("The native progress notification was not delivered.");
            }
            var bytes = _position < _first.Length ? _first : _final;
            var index = _position < _first.Length ? _position : _position - _first.Length;
            var length = Math.Min(count, bytes.Length - index);
            Array.Copy(bytes, index, buffer, offset, length); _position += length;
            return length;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
