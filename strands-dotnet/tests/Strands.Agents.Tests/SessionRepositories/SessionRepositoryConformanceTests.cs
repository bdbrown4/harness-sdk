using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.SessionRepositories;
using Probe = Strands.Agents.Tests.SessionRepositories.SessionRepositorySafetyTests.ProbeStorage;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class SessionRepositoryConformanceTests
{
    [Theory]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("session")]
    [InlineData("owner")]
    [InlineData("data")]
    [InlineData("message-id")]
    [InlineData("duplicate")]
    public async Task StoredEnvelopeAndRequestedRecordIdentityAreValidated(string tamper)
    {
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        await repository.CreateMessageAsync("session", "agent", new(Message.User("original"), 10));
        var key = Assert.Single(storage.Data).Key;
        var data = JsonNode.Parse(storage.Data[key])!.AsObject();
        switch (tamper)
        {
            case "kind": data["kind"] = "agent"; break;
            case "version": data["schemaVersion"] = 2; break;
            case "session": data["sessionId"] = "other"; break;
            case "owner": data["ownerId"] = "other"; break;
            case "data": data["data"] = new JsonArray(); break;
            case "message-id": data["data"]!["messageId"] = 20; break;
        }
        var text = data.ToJsonString();
        if (tamper == "duplicate") text = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1");
        storage.Data[key] = Encoding.UTF8.GetBytes(text);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.ReadMessageAsync("session", "agent", 10));
        Assert.Equal(1, storage.Writes);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("createdAt")]
    [InlineData("agentId")]
    public async Task MalformedAgentPayloadFailsBeforeAnyUpdate(string field)
    {
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        await repository.CreateAgentAsync("session", Agent("old"));
        var key = Assert.Single(storage.Data).Key;
        var data = JsonNode.Parse(storage.Data[key])!;
        data["data"]![field] = field == "state" ? new JsonArray() : JsonValue.Create("malformed");
        storage.Data[key] = Encoding.UTF8.GetBytes(data.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.UpdateAgentAsync("session", Agent("new")));
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public async Task AnOptInByteLimitCannotOverwriteAnExistingUnboundedRecord()
    {
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        await repository.CreateAgentAsync("session", Agent(new string('x', 2048)));
        var limited = new StorageSessionRepository(storage, new() { MaxRecordBytes = 32 });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await limited.CreateAgentAsync("session", Agent("replacement")));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await limited.ReadAgentAsync("session", "agent"));
        Assert.Equal(1, storage.Writes);
        Assert.Equal(2048, (await repository.ReadAgentAsync("session", "agent"))!.State.GetProperty("value").GetString()!.Length);
    }

    [Fact]
    public async Task TheDefaultDoesNotImposeAnArbitraryMediaByteLimit()
    {
        var repository = new StorageSessionRepository(new Probe());
        var media = new string('A', 2_000_000);
        await repository.CreateMessageAsync("session", "agent", new(new(Role.User, [new ImageBlock("image/png", media)], "large-media"), 0));
        Assert.Equal(media, Assert.IsType<ImageBlock>(Assert.Single((await repository.ReadMessageAsync("session", "agent", 0))!.Message.Content)).Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrecancelledCallsHaveNoBackendEffectsAndRetainTheirCallerToken(bool write)
    {
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (write) await repository.CreateAgentAsync("session", Agent("value"), cancellation.Token);
            else await repository.ReadAgentAsync("session", "agent", cancellation.Token);
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, storage.Reads); Assert.Equal(0, storage.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RequestedCancellationRetainsCallerIdentityAndTheBackendCause(bool write, bool ioFailure)
    {
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        using var cancellation = new CancellationTokenSource();
        Exception failure = ioFailure ? new IOException("backend was canceled") : new OperationCanceledException("independent backend token");
        ValueTask Fail() { cancellation.Cancel(); throw failure; }
        if (write) storage.BeforeWrite = _ => Fail(); else storage.BeforeRead = Fail;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (write) await repository.CreateAgentAsync("session", Agent("value"), cancellation.Token);
            else await repository.ReadAgentAsync("session", "agent", cancellation.Token);
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task IndependentBackendCancellationIsNotRelabeledAsCallerCancellation()
    {
        var failure = new OperationCanceledException("backend cancellation");
        var storage = new Probe { BeforeRead = () => throw failure };
        var repository = new StorageSessionRepository(storage);
        Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repository.ReadSessionAsync("session")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedPrepublicationWriteRetainsTheCompletePriorRecord(bool cancellation)
    {
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        await repository.CreateAgentAsync("session", Agent("old"));
        using var caller = new CancellationTokenSource();
        var failure = new IOException("write was not published");
        storage.BeforeWrite = _ => { if (cancellation) caller.Cancel(); throw failure; };
        if (cancellation)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repository.UpdateAgentAsync("session", Agent("new"), caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken); Assert.Same(failure, error.InnerException);
        }
        else Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => await repository.UpdateAgentAsync("session", Agent("new"))));
        Assert.Equal("old", (await repository.ReadAgentAsync("session", "agent"))!.State.GetProperty("value").GetString());
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public async Task ConcurrentSessionCreatesSerializeWithinOneInstance()
    {
        var entered = Gate(); var release = Gate();
        var storage = new Probe { BeforeWrite = async _ => { entered.TrySetResult(true); await release.Task; } };
        var repository = new StorageSessionRepository(storage);
        var first = repository.CreateSessionAsync(new("session")).AsTask();
        await Wait(entered.Task);
        var second = repository.CreateSessionAsync(new("session")).AsTask();
        Assert.False(second.IsCompleted);
        release.TrySetResult(true);
        await Wait(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public async Task EscapedCompletedStorageScopesDoNotPoisonFutureOperations()
    {
        var release = Gate(); Task<SessionRecord?>? escaped = null;
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        storage.BeforeRead = () =>
        {
            escaped = Task.Run(async () => { await release.Task; return await repository.ReadSessionAsync("later"); });
            return default;
        };
        Assert.Null(await repository.ReadSessionAsync("first"));
        storage.BeforeRead = null; release.TrySetResult(true);
        await Wait(escaped!); Assert.Null(await escaped!);
    }

    [Fact]
    public async Task StorageAndNamespacesRemainCallerOwnedAndIndependent()
    {
        var storage = new Probe();
        var first = new StorageSessionRepository(storage, new() { Prefix = "one" });
        var second = new StorageSessionRepository(storage, new() { Prefix = "two" });
        await first.CreateSessionAsync(new("session")); await second.CreateSessionAsync(new("session"));
        Assert.NotNull(await first.ReadSessionAsync("session")); Assert.NotNull(await second.ReadSessionAsync("session"));
        Assert.False(storage.Disposed);
        Assert.False((object)first is IDisposable); Assert.False((object)first is IAsyncDisposable);
    }

    [Fact]
    public async Task OnlySelectedMessagePayloadsAreReadAfterNumericPagination()
    {
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        await repository.CreateMessageAsync("session", "agent", new(Message.User("first"), 2));
        await repository.CreateMessageAsync("session", "agent", new(Message.User("later"), 10));
        var corrupt = storage.Data.Keys.Single(key => key.EndsWith("/10.json", StringComparison.Ordinal));
        storage.Data[corrupt] = Encoding.UTF8.GetBytes("not JSON");
        Assert.Equal(2, Assert.Single(await repository.ListMessagesAsync("session", "agent", 1)).MessageId);
        Assert.Equal(1, storage.Reads);
    }

    [Fact]
    public async Task OpaqueIdsAreHashedAndCannotEscapeTheSelectedFileRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands indexed ids " + Guid.NewGuid().ToString("N"));
        var cleanupDirectory = directory;
        try
        {
            var repository = new FileSessionRepository(directory);
            cleanupDirectory = repository.RootPath;
            await repository.CreateMessageAsync("../session/..", "agent/../other", new(Message.User("safe"), long.MinValue));
            Assert.Equal("safe", (await repository.ReadMessageAsync("../session/..", "agent/../other", long.MinValue))!.Message.Text);
            Assert.All(Directory.GetFiles(repository.RootPath, "*", SearchOption.AllDirectories), path => Assert.StartsWith(repository.RootPath + Path.DirectorySeparatorChar, path));
        }
        finally { if (Directory.Exists(cleanupDirectory)) Directory.Delete(cleanupDirectory, true); }
    }

    [Fact]
    public async Task AChildRecordAlreadyCreatesTheSessionNamespaceLikeTheSourceDirectory()
    {
        var repository = new StorageSessionRepository(new Probe());
        await repository.CreateMessageAsync("session", "agent", new(Message.User("first"), 0));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.CreateSessionAsync(new("session")));
        Assert.Null(await repository.ReadAgentAsync("session", "agent"));
        Assert.Single(await repository.ListMessagesAsync("session", "agent"));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public async Task NegativePaginationIsRejectedBeforeReadingStorage(int limit, int offset)
    {
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await repository.ListMessagesAsync("session", "agent", limit, offset));
        Assert.Equal(0, storage.Reads);
    }

    [Fact]
    public async Task NativeRichBlocksAndToolInputsRoundtripWithoutFlatteningOrCallerAliases()
    {
        var repository = new StorageSessionRepository(new Probe());
        SessionMessageRecord user;
        SessionMessageRecord assistant;
        using (var document = JsonDocument.Parse("{\"original\":\"detached\"}"))
        {
            ContentBlock[] blocks = [new TextBlock("text"), new JsonBlock(document.RootElement),
                new ImageBlock("image/png", "AQID"), new ImageBlock("image/jpeg", new Uri("https://example.test/image")),
                new ImageBlock("image/png", new S3Location("s3://bucket/image")), new AudioBlock("audio/wav", "AQID"),
                new VideoBlock("video/mp4", "AQID"), new DocumentBlock("application/pdf", "AQID", "report"),
                DocumentBlock.FromText("source text", "text report"), new GuardContentBlock("guard", qualifiers: [GuardQualifier.GuardContent]),
                new CitationsBlock([new(new WebCitationLocation("https://example.test/source"), sourceContent: [new("excerpt")])], [new("cited text")], document.RootElement)];
            user = new(new(Role.User, blocks, "rich-original", document.RootElement), long.MaxValue);
            assistant = new(new(Role.Assistant, [new ToolUseBlock("call", "tool", document.RootElement)], "assistant-original", document.RootElement), long.MinValue);
            blocks[0] = new TextBlock("caller mutated");
        }
        await repository.CreateMessageAsync("session", "agent", user);
        await repository.CreateMessageAsync("session", "agent", assistant);
        Assert.Equal(JsonSerializer.Serialize(user.Message), JsonSerializer.Serialize((await repository.ReadMessageAsync("session", "agent", long.MaxValue))!.Message));
        Assert.Equal(JsonSerializer.Serialize(assistant.Message), JsonSerializer.Serialize((await repository.ReadMessageAsync("session", "agent", long.MinValue))!.Message));
        Assert.Equal("text", Assert.IsType<TextBlock>(user.Message.Content[0]).Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveOptInLimitsAreRejectedAtConstruction(int maximum)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new StorageSessionRepository(new Probe(), new() { MaxRecordBytes = maximum }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDrainsPhysicalStorageBeforeReleasingTheGateAndReportsPossibleLatePublication(bool ignoresCancellation)
    {
        var entered = Gate(); var release = Gate();
        var storage = new Probe(); var repository = new StorageSessionRepository(storage);
        await repository.CreateAgentAsync("session", Agent("old"));
        storage.IgnoreWriteCancellation = ignoresCancellation;
        storage.BeforeWrite = async _ => { entered.TrySetResult(true); await release.Task; };
        using var caller = new CancellationTokenSource();
        var update = repository.UpdateAgentAsync("session", Agent("new"), caller.Token).AsTask();
        await Wait(entered.Task);
        caller.Cancel();
        var read = repository.ReadAgentAsync("session", "agent").AsTask();
        Assert.False(update.IsCompleted); Assert.False(read.IsCompleted);
        release.TrySetResult(true);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        Assert.Equal(caller.Token, error.CancellationToken);
        await Wait(read);
        Assert.Equal(ignoresCancellation ? "new" : "old", (await read)!.State.GetProperty("value").GetString());
    }

    private static SessionAgentRecord Agent(string value) => new("agent", SessionRepositoryTests.Object(value), SessionRepositoryTests.Object("manager"));
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(8000))); await task; }
}
