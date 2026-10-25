using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;
using static Strands.Agents.Tests.Models.OpenAIResponsesStructuredOutputTests;

namespace Strands.Agents.Tests.Models;

public sealed class OpenAIResponsesStructuredOutputProtocolTests
{
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"n\":\"wrong-type\"}")]
    [InlineData("")]
    public async Task LaterInvalidTextDefeatsEarlierValidText(string later)
    {
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}", later));
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<StructuredOutputException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.True(transport.Body.Closed);
    }

    [Fact]
    public async Task SeparateJsonFragmentsAreNeverConcatenated()
    {
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":", "1}"));
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<StructuredOutputException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
    }

    [Fact]
    public async Task MultipleMessagesUseWireOrderAndValidateAllText()
    {
        var wire = """{"output":[{"type":"message","content":[{"type":"output_text","text":"{\"n\":3}"}]},{"type":"message","content":[{"type":"output_text","text":"{\"n\":4}"}]}]}""";
        using var transport = new ResponsesStructuredTransport(wire);
        using var client = new HttpClient(transport);
        var observed = new List<int>();
        var options = Options() with { Deserializer = value => { var n = value.GetProperty("n").GetInt32(); observed.Add(n); return new(n); } };
        Assert.Equal(3, (await new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), options)).N);
        Assert.Equal(new[] { 3, 4 }, observed);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("in_progress")]
    [InlineData("queued")]
    public async Task PythonParsingDoesNotAddACompletionStatusGate(string status)
    {
        var wire = JsonSerializer.Serialize(new { status, error = new { code = "example" }, incomplete_details = new { reason = "max_output_tokens" }, output = new[] { new { type = "message", role = "assistant", status = "incomplete", content = new[] { new { type = "output_text", text = "{\"n\":1}" } } } } });
        using var transport = new ResponsesStructuredTransport(wire);
        using var client = new HttpClient(transport);
        Assert.Equal(1, (await new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options())).N);
    }

    [Fact]
    public async Task RefusalAlongsideValidTextDoesNotRejectThatText()
    {
        using var transport = new ResponsesStructuredTransport("""{"output":[{"type":"message","content":[{"type":"refusal","refusal":"no"},{"type":"output_text","text":"{\"n\":1}"}]}]}""");
        using var client = new HttpClient(transport);
        Assert.Equal(1, (await new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options())).N);
    }

    [Theory]
    [InlineData("{\"output\":[]}")]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"no\"}]}]}")]
    [InlineData("{\"output\":[{\"type\":\"web_search_call\"}]}")]
    public async Task NoParsedTextIsAStructuredFailure(string wire)
    {
        using var transport = new ResponsesStructuredTransport(wire);
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<StructuredOutputException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
    }

    [Theory]
    [InlineData("web_search_call")]
    [InlineData("file_search_call")]
    [InlineData("reasoning")]
    [InlineData("mcp_call")]
    [InlineData("code_interpreter_call")]
    [InlineData("future_server_tool")]
    public async Task NativeServerAndUnknownItemsDoNotBecomeLocalToolBodies(string type)
    {
        var wire = JsonSerializer.Serialize(new { output = new object[] { new { type }, new { type = "message", content = new[] { new { type = "output_text", text = "{\"n\":9}" } } } } });
        using var transport = new ResponsesStructuredTransport(wire);
        using var client = new HttpClient(transport);
        Assert.Equal(9, (await new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options())).N);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("params")]
    [InlineData("null")]
    public async Task ExistingTextFormatConflictsBeforeCredentialsOrHttp(string configuration)
    {
        var credentials = 0;
        var options = new OpenAIResponsesModelOptions("model", _ => { credentials++; return new ValueTask<string>("key"); });
        options = configuration == "native" ? options with { TextFormat = JsonSerializer.SerializeToElement(new { type = "text" }) }
            : options with { Params = JsonSerializer.SerializeToElement(new { text = new { format = configuration == "null" ? null : new { type = "text" } } }) };
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}"));
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<ArgumentException>(() => new OpenAIResponsesModel(client, options).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.Equal(0, credentials);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ManagedProviderStateIsRejectedBeforeCredentialsOrHttp()
    {
        var credentials = 0;
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}"));
        using var client = new HttpClient(transport);
        var model = new OpenAIResponsesModel(client, new("model", _ => { credentials++; return new ValueTask<string>("key"); }) { Stateful = true });
        var state = new Dictionary<string, JsonElement> { ["openaiResponses"] = JsonSerializer.SerializeToElement(new { modelId = "model", responseId = "old" }) };
        await Assert.ThrowsAsync<ArgumentException>(() => model.InvokeStructuredAsync(new([Message.User("question")]) { ModelState = state }, Options()));
        Assert.Equal(0, credentials);
        Assert.Empty(transport.Requests);
        Assert.Equal("old", state["openaiResponses"].GetProperty("responseId").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveResponseBoundsAreRejected(int limit)
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAIResponsesModel(client, new("model", "key") { MaxResponseBytes = limit }));
    }

    [Fact]
    public async Task ResponseBytesAreBoundedAndClosed()
    {
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}"));
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<ModelException>(() => new OpenAIResponsesModel(client, new("model", "key") { MaxResponseBytes = 8 }).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.Contains("MaxResponseBytes", error.Message, StringComparison.Ordinal);
        Assert.True(transport.Body.Closed);
    }

    [Fact]
    public async Task StrictMatchingConfiguredFunctionArgumentsAreEagerlyParsed()
    {
        using var transport = new ResponsesStructuredTransport(FunctionWire("call", "{broken"));
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<StructuredOutputException>(() => new OpenAIResponsesModel(client, FunctionOptions(true)).InvokeStructuredAsync(new([Message.User("question")]), Options()));
    }

    [Theory]
    [InlineData(false, "call", "{broken")]
    [InlineData(true, "unmatched", "{broken")]
    [InlineData(true, "call", "{\"n\":\"not-the-tool-schema\"}")]
    public async Task FunctionArgumentsAreParsedOnlyWhenMatchingStrictAndNeverSchemaValidated(bool strict, string name, string arguments)
    {
        using var transport = new ResponsesStructuredTransport(FunctionWire(name, arguments));
        using var client = new HttpClient(transport);
        Assert.Equal(1, (await new OpenAIResponsesModel(client, FunctionOptions(strict)).InvokeStructuredAsync(new([Message.User("question")]), Options())).N);
    }

    [Fact]
    public async Task JsonChatFunctionWrapperCannotMasqueradeAsSdkPydanticTool()
    {
        var credentials = 0;
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}"));
        using var client = new HttpClient(transport);
        var options = new OpenAIResponsesModelOptions("model", _ => { credentials++; return new ValueTask<string>("key"); })
        { Params = JsonSerializer.SerializeToElement(new { tools = new[] { new { type = "function", function = new { name = "call" } } } }) };
        await Assert.ThrowsAsync<ArgumentException>(() => new OpenAIResponsesModel(client, options).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.Equal(0, credentials);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task NativeFalseIsAParsedValueRatherThanAnAbsenceMarker()
    {
        using var transport = new ResponsesStructuredTransport(Wire("false", "true"));
        using var client = new HttpClient(transport);
        var output = await new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), new StructuredOutputOptions<bool> { Schema = JsonSerializer.SerializeToElement(new { type = "boolean" }) });
        Assert.False(output);
    }

    private static OpenAIResponsesModelOptions FunctionOptions(bool strict) => new("model", "key")
    { Params = JsonSerializer.SerializeToElement(new { tools = new[] { new { type = "function", name = "call", strict, parameters = Options().Schema } } }) };
    private static string FunctionWire(string name, string arguments) => JsonSerializer.Serialize(new
    { output = new object[] { new { type = "message", content = new[] { new { type = "output_text", text = "{\"n\":1}" } } }, new { type = "function_call", name, arguments } } });
}

