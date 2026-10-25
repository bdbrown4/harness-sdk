using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class SwarmToolLifecycleConformanceTests
{
    [Theory]
    [InlineData("parent")]
    [InlineData("duplicate")]
    [InlineData("null")]
    public async Task InvalidBuilderOwnershipNeverDisposesTheParent(string kind)
    {
        var childClosed = 0;
        var parentClosed = 0;
        var builds = 0;
        var modelCalls = 0;
        var model = new Events(_ => { modelCalls++; return Answer("parent usable"); });
        await using var parent = new Agent(new() { Model = model, OwnedResources = [new Resource(() => parentClosed++)] });
        Agent? child = null;
        var tool = SwarmTool.Create(new()
        {
            Builder = (_, _, _) =>
            {
                builds++;
                if (kind == "parent") return new(parent);
                if (kind == "null") return new((Agent)null!);
                child ??= new(new() { Model = model, OwnedResources = [new Resource(() => childClosed++)] });
                return new(child);
            }
        });
        var input = Team("a", "b");
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal(0, modelCalls);
        Assert.Equal(0, parentClosed);
        Assert.Equal(kind == "duplicate" ? 1 : 0, childClosed);
        Assert.Equal(kind == "duplicate" ? 2 : 1, builds);
        Assert.Equal("parent usable", (await parent.InvokeAsync("still usable")).ToString());
    }

    [Fact]
    public async Task ParentShutdownJoinsAcceptedChildrenAndRejectsLaterAdmission()
    {
        var model = new GatedModel();
        var builds = 0;
        var closed = 0;
        var tool = SwarmTool.Create(new()
        {
            Builder = (_, _, _) =>
            {
                builds++;
                return new(new Agent(new()
                {
                    Model = model,
                    OwnedResources = [new Resource(() => { Assert.True(model.Exited); closed++; })]
                }));
            }
        });
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("unused")) });
        var input = Team("a");
        var active = tool.InvokeResultAsync(input, Context(parent, tool, input)).AsTask();
        try
        {
            await Ready(model.Entered.Task, active);
            await parent.ShutdownAsync();
            Assert.True(model.Exited);
            Assert.Equal(1, closed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => tool.InvokeResultAsync(input, Context(parent, tool, input)).AsTask());
            Assert.Equal(1, builds);
        }
        finally
        {
            model.Release.TrySetResult(true);
            try { await active; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task EarlyStreamConsumerDisposalJoinsChildAndBorrowedModelStaysReusable()
    {
        var model = new GatedModel();
        var closed = 0;
        var tool = SwarmTool.Create(new()
        {
            Builder = (_, _, _) => new(new Agent(new()
            {
                Model = model,
                OwnedResources = [new Resource(() => { Assert.True(model.Exited); closed++; })]
            }))
        });
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("parent reusable")) });
        var input = Team("a");
        var iterator = ((IStreamingTool)tool).StreamAsync(input, Context(parent, tool, input)).GetAsyncEnumerator();
        try
        {
            while (!model.Entered.Task.IsCompleted) Assert.True(await iterator.MoveNextAsync());
            await iterator.DisposeAsync();
            Assert.True(model.Exited);
            Assert.Equal(1, closed);
            Assert.Equal(0, model.Disposals);
            Assert.Equal("parent reusable", (await parent.InvokeAsync("still usable")).ToString());
        }
        finally
        {
            model.Release.TrySetResult(true);
            await iterator.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCleanupCancellationUsesTheNativeConsumerBoundary(bool streamDirectly)
    {
        using var caller = new CancellationTokenSource();
        var original = new IOException("independent builder failure");
        var closed = 0;
        var tool = SwarmTool.Create(new()
        {
            Builder = (specification, _, _) => specification.Name == "b" ? throw original : new(new Agent(new()
            {
                Model = new Events(_ => Answer("must not run")),
                OwnedResources = [new Resource(() => { closed++; caller.Cancel(); })]
            }))
        });
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("unused")) });
        var input = Team("a", "b");
        if (streamDirectly)
        {
            var events = new List<IToolStreamEvent>();
            await foreach (var item in ((IStreamingTool)tool).StreamAsync(input, Context(parent, tool, input), caller.Token))
                events.Add(item);
            var terminal = Assert.Single(events.OfType<ToolStreamResultEvent>()).Result;
            Assert.Equal(ToolResultStatus.Error, terminal.Status);
            Assert.Same(original, terminal.Error);
        }
        else
        {
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                tool.InvokeResultAsync(input, Context(parent, tool, input), caller.Token).AsTask());
            Assert.Equal(caller.Token, cancellation.CancellationToken);
        }
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(1, closed);
    }

    internal static JsonElement Team(params string[] names) => JsonSerializer.SerializeToElement(new
    {
        task = "work",
        agents = names.Select(name => new { name, instructions = "Do." }).ToArray()
    });
    internal static ToolContext Context(Agent parent, ITool tool, JsonElement input) =>
        new(parent, new("team-call", tool.Spec.Name, input), new Dictionary<string, object?>());
    internal static ModelStreamEvent[] Answer(string text) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0),
        new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0),
        new ModelMessageStopEvent(StopReasons.EndTurn)
    ];
    private static async Task Ready(Task ready, Task active)
    {
        await Task.WhenAny(ready, active, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ready.IsCompleted, "The accepted child must start before testing lifecycle cancellation.");
    }
    internal sealed class Events(Func<ModelRequest, IReadOnlyList<ModelStreamEvent>> events) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in events(request)) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
    internal sealed class Resource(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { dispose(); return default; }
    }
    private sealed class GatedModel : IModel, IAsyncDisposable
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Exited { get; private set; }
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Release.TrySetCanceled(cancellationToken));
            try
            {
                Entered.TrySetResult(true);
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "partial");
                await Release.Task.ConfigureAwait(false);
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            finally { Exited = true; }
        }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
