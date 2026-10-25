using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class GoogleLiveVertexTests
{
    private const string ModelResource = "projects/fixture-project/locations/us-central1/publishers/google/models/gemini-live-fixture";

    [Fact]
    public async Task VertexConnectsWithoutAnApiKey()
    {
        await using var server = new WebSocketServer();
        await using var model = new GoogleGeminiLiveModel(Options(server) with { ApiKey = "" });
        var starting = model.StartAsync();
        var peer = await Within(server.Connection);
        await Within(peer.ReceiveAsync());
        await peer.SendAsync("""{"setupComplete":{}}""");
        await starting;
        Assert.Equal("Bearer fixture-token", peer.Headers["Authorization"]);
    }

    [Fact]
    public async Task VertexUsesBearerAuthenticationAndTheExactModelResource()
    {
        await using var server = new WebSocketServer();
        await using var model = new GoogleGeminiLiveModel(Options(server) with { ApiKey = "developer-key-must-not-leak" });
        var starting = model.StartAsync();
        var peer = await Within(server.Connection);
        var setup = (await Within(peer.ReceiveAsync())).GetProperty("setup");
        await peer.SendAsync("""{"setupComplete":{}}""");
        await starting;
        Assert.True(peer.Headers.TryGetValue("Authorization", out var authorization));
        Assert.Equal("Bearer fixture-token", authorization);
        Assert.DoesNotContain("key=", peer.RequestLine);
        Assert.Equal(ModelResource, setup.GetProperty("model").GetString());
    }

    [Fact]
    public async Task VertexSeedsAcknowledgedTextHistoryAndNormalizesNativeOutput()
    {
        await using var server = new WebSocketServer();
        await using var model = new GoogleGeminiLiveModel(Options(server) with
        {
            Params = JsonSerializer.SerializeToElement(new { generationConfig = new { temperature = 0.25 } })
        });
        var starting = model.StartAsync(new() { SystemPrompt = "Be concise.", Messages = [Message.User("earlier"), Message.Assistant("reply")] });
        var peer = await Within(server.Connection);
        var setup = (await Within(peer.ReceiveAsync())).GetProperty("setup");
        Assert.False(setup.TryGetProperty("historyConfig", out _));
        Assert.Equal(ModelResource, setup.GetProperty("model").GetString());
        Assert.Equal("Be concise.", setup.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(0.25, setup.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
        Assert.Equal("AUDIO", setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        Assert.False(starting.IsCompleted);
        await peer.SendAsync("""{"setupComplete":{}}""");
        await starting;
        var history = (await Within(peer.ReceiveAsync())).GetProperty("clientContent");
        Assert.Equal("earlier", history.GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("model", history.GetProperty("turns")[1].GetProperty("role").GetString());
        Assert.Equal("reply", history.GetProperty("turns")[1].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.True(history.GetProperty("turnComplete").GetBoolean());
        await model.SendAsync(new BidiMessage([new TextBlock("now")]));
        Assert.Equal("now", (await Within(peer.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        await peer.SendAsync("""{"serverContent":{"modelTurn":{"parts":[{"text":"answer"},{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AQI="}}]},"turnComplete":true},"usageMetadata":{"promptTokenCount":2,"responseTokenCount":3,"totalTokenCount":5}}""");
        var events = new List<IBidiOutputEvent>();
        while (await reader.MoveNextAsync()) { events.Add(reader.Current); if (reader.Current is BidiResponseStopEvent) break; }
        Assert.Equal("answer", Assert.Single(events.OfType<BidiTextDeltaEvent>()).Delta);
        Assert.Equal("AQI=", Assert.Single(events.OfType<BidiAudioDeltaEvent>()).Audio);
        Assert.Equal(new Usage(2, 3, ReportedTotalTokens: 5), Assert.Single(events.OfType<BidiUsageEvent>()).Usage);
        Assert.Single(events.OfType<BidiAudioStopEvent>());
        Assert.Single(events.OfType<BidiTextStopEvent>());
    }

    [Fact]
    public async Task EveryPhysicalResumeAndFreshFallbackResolvesItsOwnCredential()
    {
        await using var server = new WebSocketServer();
        var calls = 0;
        await using var model = new GoogleGeminiLiveModel(Options(server) with { AccessTokenProvider = _ => Task.FromResult("token-" + Interlocked.Increment(ref calls)) });
        var original = await StartAsync(model, server.Connection);
        await ObserveHandleAsync(model, original);
        var resumedConnection = server.AcceptNextAsync();
        var restarting = model.RestartAsync(new() { Messages = [Message.User("history")] });
        var resumed = await Within(resumedConnection);
        var resumedSetup = (await Within(resumed.ReceiveAsync())).GetProperty("setup");
        Assert.Equal("Bearer token-2", resumed.Headers["Authorization"]);
        Assert.Equal("handle", resumedSetup.GetProperty("sessionResumption").GetProperty("handle").GetString());
        Assert.False(resumedSetup.TryGetProperty("historyConfig", out _));
        var freshConnection = server.AcceptNextAsync();
        await resumed.SendAsync("""{"error":{"code":400,"message":"expired handle"}}""");
        var fresh = await Within(freshConnection);
        var freshSetup = (await Within(fresh.ReceiveAsync())).GetProperty("setup");
        Assert.Equal("Bearer token-3", fresh.Headers["Authorization"]);
        Assert.Equal(ModelResource, freshSetup.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, freshSetup.GetProperty("sessionResumption").GetProperty("handle").ValueKind);
        Assert.False(freshSetup.TryGetProperty("historyConfig", out _));
        await fresh.SendAsync("""{"setupComplete":{}}""");
        await WaitAsync(restarting);
        Assert.Equal("history", (await Within(fresh.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(3, calls);
        Assert.Equal("Bearer token-1", original.Headers["Authorization"]);
    }

    [Fact]
    public async Task AnExpiredResumeDeadlineCannotReplaceTheFreshCredentialFailure()
    {
        await using var server = new WebSocketServer();
        var resumeDeadline = Completion();
        var calls = 0;
        var failure = new IOException("fresh credential fixture failure");
        CancellationTokenRegistration registration = default;
        await using var model = new GoogleGeminiLiveModel(Options(server) with
        {
            SetupTimeout = TimeSpan.FromSeconds(4),
            AccessTokenProvider = async token =>
            {
                var attempt = Interlocked.Increment(ref calls);
                if (attempt == 2) registration = token.Register(() => resumeDeadline.TrySetResult(true));
                if (attempt == 3)
                {
                    await resumeDeadline.Task;
                    Assert.False(token.IsCancellationRequested);
                    throw failure;
                }
                return "token";
            }
        });
        try
        {
            var original = await StartAsync(model, server.Connection);
            await ObserveHandleAsync(model, original);
            var resumedConnection = server.AcceptNextAsync();
            var restarting = model.RestartAsync();
            var resumed = await Within(resumedConnection);
            await Within(resumed.ReceiveAsync());
            // Distinct attempt deadlines leave a deterministic margin for the fresh callback.
            await Task.Delay(TimeSpan.FromSeconds(2));
            await resumed.SendAsync("""{"error":{"code":400,"message":"expired handle"}}""");
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => restarting));
            Assert.Equal(3, calls);
        }
        finally { registration.Dispose(); }
    }

    [Fact]
    public async Task StopThenStartRefreshesTheCredential()
    {
        await using var server = new WebSocketServer();
        var calls = 0;
        await using var model = new GoogleGeminiLiveModel(Options(server) with { AccessTokenProvider = _ => Task.FromResult("token-" + Interlocked.Increment(ref calls)) });
        var first = await StartAsync(model, server.Connection);
        await model.StopAsync();
        var nextConnection = server.AcceptNextAsync();
        var second = await StartAsync(model, nextConnection);
        Assert.Equal("Bearer token-1", first.Headers["Authorization"]);
        Assert.Equal("Bearer token-2", second.Headers["Authorization"]);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CredentialModelExceptionCannotTriggerResumptionFallbackOrCloseTheOldConnection()
    {
        await using var server = new WebSocketServer();
        var calls = 0;
        var failure = new ModelException("credential fixture failure");
        await using var model = new GoogleGeminiLiveModel(Options(server) with
        {
            AccessTokenProvider = _ => Interlocked.Increment(ref calls) == 1 ? Task.FromResult("first-token") : Task.FromException<string>(failure)
        });
        var peer = await StartAsync(model, server.Connection);
        await ObserveHandleAsync(model, peer);
        Assert.Same(failure, await Assert.ThrowsAsync<ModelException>(() => model.RestartAsync()));
        Assert.Equal(2, calls);
        await model.SendAsync(new BidiMessage([new TextBlock("still connected")]));
        Assert.Equal("still connected", (await Within(peer.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task CredentialCallbackCanSendOnThePreviousConnectionBeforeReplacement()
    {
        await using var server = new WebSocketServer();
        var sent = Completion();
        var release = Completion();
        var calls = 0;
        GoogleGeminiLiveModel? model = null;
        model = new(Options(server) with
        {
            AccessTokenProvider = async _ =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    await model!.SendAsync(new BidiMessage([new TextBlock("during authentication")]));
                    sent.TrySetResult(true);
                    await release.Task;
                }
                return "token";
            }
        });
        await using var owned = model;
        var old = await StartAsync(model, server.Connection);
        var nextConnection = server.AcceptNextAsync();
        var restarting = model.RestartAsync();
        try
        {
            await Within(sent.Task);
            Assert.False(restarting.IsCompleted);
            Assert.Equal("during authentication", (await Within(old.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        }
        finally { release.TrySetResult(true); }
        var next = await Within(nextConnection);
        await Within(next.ReceiveAsync());
        await next.SendAsync("""{"setupComplete":{}}""");
        await WaitAsync(restarting);
    }

    [Fact]
    public async Task CancellingCredentialRefreshLeavesThePreviousConnectionUsable()
    {
        await using var server = new WebSocketServer();
        var entered = Completion();
        var cancelled = Completion();
        var release = Completion();
        var calls = 0;
        await using var model = new GoogleGeminiLiveModel(Options(server) with
        {
            AccessTokenProvider = async token =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    using var registration = token.Register(() => cancelled.TrySetResult(true));
                    entered.TrySetResult(true);
                    await release.Task;
                }
                return "token";
            }
        });
        var old = await StartAsync(model, server.Connection);
        await ObserveHandleAsync(model, old);
        using var cancellation = new CancellationTokenSource();
        var restarting = model.RestartAsync(cancellationToken: cancellation.Token);
        try
        {
            await Within(entered.Task);
            cancellation.Cancel();
            await Within(cancelled.Task);
            Assert.False(restarting.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarting);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(2, calls);
        await model.SendAsync(new BidiMessage([new TextBlock("still usable")]));
        Assert.Equal("still usable", (await Within(old.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledCredentialCallbackIsDrainedWithoutOpeningALateConnection(bool throws)
    {
        await using var server = new WebSocketServer();
        var entered = Completion();
        var cancelled = Completion();
        var release = Completion();
        var failure = new IOException("callback fixture failure");
        await using var model = new GoogleGeminiLiveModel(Options(server) with
        {
            AccessTokenProvider = async token =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult(true));
                entered.TrySetResult(true);
                await release.Task;
                if (throws) throw failure;
                return "late-token";
            }
        });
        using var cancellation = new CancellationTokenSource();
        var starting = model.StartAsync(cancellationToken: cancellation.Token);
        try
        {
            await Within(entered.Task);
            cancellation.Cancel();
            await Within(cancelled.Task);
            Assert.False(starting.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        if (throws) Assert.Same(failure, error.InnerException);
        Assert.False(server.Connection.IsCompleted);
    }

    [Fact]
    public async Task DisposalCancelsAndJoinsThePhysicalCredentialCallback()
    {
        await using var server = new WebSocketServer();
        var entered = Completion();
        var cancelled = Completion();
        var release = Completion();
        var model = new GoogleGeminiLiveModel(Options(server) with
        {
            AccessTokenProvider = async token =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult(true));
                entered.TrySetResult(true);
                await release.Task;
                return "late-token";
            }
        });
        var starting = model.StartAsync();
        Task? disposing = null;
        Exception? primary = null;
        try
        {
            await Within(entered.Task);
            disposing = model.DisposeAsync().AsTask();
            await Within(cancelled.Task);
            Assert.False(disposing.IsCompleted);
            Assert.False(starting.IsCompleted);
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            await WaitAsync(disposing);
            await model.DisposeAsync();
            Assert.False(server.Connection.IsCompleted);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            var cleanup = new List<Exception>();
            try { await starting; }
            catch (OperationCanceledException) { }
            catch (Exception error) { cleanup.Add(error); }
            if (disposing is not null)
            {
                try { await disposing; }
                catch (Exception error) { cleanup.Add(error); }
            }
            try { await model.DisposeAsync(); }
            catch (Exception error) { cleanup.Add(error); }
            var failure = primary ?? cleanup.FirstOrDefault();
            if (failure is not null)
            {
                var secondary = cleanup.Where(error => !ReferenceEquals(error, failure)).Distinct().ToList();
                if (secondary.Count > 0)
                {
                    const string key = "Strands.CleanupErrors";
                    if (failure.Data[key] is not List<Exception> retained) failure.Data[key] = retained = [];
                    foreach (var error in secondary) if (!retained.Contains(error)) retained.Add(error);
                }
                if (primary is null) ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    [Fact]
    public async Task SetupDeadlineCancelsAndDrainsCredentialsBeforeReportingTimeout()
    {
        await using var server = new WebSocketServer();
        var entered = Completion();
        var cancelled = Completion();
        var release = Completion();
        await using var model = new GoogleGeminiLiveModel(Options(server) with
        {
            SetupTimeout = TimeSpan.FromMilliseconds(250),
            AccessTokenProvider = async token =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult(true));
                entered.TrySetResult(true);
                await release.Task;
                return "late-token";
            }
        });
        var starting = model.StartAsync();
        try
        {
            await Within(entered.Task);
            await Within(cancelled.Task);
            Assert.False(starting.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAsync<ModelException>(() => starting);
        Assert.Contains("timed out", error.Message);
        Assert.False(server.Connection.IsCompleted);
    }

    [Fact]
    public async Task PrecancelledStartDoesNotInvokeCredentials()
    {
        await using var server = new WebSocketServer();
        var calls = 0;
        await using var model = new GoogleGeminiLiveModel(Options(server) with { AccessTokenProvider = _ => { calls++; return Task.FromResult("token"); } });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.StartAsync(cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, calls);
        Assert.False(server.Connection.IsCompleted);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task CredentialLifecycleReentryFailsBeforeMutation(string operation)
    {
        await using var server = new WebSocketServer();
        GoogleGeminiLiveModel? model = null;
        model = new(Options(server) with
        {
            AccessTokenProvider = async _ =>
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    if (operation == "start") await model!.StartAsync();
                    else if (operation == "restart") await model!.RestartAsync();
                    else if (operation == "stop") await model!.StopAsync();
                    else await model!.DisposeAsync();
                });
                Assert.Contains("credential callback", error.Message);
                return "token";
            }
        });
        await using var owned = model;
        var peer = await StartAsync(model, server.Connection);
        await model.SendAsync(new BidiMessage([new TextBlock("usable")]));
        Assert.Equal("usable", (await Within(peer.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task RetainedExecutionContextStopsBeingReentrantAfterTheCallbackReturns()
    {
        await using var server = new WebSocketServer();
        var release = Completion();
        Task? retained = null;
        GoogleGeminiLiveModel? model = null;
        model = new(Options(server) with
        {
            AccessTokenProvider = _ =>
            {
                retained = Task.Run(async () => { await release.Task; await model!.StopAsync(); });
                return Task.FromResult("token");
            }
        });
        await using var owned = model;
        await StartAsync(model, server.Connection);
        release.TrySetResult(true);
        await WaitAsync(retained!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.SendAsync(new BidiMessage([new TextBlock("stopped")])));
    }

    [Fact]
    public async Task AnAlreadyStartedModelRejectsAdmissionBeforeRefreshingCredentials()
    {
        await using var server = new WebSocketServer();
        var calls = 0;
        await using var model = new GoogleGeminiLiveModel(Options(server) with { AccessTokenProvider = _ => { calls++; return Task.FromResult("token"); } });
        await StartAsync(model, server.Connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.StartAsync());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("secret\r\nInjected: header")]
    [InlineData("secret\u0000")]
    [InlineData("secret雪")]
    public async Task InvalidBearerTokensFailWithoutLeakingTheValueOrConnecting(string? token)
    {
        await using var server = new WebSocketServer();
        await using var model = new GoogleGeminiLiveModel(Options(server) with { AccessTokenProvider = _ => Task.FromResult(token!) });
        var error = await Assert.ThrowsAsync<ArgumentException>(() => model.StartAsync());
        Assert.DoesNotContain("secret", error.ToString());
        Assert.False(server.Connection.IsCompleted);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task VertexRejectsExplicitUnsupportedHistoryConfigurationBeforeCredentials(string value)
    {
        await using var server = new WebSocketServer();
        var calls = 0;
        var options = Options(server) with { Params = JsonDocument.Parse("{\"historyConfig\":" + value + "}").RootElement.Clone(), AccessTokenProvider = _ => { calls++; return Task.FromResult("token"); } };
        Assert.Throws<ArgumentException>(() => new GoogleGeminiLiveModel(options));
        Assert.Equal(0, calls);
        Assert.False(server.Connection.IsCompleted);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("models/model")]
    [InlineData("projects/p/locations/l/publishers/other/models/m")]
    [InlineData("projects//locations/l/publishers/google/models/m")]
    [InlineData("projects/p/locations/l/publishers/google/models/m/extra")]
    public async Task VertexRequiresAFullyQualifiedGoogleModelResource(string modelId)
    {
        await using var server = new WebSocketServer();
        Assert.Throws<ArgumentException>(() => new GoogleGeminiLiveModel(Options(server) with { ModelId = modelId }));
        Assert.False(server.Connection.IsCompleted);
    }

    [Fact]
    public async Task VertexRequiresAnExplicitEndpointAndTokenProvider()
    {
        await using var server = new WebSocketServer();
        Assert.Throws<ArgumentException>(() => new GoogleGeminiLiveModel(Options(server) with { Endpoint = null }));
        Assert.Throws<ArgumentException>(() => new GoogleGeminiLiveModel(Options(server) with { AccessTokenProvider = null }));
        Assert.Throws<ArgumentException>(() => new GoogleGeminiLiveModel(Options(server) with { Endpoint = new Uri("https://example.invalid") }));
        Assert.Throws<ArgumentException>(() => new GoogleGeminiLiveModel(Options(server) with { UseVertexAi = false, ApiKey = "key" }));
        Assert.False(server.Connection.IsCompleted);
    }

    [Fact]
    public async Task VertexConfigurationAndSummaryDoNotRetainCredentials()
    {
        await using var server = new WebSocketServer();
        var provider = new Func<CancellationToken, Task<string>>(_ => Task.FromResult("private-token"));
        await using var model = new GoogleGeminiLiveModel(Options(server) with { ApiKey = "ignored-private-key", AccessTokenProvider = provider });
        var config = model.GetConfig();
        Assert.Null(config.ApiKey);
        Assert.Same(provider, config.AccessTokenProvider);
        var json = JsonSerializer.Serialize(config);
        Assert.DoesNotContain("AccessTokenProvider", json);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("private", config.ToString());
        Assert.DoesNotContain("private", model.Metadata.ToString());
    }

    [Fact]
    public async Task VertexRemovesEndpointApiKeysAndRetainsUnrelatedQueryFields()
    {
        await using var server = new WebSocketServer();
        await using var model = new GoogleGeminiLiveModel(Options(server) with { Endpoint = new Uri(server.Endpoint + "?trace=fixture&%6bey=secret&key=other") });
        var peer = await StartAsync(model, server.Connection);
        Assert.Contains("trace=fixture", peer.RequestLine);
        Assert.DoesNotContain("key", peer.RequestLine);
        Assert.DoesNotContain("secret", peer.RequestLine);
    }

    private static TaskCompletionSource<bool> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitAsync(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(8))) != task) throw new TimeoutException("Vertex fixture exceeded its safety deadline.");
        await task;
    }

    private static async Task<Peer> StartAsync(GoogleGeminiLiveModel model, Task<Peer> connection)
    {
        var starting = model.StartAsync();
        var peer = await Within(connection);
        await Within(peer.ReceiveAsync());
        await peer.SendAsync("""{"setupComplete":{}}""");
        await WaitAsync(starting);
        return peer;
    }

    private static async Task ObserveHandleAsync(GoogleGeminiLiveModel model, Peer peer)
    {
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        await peer.SendAsync("""{"sessionResumptionUpdate":{"resumable":true,"newHandle":"handle"},"usageMetadata":{}}""");
        Assert.True(await reader.MoveNextAsync());
        Assert.IsType<BidiUsageEvent>(reader.Current);
    }

    private static GoogleGeminiLiveModelOptions Options(WebSocketServer server) => new()
    {
        ModelId = ModelResource,
        UseVertexAi = true,
        Endpoint = server.Endpoint,
        AccessTokenProvider = _ => Task.FromResult("fixture-token")
    };
}
