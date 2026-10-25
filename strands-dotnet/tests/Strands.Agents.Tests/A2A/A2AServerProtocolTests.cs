using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AServerProtocolTests
{
    [Fact]
    public async Task PushConfigurationIsRejectedBeforeExecutionRatherThanSilentlyIgnored()
    {
        var created = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { created++; return new(new Agent(new() { Model = new A2AServerTests.EchoModel() })); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var parameters = JsonSerializer.SerializeToElement(new { message = A2AServerTests.Send("hello").GetProperty("message"), configuration = new { pushNotificationConfig = new { url = "http://callback/" } } });

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => client.CallAsync("message/send", parameters));

        Assert.Equal(-32003, error.Code); Assert.Equal(0, created);
    }
    [Theory]
    [InlineData("{", -32700)]
    [InlineData("[]", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"missing\",\"params\":{}}", -32601)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tasks/get\",\"params\":{\"id\":\"missing\"}}", -32001)]
    public async Task InvalidRequestsRetainJsonRpcErrorClassification(string body, int code)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("must not create") });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://local/rpc") { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        using var response = await server.HandleAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.GetProperty("error").GetProperty("code").GetInt32());
    }
    [Theory]
    [InlineData("message/send", "\"1\"")]
    [InlineData("message/send", "null")]
    [InlineData("message/send", "{}")]
    [InlineData("tasks/get", "\"1\"")]
    [InlineData("tasks/get", "null")]
    [InlineData("tasks/get", "{}")]
    public async Task WrongHistoryLengthKindsAreInvalidParametersBeforeAnyExecution(string method, string value)
    {
        var created = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { created++; return new(new Agent(new() { Model = new A2AServerTests.EchoModel() })); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var task = await client.CallAsync("message/send", A2AServerTests.Send("hello"));
        var parameters = method == "tasks/get"
            ? "{\"id\":" + JsonSerializer.Serialize(task.GetProperty("id").GetString()) + ",\"historyLength\":" + value + "}"
            : "{\"message\":" + A2AServerTests.Send("again").GetProperty("message").GetRawText() + ",\"configuration\":{\"historyLength\":" + value + "}}";

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => client.CallAsync(method, JsonSerializer.Deserialize<JsonElement>(parameters)));

        Assert.Equal(-32602, error.Code); Assert.Equal(1, created);
    }
    [Fact]
    public async Task BackendInitializationFailureIsACorrelatedSanitizedInternalError()
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("backend secret") });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => client.CallAsync("message/send", A2AServerTests.Send("hello")));

        Assert.Equal(-32603, error.Code); Assert.DoesNotContain("backend secret", error.Message);
    }
    [Fact]
    public async Task IncompatibleAcceptedOutputModesAreRejectedBeforeConstructingANativeAgent()
    {
        var created = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { created++; return new(new Agent(new() { Model = new A2AServerTests.EchoModel() })); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var parameters = JsonSerializer.SerializeToElement(new { message = A2AServerTests.Send("hello").GetProperty("message"), configuration = new { acceptedOutputModes = new[] { "image/png" } } });

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => client.CallAsync("message/send", parameters));

        Assert.Equal(-32005, error.Code); Assert.Equal(0, created);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[42]")]
    [InlineData("[\"not a media type\"]")]
    public async Task MalformedOutputModesRemainInvalidParameters(string modes)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("must not execute") });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var parameters = JsonSerializer.Deserialize<JsonElement>("{\"message\":" + A2AServerTests.Send("hello").GetProperty("message").GetRawText() + ",\"configuration\":{\"acceptedOutputModes\":" + modes + "}}");
        Assert.Equal(-32602, (await Assert.ThrowsAsync<A2ARpcException>(() => client.CallAsync("message/send", parameters))).Code);
    }
    [Fact]
    public async Task ExecutionCannotPublishAnArtifactOutsideItsNegotiatedOutputModes()
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new A2AServerTests.EchoModel() })) });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var parameters = JsonSerializer.SerializeToElement(new { message = A2AServerTests.Send("hello").GetProperty("message"), configuration = new { acceptedOutputModes = new[] { "application/json" } } });

        var task = await client.CallAsync("message/send", parameters);

        Assert.Equal("failed", task.GetProperty("status").GetProperty("state").GetString()); Assert.Empty(task.GetProperty("artifacts").EnumerateArray());
    }
    [Fact]
    public async Task DiscoverySnapshotsMimeListsAndUsesAnExplicitNameAsTheDefaultDescription()
    {
        var input = new[] { "text/plain", "application/json" }; var output = new[] { "text/plain" };
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("must not execute") });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { Description = "", InputModes = input, OutputModes = output });
        input[0] = "image/png"; output[0] = "image/jpeg";
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));

        var card = await client.GetAgentCardAsync();

        Assert.Equal("Hosted", card.GetProperty("description").GetString()); Assert.Equal("text/plain", card.GetProperty("defaultInputModes")[0].GetString()); Assert.Equal("text/plain", card.GetProperty("defaultOutputModes")[0].GetString());
    }
}
