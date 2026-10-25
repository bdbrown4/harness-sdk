using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class InvocationCompositionTests
{
    [Fact]
    public async Task JoinedNotebookExecutionPreservesRedactedInputAndOriginalEventCorrelation()
    {
        var state = new Dictionary<string, object?> { ["request"] = new object() };
        var hooks = new HookRegistry();
        var before = 0;
        var appended = new List<MessageAddedEvent>();
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            before++;
            Assert.Same(state, value.InvocationState);
            value.Messages = [Message.User("redacted")];
            return default;
        });
        hooks.Register<MessageAddedEvent>((value, _) => { appended.Add(value); return default; });
        var scheduler = new DuplicateScheduler(state);
        var notebook = Notebook.Create();
        var model = new GatedNotebookModel();
        await using var agent = new Agent(new() { Model = model, Tools = [notebook], Hooks = hooks, ToolBatchExecutor = scheduler });
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "" });
        var primary = agent.InvokeAsync("secret", new() { IdempotencyToken = "joined", InvocationState = state });
        Task<AgentResult>? duplicate = null;
        try
        {
            Assert.Same(model.Entered.Task, await Task.WhenAny(model.Entered.Task, Task.Delay(TimeSpan.FromSeconds(8))));
            duplicate = agent.InvokeAsync("ignored duplicate", new() { IdempotencyToken = "joined", InvocationState = new Dictionary<string, object?>() });
            Assert.False(duplicate.IsCompleted);
        }
        finally
        {
            model.Release.TrySetResult(true);
            try { await primary; } catch { }
            if (duplicate is not null) { try { await duplicate; } catch { } }
        }

        Assert.Same(await primary, await duplicate!);
        Assert.Same(state, (await primary).InvocationState);
        Assert.Equal(1, before);
        Assert.Equal(1, scheduler.Calls);
        Assert.Equal(2, model.Calls);
        Assert.Equal(4, appended.Count);
        Assert.All(appended, value => Assert.Same(state, value.InvocationState));
        Assert.Equal("redacted", agent.Messages[0].Text);
        Assert.Equal("redacted", model.Input);
        Assert.DoesNotContain(agent.Messages, message => message.Text.IndexOf("secret", StringComparison.Ordinal) >= 0 || message.Text.IndexOf("ignored duplicate", StringComparison.Ordinal) >= 0);
        Assert.Equal("effect", agent.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
        Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());

        var snapshot = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.State });
        await using var copy = new Agent(new() { Model = model });
        await copy.LoadSnapshotAsync(snapshot);
        var read = JsonSerializer.SerializeToElement(new { mode = "read", name = "notes" });
        Assert.Equal("effect", await notebook.InvokeAsync(read, new(copy, new("read", notebook.Spec.Name, read), new Dictionary<string, object?>())));
    }

    private sealed class DuplicateScheduler(IReadOnlyDictionary<string, object?> state) : IToolBatchExecutor
    {
        internal int Calls;
        public async ValueTask ExecuteAsync(ToolBatchExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Same(state, context.InvocationState);
            Assert.Single(context.Calls);
            var first = context.ExecuteAsync(0);
            Assert.Same(first, context.ExecuteAsync(0));
            Assert.NotNull(await first);
        }
    }

    private sealed class GatedNotebookModel : IModel
    {
        internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal string? Input;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = Interlocked.Increment(ref Calls) == 1;
            if (first)
            {
                Input = request.Messages[0].Text;
                Entered.TrySetResult(true);
                await Release.Task;
            }
            yield return new ModelMessageStartEvent();
            if (first)
            {
                var input = JsonSerializer.SerializeToElement(new { mode = "write", name = "notes", newStr = "effect" });
                yield return new ModelContentBlockStartEvent(0, new("call", "notebook", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: input.GetRawText());
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(first ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
}
