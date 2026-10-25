using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Injection;
using Strands.Agents.Middleware;
using Xunit;

namespace Strands.Agents.Tests.Injection;

public sealed class ContextInjectorTests
{
    [Fact]
    public async Task EveryTurnAppendsAfterCorrelatedToolResults()
    {
        var model = new ToolCycle();
        var rendered = 0;
        var injector = new ContextInjector((_, _) => { rendered++; return new ValueTask<string?>("context"); }, new() { Trigger = InjectionTrigger.EveryTurn });
        var tool = new FunctionTool(new("work", "Runs the operation.", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("tool output"));
        await using var agent = new Agent(new() { Model = model, Tools = new[] { tool }, Plugins = new[] { injector } });

        await agent.InvokeAsync("the task");

        Assert.Equal(2, rendered);
        var resultTurn = model.Requests[1].Messages.Last();
        Assert.Equal("operation", Assert.IsType<ToolResultBlock>(resultTurn.Content[0]).ToolUseId);
        Assert.Equal("\n\ncontext", Assert.IsType<TextBlock>(resultTurn.Content[1]).Text);
        Assert.Equal(1, model.Requests[1].DynamicTrailingBlocks);
        Assert.Single(agent.Messages.Single(message => message.Content.OfType<ToolResultBlock>().Any()).Content);
    }

    [Fact]
    public async Task ACustomTriggerCanSelectOnlyTheToolResultTurn()
    {
        var model = new ToolCycle();
        var rendered = 0;
        var injector = new ContextInjector((_, _) => { rendered++; return new ValueTask<string?>("context"); }, new()
        {
            Predicate = context => context.Messages.Last().Content.OfType<ToolResultBlock>().Any()
        });
        var tool = new FunctionTool(new("work", "Runs the operation.", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("tool output"));
        await using var agent = new Agent(new() { Model = model, Tools = new[] { tool }, Plugins = new[] { injector } });

        await agent.InvokeAsync("the task");

        Assert.Equal(1, rendered);
        Assert.Equal("the task", model.Requests[0].Messages[0].Text);
        Assert.Equal(2, model.Requests[1].Messages.Last().Content.Count);
    }

    [Fact]
    public async Task MultipleInjectorsRunInRegistrationOrderAndAccumulateAnExistingCacheSuffix()
    {
        var model = new Responses();
        var first = new ContextInjector((_, _) => new ValueTask<string?>("first"), new() { Name = "first" });
        var second = new ContextInjector((context, _) => new ValueTask<string?>(context.Messages.Last().Text.EndsWith("first", StringComparison.Ordinal) ? "second" : "wrong order"), new() { Name = "second" });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { first, second } });
        agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => new ValueTask<InvokeModelContext>(context with { DynamicTrailingBlocks = 1 }));

        await agent.InvokeAsync("the task");

