using System.Text.Json;
using Strands.Agents.Context;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.Context;

public sealed class StashTests
{
    [Fact]
    public async Task CallerSuppliedDuplicateTrackingIdsCannotAliasDifferentOriginalContent()
    {
        var stash = new Stash(new InMemoryStorage(), "session", "agent");
        var first = new Message(Role.User, [new ToolResultBlock("call", "first")], "caller-reused");
        var second = new Message(Role.User, [new ToolResultBlock("call", "second")], "caller-reused");
        await stash.StoreMessageAsync(first);
        await stash.StoreMessageAsync(second);
        var firstRef = Assert.Single(stash.GetReferences(first.Content[0], first, 0));
        var secondRef = Assert.Single(stash.GetReferences(second.Content[0], second, 0));
        Assert.NotEqual(firstRef, secondRef);
        Assert.Equal("first", Assert.IsType<TextBlock>(await stash.RetrieveAsync(firstRef)).Text);
        Assert.Equal("second", Assert.IsType<TextBlock>(await stash.RetrieveAsync(secondRef)).Text);
    }

    [Fact]
    public async Task ReusedProviderToolIdentifiersCannotOverwriteEarlierResults()
    {
        var stash = new Stash(new InMemoryStorage(), "session", "agent");
        var first = new Message(Role.User, [new ToolResultBlock("reused", "first")]);
        var second = new Message(Role.User, [new ToolResultBlock("reused", "second")]);
        await stash.StoreMessageAsync(first);
        await stash.StoreMessageAsync(second);
        var firstRef = Assert.Single(stash.GetReferences(first.Content[0], first, 0));
        var secondRef = Assert.Single(stash.GetReferences(second.Content[0], second, 0));
        Assert.NotEqual(firstRef, secondRef);
        Assert.Equal("first", Assert.IsType<TextBlock>(await stash.RetrieveAsync(firstRef)).Text);
        Assert.Equal("second", Assert.IsType<TextBlock>(await stash.RetrieveAsync(secondRef)).Text);
    }

    [Fact]
    public async Task AutomaticNamespacesIsolateAgentsAndSessionDeletionRetainsOtherSessions()
    {
        var storage = new InMemoryStorage();
        var first = new Stash(storage, "session", "first");
        var second = new Stash(storage, "session", "second");
        var otherSession = new Stash(storage, "session-other", "first");
        var original = Message.User("secret");
        await first.StoreMessageAsync(original);
        await otherSession.StoreMessageAsync(original);
        var reference = Assert.Single(first.GetReferences(original.Content[0], original, 0));
        Assert.Null(await second.RetrieveAsync(reference));
        await first.ClearSessionAsync();
        Assert.Empty(await first.ListAsync());
        Assert.Empty(await second.ListAsync());
        Assert.Equal("secret", Assert.IsType<TextBlock>(await otherSession.RetrieveAsync(reference)).Text);
    }

    [Fact]
    public async Task ExplicitNamespacedStashSharesItsExactRootAndSessionDeletionLeavesItToTheCaller()
    {
        var storage = new InMemoryStorage();
        var root = storage.Namespace("team/");
        var first = new Stash(root, "session", "first", true);
        var second = new Stash(root, "other", "second", true);
        var message = Message.User("shared");
        await first.StoreMessageAsync(message);
        var reference = Assert.Single(first.GetReferences(message.Content[0], message, 0));
        Assert.Equal("shared", Assert.IsType<TextBlock>(await second.RetrieveAsync(reference)).Text);
        Assert.Equal(["team/" + reference], await storage.ListAsync());
        await first.ClearSessionAsync();
        Assert.Single(await second.ListAsync());
        await second.ClearAsync();
        Assert.Empty(await storage.ListAsync());
    }

    [Fact]
    public async Task SnapshotsRestoreRichOriginalsAndPlaceholdersCannotReplaceThem()
    {
        var original = new Message(Role.User, [new ToolResultBlock("tool", new ContentBlock[] { new TextBlock("large"), new ImageBlock("image/png", "aW1hZ2U=") }, structuredContent: JsonSerializer.SerializeToElement(new { value = 42 }))]);
        var stash = new Stash(new InMemoryStorage(), "session", "first");
        await stash.StoreMessageAsync(original);
        var snapshot = await stash.TakeSnapshotAsync();
        var restored = new Stash(new InMemoryStorage(), "session", "restored");
        await restored.LoadSnapshotAsync(snapshot);
        var placeholder = new Message(original.Role, [new ToolResultBlock("tool", "[Truncated]")], original.TrackingId);
        await restored.StoreMessageAsync(placeholder);
        var references = stash.GetReferences(original.Content[0], original, 0);
        Assert.Equal(3, references.Count);
        Assert.Equal("large", Assert.IsType<TextBlock>(await restored.RetrieveAsync(references[0])).Text);
        Assert.Equal("aW1hZ2U=", Assert.IsType<ImageBlock>(await restored.RetrieveAsync(references[1])).Data);
        Assert.Equal(42, Assert.IsType<JsonBlock>(await restored.RetrieveAsync(references[2])).Json.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task RetrievalResultsAndSignedReasoningAreExcludedFromOriginalContentStorage()
    {
        var stash = new Stash(new InMemoryStorage(), "session", "agent");
        await stash.StoreMessageAsync(new(Role.Assistant, [new ReasoningBlock("private", "signature"), new RedactedReasoningBlock("opaque"), new CachePointBlock(),
            new Strands.Agents.Models.GoogleContentBlock(JsonSerializer.Deserialize<JsonElement>("""{"text":"private thought","thought":true,"thoughtSignature":"opaque-signature"}"""))]));
        await stash.StoreMessageAsync(new(Role.User, [new ToolResultBlock("retrieval", "do not stash")]), new HashSet<string> { "retrieval" });
        Assert.Empty(await stash.ListAsync());
    }
}
