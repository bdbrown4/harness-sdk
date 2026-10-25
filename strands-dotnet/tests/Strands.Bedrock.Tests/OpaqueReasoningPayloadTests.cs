using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class OpaqueReasoningPayloadTests
{
    [Fact]
    public void PayloadInputIsDetached()
    {
        var input = new byte[] { 1, 2, 3 };
        var item = new ModelContentBlockDeltaEvent(0) { RedactedReasoningBytes = input };

        input[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3 }, item.RedactedReasoningBytes);
    }

    [Fact]
    public void PayloadGetterIsDetached()
    {
        var item = Chunk(1, 2, 3);
        var exposed = item.RedactedReasoningBytes!;

        exposed[1] = 99;

        Assert.Equal(new byte[] { 1, 2, 3 }, item.RedactedReasoningBytes);
    }

    [Fact]
    public void RecordWithKeepsPayloadAndNullClearsOnlyTheCopy()
    {
        var original = Chunk(1, 2, 3);
        var copy = original with { Index = 1 };
        var cleared = original with { RedactedReasoningBytes = null };
        var exposed = copy.RedactedReasoningBytes!;

        exposed[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3 }, original.RedactedReasoningBytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.RedactedReasoningBytes);
        Assert.Null(cleared.RedactedReasoningBytes);
        Assert.Equal(1, copy.Index);
        Assert.Equal(0, original.Index);
    }

    [Fact]
    public void LegacyPositionalShapeAndDefaultJsonRemainStable()
    {
        var legacy = new ModelContentBlockDeltaEvent(7, "text", "{}", "thought", "signature");

        var (index, text, input, reasoning, signature) = legacy;

        Assert.Equal(7, index);
        Assert.Equal("text", text);
        Assert.Equal("{}", input);
        Assert.Equal("thought", reasoning);
        Assert.Equal("signature", signature);
        Assert.Null(legacy.RedactedReasoningBytes);
        var json = JsonSerializer.SerializeToElement(legacy);
        Assert.Equal(new[] { "Citations", "Index", "Reasoning", "Signature", "Text", "ToolInput" }, json.EnumerateObject().Select(p => p.Name).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AgentAccumulatesFragmentsAndAcceptsMatchingCompletedStop()
    {
        await using var agent = new Agent(new AgentOptions
        {
            Model = Events(
            new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0),
            Chunk(1), Chunk(2, 3), Chunk(4, 5),
            new ModelContentBlockStopEvent(0) { ContentBlock = new RedactedReasoningBlock("AQIDBAU=") },
            new ModelMessageStopEvent(StopReasons.EndTurn))
        });

        var result = await agent.InvokeAsync("retain all fragments");

        var final = Assert.IsType<RedactedReasoningBlock>(Assert.Single(result.Message!.Content));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, Convert.FromBase64String(final.Data));
        Assert.Equal(final.Data, Assert.IsType<RedactedReasoningBlock>(Assert.Single(agent.Messages.Last().Content)).Data);
    }

    [Fact]
    public async Task AgentRejectsMismatchedCompletedOpaqueStop()
    {
        await using var agent = new Agent(new AgentOptions
        {
            Model = Events(
            new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), Chunk(1, 2, 3),
            new ModelContentBlockStopEvent(0) { ContentBlock = new RedactedReasoningBlock("AQIE") },
            new ModelMessageStopEvent(StopReasons.EndTurn))
        });

        var error = await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("reject rewritten bytes"));

        Assert.Contains("rewrite", error.Message);
        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant);
    }

    [Theory]
    [InlineData("same-text")]
    [InlineData("text-first")]
    [InlineData("reasoning-first")]
    [InlineData("signature-first")]
    [InlineData("bytes-first-text")]
    [InlineData("bytes-first-reasoning")]
    public async Task OpaqueAndOrdinaryFieldsCannotShareABlock(string order)
    {
        ModelContentBlockDeltaEvent[] body = order switch
        {
            "same-text" => [new(0, Text: "x") { RedactedReasoningBytes = [1] }],
            "text-first" => [new(0, Text: "x"), Chunk(1)],
            "reasoning-first" => [new(0, Reasoning: "x"), Chunk(1)],
            "signature-first" => [new(0, Signature: "sig"), Chunk(1)],
            "bytes-first-text" => [Chunk(1), new(0, Text: "x")],
            "bytes-first-reasoning" => [Chunk(1), new(0, Reasoning: "x")],
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
        var events = new List<ModelStreamEvent> { new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0) };
        events.AddRange(body);
        events.Add(new ModelContentBlockStopEvent(0));
        events.Add(new ModelMessageStopEvent(StopReasons.EndTurn));
        await using var agent = new Agent(new AgentOptions { Model = Events(events.ToArray()) });

        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("reject mixed block"));

        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant);
    }

    [Fact]
    public async Task EmptyOpaqueFragmentRemainsAnOpaqueBlock()
    {
        await using var agent = new Agent(new AgentOptions
        {
            Model = Events(
            new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), Chunk(),
            new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn))
        });

        var result = await agent.InvokeAsync("retain explicit empty opaque data");

        Assert.Equal("", Assert.IsType<RedactedReasoningBlock>(Assert.Single(result.Message!.Content)).Data);
    }

    [Fact]
    public async Task PublicBedrockChunksExposeExactBytesAndCompletedStop()
    {
        using var body = CompleteWire();
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());

        var events = await BedrockModelTests.CollectAsync(model, new([Message.User("inspect chunks")]));

        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
        var start = Assert.Single(events.OfType<ModelContentBlockStartEvent>());
        Assert.Equal(0, start.Index);
        Assert.Null(start.ContentBlock);
        var chunks = events.OfType<ModelContentBlockDeltaEvent>().ToArray();
        Assert.Equal(2, chunks.Length);
        Assert.Equal(new byte[] { 1 }, chunks[0].RedactedReasoningBytes);
        Assert.Equal(new byte[] { 2, 3 }, chunks[1].RedactedReasoningBytes);
        var exposed = chunks[0].RedactedReasoningBytes!;
        exposed[0] = 99;
        Assert.Equal(new byte[] { 1 }, chunks[0].RedactedReasoningBytes);
        var stop = Assert.Single(events.OfType<ModelContentBlockStopEvent>());
        Assert.Equal(0, stop.Index);
        Assert.Equal("AQID", Assert.IsType<RedactedReasoningBlock>(stop.ContentBlock).Data);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task BedrockRetainsTheAggregateBinaryBufferBoundary(long limit, bool accepted)
    {
        using var body = CompleteWire();
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new() { MaximumBufferedContentBytes = limit });
        await using var agent = new Agent(new AgentOptions { Model = model });

        if (accepted)
        {
            var result = await agent.InvokeAsync("exact byte budget");
            Assert.Equal("AQID", Assert.IsType<RedactedReasoningBlock>(Assert.Single(result.Message!.Content)).Data);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("exceed byte budget"));
            Assert.Contains("buffered", error.Message);
            Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant);
        }
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task ClosedOpaqueBlocksReleaseParserBudgetForTheNextBlock()
    {
        using var body = BedrockModelTests.Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AQID"}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"reasoningContent":{"redactedContent":"BAUG"}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":1}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new() { MaximumBufferedContentBytes = 3 });
        await using var agent = new Agent(new AgentOptions { Model = model });

        var result = await agent.InvokeAsync("reuse released byte budget");

        Assert.Equal(new[] { "AQID", "BAUG" }, result.Message!.Content.Select(block => Assert.IsType<RedactedReasoningBlock>(block).Data));
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task CallerCancellationClosesOpenOpaqueResponseAndRetainsBorrowedClient()
    {
        using var prefix = BedrockModelTests.Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AQ=="}}}"""));
        using var body = new BlockingAfterPrefix(prefix.ToArray());
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());
        using var caller = new CancellationTokenSource();
        var operation = BedrockModelTests.CollectAsync(model, new([Message.User("cancel open block")]), caller.Token);
        try
        {
            await Within(body.ReadBlocked.Task);
            caller.Cancel();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(operation));

            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.True(body.Disposed);
            Assert.False(client.Disposed);
        }
        finally
        {
            caller.Cancel();
            body.Dispose();
            try { await Within(operation); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task EarlyOpaqueConsumerDisposalClosesResponseAndRetainsBorrowedClient()
    {
        using var body = CompleteWire();
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());
        using var caller = new CancellationTokenSource();
        var iterator = model.StreamAsync(new([Message.User("close after chunk")]), caller.Token).GetAsyncEnumerator(caller.Token);
        Task<bool>? pendingMove = null;
        Exception? primary = null;
        var disposeAttempted = false;
        var observed = false;
        try
        {
            while (true)
            {
                pendingMove = iterator.MoveNextAsync().AsTask();
                var next = await Within(pendingMove);
                pendingMove = null;
                if (!next) break;
                if (iterator.Current is ModelContentBlockDeltaEvent { RedactedReasoningBytes: not null })
                {
                    observed = true;
                    break;
                }
            }

            disposeAttempted = true;
            await iterator.DisposeAsync();

            Assert.True(body.Disposed);
            Assert.False(client.Disposed);
            Assert.True(observed);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            Exception? cleanup = null;
            try { caller.Cancel(); }
            catch (Exception error) { cleanup ??= error; }
            try { body.Dispose(); }
            catch (Exception error) { cleanup ??= error; }
            if (pendingMove is not null)
            {
                try { await pendingMove; }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
                catch (Exception error) { cleanup ??= error; }
            }
            if (!disposeAttempted)
            {
                try { await iterator.DisposeAsync(); }
                catch (Exception error) { cleanup ??= error; }
            }
            if (primary is null && cleanup is not null) ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }

    private static ModelContentBlockDeltaEvent Chunk(params byte[] bytes) => new(0) { RedactedReasoningBytes = bytes };
    private static EventModel Events(params ModelStreamEvent[] events) => new(events);
    private static BedrockModelTests.TrackingStream CompleteWire() => BedrockModelTests.Wire(
        ("messageStart", """{"role":"assistant"}"""),
        ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AQ=="}}}"""),
        ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AgM="}}}"""),
        ("contentBlockStop", """{"contentBlockIndex":0}"""),
        ("messageStop", """{"stopReason":"end_turn"}"""));

    private static async Task<T> Within<T>(Task<T> operation)
    {
        using var watchdog = new CancellationTokenSource();
        var delay = Task.Delay(TimeSpan.FromSeconds(10), watchdog.Token);
        try
        {
            Assert.Same(operation, await Task.WhenAny(operation, delay));
            return await operation;
        }
        finally
        {
            watchdog.Cancel();
            try { await delay; }
            catch (OperationCanceledException) when (watchdog.IsCancellationRequested) { }
        }
    }

    private sealed class EventModel(ModelStreamEvent[] events) : IModel, IModelMetadataProvider
    {
        public ModelMetadata Metadata => new(ContextWindowLimit: 1_000);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
            await Task.CompletedTask;
        }
    }

    private sealed class BlockingAfterPrefix(byte[] prefix) : Stream
    {
        private readonly MemoryStream _prefix = new(prefix);
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_prefix.Position < _prefix.Length) return _prefix.ReadAsync(buffer, offset, count, cancellationToken);
            ReadBlocked.TrySetResult(true);
            return _read.Task;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _prefix.Dispose();
            _read.TrySetException(new ObjectDisposedException(nameof(BlockingAfterPrefix)));
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
