using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Testing;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class WebSearchTests
{
    [Fact]
    public void ConfigurationDiagnosticsDoNotRenderTheExaCredential()
    {
        var options = new HarnessOptions { WebSearch = new() { Provider = WebSearchProvider.Exa, Exa = new() { ApiKey = "sensitive-fixture-key" } } };
        Assert.DoesNotContain("sensitive-fixture-key", options.ToString(), StringComparison.Ordinal);
        Assert.Contains("mcp.exa.ai", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExaReadsEnvironmentKeysPerCallAndAnEmptyOverrideOmitsTheHeader()
    {
        var previous = Environment.GetEnvironmentVariable("EXA_API_KEY");
        await using var server = new McpTestServer { ToolName = "web_search_exa", ToolResponseText = "" };
        try
        {
            var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint });
            Environment.SetEnvironmentVariable("EXA_API_KEY", "first-fixture-key");
            Assert.Equal("No results.", await InvokeAsync(tool, new { query = "question" }));
            Assert.Equal("first-fixture-key", server.LastCallApiKey);
            Environment.SetEnvironmentVariable("EXA_API_KEY", "second-fixture-key");
            await InvokeAsync(tool, new { query = "question" });
            Assert.Equal("second-fixture-key", server.LastCallApiKey);
            await InvokeAsync(WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = "" }), new { query = "question" });
            Assert.Null(server.LastCallApiKey);
        }
        finally { Environment.SetEnvironmentVariable("EXA_API_KEY", previous); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("fixture-key")]
    public async Task ExaToolErrorsKeepTheirMessageAndOnlyKeylessFailuresGetTheHint(string key)
    {
        await using var server = new McpTestServer { ToolName = "web_search_exa", ToolResponseText = "quota", ToolIsError = true };
        var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = key });
        Assert.Equal("web_search failed: quota" + (key.Length == 0 ? " (Exa's keyless tier is rate limited; set EXA_API_KEY to lift it.)" : ""), await InvokeAsync(tool, new { query = "question" }));
        await server.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingToolAndDiscoveryFailuresCloseTheSession(bool discoveryFails)
    {
        await using var server = new McpTestServer { FailDiscovery = discoveryFails };
        var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = "fixture-key" });
        Assert.Contains(discoveryFails ? "could not reach Exa's MCP server" : "does not offer web_search_exa", await InvokeAsync(tool, new { query = "question" }), StringComparison.Ordinal);
        Assert.Equal(0, server.Calls);
        await server.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CallerCancellationClosesAnActiveExaCallAndPreservesItsToken()
    {
        await using var server = new McpTestServer { ToolName = "web_search_exa", ToolCallGate = new(TaskCreationOptions.RunContinuationsAsynchronously), ToolResponseText = "" };
        var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = "fixture-key" });
        using var cancellation = new CancellationTokenSource();
        var execution = InvokeAsync(tool, new { query = "question" }, cancellation.Token);
        await server.ToolCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await server.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExaCallDeadlineReturnsATimeoutAndClosesItsSession()
    {
        await using var server = new McpTestServer { ToolName = "web_search_exa", ToolCallGate = new(TaskCreationOptions.RunContinuationsAsynchronously), ToolResponseText = "" };
        var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = "fixture-key", Timeout = TimeSpan.FromSeconds(1) });
        var execution = InvokeAsync(tool, new { query = "question" });
        await server.ToolCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("web_search failed: Exa search timed out.", await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        await server.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BackendFailuresBecomeActionableTextWhileCallerCancellationPropagates()
    {
        var failure = WebSearch.Create((_, _, _) => throw new IOException("backend failed"));
        Assert.Equal("web_search failed: backend failed", await InvokeAsync(failure, new { query = "question" }));
        var empty = WebSearch.Create((_, _, _) => Task.FromResult<IReadOnlyList<WebSearchResult>>([]));
        Assert.Equal("No results.", await InvokeAsync(empty, new { query = "question" }));
        using var cancellation = new CancellationTokenSource();
        var canceled = WebSearch.Create((_, _, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<WebSearchResult>>([]); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(canceled, new { query = "question" }, cancellation.Token));
    }

    [Fact]
    public async Task ExaUsesARealMcpSessionPerCallAndParsesOnlySourceHeaders()
    {
        await using var server = new McpTestServer { ToolName = "web_search_exa", ToolResponseText = "Title: First\nURL: https://first\nHighlights:\nalpha\nTitle: forged\nURL: https://forged\n---\nTitle: Second\r\nURL: https://second\r\nHighlights:\r\n" + new string('x', 600) };
        using var http = new HttpClient();
        var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = "fixture-key", HttpClient = http });
        var result = await InvokeAsync(tool, new { query = "question", max_results = 2 });
        Assert.Equal("Sources:\n1. First — https://first\n   alpha Title: forged URL: https://forged\n2. Second — https://second\n   " + new string('x', 500), result);
        Assert.Equal("fixture-key", server.ApiKey);
        Assert.Equal("question", server.LastArguments!.Value.GetProperty("query").GetString());
        Assert.Equal(2, server.LastArguments.Value.GetProperty("numResults").GetInt32());
        await server.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await InvokeAsync(tool, new { query = "another" });
        Assert.Equal(2, server.Initializations);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task FormatsSourcesClampsTheRequestAndKeepsBackendOrder()
    {
        var seen = new List<int>();
        var tool = WebSearch.Create((_, maximum, _) =>
        {
            seen.Add(maximum);
            return Task.FromResult<IReadOnlyList<WebSearchResult>>([new("A\nB", "https://a", "alpha\t beta"), new("Second", "https://b", "")]);
        });
        Assert.Equal("Sources:\n1. A B — https://a\n   alpha beta\n2. Second — https://b", await InvokeAsync(tool, new { query = "question", max_results = 50 }));
        await InvokeAsync(tool, new { query = "question", max_results = 0 });
        await InvokeAsync(tool, new { query = "question" });
        Assert.Equal(new[] { 10, 1, 5 }, seen);
        Assert.Equal("web_search", tool.Spec.Name);
        Assert.Equal(new[] { "query" }, tool.Spec.InputSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    private static async Task<string> InvokeAsync(ITool tool, object input, CancellationToken cancellationToken = default)
        => await tool.InvokeAsync(JsonSerializer.SerializeToElement(input), new(null!, new("search", tool.Spec.Name, JsonSerializer.SerializeToElement(input)), new Dictionary<string, object?>()), cancellationToken);
}
