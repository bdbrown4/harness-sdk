using System.Net.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

[Collection("SDK logging")]
public sealed class McpToolChangeDiagnosticsTests
{
    [Theory]
    [InlineData(McpToolChangeProfile.Python)]
    [InlineData(McpToolChangeProfile.TypeScript)]
    public async Task CallbackFailuresKeepTheAcceptedCacheAndTheOriginalDiagnosticCause(McpToolChangeProfile profile)
    {
        var previous = SdkLogging.Logger;
        var observed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SdkLogging.ConfigureLogging(new ObserverLogger(error => observed.TrySetResult(error)));
        try
        {
            await using var server = new McpToolChangeLoopbackServer();
            using var http = new HttpClient();
            await using var client = await ConnectAsync(http, server.Endpoint);
            var failure = new IOException("observer rejected accepted update");
            await using var subscription = await client.WatchToolChangesAsync((snapshot, token) => throw failure,
                options: new() { Profile = profile, Debounce = TimeSpan.Zero });

            server.ToolName = "second";
            await server.NotifyAsync();
            Assert.Same(observed.Task, await Task.WhenAny(observed.Task, Task.Delay(10_000)));

            Assert.Same(failure, await observed.Task);
            Assert.Equal("second", Assert.Single(subscription.Current.Tools).ServerName);
            Assert.Equal(1, subscription.Current.Version);
            Assert.False(subscription.Completion.IsCompleted);
        }
        finally { SdkLogging.ConfigureLogging(previous); }
    }

    [Fact]
    public async Task AThrowingDiagnosticListenerCannotStopLaterNotificationsOrReplaceAcceptedState()
    {
        var previous = SdkLogging.Logger;
        var warned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SdkLogging.ConfigureLogging(new ObserverLogger(error => { warned.TrySetResult(true); throw new IOException("diagnostic listener"); }));
        try
        {
            await using var server = new McpToolChangeLoopbackServer();
            using var http = new HttpClient();
            await using var client = await ConnectAsync(http, server.Endpoint);
            var callbacks = 0;
            var recovered = new TaskCompletionSource<McpToolsChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = await client.WatchToolChangesAsync((snapshot, token) =>
            {
                if (Interlocked.Increment(ref callbacks) == 1) throw new InvalidOperationException("first observer failure");
                recovered.TrySetResult(snapshot);
                return default;
            }, options: new() { Debounce = TimeSpan.Zero });
            await server.NotifyAsync();
            Assert.Same(warned.Task, await Task.WhenAny(warned.Task, Task.Delay(10_000)));
            await subscription.RefreshDrain;
            server.ToolName = "recovered";

            await server.NotifyAsync();
            Assert.Same(recovered.Task, await Task.WhenAny(recovered.Task, Task.Delay(10_000)));

            Assert.Equal("recovered", Assert.Single((await recovered.Task).Tools).ServerName);
            Assert.Equal(2, subscription.Current.Version);
        }
        finally { SdkLogging.ConfigureLogging(previous); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticCallbacksCannotWaitForTheirOwnRefreshWorkerToClose(bool clientDisposal)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        var previous = SdkLogging.Logger;
        var attempted = new TaskCompletionSource<(Exception? Error, Task? Pending)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Client? client = null;
        McpToolChangeSubscription? subscription = null;
        SdkLogging.ConfigureLogging(new ObserverLogger(error =>
        {
            try { attempted.TrySetResult((null, clientDisposal ? client!.DisposeAsync().AsTask() : subscription!.DisposeAsync().AsTask())); }
            catch (Exception rejected) { attempted.TrySetResult((rejected, null)); }
        }));
        try
        {
            client = await ConnectAsync(http, server.Endpoint);
            subscription = await client.WatchToolChangesAsync((snapshot, token) => throw new IOException("callback failed"), options: new() { Debounce = TimeSpan.Zero });
            await server.NotifyAsync();
            Assert.Same(attempted.Task, await Task.WhenAny(attempted.Task, Task.Delay(10_000)));
            var result = await attempted.Task;
            if (result.Pending is not null)
            {
                Assert.Same(result.Pending, await Task.WhenAny(result.Pending, Task.Delay(10_000)));
                await result.Pending;
            }
            Assert.IsType<InvalidOperationException>(result.Error);
        }
        finally
        {
            if (subscription is not null) await subscription.DisposeAsync();
            if (client is not null) await client.DisposeAsync();
            SdkLogging.ConfigureLogging(previous);
        }
    }

    private static Task<Client> ConnectAsync(HttpClient http, Uri endpoint) => Client.CreateHttpAsync(http,
        new() { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new() { ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(5) });

    private sealed class ObserverLogger(Action<Exception?> observer) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Warning) observer(exception); }
    }
}
