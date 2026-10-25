using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Mcp;
using Strands.Agents.Tests.Mcp;
using Strands.Harness.Mcp;

namespace Strands.Harness.Tests;

public sealed class McpAuthenticationHarnessTests
{
    [Fact]
    public async Task DeclarativeClientCredentialsResolveWithoutMappingIntoAnInteractiveGrant()
    {
        var configuration = McpServerConfiguration.Parse("""
            {"secured":{"url":"https://example.test/mcp","auth":{"clientId":"client","clientSecret":"secret","scopes":["read"]}}}
            """, continueOnError: false);

        var server = Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));

        Assert.Equal("streamable-http", server.Transport);
        Assert.Equal("https://example.test/mcp", server.Options.Url);
        Assert.Equal("client", server.Options.Auth!.ClientId);
        Assert.Equal("secret", server.Options.Auth.ClientSecret);
        Assert.Equal(new[] { "read" }, server.Options.Auth.Scopes);
    }

    [Fact]
    public async Task PythonCredentialNamesAndExplicitExpiryPolicyAreBoundWithoutChangingTheGrant()
    {
        var configuration = McpServerConfiguration.Parse("""
            {"secured":{"url":"https://example.test/mcp","auth":{"client_id":"client","client_secret":"secret"},"authOptions":{"refreshOnExpiry":true,"maximumTokenBytes":1234}}}
            """, continueOnError: false);

        var server = Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));

        Assert.Equal("client", server.Options.Auth!.ClientId);
        Assert.True(server.Options.AuthOptions!.RefreshOnExpiry);
        Assert.Equal(1234, server.Options.AuthOptions.MaximumTokenBytes);
    }

    [Fact]
    public async Task TypedCredentialsAndScopeListsAreCopiedBeforeCallerMutation()
    {
        var scopes = new[] { "original" };
        var configuration = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        {
            ["secured"] = new() { Url = "https://example.test/mcp", Auth = new() { ClientId = "client", ClientSecret = "secret", Scopes = scopes }, AuthOptions = new() { MaximumTokenBytes = 99 } },
        }, continueOnError: false);
        scopes[0] = "changed";

        var server = Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));

        Assert.Equal(new[] { "original" }, server.Options.Auth!.Scopes);
        Assert.Equal(99, server.Options.AuthOptions!.MaximumTokenBytes);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"clientId\":null,\"clientSecret\":\"secret\"}")]
    [InlineData("{\"clientId\":\"id\",\"clientSecret\":123}")]
    [InlineData("{\"clientId\":\"id\",\"clientSecret\":\"secret\",\"scopes\":[1]}")]
    [InlineData("{\"clientId\":\"id\",\"client_id\":\"other\",\"clientSecret\":\"secret\"}")]
    public async Task InvalidCredentialsFailBeforeTransportAllocationAndNeverIncludeSecretValues(string authentication)
    {
        var configuration = McpServerConfiguration.Parse("{\"secured\":{\"url\":\"https://example.test/mcp\",\"auth\":" + authentication + "}}", continueOnError: false);

        var failure = await Assert.ThrowsAnyAsync<ArgumentException>(() => configuration.ResolveAsync(new LocalFileSandbox(), default));

        Assert.DoesNotContain("\"secret\"", failure.Message);
    }

    [Theory]
    [InlineData("{\"command\":\"unused\",\"transport\":\"stdio\"}")]
    [InlineData("{\"url\":\"https://example.test/mcp\",\"transport\":\"sse\"}")]
    public async Task AuthCannotSilentlySelectAnotherTransportOrAuthenticationFlow(string entry)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(entry)!;
        fields["auth"] = JsonSerializer.SerializeToElement(new { clientId = "client", clientSecret = "do-not-echo" });
        var configuration = McpServerConfiguration.Parse(JsonSerializer.Serialize(new { secured = fields }), continueOnError: false);

        var failure = await Assert.ThrowsAsync<NotSupportedException>(() => configuration.ResolveAsync(new LocalFileSandbox(), default));

        Assert.DoesNotContain("do-not-echo", failure.Message);
    }

    [Theory]
    [InlineData("{\"maximumTokenBytes\":0}")]
    [InlineData("{\"maximumMetadataBytes\":-1}")]
    [InlineData("{\"maximumRequestBodyBytes\":2147483648}")]
    [InlineData("{\"refreshOnExpiry\":\"true\"}")]
    public async Task ExplicitAuthenticationPolicyIsValidatedBeforeConnection(string options)
    {
        var configuration = McpServerConfiguration.Parse("{\"secured\":{\"url\":\"https://example.test/mcp\",\"auth\":{\"clientId\":\"id\",\"clientSecret\":\"secret\"},\"authOptions\":" + options + "}}", continueOnError: false);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => configuration.ResolveAsync(new LocalFileSandbox(), default));
    }

    [Fact]
    public async Task RealHarnessConnectionAuthenticatesInitializationDiscoveryToolCallsAndOwnedSessionCleanup()
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult(RpcResponse(request));
        await using var agent = await Harness.CreateAsync(new()
        {
            Model = new UnusedModel(),
            BuiltinTools = [],
            BuiltinPlugins = [],
            BackgroundTasks = null,
            Context = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Session = new() { Enabled = false },
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            {
                ["secured"] = new() { Url = server.Resource.AbsoluteUri, Auth = new() { ClientId = "client", ClientSecret = "secret" } },
            }, continueOnError: false),
        });

        var result = await agent.InvokeToolAsync(new("call", "secured_echo", JsonSerializer.SerializeToElement(new { value = "Café 雪" })));

        Assert.Equal("Café 雪", result.Content);
        Assert.Single(server.Requests, value => value.Path == "/token");
        Assert.Contains(server.Requests, value => value.Method == "POST" && Encoding.UTF8.GetString(value.Body).Contains("tools/call"));
        await agent.DisposeAsync();
        Assert.Equal("Bearer access-1", Assert.Single(server.Requests, value => value.Method == "DELETE").Headers["Authorization"]);
    }

    [Fact]
    public async Task PluginFailureClosesTheAuthenticatedOwnedSessionAndKeepsTheOriginalFailure()
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult(RpcResponse(request));
        var failure = new InvalidOperationException("application plugin failed");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => Harness.CreateAsync(new()
        {
            Model = new UnusedModel(),
            BuiltinTools = [],
            BuiltinPlugins = [],
            BackgroundTasks = null,
            Plugins = [new FailedPlugin(failure)],
            Context = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Session = new() { Enabled = false },
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            {
                ["secured"] = new() { Url = server.Resource.AbsoluteUri, Auth = new() { ClientId = "client", ClientSecret = "secret" } },
            }, continueOnError: false),
        }));

        Assert.Same(failure, actual);
        Assert.Equal("Bearer access-1", Assert.Single(server.Requests, value => value.Method == "DELETE").Headers["Authorization"]);
    }

    [Fact]
    public async Task CancellationDuringTokenAcquisitionRetainsCallerIdentityAndDoesNotStartLaterServers()
    {
        await using var server = new OAuthLoopbackServer();
        await using var later = new OAuthLoopbackServer();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Respond = async request =>
        {
            if (request.Path != "/token") return null;
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            return null;
        };
        using var cancellation = new CancellationTokenSource();
        var pending = Harness.CreateAsync(new()
        {
            Model = new UnusedModel(),
            BuiltinTools = [],
            BuiltinPlugins = [],
            BackgroundTasks = null,
            Context = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Session = new() { Enabled = false },
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            {
                ["secured"] = new() { Url = server.Resource.AbsoluteUri, Auth = new() { ClientId = "client", ClientSecret = "secret" } },
                ["later"] = new() { Url = later.Resource.AbsoluteUri },
            }),
        }, cancellation.Token);
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));
            cancellation.Cancel();
            Assert.Same(pending, await Task.WhenAny(pending, Task.Delay(10_000)));
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending)).CancellationToken);
            Assert.Empty(later.Requests);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task EnvironmentExpansionAppliesToCredentialFieldsWithoutLeakingUnknownValuesToDiagnostics()
    {
        var variable = "STRANDS_AUTH_TEST_" + Guid.NewGuid().ToString("N");
        var sink = new DiagnosticSink();
        Trace.Listeners.Add(sink);
        Environment.SetEnvironmentVariable(variable, "expanded-secret");
        try
        {
            var json = "{\"secured\":{\"url\":\"https://example.test/mcp\",\"auth\":{\"clientId\":\"id\",\"clientSecret\":\"${env:" + variable + "}\",\"ignoredAuthField\":\"never-log-this-value\"}}}";
            var server = Assert.Single(await McpServerConfiguration.Parse(json, continueOnError: false).ResolveAsync(new LocalFileSandbox(), default));

            Assert.Equal("expanded-secret", server.Options.Auth!.ClientSecret);
            Assert.Contains(sink.Messages, message => message.Contains("ignoredAuthField"));
            Assert.DoesNotContain(sink.Messages, message => message.Contains("never-log-this-value") || message.Contains("expanded-secret"));
        }
        finally { Trace.Listeners.Remove(sink); sink.Dispose(); Environment.SetEnvironmentVariable(variable, null); }
    }

    private sealed class DiagnosticSink : TraceListener
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public override void Write(string? message) { if (message is not null) Messages.Enqueue(message); }
        public override void WriteLine(string? message) { if (message is not null) Messages.Enqueue(message); }
    }
    private sealed class FailedPlugin(Exception failure) : IPlugin
    {
        public string Name => "failing-auth-fixture";
        public IReadOnlyList<ITool> GetTools() => [];
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => throw failure;
    }

    private static OAuthResponse? RpcResponse(OAuthRequest request)
    {
        if (request.Path != "/mcp" || !request.Headers.TryGetValue("Authorization", out var authorization) || !authorization.StartsWith("Bearer access-", StringComparison.Ordinal)) return null;
        if (request.Method == "DELETE") return new(204, "");
        if (request.Method != "POST") return new(405, "");
        using var document = JsonDocument.Parse(request.Body);
        var rpc = document.RootElement;
        if (!rpc.TryGetProperty("id", out var id)) return new(202, "");
        if (rpc.GetProperty("method").GetString() == "server/discover") return OAuthLoopbackServer.Json(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } });
        object result = rpc.GetProperty("method").GetString() switch
        {
            "initialize" => new { protocolVersion = "2025-11-25", serverInfo = new { name = "oauth-fixture", version = "1" }, capabilities = new { tools = new { } } },
            "tools/list" => new { tools = new[] { new { name = "echo", description = "Echoes a value", inputSchema = new { type = "object" } } } },
            "tools/call" => new { content = new[] { new { type = "text", text = rpc.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString() } } },
            _ => throw new InvalidDataException("Unexpected fixture RPC method."),
        };
        return new(200, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), new Dictionary<string, string> { ["Mcp-Session-Id"] = "owned-oauth-session" });
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The auth fixture must not call a model.");
    }
}
