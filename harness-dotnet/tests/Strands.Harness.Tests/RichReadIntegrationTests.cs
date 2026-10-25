using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Strands.Agents.Files;
using Strands.Agents.Models;

namespace Strands.Harness.Tests;

public sealed class RichReadIntegrationTests
{
    private static readonly byte[] Bytes = [0, 0xef, 0xbb, 0xbf, 255, 42];

    [Fact]
    public async Task ConsumerReadReplacementKeepsItsIdentityAndStandaloneMediaPolicy()
    {
        var files = new Files();
        var replacement = FileTools.CreateRead(files);
        var models = new List<BedrockModel>();
        var inspected = new List<(Agent Agent, ToolResultBlock Result, ITool Tool)>();
        try
        {
            await using var parent = await Harness.CreateAsync(RichReadTests.Minimal(new NoModel()) with
            {
                Model = null,
                ModelId = "bedrock/global.anthropic.claude-opus-5",
                BuiltinTools = ["subagent"],
                Tools = [replacement],
                Sandbox = files,
                Plugins = [new InspectReads(inspected)],
                ModelFactory = _ =>
                {
                    var model = new BedrockModel(new() { ModelId = models.Count == 0 ? "global.anthropic.claude-opus-5" : "openai.gpt-oss-120b" });
                    models.Add(model); return model;
                },
            });
            await parent.InvokeToolAsync(new("delegate", "subagent", JsonSerializer.SerializeToElement(new { task = "read", tools = new[] { "read" } })));
            var child = Assert.Single(inspected);
            Assert.Same(replacement, child.Tool);
            Assert.Equal(ToolResultStatus.Success, child.Result.Status);
            Assert.IsType<ImageBlock>(Assert.Single(child.Result.ContentBlocks));
        }
        finally { foreach (var model in models) model.Dispose(); }
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public async Task StandardChildRebindsOnlyReadPolicyAndRetainsSelectedFilesystem(bool parentMedia, bool childMedia, bool? explicitMedia)
    {
        var files = new Files();
        var models = new List<BedrockModel>();
        var inspected = new List<(Agent Agent, ToolResultBlock Result, ITool Tool)>();
        var plugin = new InspectReads(inspected);
        var options = RichReadTests.Minimal(new NoModel()) with
        {
            Model = null,
            ModelId = "bedrock/global.anthropic.claude-opus-5",
            BuiltinTools = ["read", "subagent"],
            Read = new() { Media = explicitMedia },
            Sandbox = files,
            Plugins = [plugin],
            ModelFactory = _ =>
            {
                var media = models.Count == 0 ? parentMedia : childMedia;
                var model = new BedrockModel(new() { ModelId = media ? "global.anthropic.claude-opus-5" : "openai.gpt-oss-120b" });
                models.Add(model); return model;
            },
        };
        try
        {
            await using var parent = await Harness.CreateAsync(options);
            var input = JsonSerializer.SerializeToElement(new { task = "inspect selected read", tools = new[] { "read" } });
            await parent.InvokeToolAsync(new("delegate", "subagent", input));
            var observed = Assert.Single(inspected);
            Assert.NotSame(parent, observed.Agent);
            Assert.NotSame(parent.Tools.Single(tool => tool.Spec.Name == "read"), observed.Tool);
            Assert.Same(models[1], observed.Agent.Model);
            Assert.Equal(ToolResultStatus.Success, observed.Result.Status);
            Assert.Equal(explicitMedia ?? childMedia, observed.Result.ContentBlocks.Count != 0);
            Assert.Equal(1, files.Reads);
            Assert.DoesNotContain(observed.Agent.Tools, tool => tool.Spec.Name is "write" or "edit");
            Assert.Empty(parent.Messages);
        }
        finally { foreach (var model in models) model.Dispose(); }
    }

    [Fact]
    public async Task RichReadCommitsExactBytesThroughRealSessionAndSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-rich-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new FileSessionStore(directory);
            var model = new ReadingModel();
            await using var agent = await Harness.CreateAsync(RichReadTests.Minimal(model) with
            { Sandbox = new Files(), AgentOptions = new() { Model = model, SessionStore = session, SessionId = "rich" } });
            Assert.Equal("viewed", (await agent.InvokeAsync("read both")).ToString());
            AssertRich(agent.Messages);
            var snapshot = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.Messages });
            await using var restored = new Agent(new() { Model = new NoModel() });
            await restored.LoadSnapshotAsync(snapshot);
            AssertRich(restored.Messages);
            await using var resumed = new Agent(new() { Model = new NoModel(), SessionStore = new FileSessionStore(directory), SessionId = "rich" });
            await resumed.InitializeAsync();
            AssertRich(resumed.Messages);
            Assert.Equal(2, model.Turns);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("responses")]
    [InlineData("anthropic")]
    [InlineData("google")]
    public async Task ReadMediaReachesActualProviderProjection(string provider)
    {
        var files = new Files();
        var read = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(files));
        var input = JsonSerializer.SerializeToElement(new { path = "/selected/pixel.png" });
        var call = new ToolUseBlock("image-call", "read", input);
        var rich = await read.InvokeResultAsync(input, new(null!, call, new Dictionary<string, object?>()));
        using var handler = new Capture();
        using var http = new HttpClient(handler);
        IModel model = provider switch
        {
            "openai" => new OpenAIModel(http, new("model", "key")),
            "responses" => new OpenAIResponsesModel(http, new("model", "key")),
            "anthropic" => new AnthropicModel(http, new("model", "key")),
            _ => new GoogleModel(http, new("model", "key")),
        };
        await Assert.ThrowsAsync<ModelException>(async () =>
        { await foreach (var _ in model.StreamAsync(new([Message.User("read"), new(Role.Assistant, [call]), new(Role.User, [rich])]))) { } });
        Assert.NotNull(handler.Body);
        Assert.Contains(JsonStrings(JsonSerializer.Deserialize<JsonElement>(handler.Body)), value => value.Contains(Convert.ToBase64String(Bytes)));
        Assert.Contains("image/png", handler.Body);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("responses")]
    [InlineData("anthropic")]
    [InlineData("google")]
    public async Task PdfReadReachesActualProviderProjection(string provider)
    {
        var read = Assert.IsAssignableFrom<IRichTool>(FileTools.CreateRead(new Files()));
        var input = JsonSerializer.SerializeToElement(new { path = "/selected/Report_(copy).pdf" });
        var call = new ToolUseBlock("document-call", "read", input);
        var rich = await read.InvokeResultAsync(input, new(null!, call, new Dictionary<string, object?>()));
        using var handler = new Capture();
        using var http = new HttpClient(handler);
        IModel model = provider switch
        {
            "openai" => new OpenAIModel(http, new("model", "key")),
            "responses" => new OpenAIResponsesModel(http, new("model", "key")),
            "anthropic" => new AnthropicModel(http, new("model", "key")),
            _ => new GoogleModel(http, new("model", "key")),
        };
        await Assert.ThrowsAsync<ModelException>(async () =>
        { await foreach (var _ in model.StreamAsync(new([Message.User("read"), new(Role.Assistant, [call]), new(Role.User, [rich])]))) { } });
        Assert.NotNull(handler.Body);
        Assert.Contains(JsonStrings(JsonSerializer.Deserialize<JsonElement>(handler.Body)), value => value.Contains(Convert.ToBase64String(Bytes)));
        Assert.Contains("application/pdf", handler.Body);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"yes\"")]
    public void InvalidPortableMediaFailsBeforeModelAllocation(string media)
    {
        Assert.Throws<ArgumentException>(() => HarnessAgentConfig.Parse("{\"builtinTools\":{\"read\":{\"media\":" + media + "}}}"));
    }

    private static IEnumerable<string> JsonStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) yield return element.GetString()!;
        else if (element.ValueKind == JsonValueKind.Object)
        { foreach (var property in element.EnumerateObject()) foreach (var value in JsonStrings(property.Value)) yield return value; }
        else if (element.ValueKind == JsonValueKind.Array)
        { foreach (var item in element.EnumerateArray()) foreach (var value in JsonStrings(item)) yield return value; }
    }

    private static void AssertRich(IReadOnlyList<Message> messages)
    {
        var results = messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal("image-call", results[0].ToolUseId);
        Assert.Equal(Bytes, Convert.FromBase64String(Assert.IsType<ImageBlock>(Assert.Single(results[0].ContentBlocks)).Data));
        var document = Assert.IsType<DocumentBlock>(Assert.Single(results[1].ContentBlocks));
        Assert.Equal("document-call", results[1].ToolUseId);
        Assert.Equal("Report (copy) pdf", document.Name);
        Assert.Equal(Bytes, Convert.FromBase64String(document.Data));
    }
    private sealed class ReadingModel : IModel
    {
        internal int Turns;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = Turns++ == 0;
            yield return new ModelMessageStartEvent();
            if (first)
            {
                yield return new ModelContentBlockStartEvent(0, new("image-call", "read", JsonSerializer.SerializeToElement(new { path = "/selected/pixel.png" })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelContentBlockStartEvent(1, new("document-call", "read", JsonSerializer.SerializeToElement(new { path = "/selected/Report_(copy).pdf" })));
                yield return new ModelContentBlockStopEvent(1);
            }
            else
            {
                AssertRich(request.Messages);
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "viewed");
                yield return new ModelContentBlockStopEvent(0);
            }
            yield return new ModelMessageStopEvent(first ? StopReasons.ToolUse : StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
    private sealed class InspectReads(List<(Agent Agent, ToolResultBlock Result, ITool Tool)> observed) : IPlugin
    {
        public string Name => "inspect-read-policy";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            agent.Hooks.Register<BeforeInvocationEvent>(async (value, token) =>
            {
                var tool = agent.Tools.Single(tool => tool.Spec.Name == "read");
                var input = JsonSerializer.SerializeToElement(new { path = "/selected/pixel.png" });
                var result = await Assert.IsAssignableFrom<IRichTool>(tool).InvokeResultAsync(input, new(agent, new("read", "read", input), value.InvocationState), token);
                observed.Add((agent, result, tool));
                value.CancelReason = "inspected without a provider request";
            });
            return default;
        }
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No network request is permitted.");
    }
    private sealed class Files : ISandbox, IFileSystem
    {
        internal int Reads;
        public ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) { Reads++; return new((byte[])Bytes.Clone()); }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Binary files must not use text.");
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => new(Array.Empty<FileEntry>());
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Capture : HttpMessageHandler
    {
        internal string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"projection captured\"}}", Encoding.UTF8, "application/json") };
        }
    }
}
