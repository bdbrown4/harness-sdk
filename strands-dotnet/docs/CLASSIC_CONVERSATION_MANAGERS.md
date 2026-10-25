# Classic conversation managers

The `Strands.Agents.Conversation` namespace provides native stateful classic managers with Python's observable checkpoint and reduction behavior. Select them explicitly through `AgentOptions.ConversationManager`. The original `Strands.Agents.SlidingWindowConversationManager` remains a stateless model-visible projection and retains its API. A namespace alias avoids ambiguous type names:

```csharp
using Classic = Strands.Agents.Conversation;

await using var agent = new Agent(new()
{
    Model = model,
    ConversationManager = new Classic.SlidingWindowConversationManager(new()
    {
        WindowSize = 40,
        PerTurn = 3
    })
});
```

The existing native agent default remains no classic manager. An explicitly configured first-class `Context.ContextManager` takes precedence. A classic instance binds to one agent; default children receive fresh state and hooks. Models and an optional dedicated summarization agent remain borrowed. Supplying the same manager through both the option and `Plugins` registers it once.

`NullConversationManager` keeps history and rethrows the original reactive overflow. Classic `SlidingWindowConversationManager` defaults to forty messages and reactive tool-result truncation. Routine management happens after invocations; `PerTurn = 1` also manages before every model call, a positive N selects every Nth call, and null disables that cadence. The call counter advances only when cadence is enabled and survives restoration. A zero-sized window removes unpinned history. Explicit pins, `PinFirst`, and adjacent correlated tool-pair partners are protected.

On reactive overflow, the oldest eligible tool-result message is attempted first. The default Python text policy preserves the first and last 200 Unicode characters, uses its removed-character notice, and replaces images with Python's format/byte-count placeholder. Status, correlation, structured JSON and other rich content remain intact. If truncation cannot help, trimming finds a plain user boundary, then Python's complete tool-pair fallback.

Set `SlidingWindowConversationManagerOptions.ToolResultTruncationPolicy` to `ClassicToolResultTruncationPolicy.TypeScript` to select the pinned TypeScript **truncation policy only**. This replaces nested image/video and binary/S3 documents with format/source placeholders, including byte counts only for actual inline bytes. Text-source documents keep their document fields; ordered text segments are shortened individually. Large `JsonBlock` values become a size placeholder, while small JSON blocks keep their type and ordering. `StructuredContent`, result metadata, execution facts and other unsupported rich blocks remain intact. This option is inherited by default child managers and is configuration rather than serialized manager state.

That policy uses a 450 UTF-16-unit threshold and at most 200 units from each end, with the source XML-like removed-character marker. A completed preview naturally falls below the threshold. The native implementation moves cuts inward when needed to keep surrogate pairs valid and reports the actual removed UTF-16-unit count; it deliberately avoids JavaScript slicing's possible lone surrogate. JSON size counts compact native JSON with decoded Unicode and preserves native numeric representation/precision, rather than coercing values through JavaScript numbers. Placeholder format descriptions use the native MIME subtype; a nameless binary document uses `document` as its description. These are explicit native representation choices.

The option leaves routine management, pins, oldest-result selection, counters, trim boundaries and lifecycle unchanged. It does not implement TypeScript's separate tool-pair user-anchor fallback or claim complete TypeScript classic-manager equivalence. The first-class context strategies retain their existing token-budget previews and original-content retrieval behavior.

`SummarizingConversationManager` defaults to a 0.3 summary ratio and ten recent messages. Ratios are clamped to [0.1, 0.8]; complete tool boundaries can move the split as in Python. The manager calls `agent.AuxModel` directly with the actual old conversation, the pinned instructions, and the summarization request. `SummarizationSystemPrompt` can replace those instructions. Alternatively, `SummarizationAgent` invokes a borrowed, inactive agent and restores its prior messages/provider state and persisted session afterward; that option is mutually exclusive with an instruction override. The caller must keep that agent inactive throughout the summary operation. Only visible reply text enters the user-role summary. Reasoning and tool-use blocks cannot become user protocol content.

Summary replacement counts original messages, excluding a previous summary only when that summary is actually replaced. Model failures do not advance counters or replace history. These are native correctness guarantees beyond Python's counter mutation before its model call. Proactive summaries are best effort; reactive failures propagate. `ProactiveCompressionThreshold` enables model-counted or estimated utilization checks in (0, 1]; 0.7 corresponds to the source Boolean true default. Requested caller cancellation always propagates with its token and retains a different backend failure as the cause.

`GetState()` returns detached JSON with `__name__` and `removed_message_count`; sliding state also contains `model_call_count`, and summary state contains the previous native `summary_message`. `RestoreFromSession` validates the complete state before assignment. Missing old sliding cadence restores as zero. Its optional returned summary-prepend list is for append-only repository consumers. Native complete-history snapshots and ordinary sessions intentionally do not prepend it again.

`SnapshotFields.ConversationManagerState` selects classic state and participates in `All` and the native session preset. No manager means no fabricated checkpoint field. Missing or JSON-null manager state preserves the configured state. Nonnull state requires a configured matching manager. Manager-only changes trigger ordinary session saving, and failed saves remain dirty for retry. The positional `SessionSnapshot` constructor and deconstruction are unchanged; the new nullable field is additive. Its message representation remains the native format, independent of Python's wire schema.

Validation precedes initialization, history/application-state mutation and file creation. Snapshot restore cannot replace a manager during an admitted asynchronous reduction. Bound public manager restoration and explicit reductions require an inactive agent; invocation admission also rejects an admitted reduction. Registered lifecycle reductions receive a private, live receipt for their own manager and agent, which escaped work cannot reuse. Shutdown and disposal reject active reductions before closing owned resources. Reentrant reduction callbacks fail rather than awaiting themselves. Routine cleanup preserves unanswered human batches; explicit reduction cannot replace their history. These lifecycle protections are deliberate native safeguards.

This implementation does not add an append-only repository session backend, removed-offset message listing, or automatic repository summary prepend. TypeScript's user-anchor trim behavior remains a separate lifecycle gap. Bare first-class context retry/skip state remains transient, and ordinary stash checkpoints remain a separate prerequisite. This is not a full SDK/harness parity or live-provider verification claim.

Source baseline: `3bd98e618320f0f286acbfb2252f72388030421b`, Python `agent/conversation_manager/{conversation_manager,sliding_window_conversation_manager,summarizing_conversation_manager}.py`, shared compression/pin helpers, `agent.py` snapshot methods, and `repository_session_manager.py`. TypeScript classic managers are compared explicitly where their lifecycle and truncation contracts differ.
