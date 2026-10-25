using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class OpenAIResponsesStructuredOutputTests
{
    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("\"\"")]
    [InlineData("[]")]
    public async Task PythonFalseyTextNormalizesBeforeAddingTheTypedFormat(string json)
    {
        await using var server = new ResponsesStructuredLoopback(Wire("{\"n\":5}"));
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler);
        using var parameters = JsonDocument.Parse("{\"text\":" + json + "}");
        var model = new OpenAIResponsesModel(client, new("model", "key") { Endpoint = server.Endpoint, Params = parameters.RootElement });

        var output = await model.InvokeStructuredAsync(new([Message.User("question")]), Options());

        Assert.Equal(5, output.N);
        var request = await server.Request;
        Assert.Equal(JsonValueKind.Object, request.Body.GetProperty("text").ValueKind);
        Assert.Equal("json_schema", request.Body.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.True(request.Body.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("\"configured\"")]
    [InlineData("[1]")]
    public async Task PythonTruthyNonobjectTextStillRejectsBeforeCredentialsOrHttp(string json)
    {
        var credentials = 0;
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":5}"));
        using var client = new HttpClient(transport);
        using var parameters = JsonDocument.Parse("{\"text\":" + json + "}");
        var model = new OpenAIResponsesModel(client, new("model", _ => { credentials++; return new ValueTask<string>("key"); }) { Params = parameters.RootElement });

        await Assert.ThrowsAsync<ArgumentException>(() => model.InvokeStructuredAsync(new([Message.User("question")]), Options()));

        Assert.Equal(0, credentials);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ParamsNullTextNormalizesBeforeAddingTheTypedFormat()
    {
        await using var server = new ResponsesStructuredLoopback(Wire("{\"n\":5}"));
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key")
        {
            Endpoint = server.Endpoint,
            Params = JsonSerializer.SerializeToElement(new { text = (object?)null }),
        });

        var output = await model.InvokeStructuredAsync(new([Message.User("question")]), Options());

        Assert.Equal(5, output.N);
        var request = await server.Request;
        Assert.Equal(JsonValueKind.Object, request.Body.GetProperty("text").ValueKind);
        Assert.Equal("json_schema", request.Body.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.True(request.Body.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task NativeJsonRequestPreservesConfiguredServerToolsAndTextSiblings()
    {
        await using var server = new ResponsesStructuredLoopback(Wire("{\"n\":42}"));
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "secret")
        {
            Endpoint = server.Endpoint,
            Stateful = true,
            Params = JsonSerializer.SerializeToElement(new { text = new { verbosity = "low" }, tools = new[] { new { type = "web_search_preview" } }, max_output_tokens = 123 }),
        });

        var answer = await model.InvokeStructuredAsync(new([Message.User("question")], SystemPrompt: "system"), Options());

        Assert.Equal(42, answer.N);
        var request = await server.Request;
        Assert.Contains("Authorization: Bearer secret", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Accept: application/json", request.Headers, StringComparison.OrdinalIgnoreCase);
        var root = request.Body;
        Assert.False(root.TryGetProperty("stream", out _));
        Assert.True(root.GetProperty("store").GetBoolean());
        Assert.Equal("system", root.GetProperty("instructions").GetString());
        Assert.False(root.TryGetProperty("previous_response_id", out _));
        Assert.Equal(123, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("web_search_preview", root.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("low", root.GetProperty("text").GetProperty("verbosity").GetString());
        Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        Assert.Equal("object", root.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("type").GetString());
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task MultipleIndependentTextPartsReturnTheFirstAfterAllAreValidated()
    {
        await using var server = new ResponsesStructuredLoopback(Wire("{\"n\":1}", "{\"n\":2}"));
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler);
        var parsed = new List<int>();
        var options = Options() with { Deserializer = value => { var n = value.GetProperty("n").GetInt32(); parsed.Add(n); return new(n); } };

        var answer = await new OpenAIResponsesModel(client, new("model", "key") { Endpoint = server.Endpoint }).InvokeStructuredAsync(new([Message.User("question")]), options);

        Assert.Equal(1, answer.N);
        Assert.Equal(new[] { 1, 2 }, parsed);
        await server.Request;
    }

    internal static StructuredOutputOptions<Answer> Options() => new()
    {
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { n = new { type = "integer" } }, required = new[] { "n" }, additionalProperties = false }),
    };

    internal static string Wire(params string[] texts) => JsonSerializer.Serialize(new
    {
        id = "response-one",
        status = "completed",
        output = new[] { new { type = "message", role = "assistant", status = "completed", content = texts.Select(text => new { type = "output_text", text }).ToArray() } },
    });

    public sealed record Answer(int N);
}

internal sealed class ResponsesStructuredLoopback : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serving;
    private readonly TaskCompletionSource<(string Headers, JsonElement Body)> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _headersSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpClient? _client;
    internal Uri Endpoint { get; }
    internal Task<(string Headers, JsonElement Body)> Request => _request.Task;
    internal Task ResponseHeadersSent => _headersSent.Task;

    internal ResponsesStructuredLoopback(string response, Task? releaseBody = null)
    {
        _listener.Start();
        Endpoint = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/responses");
        _serving = ServeAsync(response, releaseBody);
    }

    private async Task ServeAsync(string response, Task? releaseBody)
    {
        try
        {
            using var connection = await _listener.AcceptTcpClientAsync();
            _client = connection;
            using var stream = connection.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 65536)
            {
                if (await stream.ReadAsync(one, 0, 1, _lifetime.Token) == 0) throw new IOException("Request ended before headers.");
                header.Add(one[0]);
                if (header.Count >= 4 && header.Skip(header.Count - 4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            }
            var headers = Encoding.ASCII.GetString(header.ToArray());
            var lengthLine = headers.Split(new[] { "\r\n" }, StringSplitOptions.None).Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = int.Parse(lengthLine.Substring("Content-Length:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            if (length > 1024 * 1024) throw new IOException("Unexpected request size.");
            var bytes = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(bytes, offset, length - offset, _lifetime.Token);
                if (read == 0) throw new IOException("Request ended before its body.");
                offset += read;
            }
            using var json = JsonDocument.Parse(bytes);
            _request.TrySetResult((headers, json.RootElement.Clone()));
            var body = Encoding.UTF8.GetBytes(response);
            var prefix = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(prefix, 0, prefix.Length, _lifetime.Token);
            _headersSent.TrySetResult(true);
            if (releaseBody is not null)
            {
                await Task.WhenAny(releaseBody, Task.Delay(Timeout.Infinite, _lifetime.Token));
                _lifetime.Token.ThrowIfCancellationRequested();
                await releaseBody;
            }
            await stream.WriteAsync(body, 0, body.Length, _lifetime.Token);
        }
        catch (Exception error)
        {
            _request.TrySetException(error);
            _headersSent.TrySetException(error);
            if (!_lifetime.IsCancellationRequested) throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        _client?.Dispose();
        try { await _serving; }
        finally { _lifetime.Dispose(); }
    }
}
