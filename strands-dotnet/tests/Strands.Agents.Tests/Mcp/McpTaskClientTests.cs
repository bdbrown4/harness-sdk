using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpTaskClientTests
{
    [Fact]
    public async Task GenuineLoopbackHttpCarriesEncodedTaskRoutingAndReturnsRichOutput()
    {
        using var handler = new TaskServer
        {
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new Dictionary<string, object>
                { ["query"] = new Dictionary<string, object> { ["type"] = "string", ["x-mcp-header"] = "Query" } }
            })
        };
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await Client.CreateHttpAsync(new McpClientSettings { TasksConfig = new() }, http,
            new HttpClientTransportOptions { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new McpClientOptions { ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(5) });
        await client.ListToolsAsync();

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { query = "Café 雪" }));

        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("answer").GetInt32());
        Assert.Equal("=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(handler.TaskId)) + "?=", Assert.Single(handler.Requests, request => request.Method == "tasks/get").Name);
        Assert.Equal("Café 雪", Assert.Single(handler.Requests, request => request.Method == "tools/call").Parameters.GetProperty("arguments").GetProperty("query").GetString());
        Assert.Equal("=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Café 雪")) + "?=", Assert.Single(handler.Requests, request => request.Method == "tools/call").ParameterHeader);
    }

    [Fact]
    public async Task ExplicitLifecycleRoutesOpaqueTaskIdsAndNeverPollsDuringSubmission()
    {
        using var server = new TaskServer();
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        var submitted = await client.SubmitToolAsync("lookup", JsonSerializer.SerializeToElement(new { query = "value" }));

        Assert.NotNull(submitted.Task);
        Assert.Null(submitted.Result);
        Assert.DoesNotContain(server.Requests, request => request.Method == "tasks/get");
        var state = await client.GetTaskAsync(server.TaskId);
        Assert.Equal(McpTaskStatus.Completed, state.Status);
        Assert.Equal("done", Assert.IsType<TextContentBlock>(Assert.Single(state.Result!.Content)).Text);
        Assert.Equal("complete", (await client.CancelTaskAsync(server.TaskId)).GetProperty("resultType").GetString());
        var get = Assert.Single(server.Requests, request => request.Method == "tasks/get");
        Assert.Equal("=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(server.TaskId)) + "?=", get.Name);
        Assert.Equal("tasks/get", get.RoutingMethod);
        Assert.True(get.Parameters.GetProperty("_meta").GetProperty("io.modelcontextprotocol/clientCapabilities").GetProperty("extensions").TryGetProperty("io.modelcontextprotocol/tasks", out _));
    }

    [Fact]
    public async Task AutomaticToolCallsPollAndValidateTheFinalOutputSchema()
    {
        using var server = new TaskServer();
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);
        await client.ListToolsAsync();

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { query = "value" }));

        Assert.Equal("done", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("answer").GetInt32());
        Assert.Single(server.Requests, request => request.Method == "tasks/get");
    }

    [Fact]
    public async Task AdaptedTaskToolsKeepPrefixCorrelationRichContentAndStructuredOutput()
    {
        using var server = new TaskServer();
        server.Respond = (request, _) => Task.FromResult(request.Method == "tasks/get" ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
        {
            resultType = "complete",
            taskId = server.TaskId,
            status = "completed",
            createdAt = "2026-10-20T10:00:00Z",
            lastUpdatedAt = "2026-10-20T10:00:01Z",
            ttlMs = (int?)null,
            result = new { content = new object[] { new { type = "text", text = "rich" }, new { type = "image", mimeType = "image/png", data = "AQID" }, new { type = "audio", mimeType = "audio/wav", data = "BAUG" } }, structuredContent = new { answer = 42 } }
        }) : null);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);
        var tool = Assert.Single(await client.ListToolsAsync(new() { Prefix = "remote" }));
        await using var agent = new Agent(new() { Model = new UnusedModel() });
        var input = JsonSerializer.SerializeToElement(new { query = "value" });

        var result = await tool.InvokeResultAsync(input, new ToolContext(agent, new ToolUseBlock("caller-id", tool.Spec.Name, input), new Dictionary<string, object?>()));

        Assert.Equal("caller-id", result.ToolUseId);
        Assert.Equal("remote_lookup", tool.Spec.Name);
        Assert.Equal("lookup", Assert.Single(server.Requests, request => request.Method == "tools/call").Parameters.GetProperty("name").GetString());
        Assert.Equal("AQID", Assert.IsType<ImageBlock>(result.ContentBlocks[1]).Data);
        Assert.Equal("BAUG", Assert.IsType<AudioBlock>(result.ContentBlocks[2]).Data);
        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("answer").GetInt32());
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task ServerTerminalStatusesKeepTheirDistinctMeaning(string status)
    {
        using var server = new TaskServer();
        server.Respond = (request, _) =>
        {
            if (request.Method != "tasks/get") return Task.FromResult<HttpResponseMessage?>(null);
            var task = new Dictionary<string, object?> { ["resultType"] = "complete", ["taskId"] = server.TaskId, ["status"] = status, ["createdAt"] = "2026-10-20T10:00:00Z", ["lastUpdatedAt"] = "2026-10-20T10:00:01Z", ["ttlMs"] = null };
            if (status == "completed") task["result"] = new { content = new[] { new { type = "text", text = "tool failed" } }, isError = true };
            if (status == "failed") task["error"] = new { code = -32001, message = "task failed", data = new { detail = 7 } };
            return Task.FromResult<HttpResponseMessage?>(TaskServer.JsonResponse(request.Envelope.GetProperty("id"), task));
        };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();

        if (status == "completed") Assert.True((await invocation).IsError);
        else if (status == "failed")
        {
            var error = await Assert.ThrowsAsync<McpTaskFailedException>(() => invocation);
            Assert.Equal(-32001, error.Code);
            Assert.Equal(7, error.Details!.Value.GetProperty("detail").GetInt32());
        }
        else await Assert.ThrowsAsync<McpTaskCancelledException>(() => invocation);
        Assert.DoesNotContain(server.Requests, request => request.Method == "tasks/cancel");
    }

    [Fact]
    public async Task CallerCancellationKeepsItsTokenAndRequestsCooperativeTaskCancellation()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new TaskServer
        {
            Respond = async (request, token) =>
            {
                if (request.Method != "tasks/get") return null;
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return null;
            }
        };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);
        using var cancellation = new CancellationTokenSource();
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), cancellationToken: cancellation.Token).AsTask();
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(server.Requests, request => request.Method == "tasks/cancel");
    }

    [Fact]
    public async Task RequestDeadlineIsReportedAsTimeoutInsteadOfCallerCancellation()
    {
        using var server = new TaskServer
        {
            Respond = async (request, token) =>
            {
                if (request.Method != "tasks/get") return null;
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return null;
            }
        };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, new() { RequestTimeout = TimeSpan.FromMilliseconds(200), PollTimeout = TimeSpan.FromSeconds(10), PollInterval = TimeSpan.FromMilliseconds(10) });

        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.GetTaskAsync(server.TaskId).AsTask());

        Assert.Contains("tasks/get", error.Message);
    }

    [Fact]
    public async Task CancellationDoesNotWaitForAnUncooperativeHttpSend()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new TaskServer
        {
            Respond = async (request, _) =>
            {
                if (request.Method != "tasks/get") return null;
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return null;
            }
        };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);
        using var cancellation = new CancellationTokenSource();
        var invocation = client.GetTaskAsync(server.TaskId, cancellation.Token).AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));
            cancellation.Cancel();
            Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(1000)));
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation)).CancellationToken);
        }
        finally
        {
            release.TrySetResult(true);
            try { await invocation; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task EveryEmbeddedInputRequestIsValidatedBeforeAnyHandlerRuns()
    {
        var calls = 0;
        using var server = new TaskServer();
        server.Respond = (request, _) => Task.FromResult(request.Method == "tasks/get" ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), InputState(server.TaskId, new Dictionary<string, object>
        {
            ["first"] = new { method = "elicitation/create", @params = new { message = "approve", requestedSchema = new { type = "object", properties = new { } } } },
            ["malformed"] = new { method = "elicitation/create", @params = new { message = 42, requestedSchema = new { type = "object", properties = new { } } } }
        })) : null);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, handlers: new()
        {
            ElicitationHandler = (_, _) => { calls++; return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }); }
        });

        var error = await Record.ExceptionAsync(() => client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask());

        Assert.Equal(0, calls);
        Assert.IsType<InvalidDataException>(error);
        Assert.DoesNotContain(server.Requests, request => request.Method == "tasks/update");
    }

    [Theory]
    [InlineData("{\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[{\"content\":{\"type\":\"text\",\"text\":\"hello\"}}],\"maxTokens\":1}}")]
    [InlineData("{\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[{\"role\":\"system\",\"content\":{\"type\":\"text\",\"text\":\"hello\"}}],\"maxTokens\":1}}")]
    [InlineData("{\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[{\"role\":\"user\",\"content\":null}],\"maxTokens\":1}}")]
    [InlineData("{\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[{\"role\":\"user\",\"content\":{\"type\":\"resource_link\",\"name\":\"secret\",\"uri\":\"file:///secret\"}}],\"maxTokens\":1}}")]
    [InlineData("{\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[{\"role\":\"user\",\"content\":{\"type\":\"text\",\"text\":42}}],\"maxTokens\":1}}")]
    [InlineData("{\"method\":\"sampling/createMessage\",\"params\":{\"messages\":[{\"role\":\"user\",\"content\":{\"type\":\"text\",\"text\":\"hello\"},\"_meta\":true}],\"maxTokens\":1}}")]
    [InlineData("{\"method\":\"elicitation/create\",\"params\":{\"message\":\"approve\",\"requestedSchema\":[]}}")]
    [InlineData("{\"method\":\"elicitation/create\",\"params\":{\"message\":\"approve\"}}")]
    public async Task NestedMalformedInputsCannotFollowAnAlreadyInvokedHandler(string malformed)
    {
        var calls = 0;
        using var document = JsonDocument.Parse(malformed);
        using var server = new TaskServer();
        server.Respond = (request, _) => Task.FromResult(request.Method == "tasks/get" ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), InputState(server.TaskId, new Dictionary<string, object>
        {
            ["first"] = new { method = "elicitation/create", @params = new { message = "approve", requestedSchema = new { type = "object", properties = new { } } } },
            ["malformed"] = document.RootElement.Clone()
        })) : null);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, handlers: new()
        {
            ElicitationHandler = (_, _) => { calls++; return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }); }
        });

        var error = await Record.ExceptionAsync(() => client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask());

        Assert.Equal(0, calls);
        Assert.IsType<InvalidDataException>(error);
        Assert.DoesNotContain(server.Requests, request => request.Method == "tasks/update");
    }

    [Fact]
    public async Task MalformedHandlerResponsesCannotBeSentAsTaskUpdates()
    {
        using var server = new TaskServer();
        var gets = 0;
        server.Respond = (request, _) => Task.FromResult(request.Method == "tasks/get" && ++gets == 1
            ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), InputState(server.TaskId, new Dictionary<string, object>
            {
                ["question"] = new { method = "elicitation/create", @params = new { message = "approve", requestedSchema = new { type = "object", properties = new { } } } }
            })) : null);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, handlers: new()
        {
            ElicitationHandler = (_, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = "not-a-valid-action" })
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask());

        Assert.DoesNotContain(server.Requests, request => request.Method == "tasks/update");
        Assert.Single(server.Requests, request => request.Method == "tasks/cancel");
    }

    [Fact]
    public async Task InputPollingDeduplicatesAcceptedKeysAndSendsCompleteValidatedBatches()
    {
        var gets = 0;
        var callbacks = new List<string>();
        using var server = new TaskServer();
        server.Respond = (request, _) =>
        {
            if (request.Method != "tasks/get" || ++gets > 2) return Task.FromResult<HttpResponseMessage?>(null);
            var inputs = new Dictionary<string, object>
            {
                ["a"] = new { method = "elicitation/create", @params = new { message = "first", requestedSchema = new { type = "object", properties = new { } } } },
                ["b"] = new { method = "elicitation/create", @params = new { message = "second", requestedSchema = new { type = "object", properties = new { } } } }
            };
            if (gets == 2) inputs["c"] = new { method = "elicitation/create", @params = new { message = "third", requestedSchema = new { type = "object", properties = new { } } } };
            return Task.FromResult<HttpResponseMessage?>(TaskServer.JsonResponse(request.Envelope.GetProperty("id"), InputState(server.TaskId, inputs)));
        };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, handlers: new()
        {
            ElicitationHandler = (request, _) => { callbacks.Add(request!.Message); return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }); }
        });

        await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal(new[] { "first", "second", "third" }, callbacks);
        var updates = server.Requests.Where(request => request.Method == "tasks/update").ToArray();
        Assert.Equal(2, updates.Length);
        Assert.Equal(new[] { "a", "b" }, updates[0].Parameters.GetProperty("inputResponses").EnumerateObject().Select(property => property.Name));
        Assert.Equal("c", Assert.Single(updates[1].Parameters.GetProperty("inputResponses").EnumerateObject()).Name);
    }

    [Fact]
    public async Task ConfiguredTasksCanReturnADirectResultWithoutCreatingOrPollingATask()
    {
        using var server = new TaskServer();
        server.Respond = (request, _) => Task.FromResult(request.Method == "tools/call" ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
        { resultType = "complete", content = new[] { new { type = "text", text = "direct" } } }) : null);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal("direct", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.DoesNotContain(server.Requests, request => request.Method is "tasks/get" or "tasks/cancel");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648L)]
    public async Task InvalidTaskDurationsFailBeforeConnecting(long milliseconds)
    {
        using var server = new TaskServer();
        using var http = new HttpClient(server);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ConnectAsync(http, new() { PollTimeout = TimeSpan.FromMilliseconds(milliseconds) }));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task EnablingTasksDoesNotMutateCallerCapabilitiesOrMetadata()
    {
        using var server = new TaskServer();
        using var http = new HttpClient(server);
        var capabilities = JsonSerializer.Deserialize<ClientCapabilities>("{\"extensions\":{\"example.test/feature\":{\"enabled\":true}}}")!;
        var metadata = System.Text.Json.Nodes.JsonNode.Parse("{\"caller\":true}")!.AsObject();
        var options = new McpClientOptions { ProtocolVersion = "2026-07-28", Capabilities = capabilities, InitializeMeta = metadata };
        var before = JsonSerializer.Serialize(capabilities);
        await using var client = await Client.CreateHttpAsync(new McpClientSettings { TasksConfig = new() }, http,
            new HttpClientTransportOptions { Endpoint = new Uri("https://mcp.example.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false }, options);

        await client.GetTaskAsync(server.TaskId);

        Assert.Equal(before, JsonSerializer.Serialize(capabilities));
        Assert.Equal("{\"caller\":true}", metadata.ToJsonString());
        Assert.Same(capabilities, options.Capabilities);
        Assert.Same(metadata, options.InitializeMeta);
    }

    [Fact]
    public async Task DisposingAClientStopsPollingWhileLeavingItsHttpClientBorrowed()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new TaskServer
        {
            Respond = async (request, token) =>
            {
                if (request.Method != "tasks/get") return null;
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return null;
            }
        };
        using var http = new HttpClient(server);
        var client = await ConnectAsync(http);
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        await client.DisposeAsync();

        Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(5000)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.False(client.IsConnected);
        Assert.False(server.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetTaskAsync(server.TaskId).AsTask());
        await client.DisposeAsync();
    }

    [Theory]
    [InlineData("tasks/get")]
    [InlineData("tasks/update")]
    [InlineData("tasks/cancel")]
    public async Task LifecycleResultsCannotChangeIdentityOrSmuggleTaskStateIntoAnAcknowledgement(string method)
    {
        using var server = new TaskServer();
        server.Respond = (request, _) => Task.FromResult(request.Method == method ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
        { resultType = "complete", taskId = "unrelated", status = "working", createdAt = "2026-10-20T10:00:00Z", lastUpdatedAt = "2026-10-20T10:00:00Z", ttlMs = (int?)null }) : null);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        if (method == "tasks/get") await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTaskAsync(server.TaskId).AsTask());
        else if (method == "tasks/cancel") await Assert.ThrowsAsync<InvalidDataException>(() => client.CancelTaskAsync(server.TaskId).AsTask());
        else await Assert.ThrowsAsync<InvalidDataException>(() => client.UpdateTaskAsync(server.TaskId, new Dictionary<string, InputResponse>()).AsTask());
    }

    [Fact]
    public async Task DisposalPublishesItsSharedTaskBeforeCallingInputCancellationCallbacks()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? reentrantDisposal = null;
        Client? client = null;
        using var server = new TaskServer();
        server.Respond = (request, _) => Task.FromResult(request.Method == "tasks/get" ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), InputState(server.TaskId, new Dictionary<string, object>
        {
            ["question"] = new { method = "elicitation/create", @params = new { message = "approve", requestedSchema = new { type = "object", properties = new { } } } }
        })) : null);
        using var http = new HttpClient(server);
        client = await ConnectAsync(http, handlers: new()
        {
            ElicitationHandler = async (_, token) =>
            {
                using var registration = token.Register(() =>
                {
                    reentrantDisposal = client!.DisposeAsync().AsTask();
                    cancelled.TrySetResult(true);
                });
                entered.TrySetResult(true);
                await cancelled.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new ElicitResult { Action = "cancel" };
            }
        });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        var disposal = client.DisposeAsync().AsTask();
        Assert.Same(disposal, await Task.WhenAny(disposal, Task.Delay(5000)));
        await disposal;

        Assert.Same(disposal, reentrantDisposal);
        Assert.Same(disposal, client.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
    }

    [Fact]
    public async Task TaskShutdownObservationDoesNotPermitProviderLifecycleReentry()
    {
        var entered = McpToolProviderTests.Signal();
        var cancelled = McpToolProviderTests.Signal();
        Task? observed = null;
        Task? unexpectedRemoval = null;
        Exception? observationFailure = null;
        Exception? providerFailure = null;
        Client? client = null;
        McpToolProvider? provider = null;
        using var server = InputCancellationServer();
        using var http = new HttpClient(server);
        await using var agent = McpRouterTests.NewAgent();
        var context = new ToolProviderContext(agent);
        client = await ConnectAsync(http, handlers: new()
        {
            ElicitationHandler = async (_, token) =>
            {
                using var registration = token.Register(() =>
                {
                    try { observed = client!.DisposeAsync().AsTask(); }
                    catch (Exception error) { observationFailure = error; }
                    try { unexpectedRemoval = provider!.RemoveConsumerAsync(context).AsTask(); }
                    catch (Exception error) { providerFailure = error; }
                    cancelled.TrySetResult(true);
                });
                entered.TrySetResult(true);
                await cancelled.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new ElicitResult { Action = "cancel" };
            }
        });
        provider = new(_ => Task.FromResult(client));
        await provider.AddConsumerAsync(context);
        await provider.LoadToolsAsync(context);
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();
        try
        {
            await McpToolProviderTests.Bounded(entered.Task);
            var removing = provider.RemoveConsumerAsync(context).AsTask();
            await McpToolProviderTests.Bounded(removing);
            await removing;
            Assert.Null(observationFailure);
            Assert.Same(client.DisposeAsync().AsTask(), observed);
            Assert.IsType<InvalidOperationException>(providerFailure);
            Assert.Null(unexpectedRemoval);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
            Assert.False(server.Disposed);
        }
        finally
        {
            cancelled.TrySetResult(true);
            await client.DisposeAsync();
            await provider.RemoveConsumerAsync(context);
            if (unexpectedRemoval is not null) await unexpectedRemoval;
        }
    }

    [Fact]
    public async Task ThrowingTaskCancellationCallbackPreservesItsCauseAndUnwindsItsScope()
    {
        var entered = McpToolProviderTests.Signal();
        var cancelled = McpToolProviderTests.Signal();
        var original = new IOException("task cancellation observer failed");
        Task? observed = null;
        Task? otherDisposal = null;
        Client? client = null;
        using var server = InputCancellationServer();
        using var http = new HttpClient(server);
        using var otherServer = new TaskServer();
        using var otherHttp = new HttpClient(otherServer);
        await using var other = await ConnectAsync(otherHttp);
        client = await ConnectAsync(http, handlers: new()
        {
            ElicitationHandler = async (_, token) =>
            {
                using var registration = token.Register(() =>
                {
                    try
                    {
                        otherDisposal = other.DisposeAsync().AsTask();
                        observed = client!.DisposeAsync().AsTask();
                    }
                    finally { cancelled.TrySetResult(true); }
                    throw original;
                });
                entered.TrySetResult(true);
                await cancelled.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new ElicitResult { Action = "cancel" };
            }
        });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();
        Task? disposal = null;
        try
        {
            await McpToolProviderTests.Bounded(entered.Task);
            disposal = client.DisposeAsync().AsTask();
            // This second call runs on the initiating thread after the synchronous callback unwinds.
            Assert.Same(disposal, client.DisposeAsync().AsTask());
            await McpToolProviderTests.Bounded(disposal);
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposal);
            Assert.Contains(failure.Flatten().InnerExceptions, error => ReferenceEquals(error, original));
            Assert.Same(disposal, observed);
            Assert.NotNull(otherDisposal);
            await otherDisposal;
            Assert.Same(disposal, client.DisposeAsync().AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
            Assert.False(server.Disposed);
            Assert.False(otherServer.Disposed);
        }
        finally
        {
            cancelled.TrySetResult(true);
            var joining = disposal ?? client.DisposeAsync().AsTask();
            try { await McpToolProviderTests.Bounded(joining); await joining; }
            catch (AggregateException error) when (error.Flatten().InnerExceptions.All(cause => ReferenceEquals(cause, original))) { }
        }
    }

    [Fact]
    public async Task OwnedTransportSynchronousCleanupStillRefusesDirectClientReentry()
    {
        using var server = new McpRouterTests.RouterServer();
        using var http = new HttpClient(server);
        Client? client = null;
        Task? reentered = null;
        Exception? rejected = null;
        Task Observe()
        {
            try { reentered = client!.DisposeAsync().AsTask(); }
            catch (Exception error) { rejected = error; }
            return Task.CompletedTask;
        }
        var transport = new McpToolProviderLifetimeTests.OwnedTransport(http)
        {
            Disposing = Observe
        };
        client = await transport.ConnectClientAsync(CancellationToken.None);
        var disposal = client.DisposeAsync().AsTask();
        await McpToolProviderTests.Bounded(disposal);
        await disposal;
        Assert.IsType<InvalidOperationException>(rejected);
        Assert.Null(reentered);
        Assert.Equal(1, transport.Disposals);
        Assert.Same(disposal, client.DisposeAsync().AsTask());
        Assert.False(server.Disposed);
    }

    [Fact]
    public async Task ADeferredProtocolSessionCallbackCanObserveDisposalAfterTheSynchronousPrefixEnds()
    {
        using var server = new McpRouterTests.RouterServer();
        using var http = new HttpClient(server);
        var entered = McpToolProviderTests.Signal();
        var release = McpToolProviderTests.Signal();
        Client? client = null;
        Task? observed = null;
        var transport = new McpToolProviderLifetimeTests.OwnedTransport(http)
        {
            BeforeSessionDispose = async () =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                observed = client!.DisposeAsync().AsTask();
            }
        };
        client = await transport.ConnectClientAsync(CancellationToken.None);
        var disposal = client.DisposeAsync().AsTask();
        try
        {
            await McpToolProviderTests.Bounded(entered.Task);
            Assert.False(disposal.IsCompleted);
            release.TrySetResult(true);
            await McpToolProviderTests.Bounded(disposal);
            await disposal;
            Assert.Same(disposal, observed);
            Assert.Equal(1, transport.Disposals);
            Assert.False(server.Disposed);
        }
        finally
        {
            release.TrySetResult(true);
            await client.DisposeAsync();
        }
    }

    private static TaskServer InputCancellationServer()
    {
        var server = new TaskServer();
        server.Respond = (request, _) => Task.FromResult(request.Method == "tasks/get" ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), InputState(server.TaskId, new Dictionary<string, object>
        {
            ["question"] = new { method = "elicitation/create", @params = new { message = "approve", requestedSchema = new { type = "object", properties = new { } } } }
        })) : null);
        return server;
    }

    internal static object InputState(string taskId, Dictionary<string, object> inputRequests) => new
    {
        resultType = "complete",
        taskId,
        status = "input_required",
        createdAt = "2026-10-20T10:00:00Z",
        lastUpdatedAt = "2026-10-20T10:00:01Z",
        ttlMs = (int?)null,
        inputRequests
    };

    internal static Task<Client> ConnectAsync(HttpClient http, McpTasksConfig? tasks = null, McpClientHandlers? handlers = null) => Client.CreateHttpAsync(
        new McpClientSettings { TasksConfig = tasks ?? new() { PollInterval = TimeSpan.FromMilliseconds(10) } }, http,
        new HttpClientTransportOptions { Endpoint = new Uri("https://mcp.example.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new McpClientOptions { ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(5), Handlers = handlers ?? new() });

    internal sealed class TaskServer : HttpMessageHandler
    {
        internal string TaskId { get; } = " task/\u96ea ";
        internal List<Request> Requests { get; } = [];
        internal bool Disposed { get; private set; }
        internal JsonElement? InputSchema { get; set; }
        internal Func<Request, CancellationToken, Task<HttpResponseMessage?>>? Respond { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            var text = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement.Clone();
            var method = root.GetProperty("method").GetString()!;
            var recorded = new Request(method, root.TryGetProperty("params", out var parameters) ? parameters.Clone() : JsonSerializer.SerializeToElement(new { }),
                request.Headers.TryGetValues("Mcp-Name", out var names) ? names.Single() : null,
                request.Headers.TryGetValues("Mcp-Method", out var methods) ? methods.Single() : null, root,
                request.Headers.TryGetValues("Mcp-Param-Query", out var query) ? query.Single() : null);
            Requests.Add(recorded);
            if (!root.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            if (Respond is { } respond && await respond(recorded, cancellationToken).ConfigureAwait(false) is { } custom) return custom;
            object result = method switch
            {
                "server/discover" => new { supportedVersions = new[] { "2026-07-28" }, _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/serverInfo"] = new { name = "tasks-fixture", version = "1.0" } }, capabilities = new { tools = new { }, extensions = new Dictionary<string, object> { ["io.modelcontextprotocol/tasks"] = new { } } }, ttlMs = 0, cacheScope = "private" },
                "initialize" => new { protocolVersion = "2026-07-28", serverInfo = new { name = "tasks-fixture", version = "1.0" }, capabilities = new { tools = new { }, extensions = new Dictionary<string, object> { ["io.modelcontextprotocol/tasks"] = new { } } } },
                "tools/list" => new { tools = new[] { new { name = "lookup", description = "Find a value.", inputSchema = (object?)InputSchema ?? new { type = "object" }, outputSchema = new { type = "object", properties = new { answer = new { type = "integer" } }, required = new[] { "answer" } } } }, ttlMs = (int?)null, cacheScope = "private" },
                "tools/call" => new { resultType = "task", taskId = TaskId, status = "working", createdAt = "2026-10-20T10:00:00Z", lastUpdatedAt = "2026-10-20T10:00:00Z", ttlMs = (int?)null, pollIntervalMs = 10 },
                "tasks/get" => new { resultType = "complete", taskId = TaskId, status = "completed", createdAt = "2026-10-20T10:00:00Z", lastUpdatedAt = "2026-10-20T10:00:01Z", ttlMs = (int?)null, result = new { content = new[] { new { type = "text", text = "done" } }, structuredContent = new { answer = 42 } } },
                "tasks/update" or "tasks/cancel" => new { resultType = "complete" },
                _ => throw new InvalidOperationException("Unexpected MCP method: " + method)
            };
            return JsonResponse(id, result);
        }

        internal static HttpResponseMessage JsonResponse(JsonElement id, object result) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    internal sealed record Request(string Method, JsonElement Parameters, string? Name, string? RoutingMethod, JsonElement Envelope, string? ParameterHeader = null);
    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This test directly invokes a discovered MCP task tool.");
    }
}
