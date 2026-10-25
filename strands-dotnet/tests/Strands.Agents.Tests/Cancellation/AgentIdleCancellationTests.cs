using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentIdleCancellationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("idle cancellation")]
    public async Task ImmediateIdleRequestStopsOnlyTheNextInvocation(string? message)
    {
        var model = new CancellationTestModel(Text("next invocation"));
        await using var agent = new Agent(new() { Model = model, IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        var before = agent.CancelSignal;
        agent.Cancel(message);
        Assert.False(before.IsCancellationRequested);
        Assert.True(agent.CancelSignal.IsCancellationRequested);
        var result = await agent.InvokeAsync("unaccepted input");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Empty(model.Requests);
        Assert.DoesNotContain(agent.Messages, value => value.Role == Role.User);
        if (message is null) Assert.Null(result.Message);
        else Assert.Equal(message, Assert.Single(result.Message!.Content.OfType<TextBlock>()).Text);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal("next invocation", (await agent.InvokeAsync("next")).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredOnlyIdleRequestIsClearedOnAdmission(bool provideMessage)
    {
        var model = new CancellationTestModel(Text("natural"));
        await using var agent = new Agent(new() { Model = model, IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel(provideMessage ? "forgotten" : null, afterCurrentTools: true);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal("natural", (await agent.InvokeAsync("go")).ToString());
        Assert.DoesNotContain(agent.Messages, value => value.Text == "forgotten");
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    public async Task ImmediateRequestRetainsLastNonNullMessageFromAnyRequest(string message)
    {
        await using var agent = new Agent(new() { Model = new CancellationTestModel(Text("unexpected")), IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel("old", afterCurrentTools: true);
        agent.Cancel(message, afterCurrentTools: true);
        agent.Cancel();
        agent.Cancel(afterCurrentTools: true);
        var result = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(message, Assert.Single(result.Message!.Content.OfType<TextBlock>()).Text);
    }

    [Fact]
    public async Task InvalidLimitsDoNotConsumeAnIdleRequest()
    {
        await using var agent = new Agent(new() { Model = new CancellationTestModel(Text("unexpected")), IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel("retained");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => agent.InvokeAsync("invalid", new() { MaxTurns = 0 }));
        Assert.True(agent.CancelSignal.IsCancellationRequested);
        Assert.Equal("retained", (await agent.InvokeAsync("valid")).ToString());
    }

    [Fact]
    public void InvalidPolicyFailsDuringConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new() { Model = new CancellationTestModel(), IdleCancellationBehavior = (IdleCancellationBehavior)123 }));
    }
}
