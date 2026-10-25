using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Interventions;
using Strands.Agents.Interventions.Cedar;
using static Strands.Cedar.Tests.CedarAuthorizationTests;

namespace Strands.Cedar.Tests;

public sealed class CedarSchemaAuthorizationTests
{
    private const string Policy = "permit(principal, action == Action::\"search\", resource);";
    private static string Schema(string inputType) => "entity User; entity Resource; action search appliesTo { principal: User, resource: Resource, context: { input: { query: " + inputType + " }, session: { hour_utc: Long, call_count: Long } } };";

    [Fact]
    public async Task CancellationDuringIdentityResolutionCannotBecomeMissingPrincipalDenial()
    {
        await using var agent = CreateAgent();
        using var cancellation = new CancellationTokenSource();
        var handler = new CedarAuthorization(new() { Policies = Policy, PrincipalResolver = _ => { cancellation.Cancel(); return null; } });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handler.BeforeToolCallAsync(Event(agent), cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Null(agent.State.Get(CedarAuthorization.CallCountsStateKey));
    }

    [Fact]
    public async Task SchemaAlsoParsesEntityAttributesUsingTheirDeclaredExtensionTypes()
    {
        await using var agent = CreateAgent();
        using var entities = JsonDocument.Parse("""[{"uid":{"type":"User","id":"alice"},"attrs":{"network":"10.2.3.4"},"parents":[]}]""");
        const string policy = "permit(principal, action == Action::\"search\", resource) when { principal.network.isInRange(ip(\"10.0.0.0/8\")) };";
        var schema = Schema("String").Replace("entity User;", "entity User = { network: ipaddr };");
        var typescript = new CedarAuthorization(new() { Policies = policy, Schema = schema, Principal = new("User", "alice"), Entities = entities.RootElement });
        var python = new CedarAuthorization(new() { Policies = policy, Schema = schema, Principal = new("User", "alice"), Entities = entities.RootElement, UseSchemaForAuthorization = true });

        Assert.IsType<Deny>(await typescript.BeforeToolCallAsync(Event(agent, input: "{\"query\":\"hello\"}")));
        Assert.IsType<Proceed>(await python.BeforeToolCallAsync(Event(agent, input: "{\"query\":\"hello\"}")));
    }

    [Fact]
    public void InvalidEngineExceptionPolicyCannotBePublished()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CedarAuthorization(new() { Policies = Policy, EngineExceptionPolicy = (CedarEngineExceptionPolicy)99 }));
    }

    [Fact]
    public async Task RequestedCancellationWinsOverAnActualNativeBridgeFaultInDenyMode()
    {
        await using var agent = CreateAgent();
        using var cancellation = new CancellationTokenSource();
        var handler = new CedarAuthorization(new() { Policies = Policy, EngineExceptionPolicy = CedarEngineExceptionPolicy.Deny }, request =>
        {
            try { return NativeCedar.Execute("unknown", request); }
            catch { cancellation.Cancel(); throw; }
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handler.BeforeToolCallAsync(Event(agent), cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Null(agent.State.Get(CedarAuthorization.CallCountsStateKey));
    }

    [Fact]
    public async Task PythonSchemaModeValidatesRequestTypesWhileTypeScriptDefaultUsesPolicyValidationOnly()
    {
        await using var agent = CreateAgent();
        var typescript = new CedarAuthorization(new() { Policies = Policy, Schema = Schema("String") });
        var python = new CedarAuthorization(new() { Policies = Policy, Schema = Schema("String"), UseSchemaForAuthorization = true });

        Assert.IsType<Proceed>(await typescript.BeforeToolCallAsync(Event(agent, input: "{\"query\":42}")));
        var denied = Assert.IsType<Deny>(await python.BeforeToolCallAsync(Event(agent, input: "{\"query\":42}")));
        Assert.Contains("Cedar evaluation failed", denied.Reason, StringComparison.Ordinal);
        Assert.IsType<Proceed>(await python.BeforeToolCallAsync(Event(agent, input: "{\"query\":\"hello\"}")));
    }

    [Fact]
    public async Task NativeSchemaParsesImplicitEntityReferencesWithoutAnEvaluatorSubstitute()
    {
        await using var agent = CreateAgent();
        const string policy = "permit(principal, action == Action::\"search\", resource) when { context.input.query == User::\"alice\" };";
        var typescript = new CedarAuthorization(new() { Policies = policy, Schema = Schema("User") });
        var python = new CedarAuthorization(new() { Policies = policy, Schema = Schema("User"), UseSchemaForAuthorization = true });
        var input = "{\"query\":{\"type\":\"User\",\"id\":\"alice\"}}";

        Assert.IsType<Deny>(await typescript.BeforeToolCallAsync(Event(agent, input: input)));
        Assert.IsType<Proceed>(await python.BeforeToolCallAsync(Event(agent, input: input)));
    }

    [Fact]
    public async Task NativeSchemaParsesImplicitIpExtensions()
    {
        await using var agent = CreateAgent();
        const string policy = "permit(principal, action == Action::\"search\", resource) when { context.input.query.isInRange(ip(\"10.0.0.0/8\")) };";
        var typescript = new CedarAuthorization(new() { Policies = policy, Schema = Schema("ipaddr") });
        var python = new CedarAuthorization(new() { Policies = policy, Schema = Schema("ipaddr"), UseSchemaForAuthorization = true });

        Assert.IsType<Deny>(await typescript.BeforeToolCallAsync(Event(agent, input: "{\"query\":\"10.2.3.4\"}")));
        Assert.IsType<Proceed>(await python.BeforeToolCallAsync(Event(agent, input: "{\"query\":\"10.2.3.4\"}")));
    }

    [Fact]
    public async Task GeneratedSchemaIsUsedForAuthorizationAndInvalidRequestsDoNotConsumeQuota()
    {
        await using var agent = CreateAgent();
        var handler = new CedarAuthorization(new()
        {
            Policies = Policy,
            UseSchemaForAuthorization = true,
            Tools = [new("search", "Search", JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } }))]
        });

        Assert.IsType<Deny>(await handler.BeforeToolCallAsync(Event(agent, input: "{\"query\":2}")));
        Assert.Null(agent.State.Get(CedarAuthorization.CallCountsStateKey));
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent, input: "{\"query\":\"hello\"}")));
        Assert.Equal(1, agent.State.Get<Dictionary<string, long>>(CedarAuthorization.CallCountsStateKey)!["search"]);
    }

    [Fact]
    public async Task SchemaReloadCommitsOnlyACompletelyValidatedReplacement()
    {
        await using var agent = CreateAgent();
        var directory = Path.Combine(Path.GetTempPath(), "strands-cedar-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "policy.cedarschema");
        File.WriteAllText(path, Schema("String"));
        var handler = new CedarAuthorization(new() { Policies = Policy, Schema = path, UseSchemaForAuthorization = true });
        File.WriteAllText(path, "invalid schema");
        Assert.Throws<ArgumentException>(handler.Reload);
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent, input: "{\"query\":\"hello\"}")));
        File.WriteAllText(path, Schema("Long"));

        handler.Reload();

        Assert.IsType<Deny>(await handler.BeforeToolCallAsync(Event(agent, input: "{\"query\":\"hello\"}")));
        Assert.IsType<Proceed>(await handler.BeforeToolCallAsync(Event(agent, input: "{\"query\":2}")));
    }

    [Fact]
    public async Task PythonEnginePolicyDeniesActualAbiFailuresEvenWithFailOpenCallbackPolicy()
    {
        await using var agent = CreateAgent();
        var handler = new CedarAuthorization(new() { Policies = Policy, EngineExceptionPolicy = CedarEngineExceptionPolicy.Deny, OnError = InterventionErrorPolicy.Proceed }, request => NativeCedar.Execute("unknown", request));

        var denied = Assert.IsType<Deny>(await handler.BeforeToolCallAsync(Event(agent)));

        Assert.Contains("Unsupported Cedar operation", denied.Reason, StringComparison.Ordinal);
        Assert.Null(agent.State.Get(CedarAuthorization.CallCountsStateKey));
    }

    [Fact]
    public async Task TypeScriptDefaultPreservesTheOriginalNativeBridgeException()
    {
        await using var agent = CreateAgent();
        Exception? original = null;
        var handler = new CedarAuthorization(new() { Policies = Policy }, request =>
        {
            try { return NativeCedar.Execute("unknown", request); }
            catch (Exception failure) { original = failure; throw; }
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.BeforeToolCallAsync(Event(agent)));

        Assert.Same(original, error);
    }

    [Fact]
    public async Task EnginePolicyDoesNotHideCallbackFailures()
    {
        await using var agent = CreateAgent();
        var failure = new InvalidOperationException("application callback");
        var handler = new CedarAuthorization(new() { Policies = Policy, EngineExceptionPolicy = CedarEngineExceptionPolicy.Deny, ContextEnricher = _ => throw failure });

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.BeforeToolCallAsync(Event(agent)));

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task RequestedCancellationCannotBecomeEngineDenialOrCommitQuota()
    {
        await using var agent = CreateAgent();
        using var cancellation = new CancellationTokenSource();
        var handler = new CedarAuthorization(new() { Policies = Policy, EngineExceptionPolicy = CedarEngineExceptionPolicy.Deny }, request =>
        {
            var result = NativeCedar.Execute("authorize", request);
            cancellation.Cancel();
            return result;
        });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handler.BeforeToolCallAsync(Event(agent), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(agent.State.Get(CedarAuthorization.CallCountsStateKey));
    }
}
