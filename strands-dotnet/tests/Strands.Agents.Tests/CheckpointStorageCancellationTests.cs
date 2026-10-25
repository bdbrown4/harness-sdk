using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class CheckpointStorageCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringReceiptSaveKeepsAcceptedProviderStateAndIndependentFailureAuthority(bool failSave)
    {
        using var caller = new CancellationTokenSource();
        var failure = failSave ? new IOException("independent receipt save failed") : null;
        var store = new CancellingStore(caller.Cancel, failure);
        var model = new StatefulModel();
        var effects = 0;
        await using var agent = new Agent(new()
        {
            Model = model,
            CheckpointBehavior = CheckpointBehavior.TypeScriptRegenerate,
            SessionStore = store,
            SessionId = "session",
            Tools = [new FunctionTool(new("effect", "Record an effect", JsonSerializer.SerializeToElement(new { type = "object" })),
                (_, _, _) => { effects++; return new ValueTask<string>("accepted"); })]
        });

        if (failure is not null)
        {
            var actual = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question", cancellationToken: caller.Token));
            Assert.Same(failure, actual);
        }
        else
        {
            var result = await agent.InvokeAsync("question", cancellationToken: caller.Token);
            Assert.Equal(StopReasons.Cancelled, result.StopReason);
            Assert.Null(result.Checkpoint);
        }

        Assert.Equal(0, effects);
        Assert.Equal(1, model.Requests);
        Assert.Equal(1, model.Disposals);
        Assert.Equal("state-1", agent.ModelState.GetAll()["responseId"].GetString());
        var saved = Assert.IsType<SessionSnapshot>(await store.LoadAsync("session"));
        Assert.NotNull(saved.ModelInputReceipt);
        Assert.Equal(1, saved.ModelInputReceipt.ConsumedMessageCount);
        Assert.Equal(Assert.Single(agent.Messages).TrackingId, saved.ModelInputReceipt.LastConsumedTrackingId);
        Assert.Equal("state-1", saved.ModelState!["responseId"].GetString());
        Assert.Equal("question", Assert.Single(saved.Messages).Text);
    }

    private sealed class CancellingStore(Action cancel, Exception? failure) : ISessionStore
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        private byte[]? _bytes;
        private bool _signaled;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new(_bytes is null ? null : JsonSerializer.Deserialize<SessionSnapshot>(_bytes, Web)); }
        public async ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.ModelInputReceipt is not null && !_signaled)
            {
                _signaled = true;
                await Task.Yield();
                cancel();
                if (failure is not null) throw failure;
            }
            _bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Web);
        }
    }

    private sealed class StatefulModel : IStatefulModel
    {
        public bool Stateful => true;
        internal int Requests { get; private set; }
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            try
            {
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("effect-1", "effect", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse)
                { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("state-1") } };
                await Task.CompletedTask;
            }
            finally { Disposals++; }
        }
    }
}
