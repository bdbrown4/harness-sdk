using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;
using Strands.Agents.Models;
using Strands.Agents.SessionRepositories;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryStatefulLifetimeTests
{
    [Fact]
    public async Task FreshHumanResumeSendsOnlyTheAnswerResultAndRunsOneAcceptedBody()
    {
        var repository = new JournalProbeRepository();
        await using var server = new StatefulResponsesServer(StatefulResponsesServer.Call("first"), StatefulResponsesServer.Response("second"));
        using var client = new HttpClient();
        var model = Model(client, server);
        var effects = 0;
        var tool = Approval(() => effects++);
        string id;
        await using (var first = Create(repository, model, tool))
        {
            id = Assert.Single((await first.InvokeAsync("approve")).Interrupts).Id;
            Assert.Equal(0, effects);
        }
        await using (var restored = Create(repository, model, tool))
        {
            await restored.InvokeAsync(null, new() { InterruptResponses = [new(id, JsonSerializer.SerializeToElement(true))] });
            Assert.Empty(restored.Messages);
            Assert.Null(restored.InterruptState);
        }
        Assert.Equal(1, effects);
        AssertOutput(server.Requests[1], "approved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedAbortCanRetryOrResumeWithoutPublishingItsUnconfirmedOutput(bool retryAbort)
    {
        var repository = new JournalProbeRepository();
        await using var server = new StatefulResponsesServer(StatefulResponsesServer.Call("first"), StatefulResponsesServer.Response("second"));
        using var client = new HttpClient();
        var effects = 0;
        var tool = Approval(() => effects++);
        await using (var first = Create(repository, Model(client, server), tool))
        {
            var id = Assert.Single((await first.InvokeAsync("approve")).Interrupts).Id;
            var original = JsonSerializer.Serialize(first.InterruptState);
            var fail = true;
            var primary = new IOException("abort metadata failed");
            repository.Before = (operation, _) => { if (operation == "update-agent" && fail) { fail = false; throw primary; } return default; };
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await first.AbortInterruptsAsync("declined")));
            Assert.Equal(original, JsonSerializer.Serialize(first.InterruptState));
            if (retryAbort)
            {
                Assert.True(await first.AbortInterruptsAsync("declined"));
                Assert.Equal(new long[] { 0, 1, 2 }, (await repository.Inner.ListMessagesAsync("session", "agent")).Select(row => row.MessageId));
            }
            else
            {
                await first.InvokeAsync(null, new() { InterruptResponses = [new(id, JsonSerializer.SerializeToElement(true))] });
            }
        }
        await using var restored = Create(repository, Model(client, server), tool);
        await restored.InitializeAsync();
        if (retryAbort) await restored.InvokeAsync(null);
        Assert.Empty(restored.Messages);
        Assert.Equal(retryAbort ? 0 : 1, effects);
        AssertOutput(server.Requests[1], retryAbort ? "declined" : "approved");
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task FailedAcceptedMetadataIsRetriedBeforeFreshContinuationWithoutAnotherProviderCall()
    {
        var repository = new JournalProbeRepository();
        await using var server = new StatefulResponsesServer(StatefulResponsesServer.Response("first"), StatefulResponsesServer.Response("second"));
        using var client = new HttpClient();
        await using (var first = Create(repository, Model(client, server)))
        {
            var primary = new IOException("accepted metadata failed"); var fail = true;
            repository.Before = (operation, _) =>
            {
                if (operation == "update-agent" && first.ModelState.Get("openaiResponses") is not null && fail) { fail = false; throw primary; }
                return default;
            };
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => first.InvokeAsync("first input")));
            Assert.Empty(first.Messages);
            Assert.Single(server.Requests);
            var stored = (await repository.Inner.ReadAgentAsync("session", "agent"))!;
            Assert.Equal("first", stored.InternalState.GetProperty("snapshot").GetProperty("modelState").GetProperty("openaiResponses").GetProperty("responseId").GetString());
            Assert.Equal(0, stored.InternalState.GetProperty("statefulProjection").GetProperty("activeMessageIds").GetArrayLength());
        }
        await using var restored = Create(repository, Model(client, server));
        await restored.InvokeAsync("next");
        Assert.Equal("first", server.Requests[1].GetProperty("previous_response_id").GetString());
        Assert.Equal(1, server.Requests[1].GetProperty("input").GetArrayLength());
        Assert.Equal(new long[] { 0, 1, 2, 3 }, (await repository.Inner.ListMessagesAsync("session", "agent")).Select(row => row.MessageId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InconsistentClearReceiptPreservesHistoryCursorAndAfterInvocationCausality(bool observerThrows)
    {
        var repository = new JournalProbeRepository();
        await using var server = new StatefulResponsesServer(StatefulResponsesServer.Response("first"));
        using var client = new HttpClient();
        var manager = new RepositorySessionManager("session", repository);
        var hooks = new HookRegistry(); var primary = new IOException("message observer");
        Exception? afterError = null; var after = 0;
        hooks.Register<MessageAddedEvent>((value, _) =>
        {
            if (value.Message.Role != Role.Assistant) return default;
            // Deliberately corrupt only the internal receipt fixture: public history mutation is forbidden.
            value.Agent.GetRepositoryJournal(manager).PrepareAcknowledgedHistoryClear(value.Agent.Messages)!.Accept();
            if (observerThrows) throw primary;
            return default;
        });
        hooks.Register<AfterInvocationEvent>((value, _) => { after++; afterError = value.Error; return default; });
        await using var agent = new Agent(new() { Id = "agent", Model = Model(client, server), Hooks = hooks, Plugins = [manager] });
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => agent.InvokeAsync("input"));
        if (observerThrows) { Assert.Same(primary, failure); Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"]); }
        else Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(1, after); Assert.Same(failure, afterError);
        Assert.Equal(2, agent.Messages.Count);
        Assert.Equal(2, (await agent.CaptureRepositorySnapshotAsync(CancellationToken.None)).ModelHistoryCount);
        Assert.Single(await repository.Inner.ListMessagesAsync("session", "agent"));
        Assert.False((await repository.Inner.ReadAgentAsync("session", "agent"))!.InternalState.GetProperty("snapshot").GetProperty("modelState").TryGetProperty("openaiResponses", out _));
    }

    [Fact]
    public async Task EarlyStreamClosureDoesNotForgeAProviderAcknowledgement()
    {
        var repository = new JournalProbeRepository();
        await using var server = new StatefulResponsesServer(StatefulResponsesServer.Response("unaccepted"));
        using var client = new HttpClient();
        await using (var first = Create(repository, Model(client, server)))
        {
            await using var stream = first.StreamAsync("input").GetAsyncEnumerator();
            while (await stream.MoveNextAsync())
                if (stream.Current is ModelStreamUpdateEvent { Event: ModelMessageStopEvent }) break;
        }
        await using var restored = Create(repository, new JournalStatefulModel());
        await restored.InitializeAsync();
        Assert.Equal("input", Assert.Single(restored.Messages).Text);
        Assert.Empty(restored.ModelState.GetAll());
    }

    [Fact]
    public async Task AFailedAcceptedRowRetainsItsIdentityAndFinishesWithoutAnotherProviderCall()
    {
        var repository = new JournalProbeRepository();
        var primary = new IOException("assistant row failed"); var fail = true;
        repository.Before = (operation, _) =>
        {
            if (operation == "create-message" && repository.MessageAttempts.Count == 2 && fail) { fail = false; throw primary; }
            return default;
        };
        await using var server = new StatefulResponsesServer(StatefulResponsesServer.Response("first"));
        using var client = new HttpClient();
        await using (var first = Create(repository, Model(client, server)))
        {
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => first.InvokeAsync("input")));
            Assert.Empty(first.Messages);
        }
        Assert.Single(server.Requests);
        Assert.Equal(new long[] { 0, 1, 1 }, repository.MessageAttempts);
        await using var restored = Create(repository, new JournalStatefulModel());
        await restored.InitializeAsync();
        Assert.Empty(restored.Messages);
        Assert.Equal("first", restored.ModelState.Get("openaiResponses")!.Value.GetProperty("responseId").GetString());
    }

    [Fact]
    public async Task SyntheticMiddlewareCompletionRetainsPendingInputAndCannotInventAnAcknowledgement()
    {
        var repository = new JournalProbeRepository();
        await using var server = new StatefulResponsesServer();
        using var client = new HttpClient();
        await using (var first = Create(repository, Model(client, server)))
        {
            first.Middleware.RegisterWrap(MiddlewareStages.InvokeModel, Cached);
            Assert.Equal(StopReasons.Cancelled, (await first.InvokeAsync("input")).StopReason);
            Assert.Empty(server.Requests);
        }
        await using var restored = Create(repository, new JournalStatefulModel());
        await restored.InitializeAsync();
        Assert.Equal(new[] { "input", "cached pending message" }, restored.Messages.Select(message => message.Text));
        Assert.Empty(restored.ModelState.GetAll());
        Assert.Equal(0, (await restored.CaptureRepositorySnapshotAsync(CancellationToken.None)).ModelHistoryCount);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cached(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            context.Agent.Cancel();
            yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached pending message"), StopReasons.EndTurn, new()));
        }
    }

    private static Agent Create(ISessionRepository repository, IModel model, params ITool[] tools)
        => new(new() { Id = "agent", Model = model, Tools = tools, Plugins = [new RepositorySessionManager("session", repository)] });
    private static OpenAIResponsesModel Model(HttpClient client, StatefulResponsesServer server)
        => new(client, new("model", "key") { Stateful = true, Endpoint = server.Endpoint });
    private static FunctionTool Approval(Action effect) => new(new("save", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
    {
        if (!context.Interrupt("approval").GetBoolean()) return new("declined");
        effect(); return new("approved");
    });
    private static void AssertOutput(JsonElement request, string expected)
    {
        Assert.Equal("first", request.GetProperty("previous_response_id").GetString());
        var input = Assert.Single(request.GetProperty("input").EnumerateArray());
        Assert.Equal("function_call_output", input.GetProperty("type").GetString());
        Assert.Equal("call", input.GetProperty("call_id").GetString());
        Assert.Contains(expected, input.GetProperty("output").GetString(), StringComparison.Ordinal);
    }
}
