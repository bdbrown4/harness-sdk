using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Mcp;
using Strands.Harness.Testing;
using Xunit;

namespace Strands.Harness.Tests;

public sealed class McpProviderBindingLifetimeTests
{
    [Fact]
    public async Task CallerDeclarationsPrecedeTheSameNamedDeclarativeServerAndKeepFirstNameAuthority()
    {
        await using var application = new McpTestServer(); await using var configured = new McpTestServer(); using var http = new HttpClient();
        var provider = McpProviderHarnessTests.Provider(application, http, "docs", "application"); var model = new McpProviderAuthorityTests.GatedModel();
        Agent? parent = null; Task<ToolResultBlock>? pending = null;
        try
        {
            parent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
            { BuiltinTools = ["subagent"], AgentOptions = new() { Model = model, ToolProviders = [provider] }, McpServers = McpProviderHarnessTests.Servers(configured) });
            Assert.Equal(new[] { "application_echo", "docs_echo", "subagent" }, parent.Tools.Select(tool => tool.Spec.Name));
            pending = McpProviderAuthorityTests.Delegate(parent, new { task = "child", tools = Array.Empty<string>(), mcp_servers = new[] { "docs" } }).AsTask();
            await McpProviderAuthorityTests.Bounded(model.Entered.Task); Assert.Equal("application_echo", Assert.Single(model.Requests[0].Tools!).Name);
            await parent.DisposeAsync(); Assert.Equal(1, configured.Deletions); Assert.Equal(0, application.Deletions);
            model.Release.TrySetResult(true); Assert.Equal(ToolResultStatus.Success, (await pending).Status); Assert.Equal(1, application.Deletions);
        }
        finally { model.Release.TrySetResult(true); if (pending is not null) await Record.ExceptionAsync(async () => await pending); if (parent is not null) await parent.DisposeAsync(); }
    }

    [Fact]
    public async Task ASelectedProviderCannotRegainToolsDeniedByTheInheritedRegistrationPredicate()
    {
        await using var server = new McpTestServer(); using var http = new HttpClient(); var model = new McpProviderAuthorityTests.GatedModel();
        var provider = McpProviderHarnessTests.Provider(server, http, "docs", "docs"); Agent? parent = null; Task<ToolResultBlock>? pending = null;
        try
        {
            parent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
            { BuiltinTools = ["subagent"], AgentOptions = new() { Model = model, ToolProviders = [provider], ToolFilter = tool => tool.Spec.Name != "docs_echo" } });
            Assert.Equal("subagent", Assert.Single(parent.Tools).Spec.Name); Assert.Equal(new[] { "docs" }, McpProviderAuthorityTests.ServerNames(parent));
            pending = McpProviderAuthorityTests.Delegate(parent, new { task = "child", tools = new[] { "docs_echo" }, mcp_servers = new[] { "docs" } }).AsTask();
            await McpProviderAuthorityTests.Bounded(model.Entered.Task); Assert.Empty(model.Requests[0].Tools!);
            await parent.DisposeAsync(); Assert.Equal(0, server.Deletions); // selected declaration still has a child consumer, even though its tools were denied
            model.Release.TrySetResult(true); Assert.Equal(ToolResultStatus.Success, (await pending).Status); Assert.Equal(1, server.Deletions);
        }
        finally { model.Release.TrySetResult(true); if (pending is not null) await Record.ExceptionAsync(async () => await pending); if (parent is not null) await parent.DisposeAsync(); }
    }

    [Fact]
    public async Task CancellingAChildKeepsTheParentsSharedConnectionAndOriginalCallerToken()
    {
        await using var server = new McpTestServer(); using var http = new HttpClient(); var provider = McpProviderHarnessTests.Provider(server, http, "docs", "docs");
        var model = new CancellationModel(); using var cancellation = new CancellationTokenSource();
        await using var parent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
        { BuiltinTools = ["subagent"], AgentOptions = new() { Model = model, ToolProviders = [provider] } });
        var pending = McpProviderAuthorityTests.Delegate(parent, new { task = "child", tools = Array.Empty<string>(), mcp_servers = new[] { "docs" } }, cancellation.Token).AsTask();
        await McpProviderAuthorityTests.Bounded(model.Entered.Task); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, server.Deletions);
        var result = await parent.InvokeToolAsync(new("parent-call", "docs_echo", JsonSerializer.SerializeToElement(new { value = "parent still connected" })));
        Assert.Equal("parent still connected", result.Content); Assert.Equal(1, server.Initializations);
    }

    [Fact]
    public async Task DefaultContinueKeepsAnEmptyNamedProviderWhileAnExplicitStrictServerStopsInitialization()
    {
        await using var failing = new McpTestServer { FailInitialization = true }; await using var later = new McpTestServer();
        await using (var parent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
        {
            BuiltinTools = ["subagent"],
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            { ["failed"] = new() { Url = failing.Endpoint.AbsoluteUri } })
        }))
        {
            Assert.Equal("subagent", Assert.Single(parent.Tools).Spec.Name); Assert.Equal(new[] { "failed" }, McpProviderAuthorityTests.ServerNames(parent));
        }
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Harness.CreateAsync(McpProviderHarnessTests.Options() with
        {
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            { ["strict"] = new() { Url = failing.Endpoint.AbsoluteUri, ContinueOnError = false }, ["later"] = new() { Url = later.Endpoint.AbsoluteUri } })
        }));
        Assert.Contains("fixture initialization failure", error.Message, StringComparison.Ordinal); Assert.Equal(0, later.Initializations);
    }

    [Fact]
    public async Task CancellationDuringDeclarativeConnectionPreservesCallerIdentityAndDoesNotStartLaterServers()
    {
        await using var first = new McpTestServer { InitializationGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var later = new McpTestServer(); using var cancellation = new CancellationTokenSource();
        var pending = Harness.CreateAsync(McpProviderHarnessTests.Options() with
        {
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            { ["first"] = new() { Url = first.Endpoint.AbsoluteUri }, ["later"] = new() { Url = later.Endpoint.AbsoluteUri } }, continueOnError: false)
        }, cancellation.Token);
        await McpProviderAuthorityTests.Bounded(first.InitializationStarted.Task); cancellation.Cancel();
        try { var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(0, later.Initializations); }
        finally { first.InitializationGate.TrySetResult(true); }
    }

    [Fact]
    public async Task RepeatedEmptyGenericDeclarationsKeepTheirIdentityAndPerAgentConsumerContext()
    {
        var generic = new EmptyProvider(); var model = new McpProviderAuthorityTests.GatedModel(); Agent? parent = null; Task<ToolResultBlock>? pending = null;
        try
        {
            parent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
            { BuiltinTools = ["subagent"], AgentOptions = new() { Model = model, ToolProviders = [generic, generic] } });
            pending = McpProviderAuthorityTests.Delegate(parent, new { task = "child", tools = Array.Empty<string>(), mcp_servers = Array.Empty<string>() }).AsTask();
            await McpProviderAuthorityTests.Bounded(model.Entered.Task);
            Assert.Equal(4, generic.Added.Count); Assert.Same(generic.Added[0], generic.Added[1]); Assert.Same(generic.Added[2], generic.Added[3]);
            Assert.NotEqual(generic.Added[0].ConsumerId, generic.Added[2].ConsumerId); Assert.Empty(model.Requests[0].Tools!);
            model.Release.TrySetResult(true); Assert.Equal(ToolResultStatus.Success, (await pending).Status); Assert.Equal(2, generic.Removed.Count);
            await parent.DisposeAsync(); Assert.Equal(4, generic.Removed.Count);
        }
        finally { model.Release.TrySetResult(true); if (pending is not null) await Record.ExceptionAsync(async () => await pending); if (parent is not null) await parent.DisposeAsync(); }
    }

    private sealed class EmptyProvider : IToolProvider
    {
        internal List<ToolProviderContext> Added { get; } = [];
        internal List<ToolProviderContext> Removed { get; } = [];
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Added.Add(context); return default; }
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => new(Array.Empty<ITool>());
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Removed.Add(context); return default; }
    }

    private sealed class CancellationModel : IModel
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, cancellationToken); yield break; }
    }
}
