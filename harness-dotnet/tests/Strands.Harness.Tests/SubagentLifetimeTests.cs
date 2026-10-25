using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class SubagentLifetimeTests
{
    [Fact]
    public async Task RequestedCancellationSurvivesChildCleanupFailureAndAttemptsEveryResource()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException("child cancelled", null, cancellation.Token);
        var cleanup = new IOException("child cleanup failed");
        var disposals = new List<string>();
        await using var parent = new Agent(new() { Model = new Model(null) });
        var tool = Subagent.Create(new()
        {
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (spec, _, _) => new(new Agent(new()
            {
                Model = new Model(() => { cancellation.Cancel(); throw primary; }),
                Messages = spec.Messages,
                OwnedResources = [new Resource("first", disposals, cleanup), new Resource("second", disposals, null)],
            })),
        });
        var input = JsonSerializer.SerializeToElement(new { task = "work" });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeResultAsync(input,
            new(parent, new("delegate", "subagent", input), new Dictionary<string, object?>()), cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.Equal(["second", "first"], disposals);
    }

    [Fact]
    public async Task CancellationThrownByCompletionCallbackKeepsItsIdentityAcrossChildDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException("completion cancelled", null, cancellation.Token);
        var cleanup = new IOException("child cleanup failed");
        var hooks = new HookRegistry();
        hooks.Register<AfterInvocationEvent>((_, _) => throw primary);
        await using var parent = new Agent(new() { Model = new Model(null) });
        var tool = Subagent.Create(new()
        {
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (spec, _, _) => new(new Agent(new()
            {
                Model = new Model(cancellation.Cancel),
                Messages = spec.Messages,
                Hooks = hooks,
                OwnedResources = [new Resource("cleanup", new List<string>(), cleanup)],
            })),
        });
        var input = JsonSerializer.SerializeToElement(new { task = "work" });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeResultAsync(input,
            new(parent, new("delegate", "subagent", input), new Dictionary<string, object?>()), cancellation.Token).AsTask());

        Assert.Same(primary, error);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task BodyFailureRemainsTheToolErrorWhenChildDisposalAlsoFails()
    {
        var primary = new InvalidOperationException("child body failed");
        var cleanup = new IOException("child cleanup failed");
        await using var parent = new Agent(new() { Model = new Model(null) });
        var tool = Subagent.Create(new()
        {
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (spec, _, _) => new(new Agent(new()
            {
                Model = new Model(() => throw primary),
                Messages = spec.Messages,
                OwnedResources = [new Resource("cleanup", new List<string>(), cleanup)],
            })),
        });
        var input = JsonSerializer.SerializeToElement(new { task = "work" });

        var result = await tool.InvokeResultAsync(input, new(parent, new("delegate", "subagent", input), new Dictionary<string, object?>()));

        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal("Subagent error: child body failed", result.Content);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
    }

    private sealed class Resource(string name, IList<string> disposals, Exception? error) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        { disposals.Add(name); if (error is not null) throw error; return default; }
    }

    private sealed class Model(Action? body) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent();
            body?.Invoke();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
