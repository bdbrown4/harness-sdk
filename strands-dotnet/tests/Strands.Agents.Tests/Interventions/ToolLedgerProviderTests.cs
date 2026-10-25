using System.Text.Json;
using Strands.Agents.Interventions.Steering;

namespace Strands.Agents.Tests.Interventions;

public sealed class ToolLedgerProviderTests
{
    [Fact]
    public async Task PendingAndCompletedSnapshotsDetachInputRichOutputAndTiming()
    {
        await using var agent = new Agent(new() { Model = new NoCallsModel() });
        await using var context = await new ToolLedgerProvider().CreateAsync(agent);
        using var input = JsonDocument.Parse("{\"value\":42}");
        var call = new ToolUseBlock("call", "lookup", input.RootElement);
        await agent.Hooks.InvokeAsync(new BeforeToolCallEvent(agent, call, new Dictionary<string, object?>()));
        input.Dispose();
        var pending = context.Context; var initial = Assert.Single(pending.GetProperty("calls").EnumerateArray());
        Assert.Equal("pending", initial.GetProperty("status").GetString()); Assert.Equal(42, initial.GetProperty("args").GetProperty("value").GetInt32());
        Assert.True(DateTimeOffset.TryParse(initial.GetProperty("startTime").GetString(), out _));
        using var output = JsonDocument.Parse("{\"result\":true}"); var error = new IOException("provider failed");
        var result = new ToolResultBlock("call", "failed", ToolResultStatus.Error) { ContentBlocks = [new JsonBlock(output.RootElement)], Error = error };
        await agent.Hooks.InvokeAsync(new AfterToolCallEvent(agent, call, result)); output.Dispose();
        var completed = Assert.Single(context.Context.GetProperty("calls").EnumerateArray());
        Assert.Equal("error", completed.GetProperty("status").GetString()); Assert.Equal("provider failed", completed.GetProperty("error").GetString());
        Assert.True(completed.GetProperty("result")[0].GetProperty("json").GetProperty("result").GetBoolean());
        Assert.True(DateTimeOffset.Parse(completed.GetProperty("endTime").GetString()!) >= DateTimeOffset.Parse(completed.GetProperty("startTime").GetString()!));
        Assert.Equal("pending", initial.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(null, 101)]
    [InlineData(100, 100)]
    [InlineData(0, 0)]
    public async Task TypeScriptRetentionBoundAndPythonUnboundedOptionKeepTheExpectedCalls(int? maximum, int expected)
    {
        await using var agent = new Agent(new() { Model = new NoCallsModel() });
        await using var context = await new ToolLedgerProvider(new() { MaxEntries = maximum }).CreateAsync(agent);
        for (var index = 0; index < 101; index++)
            await agent.Hooks.InvokeAsync(new BeforeToolCallEvent(agent, new(index.ToString(), "lookup", JsonSerializer.SerializeToElement(new { index })), new Dictionary<string, object?>()));
        var entries = context.Context.GetProperty("calls"); Assert.Equal(expected, entries.GetArrayLength());
        if (maximum == 100) Assert.Equal("1", entries[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task SubscriptionsAreIndependentAndDisposalRemovesOnlyOwnedHooks()
    {
        var observed = 0; var appHooks = new HookRegistry(); appHooks.Register<BeforeToolCallEvent>((_, _) => { observed++; return default; });
        await using var first = new Agent(new() { Model = new NoCallsModel(), Hooks = appHooks }); await using var second = first.CreateChild();
        var factory = new ToolLedgerProvider(); var one = await factory.CreateAsync(first); await using var two = await factory.CreateAsync(second);
        var call = new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }));
        await first.Hooks.InvokeAsync(new BeforeToolCallEvent(first, call, new Dictionary<string, object?>()));
        Assert.Equal(1, one.Context.GetProperty("calls").GetArrayLength()); Assert.Equal(0, two.Context.GetProperty("calls").GetArrayLength());
        await one.DisposeAsync(); await one.DisposeAsync();
        await second.Hooks.InvokeAsync(new BeforeToolCallEvent(second, call, new Dictionary<string, object?>()));
        Assert.Equal(2, observed); Assert.Equal(1, two.Context.GetProperty("calls").GetArrayLength());
        Assert.Throws<ObjectDisposedException>(() => one.Context);
    }

    private sealed class NoCallsModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The model must not be called.");
    }
}
