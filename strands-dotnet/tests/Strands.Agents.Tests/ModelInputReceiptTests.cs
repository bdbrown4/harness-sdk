using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class ModelInputReceiptTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task VersionTwoReceiptRoundTripsWithoutInventingAssistant()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync("sibling", Legacy());
        var sibling = fixture.Bytes();
        var expected = Received();

        await fixture.Store.SaveAsync("target", expected);
        var loaded = Assert.IsType<SessionSnapshot>(await fixture.Store.LoadAsync("target"));

        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal(0, loaded.ModelHistoryCount);
        var user = Assert.Single(loaded.Messages);
        Assert.Equal(Role.User, user.Role);
        Assert.Equal("u", user.TrackingId);
        Assert.Equal(expected.ModelInputReceipt, loaded.ModelInputReceipt);
        Assert.Equal("state-1", loaded.ModelState!["responseId"].GetString());
        Assert.DoesNotContain(loaded.Messages, message => message.TrackingId == "discarded");
        fixture.AssertRetainedBytes(sibling);
        var serialized = JsonSerializer.SerializeToElement(loaded, Web);
        Assert.Equal(1, serialized.GetProperty("modelInputReceipt").GetProperty("consumedMessageCount").GetInt32());
    }

    [Fact]
    public async Task VersionOneCannotCarryReceipt()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync("target", Legacy());
        var before = fixture.Bytes();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.SaveAsync("target", Received() with { SchemaVersion = 1 }));

        fixture.AssertAllBytes(before);
        Assert.Equal(1, (await fixture.Store.LoadAsync("target"))!.SchemaVersion);
    }

    [Fact]
    public async Task VersionOneWithoutReceiptKeepsItsOriginalWireAndCursor()
    {
        using var fixture = new StoreFixture();
        var expected = Legacy();
        await fixture.Store.SaveAsync("target", expected);
        var path = Assert.Single(Directory.GetFiles(fixture.Root, "*.json"));
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, Web)));
        using var json = JsonDocument.Parse(bytes);
        Assert.False(json.RootElement.TryGetProperty("modelInputReceipt", out _));

        var loaded = Assert.IsType<SessionSnapshot>(await fixture.Store.LoadAsync("target"));
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(2, loaded.ModelHistoryCount);
        Assert.Null(loaded.ModelInputReceipt);
        Assert.Equal(new[] { "u-old", "a-old" }, loaded.Messages.Select(message => message.TrackingId));
        Assert.Equal("state-old", loaded.ModelState!["responseId"].GetString());
    }

    [Fact]
    public async Task ReceiptNeverMakesAUserEndedLegacyCursorValid()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync("target", Legacy());
        var before = fixture.Bytes();
        var forged = Received() with
        {
            Messages = [User("u"), new Message(Role.Assistant, [new TextBlock("answer")], "a"), User("u-next")],
            ModelHistoryCount = 1,
            ModelInputReceipt = new(3, "u-next", "discarded")
        };

        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.SaveAsync("target", forged));

        fixture.AssertAllBytes(before);
    }

    [Theory]
    [InlineData("negative-count")]
    [InlineData("nonextending-count")]
    [InlineData("past-end-count")]
    [InlineData("missing-legacy-cursor")]
    [InlineData("assistant-ended-receipt")]
    [InlineData("wrong-boundary-id")]
    [InlineData("null-boundary-id")]
    [InlineData("empty-response-id")]
    [InlineData("retained-response-id")]
    [InlineData("missing-provider-state")]
    [InlineData("empty-provider-state")]
    [InlineData("missing-receipt")]
    public async Task MalformedReceiptCannotReplaceExistingSession(string kind)
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync("target", Legacy());
        var before = fixture.Bytes();
        var valid = Received();
        var receipt = valid.ModelInputReceipt!;
        var invalid = kind switch
        {
            "negative-count" => valid with { ModelInputReceipt = receipt with { ConsumedMessageCount = -1 } },
            "nonextending-count" => valid with { ModelInputReceipt = receipt with { ConsumedMessageCount = 0 } },
            "past-end-count" => valid with { ModelInputReceipt = receipt with { ConsumedMessageCount = 2 } },
            "missing-legacy-cursor" => valid with { ModelHistoryCount = null },
            "assistant-ended-receipt" => valid with
            {
                Messages = [User("u"), new Message(Role.Assistant, [new TextBlock("answer")], "a")],
                ModelInputReceipt = receipt with { ConsumedMessageCount = 2, LastConsumedTrackingId = "a" }
            },
            "wrong-boundary-id" => valid with { ModelInputReceipt = receipt with { LastConsumedTrackingId = "other" } },
            "null-boundary-id" => valid with { ModelInputReceipt = receipt with { LastConsumedTrackingId = null! } },
            "empty-response-id" => valid with { ModelInputReceipt = receipt with { DiscardedResponseTrackingId = "" } },
            "retained-response-id" => valid with { ModelInputReceipt = receipt with { DiscardedResponseTrackingId = "u" } },
            "missing-provider-state" => valid with { ModelState = null },
            "empty-provider-state" => valid with { ModelState = new Dictionary<string, JsonElement>() },
            "missing-receipt" => valid with { ModelInputReceipt = null },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.SaveAsync("target", invalid));

        fixture.AssertAllBytes(before);
        Assert.Equal(2, (await fixture.Store.LoadAsync("target"))!.ModelHistoryCount);
    }

    private static Message User(string id) => new(Role.User, [new TextBlock("question")], id);

    private static SessionSnapshot Received() => new(2, [User("u")])
    {
        ModelHistoryCount = 0,
        ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("state-1") },
        ModelInputReceipt = new(1, "u", "discarded")
    };

    private static SessionSnapshot Legacy() => new(1, [User("u-old"), new Message(Role.Assistant, [new TextBlock("answer")], "a-old")])
    {
        ModelHistoryCount = 2,
        ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("state-old") }
    };

    private sealed class StoreFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "strands-receipt-" + Guid.NewGuid().ToString("N"));
        internal FileSessionStore Store { get; }

        internal StoreFixture() => Store = new(Root);

        internal Dictionary<string, byte[]> Bytes() => Directory.GetFiles(Root).ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

        internal void AssertRetainedBytes(IReadOnlyDictionary<string, byte[]> expected)
        {
            foreach (var pair in expected)
                Assert.True(pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)), "An unrelated session file changed.");
        }

        internal void AssertAllBytes(IReadOnlyDictionary<string, byte[]> expected)
        {
            Assert.Equal(expected.Keys.OrderBy(path => path, StringComparer.Ordinal), Directory.GetFiles(Root).OrderBy(path => path, StringComparer.Ordinal));
            AssertRetainedBytes(expected);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var path in Directory.GetFiles(Root)) File.Delete(path);
            Directory.Delete(Root);
        }
    }
}
