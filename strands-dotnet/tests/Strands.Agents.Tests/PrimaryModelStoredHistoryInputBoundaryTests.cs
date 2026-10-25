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

public sealed class PrimaryModelStoredHistoryInputBoundaryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("empty")]
    [InlineData("ordinary")]
    [InlineData("python-prefix")]
    public async Task IncomingOrphanCannotCreateOrExtendTheOwnersStoredPythonPrefix(string historyKind)
    {
        Message[] messages = historyKind switch
        {
            "empty" => [],
            "ordinary" => [new Message(Role.User, [new TextBlock("owned input")], "owned-user")],
            _ => [new Message(Role.User, [new ToolResultBlock("completed-call", "already completed")], "owned-result")]
        };
        var initial = new SessionSnapshot(1, messages)
        {
            HistoryProfile = ModelHistoryBehavior.Python,
            State = new Dictionary<string, JsonElement> { ["saved"] = Json("true") },
            ModelState = new Dictionary<string, JsonElement> { ["responseId"] = Json("\"S1\"") }
        };
        var files = new OwnedFiles();
        var model = new UnenteredModel();
        var hooks = new HookRegistry();
        var initialized = 0;
        var before = 0;
        var added = 0;
        Exception? afterError = null;
        hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            before++;
            Assert.Equal("manual input", Assert.Single(value.Messages!).Text);
            value.Messages = [new Message(Role.User, [new ToolResultBlock("incoming-orphan", "must not be accepted")], "incoming-result")];
            return default;
        });
        hooks.Register<MessageAddedEvent>((_, _) => { added++; return default; });
        hooks.Register<AfterInvocationEvent>((value, _) => { afterError = value.Error; return default; });
        Agent? target = null;
        Exception? primary = null;
        try
        {
            var store = new FileSessionStore(files.Path);
            await store.SaveAsync("owned-history", initial);
            var persisted = Assert.IsType<SessionSnapshot>(await new FileSessionStore(files.Path).LoadAsync("owned-history"));
            Assert.Equal(ModelHistoryBehavior.Python, persisted.HistoryProfile);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(messages, Web), JsonSerializer.SerializeToElement(persisted.Messages, Web)));
            target = new(new()
            {
                Id = "local-owner",
                Model = model,
                Hooks = hooks,
                ModelHistoryBehavior = ModelHistoryBehavior.Python,
                SessionStore = new FileSessionStore(files.Path),
                SessionId = "owned-history"
            });
            var rejected = await Assert.ThrowsAsync<InvalidDataException>(() => target.InvokeAsync("manual input"));
            Assert.Same(rejected, afterError);
            Assert.Equal(1, initialized);
            Assert.Equal(1, before);
            Assert.Equal(0, added);
            Assert.Equal(0, model.StreamCalls);
            Assert.Equal(0, model.CountCalls);
            Assert.Null(target.InterruptState);
            Assert.Equal("local-owner", target.Id);
            Assert.True(target.State.Get<bool>("saved"));
            Assert.Equal("S1", target.ModelState.Get<string>("responseId"));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(messages, Web), JsonSerializer.SerializeToElement(target.Messages, Web)));
            Assert.DoesNotContain(target.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.ToolUseId == "incoming-orphan");
            await target.DisposeAsync(); target = null;
            Assert.Equal(0, model.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model, files); }
    }

    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static async ValueTask DisposeOwned(Exception? primary, params IAsyncDisposable?[] resources)
    {
        Exception? failure = primary;
        var index = 0;
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try { await resource.DisposeAsync(); }
            catch (Exception error)
            {
                if (failure is null) failure = error;
                else if (!ReferenceEquals(failure, error)) failure.Data["StoredInput.Cleanup." + index++] = error;
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class UnenteredModel : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => false;
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int Disposals { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { StreamCalls++; throw new InvalidOperationException("Rejected input must not enter the provider."); }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { CountCalls++; throw new InvalidOperationException("Rejected input must not enter token counting."); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private sealed class OwnedFiles : IAsyncDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stored-history-input", Guid.NewGuid().ToString("N"));
        public ValueTask DisposeAsync()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned fixture files escaped task TEMP.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            return default;
        }
    }
}
