using System.Net.Http;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2APushNotificationTests
{
    [Fact]
    public async Task AuthorizedMessageConfigurationDeliversAddressableTaskSnapshotsAndAdvertisesCapability()
    {
        var sent = new List<(JsonElement Task, JsonElement Configuration)>();
        var authorized = 0;
        await using var executor = Executor();
        await using var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            PushNotifications = new()
            {
                AuthorizeConfiguration = (request, config, token) =>
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("https://callback.example/updates", config.GetProperty("url").GetString());
                    token.ThrowIfCancellationRequested(); authorized++;
                    return new(true);
                },
                SendAsync = (task, config, token) => { token.ThrowIfCancellationRequested(); sent.Add((task, config)); return default; }
            }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var client = Client(http);

        Assert.True((await client.GetAgentCardAsync()).GetProperty("capabilities").GetProperty("pushNotifications").GetBoolean());
        var result = await client.CallAsync("message/send", WithPush(new { url = "https://callback.example/updates", token = "opaque-token" }));

        Assert.Equal(1, authorized);
        Assert.NotEmpty(sent);
        var last = sent[sent.Count - 1];
        Assert.Equal("completed", last.Task.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(result.GetProperty("id").GetString(), last.Task.GetProperty("id").GetString());
        Assert.Equal(result.GetProperty("id").GetString(), last.Configuration.GetProperty("id").GetString());
        Assert.Equal("opaque-token", last.Configuration.GetProperty("token").GetString());
        Assert.Equal("hello", last.Task.GetProperty("artifacts")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.False(last.Task.TryGetProperty("pushNotificationConfig", out _));
    }

    [Fact]
    public async Task ConfigurationOperationsReplaceSelectListAndDeleteWithoutExecutingAnotherAgent()
    {
        var created = 0;
        var authorized = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { created++; return new(new Agent(new() { Model = new A2AServerTests.EchoModel() })); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            PushNotifications = new() { AuthorizeConfiguration = (_, _, _) => { authorized++; return new(true); }, SendAsync = (_, _, _) => default }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var client = Client(http);
        var task = await client.CallAsync("message/send", A2AServerTests.Send("hello"));
        var id = task.GetProperty("id").GetString()!;
        var first = await client.CallAsync("tasks/pushNotificationConfig/set", Json(new { taskId = id, pushNotificationConfig = new { id = "primary", url = "https://one.example/" } }));
        Assert.Equal(id, first.GetProperty("taskId").GetString());
        await client.CallAsync("tasks/pushNotificationConfig/set", Json(new { taskId = id, pushNotificationConfig = new { id = "secondary", url = "https://two.example/" } }));
        await client.CallAsync("tasks/pushNotificationConfig/set", Json(new { taskId = id, pushNotificationConfig = new { id = "primary", url = "https://replacement.example/", token = "new-token" } }));

        var selected = await client.CallAsync("tasks/pushNotificationConfig/get", Json(new { id, pushNotificationConfigId = "primary" }));
        var listed = await client.CallAsync("tasks/pushNotificationConfig/list", Json(new { id }));

        Assert.Equal("https://replacement.example/", selected.GetProperty("pushNotificationConfig").GetProperty("url").GetString());
        Assert.Equal(2, listed.GetArrayLength());
        Assert.All(listed.EnumerateArray(), item => Assert.Equal(id, item.GetProperty("taskId").GetString()));
        var deleted = await client.CallAsync("tasks/pushNotificationConfig/delete", Json(new { id, pushNotificationConfigId = "primary" }));
        Assert.Equal(JsonValueKind.Null, deleted.ValueKind);
        listed = await client.CallAsync("tasks/pushNotificationConfig/list", Json(new { id }));
        Assert.Equal("secondary", Assert.Single(listed.EnumerateArray()).GetProperty("pushNotificationConfig").GetProperty("id").GetString());
        Assert.Equal(3, authorized); Assert.Equal(1, created);
    }

    [Fact]
    public async Task DeniedDestinationCannotCreateAnAgentOrDeliverAPush()
    {
        var created = 0; var sent = 0; var authorized = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { created++; throw new InvalidOperationException("must not execute"); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            PushNotifications = new() { AuthorizeConfiguration = (_, _, _) => { authorized++; return new(false); }, SendAsync = (_, _, _) => { sent++; return default; } }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => Client(http).CallAsync("message/send", WithPush(new { url = "http://forbidden.example/" })));

        Assert.Equal(-32602, error.Code); Assert.Equal(1, authorized);
        Assert.Equal(0, created); Assert.Equal(0, sent);
    }

    [Fact]
    public async Task DeliveryFailureIsReportedWithoutChangingTheCompletedTask()
    {
        var observed = new List<Exception>(); var failure = new InvalidOperationException("private callback failure");
        await using var executor = Executor();
        await using var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            PushNotifications = new() { AuthorizeConfiguration = (_, _, _) => new(true), SendAsync = (_, _, _) => throw failure, DeliveryFailed = (_, error) => observed.Add(error) }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));

        var task = await Client(http).CallAsync("message/send", WithPush(new { url = "https://callback.example/" }));

        Assert.Equal("completed", task.GetProperty("status").GetProperty("state").GetString());
        Assert.NotEmpty(observed); Assert.All(observed, error => Assert.Same(failure, error));
    }

    [Fact]
    public async Task InMemoryStoreDetachesValuesAndEnforcesCapacityWithoutLosingThePriorConfiguration()
    {
        var store = new InMemoryA2APushNotificationConfigStore(maxConfigurations: 1);
        using (var document = JsonDocument.Parse("{\"id\":\"a\",\"url\":\"https://first.example/\"}"))
            await store.SetAsync("task", document.RootElement);
        var held = Assert.Single(await store.ListAsync("task"));
        await store.SetAsync("task", Json(new { id = "a", url = "https://replacement.example/" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync("task", Json(new { id = "b", url = "https://second.example/" })).AsTask());
        Assert.Equal("https://first.example/", held.GetProperty("url").GetString());
        Assert.Equal("https://replacement.example/", Assert.Single(await store.ListAsync("task")).GetProperty("url").GetString());
        await store.DeleteAsync("task", "a"); Assert.Empty(await store.ListAsync("task"));
    }

    [Theory]
    [InlineData("{\"url\":\"relative\"}")]
    [InlineData("{\"url\":\"https://user:secret@callback.example/\"}")]
    [InlineData("{\"url\":\"https://callback.example/\",\"authentication\":{\"schemes\":[]}}")]
    [InlineData("null")]
    public async Task MalformedConfigurationIsRejectedBeforeAuthorizationAndExecution(string configuration)
    {
        var authorized = 0; var created = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { created++; throw new InvalidOperationException("must not execute"); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            PushNotifications = new() { AuthorizeConfiguration = (_, _, _) => { authorized++; return new(true); }, SendAsync = (_, _, _) => default }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        using var document = JsonDocument.Parse(configuration);

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => Client(http).CallAsync("message/send", WithPush(document.RootElement)));

        Assert.Equal(-32602, error.Code); Assert.Equal(0, authorized); Assert.Equal(0, created);
    }

    [Fact]
    public async Task ShutdownCancelsAndPhysicallyJoinsAnInFlightBorrowedSender()
    {
        var entered = Signal(); var canceled = Signal(); var release = Signal(); var finished = Signal();
        await using var executor = Executor();
        var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            PushNotifications = new()
            {
                AuthorizeConfiguration = (_, _, _) => new(true),
                SendAsync = async (_, _, token) =>
                {
                    using var registration = token.Register(() => canceled.TrySetResult(true));
                    entered.TrySetResult(true);
                    try { await release.Task; }
                    finally { finished.TrySetResult(true); }
                }
            }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var send = Client(http).CallAsync("message/send", WithPush(new { url = "https://callback.example/" }));
        Task? stopping = null;
        try
        {
            await BoundedSignalAsync(entered.Task);
            stopping = server.DisposeAsync().AsTask();
            await BoundedSignalAsync(canceled.Task);
            Assert.False(stopping.IsCompleted); Assert.False(finished.Task.IsCompleted);
            release.TrySetResult(true);
            await stopping;
            Assert.Equal(TaskStatus.RanToCompletion, finished.Task.Status);
            await send;
        }
        finally
        {
            release.TrySetResult(true);
            await (stopping ?? server.DisposeAsync().AsTask());
            try { await send; } catch (A2ARpcException) { }
        }
    }

    [Fact]
    public async Task ShutdownJoinsAuthorizationBeforeReturningWithoutStartingTheAgent()
    {
        var entered = Signal(); var canceled = Signal(); var release = Signal(); var created = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { created++; throw new InvalidOperationException("must not execute"); } });
        var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            PushNotifications = new()
            {
                AuthorizeConfiguration = async (_, _, token) =>
                {
                    using var registration = token.Register(() => canceled.TrySetResult(true));
                    entered.TrySetResult(true); await release.Task; return true;
                },
                SendAsync = (_, _, _) => default
            }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var send = Client(http).CallAsync("message/send", WithPush(new { url = "https://callback.example/" }));
        Task? stopping = null;
        try
        {
            await BoundedSignalAsync(entered.Task);
            stopping = server.DisposeAsync().AsTask();
            await BoundedSignalAsync(canceled.Task);
            Assert.False(stopping.IsCompleted); release.TrySetResult(true);
            await stopping;
            Assert.Equal(-32603, (await Assert.ThrowsAsync<A2ARpcException>(() => send)).Code);
            Assert.Equal(0, created);
        }
        finally
        {
            release.TrySetResult(true);
            await (stopping ?? server.DisposeAsync().AsTask());
            try { await send; } catch (A2ARpcException) { }
        }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task BoundedSignalAsync(Task signal)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await TaskCancellation.WaitAsync(signal, deadline.Token);
    }
    [Fact]
    public async Task EvictingANonpersistentTaskReclaimsItsPushConfigurationsBeforeTheNextExecution()
    {
        var store = new InMemoryA2APushNotificationConfigStore(maxConfigurations: 1);
        await using var executor = Executor();
        await using var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            MaxTasks = 1,
            PushNotifications = new() { ConfigStore = store, AuthorizeConfiguration = (_, _, _) => new(true), SendAsync = (_, _, _) => default }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var client = Client(http);
        var first = await client.CallAsync("message/send", WithPush(new { url = "https://callback.example/" }));

        var second = await client.CallAsync("message/send", WithPush(new { url = "https://callback.example/" }));

        Assert.Equal("completed", second.GetProperty("status").GetProperty("state").GetString());
        Assert.Empty(await store.ListAsync(first.GetProperty("id").GetString()!));
        Assert.Single(await store.ListAsync(second.GetProperty("id").GetString()!));
    }

    private static A2AExecutor Executor() => new(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new A2AServerTests.EchoModel() })) });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureBeforeFirstPublicationReclaimsConfigurationForTheNextHealthyTask(bool persistentTasks)
    {
        var created = 0;
        var store = new InMemoryA2APushNotificationConfigStore(maxConfigurations: 1);
        await using var executor = new A2AExecutor(new()
        {
            AgentFactory = (_, _) =>
            {
                if (++created == 1) throw new InvalidOperationException("initialization failed");
                return new(new Agent(new() { Model = new A2AServerTests.EchoModel() }));
            }
        });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            TaskStore = persistentTasks ? new InMemoryA2ATaskStore() : null,
            PushNotifications = new() { ConfigStore = store, AuthorizeConfiguration = (_, _, _) => new(true), SendAsync = (_, _, _) => default }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var client = Client(http);
        Assert.Equal(-32603, (await Assert.ThrowsAsync<A2ARpcException>(() => client.CallAsync("message/send", WithPush(new { url = "https://callback.example/" })))).Code);

        var healthy = await client.CallAsync("message/send", WithPush(new { url = "https://callback.example/" }));

        Assert.Equal("completed", healthy.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(2, created);
    }

    private static A2AClient Client(HttpClient http) => new(http, new(new Uri("http://local/")));
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement WithPush(object config) => Json(new { message = A2AServerTests.Send("hello").GetProperty("message"), configuration = new { pushNotificationConfig = config } });
}
