using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class HandoffToUserExplicitContentTests
{
    [Fact]
    public void OldConstructorsDeconstructionAndDefaultEqualityRemainAvailable()
    {
        var left = new ToolResultBlock("old", "text");
        var right = new ToolResultBlock("old", "text", ToolResultStatus.Success);
        var (id, text, status) = left;
        Assert.Equal("old", id);
        Assert.Equal("text", text);
        Assert.Equal(ToolResultStatus.Success, status);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(left, left with { HasExplicitContent = false });
        Assert.NotEqual(left, left with { HasExplicitContent = true });
        Assert.NotNull(typeof(ToolResultBlock).GetConstructor([typeof(string), typeof(string), typeof(ToolResultStatus)]));
        Assert.NotNull(typeof(ToolResultBlock).GetConstructor([typeof(string), typeof(IReadOnlyList<ContentBlock>), typeof(ToolResultStatus), typeof(JsonElement?)]));
        Assert.False(new ToolResultBlock("old", Array.Empty<ContentBlock>()).HasExplicitContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldMissingFlagPayloadAndDefaultResultKeepTheOldJsonFieldSet(bool web)
    {
        var options = new JsonSerializerOptions(web ? JsonSerializerDefaults.Web : JsonSerializerDefaults.General);
        var old = web
            ? "{\"toolUseId\":\"old\",\"content\":\"text\",\"status\":\"success\",\"contentBlocks\":[],\"structuredContent\":null}"
            : "{\"ToolUseId\":\"old\",\"Content\":\"text\",\"Status\":\"success\",\"ContentBlocks\":[],\"StructuredContent\":null}";
        var restored = JsonSerializer.Deserialize<ToolResultBlock>(old, options)!;
        Assert.False(restored.HasExplicitContent);
        Assert.Equal("text", restored.Content);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(restored, options));
        Assert.False(json.RootElement.TryGetProperty(web ? "hasExplicitContent" : "HasExplicitContent", out _));
        Assert.Equal(5, json.RootElement.EnumerateObject().Count());
        Assert.True(JsonElement.DeepEquals(Json(old), json.RootElement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectResultJsonRetainsTheExplicitEmptyListIntent(bool web)
    {
        var options = new JsonSerializerOptions(web ? JsonSerializerDefaults.Web : JsonSerializerDefaults.General);
        var json = JsonSerializer.Serialize(Explicit(), options);
        Assert.True(Json(json).GetProperty(web ? "hasExplicitContent" : "HasExplicitContent").GetBoolean());
        AssertExplicit(JsonSerializer.Deserialize<ToolResultBlock>(json, options)!);
    }

    [Fact]
    public void PolymorphicMessageAndRecordCopiesRetainTheFlagAndTrackingIdentity()
    {
        var original = ResultMessage(Explicit());
        var copied = new Message(original.Role, original.Content, original.TrackingId, original.Metadata);
        var json = JsonSerializer.Serialize(copied, JsonOptions);
        var restored = JsonSerializer.Deserialize<Message>(json, JsonOptions)!;
        Assert.Equal("result-tracking", restored.TrackingId);
        AssertExplicit(Assert.IsType<ToolResultBlock>(Assert.Single(restored.Content)));
        var record = new SessionMessageRecord(original, 9, copied);
        AssertExplicit(Assert.IsType<ToolResultBlock>(Assert.Single(record.Message.Content)));
        AssertExplicit(Assert.IsType<ToolResultBlock>(Assert.Single(record.ToMessage().Content)));
        AssertExplicit(Explicit() with { ToolUseId = "current" });
    }

    [Fact]
    public async Task OrdinaryToolFactoryCorrelationCopyKeepsExplicitEmptyContent()
    {
        var supplied = Explicit() with { ToolUseId = "foreign" };
        Func<ToolResultBlock> callback = () => supplied;
        var tool = Assert.IsAssignableFrom<IRichTool>(ToolFactory.Create(callback, new() { Name = "return_result", Description = "Return result." }));
        var result = await tool.InvokeResultAsync(Json("{}"), new(null!, new("current", "return_result", Json("{}")), new Dictionary<string, object?>()));
        AssertExplicit(result);
        Assert.Equal("foreign", supplied.ToolUseId);
    }

    [Fact]
    public async Task IndexedRepositoryStoresActualFlagBytesAndFreshCodecRetainsOriginalAndRedaction()
    {
        var storage = new InMemoryStorage();
        var original = ResultMessage(Explicit());
        var redaction = new Message(Role.User, [Explicit()], "redaction-tracking");
        await new StorageSessionRepository(storage).CreateMessageAsync("session", "agent", new(original, 7, redaction));
        var key = Assert.Single(await storage.ListAsync());
        var bytes = (await storage.ReadAsync(key))!;
        Assert.Contains("\"hasExplicitContent\":true", Encoding.UTF8.GetString(bytes));
        var restored = (await new StorageSessionRepository(storage).ReadMessageAsync("session", "agent", 7))!;
        Assert.Equal("result-tracking", restored.Message.TrackingId);
        Assert.Equal("redaction-tracking", restored.ToMessage().TrackingId);
        AssertExplicit(Assert.IsType<ToolResultBlock>(Assert.Single(restored.Message.Content)));
        var effective = Assert.IsType<ToolResultBlock>(Assert.Single(restored.ToMessage().Content));
        AssertExplicit(effective);
        Assert.Equal(JsonValueKind.Array, (await CaptureAnthropicAsync(effective)).ValueKind);
    }

    [Fact]
    public async Task FreshWholeSnapshotFileLoadRetainsEmptyIntentInActualProviderFormatting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "handoff-explicit-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new FileSessionStore(directory).SaveAsync("session", new(1, History(Explicit())));
            var stored = await new FileSessionStore(directory).LoadAsync("session");
            var result = Assert.IsType<ToolResultBlock>(Assert.Single(stored!.Messages[2].Content));
            AssertExplicit(result);
            Assert.Equal("result-tracking", stored.Messages[2].TrackingId);
            Assert.Empty((await CaptureAnthropicAsync(result)).EnumerateArray());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SerializedAgentCheckpointRestoresTheFlagBeforeAnyModelCall()
    {
        await using var source = new Agent(new() { Model = new NeverCalledModel(), Messages = History(Explicit()) });
        var captured = await source.TakeSnapshotAsync(new() { Include = SnapshotFields.Messages });
        var json = JsonSerializer.Serialize(captured, JsonOptions);
        var serialized = JsonSerializer.Deserialize<AgentSnapshot>(json, JsonOptions)!;
        await using var target = new Agent(new() { Model = new NeverCalledModel() });
        await target.LoadSnapshotAsync(serialized);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(target.Messages[2].Content));
        AssertExplicit(result);
        Assert.Empty((await CaptureAnthropicAsync(result)).EnumerateArray());
    }

    [Fact]
    public async Task FreshImmutableCatalogCodecRetainsTheFlag()
    {
        var storage = new InMemoryStorage();
        string handle;
        {
            var manager = new SnapshotSessionManager(storage, "session", SnapshotSessionSaveStrategy.Trigger);
            await using var source = new Agent(new() { Id = "stable-agent", Model = new NeverCalledModel(), Messages = History(Explicit()), Plugins = [manager] });
            await source.InitializeAsync();
            handle = Assert.IsType<string>(await manager.SaveSnapshotAsync(source, false));
        }
        var freshManager = new SnapshotSessionManager(storage, "session", SnapshotSessionSaveStrategy.Trigger);
        await using var target = new Agent(new() { Id = "stable-agent", Model = new NeverCalledModel(), Plugins = [freshManager] });
        Assert.True(await freshManager.RestoreSnapshotAsync(target, handle));
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(target.Messages[2].Content));
        AssertExplicit(result);
        Assert.Empty((await CaptureAnthropicAsync(result)).EnumerateArray());
    }

    [Theory]
    [InlineData("legacy-text")]
    [InlineData("legacy-rich-empty")]
    [InlineData("explicit-empty")]
    public async Task AnthropicHonorsExplicitEmptyWhilePreservingBothLegacyConstructors(string kind)
    {
        var result = kind switch
        {
            "legacy-text" => new ToolResultBlock("current", "legacy"),
            "legacy-rich-empty" => new ToolResultBlock("current", Array.Empty<ContentBlock>()),
            _ => Explicit()
        };
        var projected = await CaptureAnthropicAsync(result);
        Assert.Equal(kind == "explicit-empty" ? JsonValueKind.Array : JsonValueKind.String, projected.ValueKind);
        if (kind == "explicit-empty") Assert.Empty(projected.EnumerateArray());
        else Assert.Equal(kind == "legacy-text" ? "legacy" : "", projected.GetString());
    }

    [Fact]
    public async Task OnlyPythonEnvelopeSelectsExplicitContentAndRetainsCurrentIdAndStatus()
    {
        var context = new ToolContext(null!, new("current", "handoff_to_user", Json("{}")), new Dictionary<string, object?>()) { InterruptHandler = (_, _, _) => Json("{\"status\":\"error\",\"content\":[],\"toolUseId\":\"foreign\"}") };
        var result = await HandoffToUser.Create(new() { Behavior = HandoffToUserBehavior.Python }).InvokeResultAsync(Json("{\"message\":\"Q\"}"), context);
        Assert.True(result.HasExplicitContent);
        Assert.Equal("current", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Empty(result.ContentBlocks);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static ToolResultBlock Explicit() => new("current", "legacy fallback must be ignored", ToolResultStatus.Error) { HasExplicitContent = true };
    private static Message ResultMessage(ToolResultBlock result) => new(Role.User, [result], "result-tracking");
    private static Message[] History(ToolResultBlock result) => [Message.User("go"), new(Role.Assistant, [new ToolUseBlock("current", "handoff_to_user", Json("{}"))]), ResultMessage(result)];
    private static void AssertExplicit(ToolResultBlock result) { Assert.True(result.HasExplicitContent); Assert.Equal("current", result.ToolUseId); Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Empty(result.ContentBlocks); Assert.Equal("legacy fallback must be ignored", result.Content); }
    private static JsonElement Json(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
    private static async Task<JsonElement> CaptureAnthropicAsync(ToolResultBlock result)
    {
        using var handler = new CaptureOnlyHandler();
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-fixture", "fixed-dummy-key") { Endpoint = new Uri("https://capture.invalid/messages") });
        await Assert.ThrowsAnyAsync<ModelException>(async () => { await foreach (var _ in model.StreamAsync(new(History(result)))) { } });
        Assert.Equal(1, handler.Calls);
        return handler.Body!.Value.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("content").Clone();
    }
    private sealed class CaptureOnlyHandler : HttpMessageHandler
    {
        internal int Calls;
        internal JsonElement? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Body = Json(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"controlled request capture\"}}", Encoding.UTF8, "application/json") };
        }
    }
    private sealed class NeverCalledModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Serialization and restore must not execute the model.");
    }
}
