using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentCancellationBackgroundTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicCancellationStopsOnlyTheForegroundAutomaticWaitAndKeepsAcceptedWorkAlive(bool inspectContext)
    {
        var started = Gate(); var release = Gate(); var after = 0; var bodyToken = CancellationToken.None; var contextToken = CancellationToken.None;
        var work = Tool("work", async (_, context, token) => { bodyToken = token; contextToken = context.CancelSignal; started.TrySetResult(true); await release.Task; return "accepted work"; });
        var model = new CancellationTestModel(Call(("work", "work", "{}")), Text("waiting"), Text("unexpected automatic continuation"));
        await using var agent = new Agent(new() { Model = model, Tools = [work], BackgroundTasks = new() { Always = ["work"] } });
        agent.Hooks.Register<AfterInvocationEvent>(async (_, _) =>
        {
            if (Interlocked.Increment(ref after) != 1) return;
            await started.Task;
            agent.Cancel();
        }, -1);
        var invocation = agent.InvokeAsync("go");
        try
        {
            await Wait(started.Task);
            await Wait(invocation);
            Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
            Assert.False(bodyToken.IsCancellationRequested);
            if (inspectContext) Assert.False(contextToken.IsCancellationRequested);
            Assert.Equal(2, model.Requests.Count);
        }
        finally { release.TrySetResult(true); await invocation; }
    }
}