internal sealed class ResponsesStructuredTransport : HttpMessageHandler
{
    internal List<JsonElement> Requests { get; } = [];
    internal ResponsesStructuredBody Body { get; }
    internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    internal string ContentType { get; set; } = "application/json";
    internal Action? BeforeResponse { get; set; }
    internal Action<HttpRequestMessage>? RequestAccepted { get; set; }
    internal Func<CancellationToken, Task<HttpResponseMessage>>? Sending { get; set; }
    internal ResponsesStructuredTransport(string wire) => Body = new(Encoding.UTF8.GetBytes(wire));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = await request.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Requests.Add(json.RootElement.Clone());
        RequestAccepted?.Invoke(request);
        if (Sending is { } send) return await send(cancellationToken);
        BeforeResponse?.Invoke();
        var response = new HttpResponseMessage(Status) { Content = new StreamContent(Body) };
        response.Content.Headers.ContentType = new(ContentType);
        return response;
    }
}

internal sealed class ResponsesStructuredBody(byte[] bytes) : MemoryStream(bytes)
{
    internal bool Closed { get; private set; }
    internal Action? Closing { get; set; }
    internal Exception? CloseError { get; set; }
    internal Exception? ReadError { get; set; }
    internal bool ReadAttempted { get; private set; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ReadAttempted = true;
        return ReadError is { } error ? Task.FromException<int>(error) : base.ReadAsync(buffer, offset, count, cancellationToken);
    }
#if NET8_0_OR_GREATER
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadAttempted = true;
        return ReadError is { } error ? ValueTask.FromException<int>(error) : base.ReadAsync(buffer, cancellationToken);
    }
#endif
    protected override void Dispose(bool disposing)
    {
        if (!Closed)
        {
            Closed = true;
            base.Dispose(disposing);
            Closing?.Invoke();
            if (CloseError is { } error) throw error;
        }
    }
}
