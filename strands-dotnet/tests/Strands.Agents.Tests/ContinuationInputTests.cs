using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class ContinuationInputTests
{
    [Fact]
    public async Task EqualValuedEventsKeepIndependentInputTransactions()
    {
        await using var agent = NewAgent(); var queue = new ContinuationInputs(agent);
        var state = new Dictionary<string, object?>();
        var first = new AfterInvocationEvent(agent, null, null, state);
        var second = new AfterInvocationEvent(agent, null, null, state);
        Assert.Equal(first, second); Assert.NotSame(first, second);
        queue.Add(first, new[] { Message.User("first") }); queue.Add(second, new[] { Message.User("second") });
        Assert.Equal("first", Assert.Single((await queue.PrepareAsync(first, StopReasons.EndTurn))!).Text);
        await queue.MarkAppendedAsync(first);
        Assert.Equal("second", Assert.Single((await queue.PrepareAsync(second, StopReasons.EndTurn))!).Text);
    }

    [Fact]
    public async Task AbandonmentCallbackFailureDoesNotSkipOtherSettlements()
    {
        await using var agent = NewAgent(); var queue = new ContinuationInputs(agent); var boundary = After(agent); var abandoned = 0;
        var expected = new IOException("not appended"); Exception? actual = null;
        queue.Add(boundary, new[] { Message.User("first") }, onAbandoned: _ => throw new IOException("failed abandon callback"));
        queue.Add(boundary, new[] { Message.User("second") }, onAbandoned: reason => { actual = reason; abandoned++; return default; });
        await queue.AbandonAsync(boundary, expected); await queue.AbandonAsync(boundary, expected);
        Assert.Equal(1, abandoned); Assert.Same(expected, actual);
    }

    [Fact]
    public async Task PreparedInputIsDetachedAndSettlesExactlyOnceAfterAppend()
    {
        await using var agent = NewAgent();
        var queue = new ContinuationInputs(agent); var boundary = After(agent); var appended = 0; var abandoned = 0;
        var messages = new[] { Message.User("original") };
        queue.Add(boundary, messages, () => { appended++; return default; }, _ => { abandoned++; return default; });
        messages[0] = Message.User("changed");
        var prepared = await queue.PrepareAsync(boundary, StopReasons.EndTurn);
        Assert.Equal("original", Assert.Single(prepared!).Text); Assert.Empty(agent.Messages); Assert.Equal(0, appended);
        await queue.MarkAppendedAsync(boundary); await queue.MarkAppendedAsync(boundary); await queue.AbandonAsync(boundary, new IOException("too late"));
        Assert.Equal(1, appended); Assert.Equal(0, abandoned);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("assistant")]
    [InlineData("dangling")]
    [InlineData("orphan")]
    public async Task InvalidContributionIsAbandonedWithoutLosingTheOtherCompleteInput(string variant)
    {
        await using var agent = NewAgent(); var queue = new ContinuationInputs(agent); var boundary = After(agent); var abandoned = 0;
        Message[] invalid = variant switch
        {
            "empty" => Array.Empty<Message>(),
            "assistant" => new[] { new Message(Role.Assistant, new ContentBlock[] { new TextBlock("incomplete input") }) },
            "dangling" => new[] { new Message(Role.Assistant, new ContentBlock[] { new ToolUseBlock("call", "work", JsonSerializer.SerializeToElement(new { })) }), Message.User("missing result") },
            _ => new[] { new Message(Role.User, new ContentBlock[] { new ToolResultBlock("orphan", "result") }) }
        };
        queue.Add(boundary, invalid, () => throw new InvalidOperationException("must not append"), error => { Assert.IsAssignableFrom<Exception>(error); abandoned++; return default; });
        queue.Add(boundary, new[] { Message.User("valid") });
        Assert.Equal("valid", Assert.Single((await queue.PrepareAsync(boundary, StopReasons.EndTurn))!).Text);
        Assert.Equal(1, abandoned); Assert.Empty(agent.Messages);
        await queue.AbandonAsync(boundary, new IOException("not consumed")); Assert.Equal(1, abandoned);
    }

    [Fact]
    public async Task InterruptDefersTransactionsUntilALaterAfterInvocationBoundary()
    {
        await using var agent = NewAgent(); var queue = new ContinuationInputs(agent); var first = After(agent); var appended = 0;
        queue.Add(first, new[] { Message.User("deferred") }, () => { appended++; return default; });
        Assert.Null(await queue.PrepareAsync(first, StopReasons.Interrupt));
        await queue.AbandonAsync(first, new IOException("event ended")); Assert.Equal(0, appended);
        var before = new BeforeModelCallEvent(agent, new(Array.Empty<Message>(), null, Array.Empty<ToolSpec>()));
        Assert.Null(await queue.PrepareAsync(before));
        var next = After(agent); queue.Add(next, new[] { Message.User("later") });
        Assert.Equal(new[] { "deferred", "later" }, (await queue.PrepareAsync(next, StopReasons.EndTurn))!.Select(message => message.Text));
        await queue.MarkAppendedAsync(next); Assert.Equal(1, appended);
    }

    [Fact]
    public async Task ADisallowedStopAbandonsItsUnappendedInputOnce()
    {
        await using var agent = NewAgent(); var queue = new ContinuationInputs(agent); var boundary = After(agent); var abandoned = 0;
        var expected = new IOException("limit reached");
        queue.Add(boundary, new[] { Message.User("pending") }, () => throw new InvalidOperationException("must not append"), error => { Assert.Same(expected, error); abandoned++; return default; });
        Assert.Null(await queue.PrepareAsync(boundary, StopReasons.MaxTurns));
        await queue.AbandonAsync(boundary, expected); await queue.AbandonAsync(boundary, expected); Assert.Equal(1, abandoned);
    }

    [Fact]
    public async Task SettlementCallbackFailureCannotSkipAnotherAcceptedContribution()
    {
        await using var agent = NewAgent(); var queue = new ContinuationInputs(agent); var boundary = After(agent); var appended = 0;
        queue.Add(boundary, new[] { Message.User("first") }, () => throw new IOException("observer failed"));
        queue.Add(boundary, new[] { Message.User("second") }, () => { appended++; return default; });
        Assert.Equal(2, (await queue.PrepareAsync(boundary, StopReasons.EndTurn))!.Count);
        await queue.MarkAppendedAsync(boundary); Assert.Equal(1, appended);
    }

    [Fact]
    public async Task EventsFromAnotherAgentCannotOwnTheTransaction()
    {
        await using var agent = NewAgent(); await using var other = NewAgent(); var queue = new ContinuationInputs(agent);
        Assert.Throws<ArgumentException>(() => queue.Add(After(other), new[] { Message.User("wrong owner") }));
    }

    private static Agent NewAgent() => new(new() { Model = new Model() });
    private static AfterInvocationEvent After(Agent agent) => new(agent, null, null);
    private sealed class Model : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }
}
