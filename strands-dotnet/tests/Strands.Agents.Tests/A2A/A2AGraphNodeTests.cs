using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.A2A;
using Strands.Agents.MultiAgent;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AGraphNodeTests
{
    [Fact]
    public async Task RealHttpRemoteNodeStreamsTypedOutputAndLeavesCallerStateLocal()
    {
        var factories = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { factories++; return new(new Agent(new() { Model = new A2AServerTests.EchoModel() })); } });
        await using var wire = new A2AServerTcpFixture(uri => new A2AServer(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
        using var http = new HttpClient();
        var graph = new Graph(new() { Nodes = [new A2ANode(new(http, new(wire.BaseUri)), "remote")] });
        var caller = new Dictionary<string, object?> { ["secret-local"] = "must remain local" };
        var updates = new List<IMultiAgentStreamEvent>();
        await foreach (var update in graph.StreamAsync(new("hello"), new() { InvocationState = caller })) updates.Add(update);

        var result = Assert.IsType<MultiAgentResultEvent>(updates.Last()).Result;
        Assert.Equal(Status.Completed, result.Status); Assert.Contains("hello", result.ToString());
        Assert.Equal(1, factories); Assert.Single(caller);
        Assert.Contains(updates, update => update is NodeStreamUpdateEvent { Inner: A2AStreamUpdateEvent });
    }
    [Theory]
    [InlineData(A2AContentMode.Rich)]
    [InlineData(A2AContentMode.TextOnly)]
    public async Task FreshFileBackedGraphResumesTheOriginalRemoteTaskWithoutRepeatingAcceptedEffects(A2AContentMode contentMode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-a2a-graph-" + Guid.NewGuid().ToString("N"));
        var effects = 0; var approvals = 0; var factories = 0;
        try
        {
            await using var executor = new A2AExecutor(new() { AgentFactory = (id, _) => { factories++; return new(A2ARecoveryTests.CreateAgent(id, new AbortInterruptTests.PausingModel(), new FileSessionStore(Path.Combine(directory, "agents")), () => effects++, () => approvals++)); } });
            await using var wire = new A2AServerTcpFixture(uri => new A2AServer(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
            using var http = new HttpClient();
            Graph Create() => new(new() { Id = "remote-graph", SessionId = "session", SessionStore = new StorageMultiAgentSessionStore(new LocalFileStorage(Path.Combine(directory, "graphs"))), Nodes = [new A2ANode(new(http, new(wire.BaseUri)), "remote", new() { AgentOptions = new() { ContentMode = contentMode } })] });
            var graph = Create(); var paused = await graph.InvokeAsync("run");
            Assert.Equal(Status.Interrupted, paused.Status); Assert.True(graph.CanResume);
            var question = Assert.Single(paused.Interrupts);
            Assert.Equal("RemoteAgent", question.Source.ToString()); Assert.Equal("remote", question.ToolUseId);
            Assert.Equal(1, effects); Assert.Equal(0, approvals);

            var restored = Create();
            var completed = await restored.InvokeAsync(MultiAgentInput.Resume([new(question.Id, JsonSerializer.SerializeToElement("yes"))]));

            Assert.Equal(Status.Completed, completed.Status); Assert.Contains("finished", completed.ToString());
            Assert.False(restored.CanResume); Assert.Equal(1, factories); Assert.Equal(1, effects); Assert.Equal(1, approvals);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task RetryingAnAcceptedAnswerAfterPersistenceFailureStillSavesBeforeAnyRemoteContinuation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-a2a-graph-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FailingStore(new StorageMultiAgentSessionStore(new LocalFileStorage(directory)));
            await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(AbortInterruptTests.Create(new(), () => { }, () => { })) });
            await using var wire = new A2AServerTcpFixture(uri => new A2AServer(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
            var handler = new CountingHandler(); using var http = new HttpClient(handler);
            var graph = new Graph(new() { SessionId = "session", SessionStore = store, Nodes = [new A2ANode(new(http, new(wire.BaseUri)), "remote")] });
            var paused = await graph.InvokeAsync("run"); var posts = handler.Posts;
            store.Failures = 2;

            await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement("yes"))])));
            Assert.Equal(posts, handler.Posts);
            await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync(MultiAgentInput.Resume([])));
            Assert.Equal(posts, handler.Posts);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task FreshGraphReconcilesALostCompletedContinuationWithoutResendingAcceptedAnswers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-a2a-graph-lost-" + Guid.NewGuid().ToString("N"));
        var effects = 0; var approvals = 0;
        try
        {
            await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(AbortInterruptTests.Create(new(), () => effects++, () => approvals++)) });
            await using var wire = new A2AServerTcpFixture(uri => new A2AServer(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
            var handler = new CountingHandler(); using var http = new HttpClient(handler);
            Graph Create() => new(new() { SessionId = "session", SessionStore = new StorageMultiAgentSessionStore(new LocalFileStorage(directory)), Nodes = [new A2ANode(new(http, new(wire.BaseUri)), "remote", new() { AgentOptions = new() { Streaming = false } })] });
            var graph = Create(); var paused = await graph.InvokeAsync("run");
            handler.DropNextPost = true;
            var failed = await graph.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement("yes"))]));
            Assert.Equal(Status.Failed, failed.Status); Assert.Equal(1, approvals); var posts = handler.Posts;

            var restored = Create(); var completed = await restored.InvokeAsync(MultiAgentInput.Resume([]));

            Assert.Equal(Status.Completed, completed.Status); Assert.Contains("finished", completed.ToString());
            Assert.Equal(posts + 1, handler.Posts); // One safe tasks/get request, without another message/send.
            Assert.Equal(1, effects); Assert.Equal(1, approvals);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("scope")]
    [InlineData("version")]
    [InlineData("node")]
    [InlineData("endpoint")]
    [InlineData("task")]
    [InlineData("question")]
    [InlineData("local-id")]
    [InlineData("source")]
    [InlineData("state")]
    public async Task InvalidRemoteCheckpointsFailAtomicallyWithoutAnyNetworkRequest(string mutation)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(AbortInterruptTests.Create(new(), () => { }, () => { })) });
        await using var wire = new A2AServerTcpFixture(uri => new A2AServer(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
        var handler = new CountingHandler(); using var http = new HttpClient(handler);
        var graph = new Graph(new() { Nodes = [new A2ANode(new(http, new(wire.BaseUri)), "remote")] });
        await graph.InvokeAsync("run"); var originalState = graph.State; var question = Assert.Single(graph.PendingInterrupts);
        var snapshot = await graph.TakeSnapshotAsync(); var data = snapshot.Data.ToDictionary(pair => pair.Key, pair => pair.Value);
        var state = JsonNode.Parse(data["state"].GetRawText())!; var row = state["nodes"]![0]!; var pause = row["interruptedNodeSnapshot"]!;
        switch (mutation)
        {
            case "scope": pause["scope"] = "agent"; break;
            case "version": pause["schemaVersion"] = "2.0"; break;
            case "node": pause["nodeId"] = "foreign"; break;
            case "endpoint": pause["binding"]!["configuration"] = new string('0', 64); break;
            case "task": pause["taskId"] = "foreign"; break;
            case "question": pause["questions"]![0]!["remoteId"] = "foreign"; break;
            case "local-id": pause["questions"]![0]!["id"] = "not-an-opaque-id"; break;
            case "source": row["pendingInterrupts"]![0]!["source"] = "Tool"; break;
            case "state": pause["finalEvent"]!["status"]!["state"] = "completed"; break;
        }
        data["state"] = JsonSerializer.SerializeToElement(state); var requests = handler.Requests;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await graph.LoadSnapshotAsync(new(data)));

        Assert.Same(originalState, graph.State); Assert.Equal(question, Assert.Single(graph.PendingInterrupts)); Assert.Equal(requests, handler.Requests);
    }
    [Fact]
    public async Task RetryingAnAcceptedLocalAgentAnswerAlsoSavesBeforeTheBody()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-agent-graph-save-" + Guid.NewGuid().ToString("N")); var approvals = 0;
        try
        {
            var store = new FailingStore(new StorageMultiAgentSessionStore(new LocalFileStorage(directory)));
            await using var agent = AbortInterruptTests.Create(new(), () => { }, () => approvals++);
            var graph = new Graph(new() { SessionId = "session", SessionStore = store, Nodes = [new AgentNode(agent, "local")] });
            var paused = await graph.InvokeAsync("run"); store.Failures = 2;
            await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement("yes"))])));
            await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync(MultiAgentInput.Resume([])));
            Assert.Equal(0, approvals);
            var completed = await graph.InvokeAsync(MultiAgentInput.Resume([]));
            Assert.Equal(Status.Completed, completed.Status); Assert.Equal(1, approvals);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void InvalidRemoteProjectionOptionsFailBeforeDiscovery()
    {
        using var http = new HttpClient(); var client = new A2AClient(http, new(new Uri("http://127.0.0.1:1/")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new A2ANode(client, options: new() { AgentOptions = new() { ContentMode = (A2AContentMode)999 } }));
    }
    [Fact]
    public async Task EachPartialAnswerSurvivesAFreshGraphAndRetainsTheRemainingOpaqueQuestion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-a2a-graph-partial-" + Guid.NewGuid().ToString("N")); var effects = 0; var approvals = 0;
        try
        {
            await using var executor = new A2AExecutor(new() { AgentFactory = (id, _) => new(A2ARecoveryTests.CreateAgent(id, new TwoQuestionModel(), new FileSessionStore(Path.Combine(directory, "agents")), () => effects++, () => approvals++)) });
            await using var wire = new A2AServerTcpFixture(uri => new A2AServer(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
            using var http = new HttpClient();
            Graph Create() => new(new() { SessionId = "session", SessionStore = new StorageMultiAgentSessionStore(new LocalFileStorage(Path.Combine(directory, "graphs"))), Nodes = [new A2ANode(new(http, new(wire.BaseUri)), "remote")] });
            var first = await Create().InvokeAsync("run"); Assert.Equal(2, first.Interrupts.Count);
            var second = await Create().InvokeAsync(MultiAgentInput.Resume([new(first.Interrupts[0].Id, JsonSerializer.SerializeToElement("first"))]));
            Assert.Equal(Status.Interrupted, second.Status); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first.Interrupts[1]), JsonSerializer.SerializeToElement(Assert.Single(second.Interrupts)))); Assert.Equal(1, approvals);
            var completed = await Create().InvokeAsync(MultiAgentInput.Resume([new(second.Interrupts[0].Id, JsonSerializer.SerializeToElement("second"))]));
            Assert.Equal(Status.Completed, completed.Status); Assert.Equal(1, effects); Assert.Equal(2, approvals);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ASingleSharedNodeKeepsIndependentGraphsAndOpaqueQuestionsSeparate()
    {
        var factories = 0; var approvals = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { Interlocked.Increment(ref factories); return new(AbortInterruptTests.Create(new(), () => { }, () => Interlocked.Increment(ref approvals))); } });
        await using var wire = new A2AServerTcpFixture(uri => new A2AServer(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
        using var http = new HttpClient(); var node = new A2ANode(new(http, new(wire.BaseUri)), "remote");
        var first = new Graph(new() { Nodes = [node] }); var second = new Graph(new() { Nodes = [node] });
        var results = await Task.WhenAll(first.InvokeAsync("one"), second.InvokeAsync("two"));
        var one = Assert.Single(results[0].Interrupts); var two = Assert.Single(results[1].Interrupts); Assert.NotEqual(one.Id, two.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => first.InvokeAsync(MultiAgentInput.Resume([new(two.Id, JsonSerializer.SerializeToElement("wrong"))])));
        var completed = await Task.WhenAll(first.InvokeAsync(MultiAgentInput.Resume([new(one.Id, JsonSerializer.SerializeToElement("one"))])), second.InvokeAsync(MultiAgentInput.Resume([new(two.Id, JsonSerializer.SerializeToElement("two"))])));
        Assert.All(completed, result => Assert.Equal(Status.Completed, result.Status)); Assert.Equal(2, factories); Assert.Equal(2, approvals);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedRemotePauseSurvivesConsumerDisposalOrCancellation(bool cancel)
    {
        var approvals = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(AbortInterruptTests.Create(new(), () => { }, () => approvals++)) });
        await using var wire = new A2AServerTcpFixture(uri => new A2AServer(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
        using var http = new HttpClient(); var graph = new Graph(new() { Nodes = [new A2ANode(new(http, new(wire.BaseUri)), "remote")] });
        using var cancellation = new CancellationTokenSource(); var iterator = graph.StreamAsync(new("run"), cancellationToken: cancellation.Token).GetAsyncEnumerator();
        try
        {
            var observed = false;
            while (await iterator.MoveNextAsync())
                if (iterator.Current is NodeStreamUpdateEvent { Inner: A2AStreamUpdateEvent update } && update.Event.TryGetProperty("status", out var status) && status.GetProperty("state").GetString() == "input-required")
                { observed = true; break; }
            Assert.True(observed);
            if (cancel) { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync()); }
        }
        finally { await iterator.DisposeAsync(); }
        Assert.True(graph.CanResume); var question = Assert.Single(graph.PendingInterrupts);
        var snapshot = await graph.TakeSnapshotAsync(); var restored = new Graph(new() { Nodes = [new A2ANode(new(http, new(wire.BaseUri)), "remote")] });
        await restored.LoadSnapshotAsync(snapshot);
        var result = await restored.InvokeAsync(MultiAgentInput.Resume([new(question.Id, JsonSerializer.SerializeToElement("yes"))]));
        Assert.Equal(Status.Completed, result.Status); Assert.Equal(1, approvals);
    }
    [Fact]
    public void RemoteRequestsCannotEnterTheNativeToolApprovalLedger()
    {
        var call = new ToolUseBlock("remote", "tool", JsonSerializer.SerializeToElement(new { }));
        Assert.Throws<ArgumentException>(() => new InterruptState(new(Role.Assistant, [call]), [], [new("question", "approval", call.ToolUseId, source: InterruptSource.RemoteAgent)], []));
    }
    [Fact]
    public async Task DisposingAPartialContinuationAfterItsPauseUpdateKeepsACorrelatedRestorableLedger()
    {
        var approvals = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (id, _) => new(A2ARecoveryTests.CreateAgent(id, new TwoQuestionModel(), new A2ARecoveryTests.SnapshotStore(), () => { }, () => approvals++)) });
        await using var wire = new A2AServerTcpFixture(uri => new A2AServer(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
        using var http = new HttpClient(); Graph Create() => new(new() { Nodes = [new A2ANode(new(http, new(wire.BaseUri)), "remote")] });
        var graph = Create(); var paused = await graph.InvokeAsync("run");
        await using (var iterator = graph.StreamAsync(MultiAgentInput.Resume([new(paused.Interrupts[0].Id, JsonSerializer.SerializeToElement("one"))])).GetAsyncEnumerator())
            while (await iterator.MoveNextAsync())
                if (iterator.Current is NodeStreamUpdateEvent { Inner: A2AStreamUpdateEvent update } && update.Event.GetProperty("kind").GetString() == "status-update" && update.Event.GetProperty("status").GetProperty("state").GetString() == "input-required") break;
        var snapshot = await graph.TakeSnapshotAsync(); var restored = Create();

        await restored.LoadSnapshotAsync(snapshot);

        Assert.Equal(paused.Interrupts[1].Id, Assert.Single(restored.PendingInterrupts).Id);
        var completed = await restored.InvokeAsync(MultiAgentInput.Resume([new(restored.PendingInterrupts[0].Id, JsonSerializer.SerializeToElement("two"))]));
        Assert.Equal(Status.Completed, completed.Status); Assert.Equal(2, approvals);
    }
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task ConsecutiveUnstructuredRemoteRequestsReceiveDistinctLocalQuestionIdentities(bool rejectInvalidAnswer, bool includeHook, bool uncertainSend)
    {
        Uri? endpoint = null; var sends = 0; var answers = new List<string>();
        object Current() => new { kind = "task", id = "task", contextId = "context", status = new { state = sends < 3 ? "input-required" : "completed", message = new { kind = "message", role = "agent", messageId = "m" + sends, taskId = "task", contextId = "context", parts = new[] { new { kind = "text", text = sends < 3 ? "More text, please." : "done" } } } } };
        await using var wire = new A2ATestServer(request =>
        {
            if (request.Method == "GET") return new(JsonSerializer.Serialize(new { name = "Remote", description = "Remote", version = "0.0.1", protocolVersion = "0.3.0", url = endpoint!.AbsoluteUri, capabilities = new { streaming = false }, defaultInputModes = new[] { "text/plain" }, defaultOutputModes = new[] { "text/plain" }, skills = Array.Empty<object>() }));
            var rpc = JsonSerializer.Deserialize<JsonElement>(request.Body);
            if (rpc.GetProperty("method").GetString() == "message/send")
            {
                if (sends > 0) answers.Add(rpc.GetProperty("params").GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString()!);
                sends++;
            }
            return new(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = rpc.GetProperty("id"), result = Current() }));
        });
        endpoint = new(wire.BaseUri, "rpc"); var handler = new CountingHandler(); using var http = new HttpClient(handler);
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { if (value.NodeId == "local") value.Interrupt("local-approval"); return default; });
        var nodes = new List<Node> { new A2ANode(new(http, new(wire.BaseUri)), "remote") };
        if (includeHook) nodes.Add(new FunctionNode("local", (_, _, _) => Completed()));
        var graph = new Graph(new() { Nodes = nodes, Hooks = hooks });
        var first = await graph.InvokeAsync("run"); var firstId = first.Interrupts.Single(question => question.Source == InterruptSource.RemoteAgent).Id;
        var hook = first.Interrupts.SingleOrDefault(question => question.Source == InterruptSource.OrchestratorHook);
        IReadOnlyList<InterruptResponse> Answer(JsonElement answer) => hook is null ? [new(firstId, answer)] : [new(hook.Id, JsonSerializer.SerializeToElement(true)), new(firstId, answer)];
        if (uncertainSend)
        {
            handler.DropNextPost = true;
            Assert.Equal(Status.Failed, (await graph.InvokeAsync(MultiAgentInput.Resume(Answer(JsonSerializer.SerializeToElement("one"))))).Status);
            var restored = new Graph(graph.Options); await restored.LoadSnapshotAsync(await graph.TakeSnapshotAsync());
            Assert.Equal(Status.Failed, (await restored.InvokeAsync(MultiAgentInput.Resume([]))).Status);
            Assert.Equal(2, sends); return;
        }
        if (rejectInvalidAnswer)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => graph.InvokeAsync(MultiAgentInput.Resume(Answer(JsonSerializer.SerializeToElement(42)))));
            Assert.Equal(1, sends); Assert.Equal(includeHook ? 2 : 1, graph.PendingInterrupts.Count); Assert.Contains(graph.PendingInterrupts, question => question.Id == firstId);
        }
        var second = await graph.InvokeAsync(MultiAgentInput.Resume(Answer(JsonSerializer.SerializeToElement("one")))); var secondId = Assert.Single(second.Interrupts).Id;
        Assert.NotEqual(firstId, secondId);
        var completed = await graph.InvokeAsync(MultiAgentInput.Resume([new(secondId, JsonSerializer.SerializeToElement("two"))]));
        Assert.Equal(Status.Completed, completed.Status); Assert.Equal(new[] { "one", "two" }, answers);
    }
    private static async IAsyncEnumerable<INodeStreamEvent> Completed()
    { await Task.CompletedTask; yield return new NodeResultUpdateEvent([new TextBlock("local done")]); }
    private sealed class TwoQuestionModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request.Messages.Any(message => message.Content.OfType<ToolResultBlock>().Any()))
            { await foreach (var update in new A2AServerTests.EchoModel().StreamAsync(request, cancellationToken)) yield return update; yield break; }
            cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent();
            var calls = new[] { new ToolUseBlock("done", "done", JsonSerializer.SerializeToElement(new { })), new ToolUseBlock("one", "approve", JsonSerializer.SerializeToElement(new { })), new ToolUseBlock("two", "approve", JsonSerializer.SerializeToElement(new { })) };
            for (var index = 0; index < calls.Length; index++) { yield return new ModelContentBlockStartEvent(index, calls[index]); yield return new ModelContentBlockStopEvent(index); }
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
    }
    private sealed class FailingStore(IMultiAgentSessionStore inner) : IMultiAgentSessionStore
    {
        internal int Failures { get; set; }
        public ValueTask<MultiAgentSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => inner.LoadAsync(sessionId, cancellationToken);
        public ValueTask SaveAsync(string sessionId, MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default)
        { if (Failures > 0) { Failures--; throw new IOException("accepted-answer save"); } return inner.SaveAsync(sessionId, snapshot, cancellationToken); }
    }
    private sealed class CountingHandler : DelegatingHandler
    {
        internal int Posts { get; private set; }
        internal int Requests { get; private set; }
        internal bool DropNextPost { get; set; }
        internal CountingHandler() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.Method == HttpMethod.Post) Posts++;
            var method = request.Content is null ? null : JsonSerializer.Deserialize<JsonElement>(await request.Content.ReadAsStringAsync()).GetProperty("method").GetString();
            var response = await base.SendAsync(request, cancellationToken);
            if (method == "message/send" && DropNextPost)
            {
                DropNextPost = false;
                using (response) _ = await response.Content.ReadAsStringAsync();
                throw new IOException("The completed response was lost after the server committed it.");
            }
            return response;
        }
    }
}
