using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolChangeReviewTests
{
    [Fact]
    public async Task ClientCloseJoinsTheInitialDiscoveryFilterBeforeReturning()
    {
        var server = new McpToolChangeLoopbackServer();
        var http = new HttpClient();
        Client? client = null;
        Task<McpToolChangeSubscription>? creation = null;
        Task? closing = null;
        var entered = Signal<bool>(); var release = Signal<bool>();
        Exception? failure = null;
        try
        {
            client = await ConnectAsync(http, server.Endpoint);
            var connected = client;
            // A synchronous filter may run before the public call first suspends.
            creation = Task.Run(async () => await connected.WatchToolChangesAsync((snapshot, token) => default, new()
            {
                ToolFilter = name => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); return true; },
            }));
            await Deadline(entered.Task);
            closing = client.DisposeAsync().AsTask();
            await Deadline(server.ListenClosed.Task);
            var held = Task.Delay(100);
            Assert.Same(held, await Task.WhenAny(closing, held));
            release.TrySetResult(true);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Deadline(creation));
            await Deadline(closing);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            release.TrySetResult(true);
            McpToolChangeSubscription? created = null;
            if (creation is not null)
            {
                try { created = await creation; }
                catch (ObjectDisposedException) when (closing is not null) { }
                catch (Exception error) { KeepFailure(ref failure, error); }
            }
            if (created is not null)
            {
                try { await created.DisposeAsync(); }
                catch (Exception error) { KeepFailure(ref failure, error); }
            }
            if (client is not null)
            {
                try { await (closing ?? client.DisposeAsync().AsTask()); }
                catch (Exception error) { KeepFailure(ref failure, error); }
            }
            try { http.Dispose(); }
            catch (Exception error) { KeepFailure(ref failure, error); }
            try { await server.DisposeAsync(); }
            catch (Exception error) { KeepFailure(ref failure, error); }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task AWorkerCannotJoinAnotherWatcherWhileSharedClientCloseJoinsBothWorkers()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var other = await client.WatchToolChangesAsync((snapshot, token) => default);
        var entered = Signal<bool>(); var attempt = Signal<bool>(); var returned = Signal<bool>();
        var attempted = Signal<Exception?>();
        Task? pending = null;
        var subscription = await client.WatchToolChangesAsync(async (snapshot, token) =>
        {
            entered.TrySetResult(true);
            await attempt.Task;
            Exception? rejected = null;
            try { pending = other.DisposeAsync().AsTask(); } catch (Exception error) { rejected = error; }
            attempted.TrySetResult(rejected);
            await returned.Task;
        }, options: new() { Debounce = TimeSpan.Zero });
        await server.NotifyAsync();
        await Deadline(entered.Task);
        var close = client.DisposeAsync().AsTask();
        try
        {
            attempt.TrySetResult(true);
            Assert.IsType<InvalidOperationException>(await Deadline(attempted.Task));
        }
        finally { returned.TrySetResult(true); }
        await Deadline(close);
        if (pending is not null) await Deadline(pending);
        await other.DisposeAsync(); await subscription.DisposeAsync();
    }

    [Fact]
    public void TheWireObserverNeverInvokesCustomJsonConverters()
    {
        var changes = new ToolChangeWireState();
        var entry = changes.Register("known-request");
        var value = new BorrowedGrant();

        changes.Receiving(new JsonRpcNotification
        {
            Method = "notifications/subscriptions/acknowledged",
            Params = new JsonObject
            {
                ["_meta"] = new JsonObject { ["io.modelcontextprotocol/subscriptionId"] = "known-request" },
                ["notifications"] = new JsonObject { ["toolsListChanged"] = JsonValue.Create(value) },
            },
        });

        Assert.Equal(0, value.Writes);
        Assert.IsType<InvalidDataException>(entry.Current.Failure);
    }

    [JsonConverter(typeof(BorrowedGrantConverter))]
    private sealed class BorrowedGrant { internal int Writes; }
    private sealed class BorrowedGrantConverter : JsonConverter<BorrowedGrant>
    {
        public override BorrowedGrant? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, BorrowedGrant value, JsonSerializerOptions options)
        { value.Writes++; writer.WriteBooleanValue(true); }
    }
    private static void KeepFailure(ref Exception? primary, Exception error)
    {
        if (primary is null) { primary = error; return; }
        if (ReferenceEquals(primary, error)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!errors.Any(previous => ReferenceEquals(previous, error))) errors.Add(error);
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Deadline<T>(Task<T> task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); return await task; }
    private static async Task Deadline(Task task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); await task; }
    private static Task<Client> ConnectAsync(HttpClient http, Uri endpoint) => Client.CreateHttpAsync(http,
        new() { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new() { ProtocolVersion = "2026-07-28" });
}
