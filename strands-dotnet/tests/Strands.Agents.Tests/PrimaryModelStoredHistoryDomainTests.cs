using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelStoredHistoryDomainTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DefaultDomainKeepsConstructorDeconstructionAndOmittedJsonWhileRecordCopiesRetainExplicitProfiles()
    {
        var messages = new[] { Message.User("old input"), Message.Assistant("old answer") };
        var legacy = new SessionSnapshot(1, messages);
        var (schema, retained) = legacy;
        Assert.Equal(1, schema);
        Assert.Same(messages, retained);
        Assert.Equal(ModelHistoryBehavior.Native, legacy.HistoryProfile);
        var python = legacy with { HistoryProfile = ModelHistoryBehavior.Python };
        var copy = python with { };
        Assert.Equal(python, copy);
        Assert.Equal(python.GetHashCode(), copy.GetHashCode());
        Assert.NotEqual(legacy, python);
        Assert.Equal(legacy, python with { HistoryProfile = ModelHistoryBehavior.Native });
        Assert.Contains("HistoryProfile = Python", python.ToString());
        foreach (var options in new[] { new JsonSerializerOptions(), Web })
        {
            var key = options.PropertyNamingPolicy?.ConvertName(nameof(SessionSnapshot.HistoryProfile)) ?? nameof(SessionSnapshot.HistoryProfile);
            var oldJson = JsonSerializer.SerializeToElement(legacy, options);
            Assert.False(oldJson.TryGetProperty(key, out _));
            var explicitJson = JsonSerializer.SerializeToElement(python, options);
            Assert.Equal(2, explicitJson.GetProperty(key).GetInt32());
            var restored = Assert.IsType<SessionSnapshot>(explicitJson.Deserialize<SessionSnapshot>(options));
            Assert.Equal(ModelHistoryBehavior.Python, restored.HistoryProfile);
            Assert.Equal(messages.Select(message => message.TrackingId), restored.Messages.Select(message => message.TrackingId));
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3")]
    [InlineData("null")]
    [InlineData("\"Python\"")]
    [InlineData("{}")]
    public async Task MalformedDomainRejectsBeforeManualInitializationOrPublication(string wire)
    {
        var initialized = 0;
        var hooks = new HookRegistry();
        hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        var model = new NoProviderModel();
        Agent? target = null;
        Exception? primary = null;
        try
        {
            target = new(new() { Id = "local", Model = model, Hooks = hooks, ModelHistoryBehavior = ModelHistoryBehavior.Python });
            var data = PairedData();
            data["historyProfile"] = Json(wire);
            data["state"] = Json("{\"imported\":true}");
            await Assert.ThrowsAsync<InvalidDataException>(() => target.LoadSnapshotAsync(new(data)).AsTask());
            Assert.Equal(0, initialized);
            Assert.Empty(target.Messages);
            Assert.Empty(target.State.GetAll());
            Assert.Equal("local", target.Id);
            Assert.Equal(0, model.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.Native, ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Native, ModelHistoryBehavior.Python)]
    [InlineData(ModelHistoryBehavior.TypeScript, ModelHistoryBehavior.Python)]
    [InlineData(ModelHistoryBehavior.Python, ModelHistoryBehavior.TypeScript)]
    public async Task ExplicitStoredProfileNeverRetargetsTheReceivingOwner(ModelHistoryBehavior owner, ModelHistoryBehavior stored)
    {
        var initialized = 0;
        var hooks = new HookRegistry();
        hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        var model = new NoProviderModel();
        Agent? target = null;
        Exception? primary = null;
        try
        {
            target = new(new() { Model = model, Hooks = hooks, ModelHistoryBehavior = owner });
            var data = PairedData();
            data["historyProfile"] = JsonSerializer.SerializeToElement(stored);
            data["modelState"] = Json("{\"foreign\":7}");
            await Assert.ThrowsAsync<InvalidDataException>(() => target.LoadSnapshotAsync(new(data)).AsTask());
            Assert.Equal(0, initialized);
            Assert.Empty(target.Messages);
            Assert.Empty(target.ModelState.GetAll());
            Assert.Equal(0, model.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceDomainRejectsEvenZeroCursorAndACompleteNativeReceiptBeforeInitialization(bool withReceipt)
    {
        var initialized = 0;
        var hooks = new HookRegistry();
        hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        var model = new NoProviderModel();
        Agent? target = null;
        Exception? primary = null;
        try
        {
            var messages = new[] { Message.User("input"), Message.Assistant("answer") };
            var data = new Dictionary<string, JsonElement>
            {
                ["messages"] = JsonSerializer.SerializeToElement(messages, Web),
                ["historyProfile"] = Json("2"),
                ["modelHistoryCount"] = Json("0")
            };
            if (withReceipt)
            {
                data["modelState"] = Json("{\"session\":\"S1\"}");
                data["modelInputReceipt"] = JsonSerializer.SerializeToElement(new ModelInputReceipt(1, messages[0].TrackingId, "discarded-response"), Web);
            }
            target = new(new() { Model = model, Hooks = hooks, ModelHistoryBehavior = ModelHistoryBehavior.Python });
            await Assert.ThrowsAsync<InvalidDataException>(() => target.LoadSnapshotAsync(new(data, schemaVersion: withReceipt ? "2.0" : "1.0")).AsTask());
            Assert.Equal(0, initialized);
            Assert.Empty(target.Messages);
            Assert.Empty(target.ModelState.GetAll());
            Assert.Equal(0, model.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    [Fact]
    public async Task UnknownSessionDomainRejectsBeforeCreatingTheStoreDirectory()
    {
        var files = new OwnedFiles();
        Exception? primary = null;
        try
        {
            var snapshot = new SessionSnapshot(1, [Message.User("input")]) { HistoryProfile = (ModelHistoryBehavior)99 };
            await Assert.ThrowsAsync<InvalidDataException>(() => new FileSessionStore(files.Path).SaveAsync("invalid", snapshot).AsTask());
            Assert.False(Directory.Exists(files.Path));
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, files); }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("status")]
    [InlineData("mixed")]
    [InlineData("role")]
    [InlineData("later")]
    [InlineData("untagged")]
    public async Task PythonPrefixPermissionDoesNotRelaxOtherHistoryGrammar(string kind)
    {
        ContentBlock[] blocks = kind switch
        {
            "duplicate" => [new ToolResultBlock("call", "first"), new ToolResultBlock("call", "second")],
            "blank" => [new ToolResultBlock(" ", "result")],
            "status" => [new ToolResultBlock("call", "result", (ToolResultStatus)99)],
            "mixed" => [new ToolResultBlock("call", "result"), new TextBlock("mixed")],
            _ => [new ToolResultBlock("call", "result")]
        };
        var first = new Message(kind == "role" ? Role.Assistant : Role.User, blocks);
        Message[] messages = kind == "later"
            ? [Message.User("ordinary input"), first] : [first];
        var snapshot = new SessionSnapshot(1, messages)
        { HistoryProfile = kind == "untagged" ? ModelHistoryBehavior.Native : ModelHistoryBehavior.Python };
        var files = new OwnedFiles();
        Exception? primary = null;
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new FileSessionStore(files.Path).SaveAsync("invalid-prefix", snapshot).AsTask());
            Assert.False(Directory.Exists(files.Path));
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, files); }
    }

    [Fact]
    public async Task ExplicitPythonPrefixRoundTripsAlongsideAStrictCorrelatedSuffix()
    {
        var request = new Message(Role.Assistant, [new ToolUseBlock("suffix-call", "next", Json("{}"))]);
        var result = new Message(Role.User, [new ToolResultBlock("suffix-call", "suffix result")]);
        var snapshot = new SessionSnapshot(1, [Prefix(), Message.User("next input"), request, result, Message.Assistant("done")])
        { HistoryProfile = ModelHistoryBehavior.Python, ModelState = new Dictionary<string, JsonElement> { ["session"] = Json("\"S1\"") } };
        var files = new OwnedFiles();
        Exception? primary = null;
        try
        {
            await new FileSessionStore(files.Path).SaveAsync("python-prefix", snapshot);
            var restored = Assert.IsType<SessionSnapshot>(await new FileSessionStore(files.Path).LoadAsync("python-prefix"));
            Assert.Equal(ModelHistoryBehavior.Python, restored.HistoryProfile);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(snapshot, Web), JsonSerializer.SerializeToElement(restored, Web)));
            Assert.NotSame(snapshot.Messages, restored.Messages);
            Assert.Equal("call", Assert.Single(restored.Messages[0].Content.OfType<ToolResultBlock>()).ToolUseId);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, files); }
    }

    [Fact]
    public async Task DomainOnlyPartialRejectsWhileStateOnlyPartialKeepsTheExistingOwnersPythonPrefix()
    {
        var initialized = 0;
        var hooks = new HookRegistry();
        hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        var model = new NoProviderModel();
        Agent? target = null;
        Exception? primary = null;
        try
        {
            target = new(new() { Model = model, Hooks = hooks, ModelHistoryBehavior = ModelHistoryBehavior.Python });
            await Assert.ThrowsAsync<InvalidDataException>(() => target.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>
            { ["historyProfile"] = Json("2"), ["state"] = Json("{\"bad\":true}") })).AsTask());
            Assert.Equal(0, initialized);
            Assert.Empty(target.State.GetAll());
            var prefix = Prefix();
            await target.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>
            { ["messages"] = JsonSerializer.SerializeToElement(new[] { prefix }, Web), ["historyProfile"] = Json("2") }));
            Assert.Equal(1, initialized);
            await target.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["state"] = Json("{\"good\":true}") }));
            Assert.True(target.State.Get<bool>("good"));
            Assert.Equal(prefix.TrackingId, Assert.Single(target.Messages).TrackingId);
            Assert.Equal("call", Assert.Single(target.Messages[0].Content.OfType<ToolResultBlock>()).ToolUseId);
            Assert.Equal(1, initialized);
            Assert.Equal(0, model.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.Native)]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task ExplicitSnapshotsCarryTheOwnersDomainOnlyWhenMessagesAreSelected(ModelHistoryBehavior profile)
    {
        var model = new NoProviderModel();
        Agent? target = null;
        Exception? primary = null;
        try
        {
            target = new(new() { Model = model, ModelHistoryBehavior = profile });
            var stateOnly = await target.TakeSnapshotAsync(new() { Include = SnapshotFields.State });
            Assert.False(stateOnly.Data.ContainsKey("historyProfile"));
            var messages = await target.TakeSnapshotAsync(new() { Include = SnapshotFields.Messages });
            if (profile == ModelHistoryBehavior.Native) Assert.False(messages.Data.ContainsKey("historyProfile"));
            else Assert.Equal((int)profile, messages.Data["historyProfile"].GetInt32());
            Assert.Empty(messages.Data["messages"].EnumerateArray());
            Assert.Equal(0, model.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    [Fact]
    public async Task NativePendingOnlyPartialRejectsIndependentOwnershipBeforeInitialization()
    {
        var model = new NoProviderModel();
        var initialized = 0;
        var hooks = new HookRegistry();
        hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        Agent? target = null;
        Exception? primary = null;
        try
        {
            var pending = new InterruptState(new Message(Role.Assistant, [new ToolUseBlock("call", "approve", Json("{}"))]), [],
                [new Interrupt("question", "approval", "call")], [])
            { AssistantMessageIsIndependent = true };
            target = new(new() { Model = model, Hooks = hooks });
            await Assert.ThrowsAsync<InvalidDataException>(() => target.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>
            { ["interruptState"] = JsonSerializer.SerializeToElement(pending, Web) })).AsTask());
            Assert.Equal(0, initialized);
            Assert.Null(target.InterruptState);
            Assert.Empty(target.Messages);
            Assert.Equal(0, model.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    private static Dictionary<string, JsonElement> PairedData() => new()
    { ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("input"), Message.Assistant("answer") }, Web) };
    private static Message Prefix() => new(Role.User, [new ToolResultBlock("call", "result")], "independent-result");
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static async ValueTask DisposeOwned(Exception? primary, params IAsyncDisposable?[] resources)
    {
        Exception? failure = primary;
        var next = 0;
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try { await resource.DisposeAsync(); }
            catch (Exception error)
            {
                if (failure is null) failure = error;
                else if (!ReferenceEquals(failure, error)) failure.Data["StoredHistory.Cleanup." + next++] = error;
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class NoProviderModel : IStatefulModel, IAsyncDisposable
    {
        public bool Stateful => false;
        internal int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("No provider operation is allowed in this snapshot boundary fixture.");
        }
        public ValueTask DisposeAsync() => default;
    }
    private sealed class OwnedFiles : IAsyncDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stored-history-domain", Guid.NewGuid().ToString("N"));
        public ValueTask DisposeAsync()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned fixture directory escaped task TEMP.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            return default;
        }
    }
}
