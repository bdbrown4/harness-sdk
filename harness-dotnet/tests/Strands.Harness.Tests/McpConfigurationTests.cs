using System.Text.Json;
using Strands.Agents.Mcp;
using Strands.Harness.Mcp;

namespace Strands.Harness.Tests;

public sealed class McpConfigurationTests
{
    [Fact]
    public async Task TaskConfigurationUsesSourceMillisecondDefaultsAndRequestTimeoutWinsOverTtlAlias()
    {
        var config = McpServerConfiguration.Parse("""
            {"tasks":{"url":"https://example.test/mcp","tasksConfig":{"pollTimeout":12000,"requestTimeout":3000,"ttl":7,"pollInterval":25}}}
            """, continueOnError: false);

        var server = Assert.Single(await config.ResolveAsync(new LocalFileSandbox(), default));

        Assert.NotNull(server.Options.TasksConfig);
        Assert.Equal(TimeSpan.FromSeconds(12), server.Options.TasksConfig.PollTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), server.Options.TasksConfig.RequestTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(25), server.Options.TasksConfig.PollInterval);
    }

    [Fact]
    public async Task TypedTaskConfigurationSurvivesTheOwnedServerMapSnapshot()
    {
        var config = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        {
            ["tasks"] = new() { Url = "https://example.test/mcp", TasksConfig = new() { PollTimeout = TimeSpan.FromSeconds(9) } }
        }, continueOnError: false);

        var server = Assert.Single(await config.ResolveAsync(new LocalFileSandbox(), default));

        Assert.NotNull(server.Options.TasksConfig);
        Assert.Equal(TimeSpan.FromSeconds(9), server.Options.TasksConfig.PollTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), server.Options.TasksConfig.RequestTimeout);
        Assert.Equal(McpTasksProtocolProfile.Negotiated, server.Options.TasksConfig.ProtocolProfile);
    }

    [Fact]
    public async Task JsonNegotiatedProfileRetainsLegacyOnlyRetentionAndSeparateRequestTimeout()
    {
        var configuration = McpServerConfiguration.Parse("""
            {"tasks":{"url":"https://example.test/mcp","tasksConfig":{"protocolProfile":"negotiated","legacyTtl":45000,"ttl":1234}}}
            """, continueOnError: false);

        var server = Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));

        Assert.Equal(McpTasksProtocolProfile.Negotiated, server.Options.TasksConfig!.ProtocolProfile);
        Assert.Equal(TimeSpan.FromSeconds(45), server.Options.TasksConfig.LegacyTimeToLive);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), server.Options.TasksConfig.RequestTimeout);
    }

    [Fact]
    public async Task TypedLegacyProfileAndExplicitWireTtlSurviveTheServerSnapshot()
    {
        var configuration = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        {
            ["legacy"] = new() { Url = "https://example.test/mcp", TasksConfig = new() { ProtocolProfile = McpTasksProtocolProfile.Legacy20251125, LegacyTimeToLive = TimeSpan.FromSeconds(45) } }
        }, continueOnError: false);

        var server = Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));

        Assert.Equal(McpTasksProtocolProfile.Legacy20251125, server.Options.TasksConfig!.ProtocolProfile);
        Assert.Equal(TimeSpan.FromSeconds(45), server.Options.TasksConfig.LegacyTimeToLive);
    }

    [Fact]
    public async Task LegacyWireTtlIsDistinctFromTheDeprecatedRequestTimeoutAlias()
    {
        var configuration = McpServerConfiguration.Parse("""
            {"legacy":{"url":"https://example.test/mcp","tasksConfig":{"protocolProfile":"legacy2025-11-25","legacyTtl":45000,"ttl":1234}}}
            """, continueOnError: false);

        var server = Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));

        Assert.Equal(McpTasksProtocolProfile.Legacy20251125, server.Options.TasksConfig!.ProtocolProfile);
        Assert.Equal(TimeSpan.FromSeconds(45), server.Options.TasksConfig.LegacyTimeToLive);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), server.Options.TasksConfig.RequestTimeout);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"pollTimeout\":0}")]
    [InlineData("{\"requestTimeout\":-1}")]
    [InlineData("{\"pollInterval\":\"100\"}")]
    [InlineData("{\"pollTimeout\":2147483648}")]
    [InlineData("{\"protocolProfile\":\"future\"}")]
    [InlineData("{\"protocolProfile\":\"finalized2026-07-28\",\"legacyTtl\":1000}")]
    [InlineData("{\"protocolProfile\":\"legacy2025-11-25\",\"legacyTtl\":0}")]
    [InlineData("{\"protocolProfile\":\"legacy2025-11-25\",\"legacyTtl\":1.5}")]
    [InlineData("{\"protocolProfile\":\"legacy2025-11-25\",\"legacyTtl\":922337203685478}")]
    public async Task MalformedTaskSettingsFailUnderStrictServerPolicy(string tasks)
    {
        var config = McpServerConfiguration.Parse("{\"tasks\":{\"url\":\"https://example.test/mcp\",\"tasksConfig\":" + tasks + "}}", continueOnError: false);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => config.ResolveAsync(new LocalFileSandbox(), default));
    }

    [Fact]
    public async Task ResolvesOwnedMapEnvironmentTransportPrefixAndAnchoredFilters()
    {
        var variable = "STRANDS_MCP_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "secret-value");
        try
        {
            var configuration = McpServerConfiguration.Parse("{\"docs.example\":{\"url\":\"https://example.test/mcp\",\"headers\":{\"Authorization\":\"Bearer ${env:" + variable + "}\"},\"toolFilters\":{\"allowed\":[\"echo\"],\"rejected\":[\"echo_private\"]}}}");
            var server = Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));
            Assert.Equal("streamable-http", server.Transport);
            Assert.Equal("docs_example", server.Options.Prefix);
            Assert.Equal("Bearer secret-value", server.Options.Headers["Authorization"]);
            Assert.True(server.Includes("echo_value"));
            Assert.False(server.Includes("prefix_echo"));
            Assert.False(server.Includes("echo_private"));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task DirectLoaderSkipsDisabledAndContinuedConfigErrorsButPortableExpansionFailsFirst()
    {
        var missing = "STRANDS_MCP_MISSING_" + Guid.NewGuid().ToString("N");
        var json = "{\"disabled\":{\"disabled\":true,\"command\":\"${" + missing + "}\"},\"bad\":{\"command\":\"${" + missing + "}\"},\"good\":{\"url\":\"https://example.test/mcp\"}}";
        var direct = await McpServerConfiguration.Parse(json).ResolveAsync(new LocalFileSandbox(), default);
        Assert.Equal("good", Assert.Single(direct).Name);
        using var parsed = JsonDocument.Parse(json);
        var disabledOnly = JsonSerializer.SerializeToElement(new { disabled = parsed.RootElement.GetProperty("disabled") });
        Assert.Empty(await McpServerConfiguration.Parse(disabledOnly.GetRawText(), continueOnError: false).ResolveAsync(new LocalFileSandbox(), default));
        var portable = McpServerConfiguration.FromPortable(disabledOnly, Directory.GetCurrentDirectory());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => portable.ResolveAsync(new LocalFileSandbox(), default));
        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"bad\":null}")]
    [InlineData("{\"good\":{\"url\":\"https://example.test\"},\"bad\":[]}")]
    [InlineData("{\"mcpServers\":[]}")]
    public async Task MalformedRootOrEntryIsFatalRegardlessOfContinuePolicy(string json)
        => await Assert.ThrowsAsync<ArgumentException>(() => McpServerConfiguration.Parse(json).ResolveAsync(new LocalFileSandbox(), default));

    [Fact]
    public async Task PortableWrappedFileResolvesWorkingDirectoryAgainstProjectRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands mcp config " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "servers.json"), "{\"mcpServers\":{\"worker\":{\"command\":\"worker\",\"cwd\":\"workspace\"}}}");
            var config = McpServerConfiguration.FromPortable(JsonSerializer.SerializeToElement("servers.json"), directory);
            var server = Assert.Single(await config.ResolveAsync(new LocalFileSandbox(), default));
            Assert.Equal(Path.Combine(directory, "workspace"), server.Options.WorkingDirectory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("{\"command\":\"worker\",\"url\":\"https://example.test\"}")]
    [InlineData("{\"url\":\"https://example.test\",\"transport\":\"auto\"}")]
    [InlineData("{\"url\":\"file:///tmp/server\"}")]
    [InlineData("{\"command\":\"worker\",\"toolFilters\":{\"allowed\":[\"[\"]}}")]
    public async Task StrictServerPolicySurfacesConfigurationFailures(string entry)
        => await Assert.ThrowsAnyAsync<ArgumentException>(() => McpServerConfiguration.Parse("{\"worker\":" + entry + "}", continueOnError: false).ResolveAsync(new LocalFileSandbox(), default));

    [Fact]
    public async Task ClientCredentialsResolveWithARedactedDescription()
    {
        var config = McpServerConfiguration.Parse("{\"private\":{\"url\":\"https://example.test/mcp\",\"auth\":{\"clientId\":\"id\",\"clientSecret\":\"never-log-this\"}}}", continueOnError: false);
        var server = Assert.Single(await config.ResolveAsync(new LocalFileSandbox(), default));
        Assert.NotNull(server.Options.Auth);
        Assert.Equal("id", server.Options.Auth.ClientId);
        Assert.DoesNotContain("never-log-this", server.Options.Auth.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedConfigurationSnapshotsListsAndMapsBeforeCallerMutation()
    {
        var arguments = new[] { "original" };
        var environment = new Dictionary<string, string> { ["VALUE"] = "original" };
        var source = new Dictionary<string, McpServerOptions> { ["worker"] = new() { Command = "worker", Arguments = arguments, Environment = environment, Prefix = "", ToolFilters = new() { Allowed = [] } } };
        var config = McpServerConfiguration.FromServers(source);
        arguments[0] = "changed"; environment["VALUE"] = "changed"; source.Clear();
        var resolved = Assert.Single(await config.ResolveAsync(new LocalFileSandbox(), default));
        Assert.Equal("original", Assert.Single(resolved.Options.Arguments));
        Assert.Equal("original", resolved.Options.Environment["VALUE"]);
        Assert.Equal("", resolved.Options.Prefix);
        Assert.False(resolved.Includes("echo"));
    }
}
