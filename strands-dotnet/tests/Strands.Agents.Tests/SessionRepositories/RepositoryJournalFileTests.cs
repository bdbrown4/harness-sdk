using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.SessionRepositories;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryJournalFileTests
{
    [Fact]
    public async Task FreshFileBackedAgentRestoresDetachedRichRowsApplicationStateAndAnExplicitNullPrompt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-live-indexed-" + Guid.NewGuid().ToString("N"));
        var repository = new FileSessionRepository(directory);
        try
        {
            Message response;
            using (var document = JsonDocument.Parse("{\"nested\":{\"kept\":true}}"))
                response = new(Role.User, [new ToolResultBlock("call", [new TextBlock("detail"), new JsonBlock(document.RootElement), new ImageBlock("image/png", "AA==")], ToolResultStatus.Error, document.RootElement) { Metadata = document.RootElement }], "result-tracking", document.RootElement);
            await using (var first = new Agent(new() { Id = "agent", Model = new JournalTestModel(), SystemPrompt = "configured", Messages = [new(Role.Assistant, [RepositoryJournalRestoreTests.Call("call")]), response], Plugins = [new RepositorySessionManager("session", repository)] }))
            {
                await first.InitializeAsync();
                using var state = JsonDocument.Parse("{\"value\":42}");
                first.State.Set("application", state.RootElement);
                await first.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["systemPrompt"] = JsonSerializer.SerializeToElement<string?>(null) }));
                await first.InvokeAsync(null);
            }
            await using var restored = new Agent(new() { Id = "agent", Model = new JournalTestModel(), SystemPrompt = "must clear", Plugins = [new RepositorySessionManager("session", new FileSessionRepository(directory))] });
            await restored.InitializeAsync();
            Assert.Null(restored.SystemPrompt);
            Assert.Equal(42, restored.State.Get("application")?.GetProperty("value").GetInt32());
            Assert.Equal("result-tracking", restored.Messages[1].TrackingId);
            Assert.True(restored.Messages[1].Metadata?.GetProperty("nested").GetProperty("kept").GetBoolean());
            var result = Assert.IsType<ToolResultBlock>(Assert.Single(restored.Messages[1].Content));
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Equal(3, result.ContentBlocks.Count);
            Assert.True(result.Metadata?.GetProperty("nested").GetProperty("kept").GetBoolean());
            Assert.True(result.StructuredContent?.GetProperty("nested").GetProperty("kept").GetBoolean());
            Assert.Equal("AA==", Assert.IsType<ImageBlock>(result.ContentBlocks[2]).Data);
        }
        finally { if (Directory.Exists(repository.RootPath)) Directory.Delete(repository.RootPath, true); }
    }

    [Fact]
    public async Task DuplicateHistoricalTrackingAndToolIdsRemainDistinctNumericArchiveRows()
    {
        var repository = new JournalProbeRepository();
        var messages = Enumerable.Range(0, 4).Select(index => new Message(index % 2 == 0 ? Role.Assistant : Role.User,
            index % 2 == 0 ? [RepositoryJournalRestoreTests.Call("reused-call")] : [new ToolResultBlock("reused-call", "result-" + index)], "duplicate-tracking")).ToArray();
        await using (var first = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Messages = messages, Plugins = [new RepositorySessionManager("session", repository)] })) await first.InitializeAsync();
        await using var restored = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        await restored.InitializeAsync();
        Assert.Equal(4, restored.Messages.Count);
        Assert.All(restored.Messages, message => Assert.Equal("duplicate-tracking", message.TrackingId));
        Assert.Equal(new long[] { 0, 1, 2, 3 }, (await repository.Inner.ListMessagesAsync("session", "agent")).Select(row => row.MessageId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstClassContextBindingsFailBeforeEffectsForEitherModelMode(bool stateful)
    {
        var repository = new JournalProbeRepository();
        var filters = 0;
        var tool = new FunctionTool(new("tool", "tool", JsonSerializer.SerializeToElement(new { })), (_, _, _) => new("unused"));
        Assert.Throws<NotSupportedException>(() => new Agent(new()
        {
            Model = stateful ? new JournalStatefulModel() : new JournalTestModel(),
            ContextManager = new Strands.Agents.Context.ContextManager(),
            Tools = [tool],
            ToolFilter = _ => { filters++; return true; },
            Plugins = [new RepositorySessionManager("session", repository)]
        }));
        Assert.Equal(0, filters);
        Assert.Empty(repository.Operations);
    }

    [Fact]
    public async Task ReobservingAnUnansweredPauseDoesNotRewriteIdenticalMetadata()
    {
        var repository = new JournalProbeRepository();
        var tool = new FunctionTool(new("approve", "approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalApprovalModel(), Tools = [tool], Plugins = [new RepositorySessionManager("session", repository)] });
        await agent.InvokeAsync("task");
        var count = repository.Operations.Count(operation => operation == "update-agent");
        Assert.Equal(StopReasons.Interrupt, (await agent.InvokeAsync(null)).StopReason);
        Assert.Equal(count, repository.Operations.Count(operation => operation == "update-agent"));
    }

    [Fact]
    public async Task BackendInitializationScopeCannotOutliveItsPhysicalCallback()
    {
        var repository = new JournalProbeRepository();
        var manager = new RepositorySessionManager("session", repository);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? escaped = null;
        repository.Before = (operation, _) =>
        {
            if (operation == "read-session" && escaped is null)
                escaped = Task.Run(async () =>
                {
                    await release.Task;
                    await using var nested = new Agent(new() { Id = "other-agent", Model = new JournalTestModel(), Plugins = [manager] });
                    await nested.InitializeAsync();
                });
            return default;
        };
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [manager] });
        try { await agent.InitializeAsync(); }
        finally { release.TrySetResult(true); }
        await escaped!;
        Assert.NotNull(await repository.Inner.ReadAgentAsync("session", "other-agent"));
    }
}

internal sealed class JournalStatefulModel : IStatefulModel
{
    public bool Stateful => true;
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await foreach (var item in new JournalTestModel().StreamAsync(request, cancellationToken)) yield return item; }
}
