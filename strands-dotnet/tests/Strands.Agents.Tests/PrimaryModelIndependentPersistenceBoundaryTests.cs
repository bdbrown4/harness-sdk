using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.Storage;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelIndependentPersistenceBoundaryTests
{
    private const string OwnerId = "independent-boundary-owner";
    private const string SessionId = "independent-boundary-session";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("save")]
    [InlineData("load")]
    public async Task KnownPythonIndependentHistoryCannotEnterFileStore(string operation)
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var seed = await CapturePublicPause(owned);
            var nativeDirectory = Path.Combine(owned.Directory, "native");
            var native = await RoundTripNativeFile(seed.Session, nativeDirectory);
            var invalid = Independent(native, ModelHistoryBehavior.Python, [OrdinaryHistory()]);
            AssertOnlyOwnershipHistoryChanged(native, invalid);
            var dtoBefore = Element(invalid);
            if (operation == "save")
            {
                var rejectedDirectory = Path.Combine(owned.Directory, "rejected");
                Assert.False(Directory.Exists(rejectedDirectory));
                await Assert.ThrowsAsync<InvalidDataException>(async () =>
                { await new FileSessionStore(rejectedDirectory).SaveAsync(SessionId, invalid); });
                Assert.False(Directory.Exists(rejectedDirectory));
            }
            else
            {
                var observedFile = Assert.Single(Directory.GetFiles(nativeDirectory, "*.json", SearchOption.TopDirectoryOnly));
                var invalidBytes = JsonSerializer.SerializeToUtf8Bytes(invalid, Web);
                File.WriteAllBytes(observedFile, invalidBytes);
                await Assert.ThrowsAsync<InvalidDataException>(async () =>
                { await new FileSessionStore(nativeDirectory).LoadAsync(SessionId); });
                Assert.Equal(invalidBytes, File.ReadAllBytes(observedFile));
                Assert.Single(Directory.GetFiles(nativeDirectory, "*", SearchOption.TopDirectoryOnly));
            }
            Assert.True(JsonElement.DeepEquals(dtoBefore, Element(invalid)));
            Assert.Equal(1, seed.Model.StreamCalls);
            Assert.Equal(1, seed.Model.IteratorFinallys);
            Assert.Equal(1, seed.ProducerEffects);
            await owned.AssertBorrowedResourcesRemainOpenAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Fact]
    public async Task LegacyIndependentFileHistoryStillRequiresCompleteGrammar()
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var seed = await CapturePublicPause(owned);
            var native = await RoundTripNativeFile(seed.Session, Path.Combine(owned.Directory, "native"));
            var unfinished = new Message(Role.Assistant,
                [new ToolUseBlock("unresolved-X", "approve", Json("{}"))], "retained-request-X");
            var invalid = Independent(native, ModelHistoryBehavior.Native, [unfinished]);
            Assert.NotEqual("pending-Y", Assert.Single(unfinished.Content.OfType<ToolUseBlock>()).ToolUseId);
            AssertOnlyOwnershipHistoryChanged(native, invalid);
            var before = Element(invalid);
            var rejectedDirectory = Path.Combine(owned.Directory, "rejected");

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            { await new FileSessionStore(rejectedDirectory).SaveAsync(SessionId, invalid); });

            Assert.False(Directory.Exists(rejectedDirectory));
            Assert.True(JsonElement.DeepEquals(before, Element(invalid)));
            Assert.Equal(ModelHistoryBehavior.Native, invalid.HistoryProfile);
            Assert.False(Element(invalid).TryGetProperty("historyProfile", out _));
            await owned.AssertBorrowedResourcesRemainOpenAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Fact]
    public async Task LegacyCompleteFileHistoryDefersOwnershipToTheReceivingProfile()
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var seed = await CapturePublicPause(owned);
            var native = await RoundTripNativeFile(seed.Session, Path.Combine(owned.Directory, "native"));
            var legacy = Independent(native, ModelHistoryBehavior.Native, [OrdinaryHistory()]);
            var directory = Path.Combine(owned.Directory, "legacy");
            await new FileSessionStore(directory).SaveAsync(SessionId, legacy);
            var reopened = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory).LoadAsync(SessionId));
            Assert.Equal(ModelHistoryBehavior.Native, reopened.HistoryProfile);
            Assert.False(Element(reopened).TryGetProperty("historyProfile", out _));
            AssertOnlyOwnershipHistoryChanged(native, reopened);
            var legacyBytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "*.json")));
            var pythonProbe = new Probe();
            var pythonModel = owned.Model(pause: false);
            var python = owned.Agent(CreateReceiver(ModelHistoryBehavior.Python, pythonModel, pythonProbe,
                new FileSessionStore(directory)));

            await Assert.ThrowsAsync<InvalidDataException>(async () => await python.InitializeAsync());

            AssertUnpublished(python, pythonModel, pythonProbe);
            Assert.Equal(legacyBytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "*.json"))));
            var tsProbe = new Probe();
            var tsModel = owned.Model(pause: false);
            var typescript = owned.Agent(CreateReceiver(ModelHistoryBehavior.TypeScript, tsModel, tsProbe,
                new FileSessionStore(directory)));

            var result = await typescript.InvokeAsync(null, Answer(seed));

            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Equal("finished", result.ToString());
            Assert.Equal(1, tsProbe.Initialized);
            Assert.Equal(1, tsProbe.ToolEffects);
            Assert.Equal(1, tsModel.StreamCalls);
            Assert.Equal(1, tsModel.IteratorFinallys);
            AssertProjectedRequest(Assert.Single(tsModel.Requests), seed.Session.InterruptState!, errorReason: null);
            Assert.Null(typescript.InterruptState);
            AssertRestoredState(typescript);
            var saved = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory).LoadAsync(SessionId));
            Assert.Equal(ModelHistoryBehavior.TypeScript, saved.HistoryProfile);
            Assert.Null(saved.InterruptState);
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            Assert.Equal(4, saved.Messages.Count);
            Assert.Equal("finished", saved.Messages[3].Text);
            await owned.AssertBorrowedResourcesRemainOpenAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogRestoreValidatesIndependentHistoryBeforePublishingState(bool knownPythonProfile)
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var storage = owned.Storage();
            var seed = await CapturePublicCatalogPause(owned, storage);
            var invalid = Independent(seed.Session,
                knownPythonProfile ? ModelHistoryBehavior.Python : ModelHistoryBehavior.Native, [OrdinaryHistory()]);
            AssertOnlyOwnershipHistoryChanged(seed.Session, invalid);
            var invalidBytes = ReplaceEnvelopeSnapshot(seed.Envelope, invalid);
            AssertEnvelopeIdentityHeld(seed.Envelope, invalidBytes);
            storage.ReplaceObserved(seed.Key, invalidBytes);
            var writesBefore = storage.Writes;
            var readsBefore = storage.Reads;
            var probe = new Probe();
            var model = owned.Model(pause: false);
            var manager = new SnapshotSessionManager(storage, SessionId);
            var target = owned.Agent(CreateReceiver(ModelHistoryBehavior.Python, model, probe, catalog: manager));

            await Assert.ThrowsAsync<InvalidDataException>(async () => { await manager.RestoreSnapshotAsync(target); });

            AssertUnpublished(target, model, probe);
            Assert.Equal(readsBefore + 1, storage.Reads);
            Assert.Equal(writesBefore, storage.Writes);
            Assert.Equal(invalidBytes, storage.GetObserved(seed.Key));
            AssertEnvelopeIdentityHeld(seed.Envelope, storage.GetObserved(seed.Key));
            Assert.Single(storage.Keys);
            await owned.AssertBorrowedResourcesRemainOpenAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Fact]
    public async Task CompleteTypeScriptIndependentCatalogStateRoundTripsWithoutEffects()
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var storage = owned.Storage();
            var seed = await CapturePublicCatalogPause(owned, storage);
            var imported = Independent(seed.Session, ModelHistoryBehavior.TypeScript, [OrdinaryHistory()]);
            AssertOnlyOwnershipHistoryChanged(seed.Session, imported);
            storage.ReplaceObserved(seed.Key, ReplaceEnvelopeSnapshot(seed.Envelope, imported));
            var writesBefore = storage.Writes;
            var readsBefore = storage.Reads;
            var firstModel = owned.Model(pause: false);
            var firstProbe = new Probe();
            var firstManager = new SnapshotSessionManager(storage, SessionId);
            var first = owned.Agent(CreateReceiver(ModelHistoryBehavior.TypeScript, firstModel, firstProbe, catalog: firstManager));

            Assert.True(await firstManager.RestoreSnapshotAsync(first));

            AssertRestoredIndependent(first, seed.Session.InterruptState!);
            AssertNoExecution(firstModel, firstProbe, initialized: 1);
            Assert.Equal(writesBefore, storage.Writes);
            Assert.Null(await firstManager.SaveSnapshotAsync(first, isLatest: true));
            Assert.Equal(writesBefore + 1, storage.Writes);
            var savedBytes = storage.GetObserved(seed.Key);
            AssertEnvelopeIdentityHeld(seed.Envelope, savedBytes);
            var saved = EnvelopeSnapshot(savedBytes);
            Assert.Equal(ModelHistoryBehavior.TypeScript, saved.HistoryProfile);
            Assert.True(Assert.IsType<InterruptState>(saved.InterruptState).AssistantMessageIsIndependent);
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            Assert.True(JsonElement.DeepEquals(Element(imported), Element(saved)));
            AssertNoExecution(firstModel, firstProbe, initialized: 1);

            var reopenedModel = owned.Model(pause: false);
            var reopenedProbe = new Probe();
            var reopenedManager = new SnapshotSessionManager(storage, SessionId);
            var reopened = owned.Agent(CreateReceiver(ModelHistoryBehavior.TypeScript, reopenedModel, reopenedProbe, catalog: reopenedManager));

            Assert.True(await reopenedManager.RestoreSnapshotAsync(reopened));

            AssertRestoredIndependent(reopened, seed.Session.InterruptState!);
            AssertNoExecution(reopenedModel, reopenedProbe, initialized: 1);
            Assert.Equal(readsBefore + 2, storage.Reads);
            Assert.Equal(writesBefore + 1, storage.Writes);
            Assert.Equal(savedBytes, storage.GetObserved(seed.Key));
            Assert.Single(storage.Keys);
            await owned.AssertBorrowedResourcesRemainOpenAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Fact]
    public async Task CompleteTypeScriptIndependentAbortUsesTheOwnersErrorProjection()
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var seed = await CapturePublicPause(owned);
            var imported = Independent(seed.Session, ModelHistoryBehavior.TypeScript, [OrdinaryHistory()]);
            var directory = Path.Combine(owned.Directory, "abort");
            await new FileSessionStore(directory).SaveAsync(SessionId, imported);
            var probe = new Probe();
            var model = owned.Model(pause: false);
            var target = owned.Agent(CreateReceiver(ModelHistoryBehavior.TypeScript, model, probe,
                new FileSessionStore(directory)));
            await target.InitializeAsync();
            AssertRestoredIndependent(target, seed.Session.InterruptState!);
            const string reason = "approval was withdrawn";

            Assert.True(await target.AbortInterruptsAsync(reason));

            Assert.Null(target.InterruptState);
            AssertProjectedRequest(target.Messages, seed.Session.InterruptState!, reason);
            AssertNoExecution(model, probe, initialized: 1, added: 2);
            AssertRestoredState(target);
            var saved = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory).LoadAsync(SessionId));
            Assert.Equal(ModelHistoryBehavior.TypeScript, saved.HistoryProfile);
            Assert.Null(saved.InterruptState);
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            AssertProjectedRequest(saved.Messages, seed.Session.InterruptState!, reason);
            var savedFile = Assert.Single(Directory.GetFiles(directory, "*.json"));
            var beforeSecondAbort = File.ReadAllBytes(savedFile);

            Assert.False(await target.AbortInterruptsAsync(reason));

            Assert.Equal(beforeSecondAbort, File.ReadAllBytes(savedFile));
            AssertNoExecution(model, probe, initialized: 1, added: 2);
            await owned.AssertBorrowedResourcesRemainOpenAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    private static async Task<PauseSeed> CapturePublicPause(OwnedResources owned)
    {
        var model = owned.Model(pause: true);
        var store = new MemoryStore();
        var effects = 0;
        var producer = owned.Agent(CreateProducer(model, () => effects++, store));
        var result = await producer.InvokeAsync("seed input");
        var session = Assert.IsType<SessionSnapshot>(store.LastSaved);
        AssertNativePause(result, session, model, effects);
        return new(session, Assert.Single(result.Interrupts).Id, model, effects);
    }

    private static async Task<CatalogSeed> CapturePublicCatalogPause(OwnedResources owned, RecordingStorage storage)
    {
        var model = owned.Model(pause: true);
        var manager = new SnapshotSessionManager(storage, SessionId);
        var effects = 0;
        var producer = owned.Agent(CreateProducer(model, () => effects++, catalog: manager));
        var result = await producer.InvokeAsync("seed input");
        Assert.Null(await manager.SaveSnapshotAsync(producer, isLatest: true));
        var key = Assert.Single(storage.Keys);
        var envelope = storage.GetObserved(key);
        var session = EnvelopeSnapshot(envelope);
        AssertNativePause(result, session, model, effects);
        AssertEnvelopeIdentityHeld(envelope, envelope);
        return new(session, key, envelope);
    }

    private static Agent CreateProducer(FiniteModel model, Action effect, ISessionStore? store = null, SnapshotSessionManager? catalog = null)
        => new(new()
        {
            Id = OwnerId,
            Model = model,
            ModelHistoryBehavior = ModelHistoryBehavior.Native,
            SystemPrompt = "stored-system",
            State = new Dictionary<string, JsonElement> { ["seedOnly"] = Json("true") },
            SessionStore = store,
            SessionId = store is null ? null : SessionId,
            Plugins = catalog is null ? [] : [catalog],
            ToolExecution = ToolExecutionStrategy.Sequential,
            Tools = [Approve(context => { effect(); return context.Interrupt("approval").ToString(); })]
        });

    private static Agent CreateReceiver(ModelHistoryBehavior profile, FiniteModel model, Probe probe,
        ISessionStore? store = null, SnapshotSessionManager? catalog = null)
    {
        var hooks = new HookRegistry();
        hooks.Register<InitializedEvent>((_, _) => { probe.Initialized++; return default; });
        hooks.Register<BeforeInvocationEvent>((_, _) => { probe.BeforeInvocation++; return default; });
        hooks.Register<BeforeModelCallEvent>((_, _) => { probe.BeforeModel++; return default; });
        hooks.Register<MessageAddedEvent>((_, _) => { probe.Added++; return default; });
        return new(new()
        {
            Id = OwnerId,
            Model = model,
            ModelHistoryBehavior = profile,
            Hooks = hooks,
            SystemPrompt = "local-system",
            Messages = [new(Role.User, [new TextBlock("local history")], "local-user")],
            State = new Dictionary<string, JsonElement> { ["local"] = Json("true") },
            ModelState = new Dictionary<string, JsonElement> { ["localProvider"] = Json("17") },
            SessionStore = store,
            SessionId = store is null ? null : SessionId,
            Plugins = catalog is null ? [] : [catalog],
            ToolExecution = ToolExecutionStrategy.Sequential,
            Tools = [Approve(context =>
            {
                probe.ToolEffects++;
                return context.Interrupt("approval").GetBoolean() ? "approved" : "declined";
            })]
        });
    }

    private static void AssertNativePause(AgentResult result, SessionSnapshot session, FiniteModel model, int effects)
    {
        Assert.Equal(StopReasons.Interrupt, result.StopReason);
        var question = Assert.Single(result.Interrupts);
        Assert.Equal("pending-Y", question.ToolUseId);
        Assert.Equal(1, effects);
        Assert.Equal(1, model.StreamCalls);
        Assert.Equal(1, model.IteratorFinallys);
        Assert.Equal(ModelHistoryBehavior.Native, session.HistoryProfile);
        Assert.Equal(OwnerId, session.AgentId);
        Assert.True(session.HasSystemPrompt);
        Assert.Equal("stored-system", session.SystemPrompt);
        Assert.True(session.State!["seedOnly"].GetBoolean());
        Assert.Equal(42, session.ModelState!["seedProvider"].GetInt32());
        Assert.Null(session.ModelHistoryCount);
        Assert.Null(session.ModelInputReceipt);
        var pending = Assert.IsType<InterruptState>(session.InterruptState);
        Assert.False(pending.AssistantMessageIsIndependent);
        Assert.Equal(question.Id, Assert.Single(pending.Interrupts).Id);
        Assert.Equal("pending-Y", Assert.Single(pending.AssistantMessage.Content.OfType<ToolUseBlock>()).ToolUseId);
        Assert.Equal(2, session.Messages.Count);
        Assert.Equal(pending.AssistantMessage.TrackingId, session.Messages[1].TrackingId);
        Assert.Empty(pending.CompletedToolResults);
    }

    private static async Task<SessionSnapshot> RoundTripNativeFile(SessionSnapshot snapshot, string directory)
    {
        await new FileSessionStore(directory).SaveAsync(SessionId, snapshot);
        var reopened = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory).LoadAsync(SessionId));
        Assert.Equal(ModelHistoryBehavior.Native, reopened.HistoryProfile);
        var pending = Assert.IsType<InterruptState>(reopened.InterruptState);
        Assert.False(pending.AssistantMessageIsIndependent);
        Assert.Equal(snapshot.Messages.Select(message => message.TrackingId), reopened.Messages.Select(message => message.TrackingId));
        Assert.Equal(snapshot.InterruptState!.AssistantMessage.TrackingId, pending.AssistantMessage.TrackingId);
        Assert.Equal("pending-Y", Assert.Single(pending.AssistantMessage.Content.OfType<ToolUseBlock>()).ToolUseId);
        Assert.Single(Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly));
        return reopened;
    }

    private static SessionSnapshot Independent(SessionSnapshot original, ModelHistoryBehavior profile, Message[] messages)
        => original with
        {
            HistoryProfile = profile,
            Messages = messages,
            InterruptState = original.InterruptState! with { AssistantMessageIsIndependent = true }
        };

    private static void AssertOnlyOwnershipHistoryChanged(SessionSnapshot original, SessionSnapshot mapped)
    {
        var before = Element(original);
        var after = Element(mapped);
        foreach (var field in before.EnumerateObject())
            if (field.Name is not ("historyProfile" or "messages" or "interruptState"))
                Assert.True(JsonElement.DeepEquals(field.Value, after.GetProperty(field.Name)), field.Name);
        foreach (var field in after.EnumerateObject())
            if (field.Name is not ("historyProfile" or "messages" or "interruptState"))
                Assert.True(before.TryGetProperty(field.Name, out _), field.Name);
        var oldPending = before.GetProperty("interruptState");
        var newPending = after.GetProperty("interruptState");
        foreach (var field in oldPending.EnumerateObject())
            if (field.Name != "assistantMessageIsIndependent")
                Assert.True(JsonElement.DeepEquals(field.Value, newPending.GetProperty(field.Name)), field.Name);
        Assert.True(newPending.GetProperty("assistantMessageIsIndependent").GetBoolean());
    }

    private static void AssertUnpublished(Agent target, FiniteModel model, Probe probe)
    {
        AssertNoExecution(model, probe, initialized: 0);
        Assert.Equal(OwnerId, target.Id);
        Assert.Equal("local-system", target.SystemPrompt);
        var local = Assert.Single(target.Messages);
        Assert.Equal("local-user", local.TrackingId);
        Assert.Equal("local history", local.Text);
        Assert.True(target.State.Get<bool>("local"));
        Assert.Equal("local", Assert.Single(target.State.GetAll()).Key);
        Assert.Equal(17, target.ModelState.Get<int>("localProvider"));
        Assert.Equal("localProvider", Assert.Single(target.ModelState.GetAll()).Key);
        Assert.Null(target.InterruptState);
    }

    private static void AssertNoExecution(FiniteModel model, Probe probe, int initialized, int added = 0)
    {
        Assert.Equal(initialized, probe.Initialized);
        Assert.Equal(0, probe.BeforeInvocation);
        Assert.Equal(0, probe.BeforeModel);
        Assert.Equal(added, probe.Added);
        Assert.Equal(0, probe.ToolEffects);
        Assert.Equal(0, model.StreamCalls);
        Assert.Equal(0, model.CountCalls);
        Assert.Equal(0, model.IteratorFinallys);
        Assert.Empty(model.Requests);
    }

    private static void AssertRestoredState(Agent target)
    {
        Assert.Equal(OwnerId, target.Id);
        Assert.Equal("stored-system", target.SystemPrompt);
        Assert.True(target.State.Get<bool>("seedOnly"));
        Assert.Equal("seedOnly", Assert.Single(target.State.GetAll()).Key);
        Assert.Equal(42, target.ModelState.Get<int>("seedProvider"));
        Assert.Equal("seedProvider", Assert.Single(target.ModelState.GetAll()).Key);
    }

    private static void AssertRestoredIndependent(Agent target, InterruptState original)
    {
        AssertRestoredState(target);
        var retained = Assert.Single(target.Messages);
        Assert.Equal("retained-user", retained.TrackingId);
        Assert.Equal("retained ordinary", retained.Text);
        var pending = Assert.IsType<InterruptState>(target.InterruptState);
        Assert.True(pending.AssistantMessageIsIndependent);
        Assert.Equal(original.AssistantMessage.TrackingId, pending.AssistantMessage.TrackingId);
        Assert.Equal(Assert.Single(original.Interrupts).Id, Assert.Single(pending.Interrupts).Id);
        Assert.Equal("pending-Y", Assert.Single(pending.AssistantMessage.Content.OfType<ToolUseBlock>()).ToolUseId);
        Assert.Empty(pending.CompletedToolResults);
    }

    private static void AssertProjectedRequest(IReadOnlyList<Message> messages, InterruptState original, string? errorReason)
    {
        Assert.Collection(messages,
            message =>
            {
                Assert.Equal(Role.User, message.Role);
                Assert.Equal("retained-user", message.TrackingId);
                Assert.Equal("retained ordinary", message.Text);
            },
            message =>
            {
                Assert.Equal(Role.Assistant, message.Role);
                Assert.Equal(original.AssistantMessage.TrackingId, message.TrackingId);
                Assert.Equal("pending-Y", Assert.Single(message.Content.OfType<ToolUseBlock>()).ToolUseId);
            },
            message =>
            {
                Assert.Equal(Role.User, message.Role);
                var result = Assert.Single(message.Content.OfType<ToolResultBlock>());
                Assert.Equal("pending-Y", result.ToolUseId);
                Assert.Equal(errorReason ?? "approved", result.Content);
                Assert.Equal(errorReason is null ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
            });
    }

    private static SessionSnapshot EnvelopeSnapshot(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return JsonSerializer.Deserialize<SessionSnapshot>(document.RootElement.GetProperty("snapshot").GetRawText(), Web)!;
    }

    private static byte[] ReplaceEnvelopeSnapshot(byte[] original, SessionSnapshot snapshot)
    {
        using var document = JsonDocument.Parse(original);
        var fields = document.RootElement.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone(), StringComparer.Ordinal);
        fields["snapshot"] = Element(snapshot);
        return JsonSerializer.SerializeToUtf8Bytes(fields, Web);
    }

    private static void AssertEnvelopeIdentityHeld(byte[] original, byte[] current)
    {
        using var before = JsonDocument.Parse(original);
        using var after = JsonDocument.Parse(current);
        Assert.Equal(before.RootElement.EnumerateObject().Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal),
            after.RootElement.EnumerateObject().Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal));
        foreach (var field in before.RootElement.EnumerateObject())
            if (field.Name != "snapshot")
                Assert.True(JsonElement.DeepEquals(field.Value, after.RootElement.GetProperty(field.Name)), field.Name);
        Assert.Equal(1, after.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(SessionId, after.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(OwnerId, after.RootElement.GetProperty("agentId").GetString());
        Assert.Equal(JsonValueKind.Null, after.RootElement.GetProperty("snapshotId").ValueKind);
        Assert.False(after.RootElement.GetProperty("stateful").GetBoolean());
    }

    private static InvocationOptions Answer(PauseSeed seed) => new() { InterruptResponses = [new(seed.QuestionId, Json("true"))] };
    private static Message OrdinaryHistory() => new(Role.User, [new TextBlock("retained ordinary")], "retained-user");
    private static FunctionTool Approve(Func<ToolContext, string> body)
        => new(new("approve", "approve", Json("{\"type\":\"object\"}")), (_, context, _) => new ValueTask<string>(body(context)));
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Web);
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed record PauseSeed(SessionSnapshot Session, string QuestionId, FiniteModel Model, int ProducerEffects);
    private sealed record CatalogSeed(SessionSnapshot Session, string Key, byte[] Envelope);
    private sealed class Probe
    {
        internal int Initialized, BeforeInvocation, BeforeModel, Added, ToolEffects;
    }

    private sealed class OwnedResources
    {
        private readonly List<IAsyncDisposable> _resources = [];
        private readonly List<Agent> _agents = [];
        private readonly List<FiniteModel> _models = [];
        private readonly List<RecordingStorage> _stores = [];
        private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        internal string Directory { get; }
        internal OwnedResources()
            => Directory = Path.GetFullPath(Path.Combine(_temporaryRoot, "strands-independent-boundaries-" + Guid.NewGuid().ToString("N")));
        internal FiniteModel Model(bool pause)
        {
            var model = new FiniteModel(pause); _models.Add(model); _resources.Add(model); return model;
        }
        internal Agent Agent(Agent agent) { _agents.Add(agent); return agent; }
        internal RecordingStorage Storage()
        {
            var storage = new RecordingStorage(); _stores.Add(storage); _resources.Add(storage); return storage;
        }
        internal async ValueTask AssertBorrowedResourcesRemainOpenAsync()
        {
            for (var index = _agents.Count - 1; index >= 0; index--) await _agents[index].DisposeAsync();
            _agents.Clear();
            foreach (var model in _models) Assert.Equal(0, model.Disposals);
            foreach (var storage in _stores) Assert.Equal(0, storage.Disposals);
        }
        internal async ValueTask DisposeAsync(Exception? primary)
        {
            Exception? failure = primary;
            for (var index = _agents.Count - 1; index >= 0; index--)
            {
                try { await _agents[index].DisposeAsync(); }
                catch (Exception error) { failure = Attach(failure, error, "agent." + index); }
            }
            _agents.Clear();
            for (var index = _resources.Count - 1; index >= 0; index--)
            {
                try { await _resources[index].DisposeAsync(); }
                catch (Exception error) { failure = Attach(failure, error, "resource." + index); }
            }
            try
            {
                var resolved = Path.GetFullPath(Directory);
                var prefix = _temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!string.Equals(resolved, Directory, StringComparison.Ordinal) || !resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(resolved).StartsWith("strands-independent-boundaries-", StringComparison.Ordinal))
                    throw new InvalidOperationException("The fixture may clean only its resolved unique temporary directory.");
                if (System.IO.Directory.Exists(resolved))
                {
                    if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("The fixture temporary root cannot be a reparse point.");
                    var pending = new Stack<string>();
                    pending.Push(resolved);
                    while (pending.Count > 0)
                        foreach (var entry in System.IO.Directory.GetFileSystemEntries(pending.Pop(), "*", SearchOption.TopDirectoryOnly))
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                                throw new InvalidOperationException("Fixture cleanup cannot traverse a reparse point.");
                            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        }
                    System.IO.Directory.Delete(resolved, recursive: true);
                }
            }
            catch (Exception error) { failure = Attach(failure, error, "directory"); }
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private static Exception Attach(Exception? primary, Exception cleanup, string key)
        {
            if (primary is null) return cleanup;
            if (!ReferenceEquals(primary, cleanup)) primary.Data["IndependentPersistence.Cleanup." + key] = cleanup;
            return primary;
        }
    }

    private sealed class MemoryStore : ISessionStore
    {
        internal SessionSnapshot? LastSaved { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return new((SessionSnapshot?)null); }
        public ValueTask SaveAsync(string id, SessionSnapshot snapshot, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            LastSaved = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot, Web), Web)!;
            return default;
        }
    }

    private sealed class RecordingStorage : IStorage, IStorageCharacteristics, IAsyncDisposable
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        public bool IsEphemeral => true;
        public bool IsNamespaced => true;
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal int Disposals { get; private set; }
        internal IReadOnlyList<string> Keys => _values.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        internal byte[] GetObserved(string key) => (byte[])_values[key].Clone();
        internal void ReplaceObserved(string key, byte[] bytes)
        {
            Assert.True(_values.ContainsKey(key));
            _values[key] = (byte[])bytes.Clone();
        }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Writes++; _values[key] = (byte[])data.Clone(); return default; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Reads++; return new(_values.TryGetValue(key, out var bytes) ? (byte[])bytes.Clone() : null); }
        public ValueTask DeleteAsync(string key, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); _values.Remove(key); return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return new(Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class FiniteModel(bool pause) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => false;
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int IteratorFinallys { get; private set; }
        internal int Disposals { get; private set; }
        internal List<IReadOnlyList<Message>> Requests { get; } = [];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            if (StreamCalls != 1) throw new InvalidOperationException("Each finite model permits one provider request.");
            Requests.Add(request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray());
            return Produce(cancellationToken);
        }
        private async IAsyncEnumerable<ModelStreamEvent> Produce([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                await Task.CompletedTask;
                token.ThrowIfCancellationRequested();
                yield return new ModelMessageStartEvent();
                if (pause)
                {
                    yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("pending-Y", "approve", Json("{}")));
                    yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{}");
                }
                else
                {
                    yield return new ModelContentBlockStartEvent(0);
                    yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
                }
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(pause ? StopReasons.ToolUse : StopReasons.EndTurn)
                { ModelState = new Dictionary<string, JsonElement> { ["seedProvider"] = Json("42") } };
            }
            finally { IteratorFinallys++; }
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); CountCalls++; return new(9); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
