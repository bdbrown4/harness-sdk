using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryStatefulJournalTests
{
    [Fact]
    public async Task FreshAgentRestoresTheRealResponsesIdentityWithoutResendingArchivedHistory()
    {
        var directory = OwnedRoot("strands-indexed-stateful-");
        try
        {
            await using var server = new StatefulResponsesServer(StatefulResponsesServer.Response("first"), StatefulResponsesServer.Response("second"));
            using var client = new HttpClient();
            var model = new OpenAIResponsesModel(client, new("model", "key") { Stateful = true, Endpoint = server.Endpoint });
            var repository = new FileSessionRepository(directory);
            await using (var first = new Agent(new() { Id = "agent", Model = model, Plugins = [new RepositorySessionManager("session", repository)] }))
            {
                await first.InvokeAsync("first input");
                Assert.Empty(first.Messages);
                Assert.Equal(2, (await repository.ListMessagesAsync("session", "agent")).Count);
            }
            await using (var second = new Agent(new() { Id = "agent", Model = model, Plugins = [new RepositorySessionManager("session", new FileSessionRepository(directory))] }))
            {
                await second.InitializeAsync();
                Assert.Empty(second.Messages);
                Assert.Equal("first", second.ModelState.Get("openaiResponses")!.Value.GetProperty("responseId").GetString());
                await second.InvokeAsync("second input");
                Assert.Empty(second.Messages);
            }
            var requests = server.Requests;
            Assert.Equal(2, requests.Count);
            Assert.Equal("first", requests[1].GetProperty("previous_response_id").GetString());
            var input = Assert.Single(requests[1].GetProperty("input").EnumerateArray());
            Assert.Equal("second input", input.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal(new long[] { 0, 1, 2, 3 }, (await repository.ListMessagesAsync("session", "agent")).Select(row => row.MessageId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledToolOutputRestoresWithItsProviderReceiptWithoutRepeatingTheBody(bool file)
    {
        var directory = OwnedRoot("strands-indexed-pending-");
        try
        {
            ISessionRepository repository = file ? new FileSessionRepository(directory) : new StorageSessionRepository(new InMemoryStorage());
            await using var server = new StatefulResponsesServer(StatefulResponsesServer.Call("first"), StatefulResponsesServer.Response("second"));
            using var client = new HttpClient();
            using var caller = new CancellationTokenSource();
            var model = new OpenAIResponsesModel(client, new("model", "key") { Stateful = true, Endpoint = server.Endpoint });
            var bodies = 0;
            var tool = new FunctionTool(new("save", "Save", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) =>
            { bodies++; caller.Cancel(); return new ValueTask<string>("saved"); });
            await using (var first = new Agent(new() { Id = "agent", Model = model, Tools = [tool], Plugins = [new RepositorySessionManager("session", repository)] }))
            {
                Assert.Equal(StopReasons.Cancelled, (await first.InvokeAsync("save", cancellationToken: caller.Token)).StopReason);
                Assert.Equal(3, first.Messages.Count);
            }
            if (file) repository = new FileSessionRepository(directory);
            await using (var second = new Agent(new() { Id = "agent", Model = model, Tools = [tool], Plugins = [new RepositorySessionManager("session", repository)] }))
            {
                await second.InvokeAsync(null);
                Assert.Empty(second.Messages);
            }
            Assert.Equal(1, bodies);
            var request = server.Requests[1];
            Assert.Equal("first", request.GetProperty("previous_response_id").GetString());
            var input = Assert.Single(request.GetProperty("input").EnumerateArray());
            Assert.Equal("function_call_output", input.GetProperty("type").GetString());
            Assert.Equal("call", input.GetProperty("call_id").GetString());
            Assert.Contains("saved", input.GetProperty("output").GetString(), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static string OwnedRoot(string prefix)
    {
        var requested = Path.GetFullPath(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")));
        var actual = new FileSessionRepository(requested).RootPath;
        var comparable = actual.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + actual.Substring(8)
            : actual.StartsWith(@"\\?\", StringComparison.Ordinal) ? actual.Substring(4) : actual;
        if (!(Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Equals(requested, comparable))
            throw new InvalidOperationException("The fixture cleanup root differs from its unique selected directory.");
        return actual;
    }
}

internal sealed class StatefulResponsesServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _run;
    private readonly List<JsonElement> _requests = [];
    internal StatefulResponsesServer(params string[] responses)
    {
        _listener.Start();
        Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/responses");
        _run = ServeAsync(responses);
    }
    internal Uri Endpoint { get; }
    internal IReadOnlyList<JsonElement> Requests { get { lock (_requests) return _requests.ToArray(); } }
    private async Task ServeAsync(string[] responses)
    {
        try
        {
            foreach (var response in responses)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var header = new List<byte>();
                var single = new byte[1];
                while (header.Count < 65536)
                {
                    if (await stream.ReadAsync(single, 0, 1, _stopping.Token) == 0) throw new IOException("HTTP request ended in its headers.");
                    header.Add(single[0]);
                    if (header.Count >= 4 && header.Skip(header.Count - 4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var lengthLine = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None)
                    .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var body = new byte[int.Parse(lengthLine.Substring("Content-Length:".Length), System.Globalization.CultureInfo.InvariantCulture)];
                for (var offset = 0; offset < body.Length;)
                {
                    var count = await stream.ReadAsync(body, offset, body.Length - offset, _stopping.Token);
                    if (count == 0) throw new IOException("HTTP request ended in its body.");
                    offset += count;
                }
                using (var json = JsonDocument.Parse(body)) lock (_requests) _requests.Add(json.RootElement.Clone());
                var payload = Encoding.UTF8.GetBytes(response);
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, 0, headers.Length, _stopping.Token);
                await stream.WriteAsync(payload, 0, payload.Length, _stopping.Token);
            }
        }
        catch (Exception error) when (_stopping.IsCancellationRequested && error is SocketException or ObjectDisposedException or OperationCanceledException) { }
    }
    internal static string Response(string id) => Frame("response.created", new { response = new { id } }) + Frame("response.completed", new { response = new { id, status = "completed" } });
    internal static string Call(string id) => Frame("response.created", new { response = new { id } }) +
        Frame("response.output_item.added", new { output_index = 0, item = new { type = "function_call", id = "item", call_id = "call", name = "save", arguments = "" } }) +
        Frame("response.function_call_arguments.delta", new { output_index = 0, item_id = "item", delta = "{}" }) +
        Frame("response.output_item.done", new { output_index = 0, item = new { type = "function_call", id = "item", call_id = "call", name = "save", arguments = "{}" } }) +
        Frame("response.completed", new { response = new { id, status = "completed" } });
    private static string Frame(string type, object data)
    {
        var values = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        values.Add("type", JsonSerializer.SerializeToElement(type));
        return "event: " + type + "\ndata: " + JsonSerializer.Serialize(values) + "\n\n";
    }
    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel(); _listener.Stop();
        try { await _run; } finally { _stopping.Dispose(); }
    }
}