        var request = Assert.Single(model.Requests);
        Assert.Equal("the task\n\nfirst\n\nsecond", Assert.Single(request.Messages).Text);
        Assert.Equal(3, request.DynamicTrailingBlocks);
        Assert.Equal("the task", agent.Messages[0].Text);
    }

    [Fact]
    public async Task EarlierUserProjectionPreservesMetadataWithoutChangingTheLastMessageSuffix()
    {
        var metadata = JsonSerializer.SerializeToElement(new { custom = new { source = "caller" } });
        var user = new Message(Role.User, new[] { new TextBlock("seed") }, "stable-id", metadata);
        var model = new Responses();
        var injector = new ContextInjector((_, _) => new ValueTask<string?>("\ncontext\n"), new() { Trigger = InjectionTrigger.EveryTurn });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector }, Messages = new[] { user, Message.Assistant("prior") } });

        await agent.InvokeAsync(null);

        var request = Assert.Single(model.Requests);
        Assert.Equal("seed\ncontext\n", request.Messages[0].Text);
        Assert.Equal("stable-id", request.Messages[0].TrackingId);
        Assert.Equal(metadata.GetRawText(), request.Messages[0].Metadata!.Value.GetRawText());
        Assert.Equal("prior", request.Messages[1].Text);
        Assert.Equal(0, request.DynamicTrailingBlocks);
        Assert.Equal("seed", agent.Messages[0].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task EmptyRenderedTextLeavesTheConversationUnchanged(string? text)
    {
        var model = new Responses();
        var injector = new ContextInjector((_, _) => new ValueTask<string?>(text));
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector } });

        await agent.InvokeAsync("the task");

        Assert.Equal("the task", Assert.Single(Assert.Single(model.Requests).Messages).Text);
        Assert.Equal(0, model.Requests[0].DynamicTrailingBlocks);
    }

    [Fact]
    public async Task EveryTurnWithNoUserMessageDoesNotInventOne()
    {
        var model = new Responses();
        var injector = new ContextInjector((_, _) => new ValueTask<string?>("context"), new() { Trigger = InjectionTrigger.EveryTurn });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector }, Messages = new[] { Message.Assistant("existing") } });

        await agent.InvokeAsync(null);

        var message = Assert.Single(Assert.Single(model.Requests).Messages);
        Assert.Equal(Role.Assistant, message.Role);
        Assert.Equal("existing", message.Text);
        Assert.Equal(0, model.Requests[0].DynamicTrailingBlocks);
    }

    [Fact]
    public async Task EmptyTypedUserContentReceivesTextWithoutAnArtificialSeparator()
    {
        var model = new Responses();
        var injector = new ContextInjector((_, _) => new ValueTask<string?>("context"));
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector } });

        await agent.InvokeContentAsync(Array.Empty<ContentBlock>());

        Assert.Equal("context", Assert.IsType<TextBlock>(Assert.Single(Assert.Single(model.Requests).Messages.Single().Content)).Text);
        Assert.Empty(agent.Messages[0].Content);
    }

    [Fact]
    public async Task SharedInjectorKeepsSimultaneousAgentsAndRepeatedCallsIndependent()
    {
        var injector = new ContextInjector(async (context, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return context.AppState.Get<string>("tenant");
        });
        var firstModel = new Responses(); var secondModel = new Responses();
        await using var first = new Agent(new() { Model = firstModel, Plugins = new[] { injector } });
        await using var second = new Agent(new() { Model = secondModel, Plugins = new[] { injector } });
        first.AppState.Set("tenant", "first"); second.AppState.Set("tenant", "second");

        await Task.WhenAll(first.InvokeAsync("one"), second.InvokeAsync("two"));
        await first.InvokeAsync("next");

        Assert.Equal("one\n\nfirst", firstModel.Requests[0].Messages[0].Text);
        Assert.Equal("two\n\nsecond", secondModel.Requests[0].Messages[0].Text);
        Assert.Equal("one", firstModel.Requests[1].Messages[0].Text);
        Assert.Equal("next\n\nfirst", firstModel.Requests[1].Messages.Last().Text);
        Assert.All(first.Messages.Where(message => message.Role == Role.User), message => Assert.DoesNotContain("first", message.Text));
        Assert.Single(first.AppState.GetAll()); Assert.Single(second.AppState.GetAll());
    }

    [Fact]
    public async Task TriggerFailuresSkipRenderingAndDoNotStopTheModel()
    {
        var model = new Responses(); var rendered = 0;
        var injector = new ContextInjector((_, _) => { rendered++; return new ValueTask<string?>("unused"); }, new() { Predicate = _ => throw new IOException("trigger unavailable") });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector } });

        Assert.Equal("done", (await agent.InvokeAsync("the task")).ToString());

        Assert.Equal(0, rendered);
        Assert.Equal("the task", Assert.Single(Assert.Single(model.Requests).Messages).Text);
    }

    [Fact]
    public async Task RequestedCancellationDuringRenderingRetainsTheCallerTokenAndPreventsModelIo()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Responses(); Exception? observed = null;
        var injector = new ContextInjector(async (_, token) => { Assert.Equal(cancellation.Token, token); entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return "unused"; });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector } });
        agent.Hooks.Register<AfterModelCallEvent>((value, _) => { observed = value.Error; return default; });
        var invocation = agent.InvokeAsync("the task", cancellationToken: cancellation.Token);
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        cancellation.Cancel();
        var result = await invocation;

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACallbackCannotHideRequestedCancellationWithAnotherFailure(bool predicate)
    {
        using var cancellation = new CancellationTokenSource();
        var model = new Responses(); Exception? observed = null;
        var injector = new ContextInjector((_, _) => { cancellation.Cancel(); throw new IOException("renderer failed during cancellation"); }, new()
        {
            Predicate = predicate ? _ => { cancellation.Cancel(); throw new IOException("trigger failed during cancellation"); }
            : null
        });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector } });
        agent.Hooks.Register<AfterModelCallEvent>((value, _) => { observed = value.Error; return default; });

        var result = await agent.InvokeAsync("the task", cancellationToken: cancellation.Token);

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task AnUnrelatedCancellationExceptionIsAnOrdinaryFailOpenRendererFailure()
    {
        var model = new Responses();
        var injector = new ContextInjector((_, _) => throw new OperationCanceledException("unrelated backend cancellation"));
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector } });

        Assert.Equal("done", (await agent.InvokeAsync("the task")).ToString());

        Assert.Single(model.Requests);
    }

    [Fact]
    public void InvalidConfigurationIsRejectedBeforeBinding()
    {
        Assert.Throws<ArgumentException>(() => new ContextInjector((_, _) => default, new() { Name = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextInjector((_, _) => default, new() { Trigger = (InjectionTrigger)99 }));
        Assert.Throws<ArgumentNullException>(() => new ContextInjector(null!));
    }

    [Fact]
    public async Task ACustomTriggerReplacesTheBuiltInPolicyAndReceivesBorrowedApplicationState()
    {
        var model = new Responses();
        var rendered = 0;
        InjectionContext? observed = null;
        var injector = new ContextInjector((_, _) => { rendered++; return new ValueTask<string?>("context"); }, new()
        {
            Predicate = context => { observed = context; return context.AppState.Get<bool>("enabled"); }
        });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector } });
        agent.AppState.Set("enabled", false);

        await agent.InvokeAsync("the task");

        Assert.Equal(0, rendered);
        Assert.NotNull(observed);
        Assert.Same(agent, observed.Agent);
        Assert.Same(agent.AppState, observed.AppState);
        Assert.Equal("the task", Assert.Single(observed.Messages).Text);
        Assert.Single(agent.AppState.GetAll());
        Assert.Equal("the task", Assert.Single(Assert.Single(model.Requests).Messages).Text);
    }

    [Fact]
    public async Task RenderingFailuresSkipInjectionWithoutLosingTheModelCall()
    {
        var model = new Responses();
        var failure = new IOException("retrieval unavailable");
        var injector = new ContextInjector((_, _) => throw failure);
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector } });

        var result = await agent.InvokeAsync("the task");

        Assert.Equal("done", result.ToString());
        Assert.Equal("the task", Assert.Single(Assert.Single(model.Requests).Messages).Text);
        Assert.Equal(0, model.Requests[0].DynamicTrailingBlocks);
    }

    [Fact]
    public async Task TheDefaultTriggerSkipsTheToolResultTurn()
    {
        var model = new ToolCycle();
        var rendered = 0;
        var injector = new ContextInjector((_, _) => { rendered++; return new ValueTask<string?>("context"); });
        var tool = new FunctionTool(new("work", "Runs the operation.", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("tool output"));
        await using var agent = new Agent(new() { Model = model, Tools = new[] { tool }, Plugins = new[] { injector } });

        await agent.InvokeAsync("the task");

        Assert.Equal(1, rendered);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("the task\n\ncontext", model.Requests[0].Messages[0].Text);
        var resultTurn = model.Requests[1].Messages.Last();
        Assert.IsType<ToolResultBlock>(Assert.Single(resultTurn.Content));
        Assert.Equal(0, model.Requests[1].DynamicTrailingBlocks);
    }

    [Fact]
    public async Task RenderedContextIsEphemeralAndPreservesTheLogicalUserMessage()
    {
        var model = new Responses();
        var injector = new ContextInjector((context, _) => new ValueTask<string?>("retrieved context"));
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { injector } });
        var input = new ContentBlock[] { new TextBlock("the task"), new JsonBlock(JsonSerializer.SerializeToElement(new { value = 1 })) };

        await agent.InvokeContentAsync(input);

        var projected = Assert.Single(Assert.Single(model.Requests).Messages);
        var durable = agent.Messages[0];
        Assert.Equal("the task\n\nretrieved context", projected.Text);
        Assert.Equal("the task", durable.Text);
        Assert.Equal(durable.TrackingId, projected.TrackingId);
        Assert.Equal(2, durable.Content.Count);
        Assert.Equal(3, projected.Content.Count);
        Assert.IsType<JsonBlock>(projected.Content[1]);
        Assert.Equal(1, model.Requests[0].DynamicTrailingBlocks);
    }

    private sealed class Responses : IModel
    {
        internal List<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private sealed class ToolCycle : IModel
    {
        internal List<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = Requests.Count == 0;
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelMessageStartEvent();
            if (first) yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("operation", "work", JsonSerializer.SerializeToElement(new { })));
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
