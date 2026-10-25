# Indexed sessions for native agents

`RepositorySessionManager` is an optional plugin over a borrowed `ISessionRepository`:

```csharp
var repository = new FileSessionRepository("./agent-sessions");
var sessions = new RepositorySessionManager("conversation-42", repository);
await using var agent = new Agent(new AgentOptions
{
    Id = "assistant",
    Model = model,
    Plugins = [sessions],
});
await agent.InvokeAsync("Continue the work.");
```

Construction performs no repository I/O. Initialization restores the selected agent and registers persistence callbacks once. An active ID cannot be bound twice to the same manager. Disposal or failed initialization releases that reservation without disposing the repository. Children omit the parent binding; an application can explicitly bind a child to an independent journal.

Accepted input, assistant, and tool-result messages receive numeric row identities through explicit framework append receipts. Tracking IDs are retained as message data and are not row keys. Existing native save paths persist application state, provider-independent model state, accepted interrupt responses, classic manager state, and dynamic string prompts. Unchanged metadata is not rewritten. A selected indexed manager conflicts with `SessionStore`, a differing `SessionId`, or the first-class `ContextManager`; these configurations fail before repository I/O. Public `LoadSnapshotAsync` cannot replace messages while a journal is bound. State-only loads remain available.

Stateful models use a distinct native metadata version with an explicit active-row projection, provider state and acknowledgement cursor. Confirmed server history stays in the audit archive; only unacknowledged local input is sent after restore. Native pending output preservation is stronger than the pinned Python repository manager's wholesale local-history discard. See [stateful indexed sessions](REPOSITORY_STATEFUL_SESSIONS.md) for validation, failure and compatibility boundaries.

## Ordering and recovery

The pinned Python repository manager restores after plugin initialization. Native Agent initialization already restores before plugins; this plugin preserves that ordering. A state-reading plugin sees restored application state and history, and its initialization writes are retained. The manager synchronizes those writes before ordinary `InitializedEvent` observers. A caller deliberately registering an earlier callback at the minimum integer priority still controls that callback's relative ordering. Pending persistence from an earlier invocation is retried at a private pre-invocation boundary before any `BeforeInvocationEvent` observer, including callbacks registered at that minimum priority.

Restoration validates native metadata, manager state, summary, effective history, and the pending interrupt ledger before replacing live fields. Indexed stateless histories opt into the pinned Python repair: remove orphaned tool results, fill missing nonterminal results, and discard stale result IDs while retaining ordinary content. The final pending tool batch remains for native Agent repair. No repair executes a tool body. Whole-snapshot stores retain their existing strict validation.

Classic restoration uses a removed **row count**, not a numeric message-ID threshold. It prepends the summarizer's saved previous summary once without appending a summary archive row. The next append ID comes from all archived rows even when the retained tail is empty. Explicit reduction receipts admit prefix removal or a generated summary followed by the retained suffix; repeated summarization replaces its previous synthetic summary without archiving it. Pinned non-prefix removal, live rich-result rewrites, and reduction of a repaired projection fail before changing live history or manager state. Externally replaced counters or summaries cannot publish an unrelated projection. Whole-snapshot sessions preserve complete active projections for those cases; this bounded journal is not complete classic projection persistence.

## Failed writes and redaction

The native metadata envelope requires a confirmed row frontier and records excluded provisional IDs. Accepted rows are written before publishing the frontier and state. Failed writes retain stable row receipts; retries do not repeat model or tool bodies. A fresh agent excludes rows beyond the confirmed frontier, and later appends never reuse their archived IDs. A failed abort retains its live pause and provisional result ID; retry can publish that result, while choosing resume excludes the abandoned abort row. Unversioned indexed records deliberately treat their listed rows as confirmed; damaged versioned native records cannot use that fallback.

This is a multi-record protocol, not a transaction, external-writer lease, or crash exactly-once guarantee. Metadata failure can leave archived unconfirmed rows. Persistent failure can leave accepted effects without a confirmed result; native pending-tool repair settles them without replaying bodies. Applications must serialize independent agents/processes sharing one agent identity. Backend exceptions remain observable, dirty state is confirmed only after successful writes, and caller-requested cancellation retains its token and independent cleanup diagnostics. A logically canceled invocation still joins admitted physical writes and cleanup before returning.

Provider input redaction uses explicit row receipts. Rows sharing the same live `Message` reference are updated together; equal-looking messages and duplicate tracking IDs are independent. The latest effective projection is restored by a fresh agent. Original audit messages remain accessible in `SessionMessageRecord.Message`; redaction is not erasure of those originals. Immutable native messages also cannot expose every Python foreign-mutable-object alias behavior. Synthetic summaries without an indexed row are not an indexed redaction target. Partial alias writes can be visible on the backend; retained operations retry without another model call, and the live replacement is committed when the complete prepared alias batch succeeds.

## Scope

The original journal increment implements ordinary native Core Agent sessions and bounded built-in classic reconstruction. The separate stateful extension adds active-row and provider acknowledgement restoration. Neither adds first-class context transient serialization, Bidi, Graph, Swarm, repository disk-format interoperability with Python, public alias mutation, or arbitrary public history replacement. Existing whole-snapshot sessions remain available for those supported native hosts. Each component's evidence and reviewed source identity remain separate; development tests do not establish hosted macOS or clean Framework 4.8 execution.
