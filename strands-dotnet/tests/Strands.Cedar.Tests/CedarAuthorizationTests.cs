using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Interventions;
using Strands.Agents.Interventions.Cedar;

namespace Strands.Cedar.Tests;

public sealed class CedarAuthorizationTests
{
    [Fact]
    public async Task RealPoliciesPermitAndDenyByActionInputAndPrincipal()
    {
        await using var agent = CreateAgent();
        var handler = new CedarAuthorization(new() { Policies = "permit(principal == User::\"alice\", action == Action::\"search\", resource) when { context.input.query == \"雪\" };", Principal = new("User", "alice") });
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent, "search", "{\"query\":\"雪\"}")));
        Assert.IsType<Deny>(await handler.BeforeToolCallAsync(Event(agent, "write", "{\"query\":\"雪\"}")));
        Assert.IsType<Deny>(await handler.BeforeToolCallAsync(Event(agent, "search", "{\"query\":\"other\"}")));
    }

    [Fact]
    public void RealParserRejectsInvalidPolicyAtConstruction() => Assert.Throws<ArgumentException>(() => new CedarAuthorization(new() { Policies = "permit(this is invalid)" }));

    [Fact]
    public async Task ExplicitInlineTextCannotBeReinterpretedAsAHostFile()
    {
        await using var agent = CreateAgent();
        var handler = new CedarAuthorization(new() { Policies = "permit(principal, action, resource);\n// loaded from selected sandbox policy.cedar", PoliciesAreInline = true });
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent)));
    }

    [Fact]
    public void OfficialGeneratedSchemaRejectsUnknownToolActions()
    {
        var tool = new ToolSpec("search", "Search", JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } }));
        Assert.Throws<ArgumentException>(() => new CedarAuthorization(new() { Policies = "permit(principal, action == Action::\"typo\", resource);", Tools = [tool] }));
    }

    [Fact]
    public async Task EvaluationErrorsDenyAndDoNotConsumeAllowance()
    {
        await using var agent = CreateAgent();
        var handler = new CedarAuthorization(new() { Policies = "permit(principal, action, resource) when { context.input.allow && context.session.call_count == 1 };" });
        Assert.IsType<Deny>(await handler.BeforeToolCallAsync(Event(agent, "search", "{}")));
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent, "search", "{\"allow\":true}")));
        Assert.IsType<Deny>(await handler.BeforeToolCallAsync(Event(agent, "search", "{\"allow\":true}")));
    }

    [Fact]
    public async Task SharedHandlersKeepCountersPerAgentAndRestorePersistedCounts()
    {
        var handler = new CedarAuthorization(new() { Policies = "permit(principal, action, resource) when { context.session.call_count <= 1 };" });
        await using var first = CreateAgent();
        await using var second = CreateAgent();
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(first)));
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(second)));
        Assert.IsType<Deny>(await handler.BeforeToolCallAsync(Event(first)));
        var fresh = new CedarAuthorization(new() { Policies = "permit(principal, action, resource) when { context.session.call_count <= 1 };" });
        Assert.IsType<Deny>(await fresh.BeforeToolCallAsync(Event(first)));
        handler.ResetCallCounts(first);
        Assert.IsType<Proceed>(await fresh.BeforeToolCallAsync(Event(first)));
    }

    [Fact]
    public async Task CallerIdentityAndContextArePreservedAndBuiltInSessionFieldsWin()
    {
        await using var agent = CreateAgent();
        var state = new Dictionary<string, object?> { ["tenant"] = "viewer" };
        var handler = new CedarAuthorization(new()
        {
            Policies = """permit(principal == User::"a\"b", action, resource) when { context.session.role == "viewer" && context.session.call_count == 1 && context.session.hour_utc >= 0 && context.session.hour_utc < 24 };""",
            PrincipalResolver = received => { Assert.Same(state, received); return new("User", "a\"b"); },
            ContextEnricher = context => { Assert.Same(state, context.InvocationState); Assert.Equal("search", context.ToolName); return JsonSerializer.SerializeToElement(new { role = context.InvocationState["tenant"], call_count = 999, hour_utc = 999 }); }
        });
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent, state: state)));
        Assert.Single(state);
        var missing = new CedarAuthorization(new() { Policies = "permit(principal, action, resource);", PrincipalResolver = _ => null });
        Assert.IsType<Deny>(await missing.BeforeToolCallAsync(Event(agent, state: state)));
    }

    [Fact]
    public async Task NamespacedSchemaUsesNamespacedAnonymousIdentityAndDefaultResource()
    {
        await using var agent = CreateAgent();
        var handler = new CedarAuthorization(new()
        {
            Policies = "permit(principal == Agent::User::\"anonymous\", action == Agent::Action::\"search\", resource == Agent::Resource::\"default\");",
            Namespace = "Agent",
            Tools = [new("search", "Search", JsonSerializer.SerializeToElement(new { type = "object" }))]
        });
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent)));
    }

    [Fact]
    public async Task NativeEntityHierarchySurvivesDisposalOfCallerJson()
    {
        await using var agent = CreateAgent();
        CedarAuthorization handler;
        using (var document = JsonDocument.Parse("""[{"uid":{"type":"User","id":"alice"},"attrs":{},"parents":[{"type":"Group","id":"readers"}]},{"uid":{"type":"Group","id":"readers"},"attrs":{},"parents":[]}]"""))
            handler = new(new() { Policies = "permit(principal in Group::\"readers\", action, resource);", Principal = new("User", "alice"), Entities = document.RootElement });
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent)));
    }

    [Fact]
    public async Task ReloadValidatesCompleteCandidateBeforeReplacingPriorPolicy()
    {
        await using var agent = CreateAgent();
        var directory = Path.Combine(Path.GetTempPath(), "strands-cedar-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var policies = Path.Combine(directory, "policy.cedar");
        var entities = Path.Combine(directory, "entities.json");
        File.WriteAllText(policies, "permit(principal, action == Action::\"search\", resource);");
        File.WriteAllText(entities, "[]");
        var handler = new CedarAuthorization(new() { Policies = policies, EntitiesPath = entities });
        File.WriteAllText(policies, "invalid");
        Assert.Throws<ArgumentException>(handler.Reload);
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent)));
        File.WriteAllText(policies, "permit(principal, action == Action::\"write\", resource);");
        File.WriteAllText(entities, "[{\"uid\":{\"id\":\"missing-type\"}}]");
        Assert.Throws<ArgumentException>(handler.Reload);
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent)));
        File.WriteAllText(entities, "[]");
        handler.Reload();
        Assert.IsType<Deny>(await handler.BeforeToolCallAsync(Event(agent)));
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent, "write")));
    }

    [Fact]
    public async Task RequestedCancellationCannotResolveIdentityOrConsumeAllowance()
    {
        await using var agent = CreateAgent();
        var calls = 0;
        var handler = new CedarAuthorization(new() { Policies = "permit(principal, action, resource);", PrincipalResolver = _ => { calls++; return new("User", "alice"); } });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handler.BeforeToolCallAsync(Event(agent), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, calls);
        Assert.Null(agent.State.Get(CedarAuthorization.CallCountsStateKey));
    }

    [Fact]
    public async Task ConcurrentAuthorizationsCannotExceedAcceptedCallQuota()
    {
        await using var agent = CreateAgent();
        var handler = new CedarAuthorization(new() { Policies = "permit(principal, action, resource) when { context.session.call_count <= 3 };" });
        var actions = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () => await handler.BeforeToolCallAsync(Event(agent)))));
        Assert.Equal(3, actions.OfType<Proceed>().Count());
        Assert.Equal(17, actions.OfType<Deny>().Count());
        Assert.Equal(3, agent.State.Get<Dictionary<string, long>>(CedarAuthorization.CallCountsStateKey)!["search"]);
    }

    [Fact]
    public async Task AgentDenialPreventsSideEffectsAndRetainsCorrelatedResult()
    {
        var calls = 0;
        var model = new ToolCycleModel("write");
        await using var agent = new Agent(new AgentOptions
        {
            Model = model,
            Tools = [new FunctionTool(new("write", "Write", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { calls++; return new ValueTask<string>("written"); })],
            Interventions = [new CedarAuthorization(new() { Policies = "permit(principal, action == Action::\"search\", resource);" })]
        });
        var result = await agent.InvokeAsync("Try writing");
        Assert.Equal("finished", result.Message!.Text);
        Assert.Equal(0, calls);
        var denied = Assert.Single(model.Result!.Content.OfType<ToolResultBlock>());
        Assert.Equal("call", denied.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, denied.Status);
        Assert.Contains("Access denied by Cedar policy", denied.Content);
    }

    [Fact]
    public async Task SessionRestoreRetainsRealAuthorizationQuota()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-cedar-session-" + Guid.NewGuid().ToString("N"));
        var store = new FileSessionStore(directory);
        var executions = 0;
        Agent Build() => new(new AgentOptions
        {
            Model = new ToolCycleModel("search"),
            SessionStore = store,
            SessionId = "cedar",
            Tools = [new FunctionTool(new("search", "Search", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { executions++; return new ValueTask<string>("found"); })],
            Interventions = [new CedarAuthorization(new() { Policies = "permit(principal, action, resource) when { context.session.call_count <= 1 };" })]
        });
        await using (var first = Build()) await first.InvokeAsync("Search");
        await using (var restored = Build())
        {
            await restored.InvokeAsync("Search again");
            Assert.Equal(1, restored.State.Get<Dictionary<string, long>>(CedarAuthorization.CallCountsStateKey)!["search"]);
            Assert.Equal(ToolResultStatus.Error, restored.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Last().Status);
        }
        Assert.Equal(1, executions);
    }

    internal static Agent CreateAgent() => new(new AgentOptions { Model = new NoCalls() });
    internal static BeforeToolCallEvent Event(Agent agent, string name = "search", string input = "{}", IReadOnlyDictionary<string, object?>? state = null) => new(agent, new ToolUseBlock("call", name, JsonDocument.Parse(input).RootElement.Clone()), state ?? new Dictionary<string, object?>());

    private sealed class NoCalls : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("Cedar evaluation must not invoke a model.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class ToolCycleModel(string name) : IModel
    {
        private int _turns;
        public Message? Result { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var first = _turns++ == 0;
            yield return new ModelMessageStartEvent();
            if (first)
            {
                yield return new ModelContentBlockStartEvent(0, new("call", name, JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockStopEvent(0);
            }
            else
            {
                Result = request.Messages.Last();
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
                yield return new ModelContentBlockStopEvent(0);
            }
            yield return new ModelMessageStopEvent(first ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
}
