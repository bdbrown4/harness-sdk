using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.EventParity;

public sealed class ModelCompletionEventTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResultFollowedByAMiddlewareFailureNeverPublishesACompletedMessage(bool disposalFailure)
    {
        var failure = new IOException("middleware completion failed"); var middleware = new MiddlewareRegistry(); var observed = 0; var after = 0;
        middleware.RegisterWrap(MiddlewareStages.InvokeModel, (_, _, _) => new LateResult(failure, disposalFailure));
        var hooks = new HookRegistry();
        hooks.Register<ModelMessageEvent>((_, _) => { observed++; return default; });
        hooks.Register<AfterModelCallEvent>((value, _) => { after++; Assert.Same(failure, value.Error); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(), Middleware = middleware, Hooks = hooks });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question")));
        Assert.Equal(0, observed); Assert.Equal(1, after); Assert.Single(agent.Messages);
    }

    private sealed class LateResult(Exception failure, bool disposalFailure) : IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>>
    {
        public IAsyncEnumerator<IMiddlewareEvent<InvokeModelResult>> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(failure, disposalFailure);
        private sealed class Enumerator(Exception failure, bool disposalFailure) : IAsyncEnumerator<IMiddlewareEvent<InvokeModelResult>>
        {
            private int _moves;
            public IMiddlewareEvent<InvokeModelResult> Current => new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("not accepted"), StopReasons.EndTurn, new()));
            public ValueTask<bool> MoveNextAsync()
            {
                if (++_moves == 1) return new(true);
                if (!disposalFailure) throw failure;
                return new(false);
            }
            public ValueTask DisposeAsync() { if (disposalFailure) throw failure; return default; }
        }
    }
}
