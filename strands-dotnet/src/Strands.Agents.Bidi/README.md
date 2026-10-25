# Strands.Agents.Bidi

This optional alpha package provides persistent bidirectional model contracts
and native OpenAI Realtime and Google Gemini Live WebSocket adapters. It follows Python
`strands.bidi.models.openai` and `strands.bidi.models.google` at baseline
`3bd98e618320f0f286acbfb2252f72388030421b`; that baseline has no TypeScript
bidirectional implementation. No package has been published. The main solution
includes this bounded model/transport and agent-loop slice; `Strands.Bidi.slnx`
provides an independent build and test scope.

The package targets `netstandard2.0;net8.0;net10.0`. Existing Framework 4.8
applications select its .NET Standard asset. Source and examples use a modern
compiler. Framework projects enable binding redirects and reference
`System.Net.Http`.

## Lifecycle and defaults

`IBidiModel` has separate `StartAsync`, `SendAsync`, `ReceiveAsync`, and
`StopAsync` operations. `IRestartableBidiModel` adds `RestartAsync` and
`IAudioBidiModel` exposes the audio configuration. These contracts model a
duplex connection rather than a normal `IModel` response stream.

```csharp
await using var model = new OpenAIRealtimeModel(new()
{
    ModelId = "your-realtime-model", TranscriptionModelId = null
}); // OPENAI_API_KEY supplies the credential.
await model.StartAsync(new() { SystemPrompt = "Answer clearly and briefly." }, ct);
await model.SendAsync(new BidiMessage([new TextBlock("Hello")]), ct);
await foreach (var output in model.ReceiveAsync(ct))
{
    if (output is BidiTextDeltaEvent text) Console.Write(text.Delta);
    if (output is BidiResponseStopEvent) break;
}
await model.StopAsync(ct);
```

`ModelId` and an explicit nullable `TranscriptionModelId` are required. An API
key can be supplied directly or through `OPENAI_API_KEY`; organization/project
also use their corresponding OpenAI environment variables. Defaults match the
pinned source: voice `alloy`, audio output modality, mono 24 kHz PCM input and
output, server VAD threshold `0.5`, prefix padding `300 ms`, silence duration
`500 ms`, automatic response creation and interruption. `Params` recursively
override session defaults, including explicit JSON null. Unsupported audio
formats and disabled automatic VAD behavior are rejected before connection.

The connection timeout is 3,000 seconds. `Connection.RestartAfter` defaults to
timeout minus 300 seconds and `AutoRestart` is true. The agent loop consumes
these declarations; the model adapter alone does not schedule restarts.
`GetConfig` returns a detached immutable options snapshot;
validated `UpdateConfig` changes apply to the next connection. A connection
keeps the configuration captured when it started.

The adapter owns its `ClientWebSocket`. One receiver is permitted per
connection, while sends are serialized against response scheduling. Disposing
a receiver releases the reader guard without closing the connection. Cancelling
a native read aborts that socket and preserves the caller token; explicitly
stop/start or restart to continue. Stop/disposal release owned transport
resources, and disposal is terminal and idempotent.

## Native behavior and limits

The adapter sends native session, input-text/image, function-call-output, audio
append, and response-create events. It normalizes response/text/audio/transcript
boundaries, completed tool calls, barge-in, and usage. Unknown events and
unhandled native error objects remain available as detached `BidiNativeEvent`
diagnostics, matching the source's nonfatal handling without losing the payload.
Tool execution remains the caller's responsibility.

Acknowledgements, speech activity, active responses, outstanding tool calls,
and late inputs determine when a continuation is requested. A result batch is
validated in full before transmission and creates one continuation after all
outstanding calls settle. Restart replays supplied history, preserves pending
tool identifiers before publishing the new connection, and sends the source's
best-effort re-anchor message. Pending calls also survive a failed reconnect
followed by another restart. Explicit stop ends that recovery state.

Supported live user content is text and inline images. Tool results contain
text/JSON. Unsupported rich blocks, URL/S3 image sources, and ambiguous
32-character history call-ID collisions fail explicitly. Unlike the pinned
history formatter, native replay retains supported inline user images. JSON
and collections are detached at public boundaries. Repeated call IDs,
ambiguous duplicate JSON fields, invalid counters, and overflowing usage fail
with typed model errors. Incoming and outgoing JSON events have a configurable
16 MiB bound; close-output has a configurable five-second timeout. These are
explicit native robustness limits.

## Google Gemini Live

`GoogleGeminiLiveModel` supports the native Gemini Developer and Vertex AI Live
WebSocket protocols. Developer mode is the default: supply an explicit `ModelId`
and `ApiKey`, or resolve the key through `GOOGLE_API_KEY`, then `GEMINI_API_KEY`.
An optional `Endpoint` changes the Developer WebSocket route. The adapter owns
each socket. API keys are omitted from options summaries.

Vertex mode requires `UseVertexAi = true`, an explicit `ws`/`wss` `Endpoint`, a
fully qualified `projects/{project}/locations/{location}/publishers/google/models/{model}`
`ModelId`, and a borrowed `AccessTokenProvider`. The callback resolves a fresh
OAuth bearer token for every physical connection attempt, including resumed
connections and fresh fallback. Vertex ignores `ApiKey` and the Developer key
environment variables, removes `key` query parameters, and sends the token only
in the WebSocket `Authorization` header. Bidi has no mandatory Google credential
dependency; [official credential composition](../../docs/GOOGLE_AUTH.md#vertex-ai-live-websockets)
uses Google's `ITokenAccess` directly.

Credentials are resolved before replacing a healthy connection. A callback
failure cannot trigger service-resumption fallback. The callback receives a
token linked to the caller, model disposal, and `SetupTimeout`. Cancellation
physically awaits the callback before returning and rechecks cancellation before
opening a socket. A callback that ignores cancellation can therefore delay
shutdown beyond the cooperative setup deadline. Callbacks cannot await Start,
Restart, Stop, or disposal on their own model. `AccessTokenProvider` is omitted
from JSON; returned tokens are not retained in the public configuration.

```csharp
await using var live = new GoogleGeminiLiveModel(new()
{
    ModelId = "your-live-model", Voice = "Aoede"
});
await live.StartAsync(new() { SystemPrompt = "Answer briefly." }, ct);
await live.SendAsync(new BidiMessage([new TextBlock("Hello")]), ct);
await foreach (var output in live.ReceiveAsync(ct))
    if (output is BidiResponseStopEvent) break;
await live.StopAsync(ct);
```

Pinned defaults are AUDIO responses, input/output transcription, sliding-window
compression, session resumption, mono PCM16 input at 16kHz and output at 24kHz.
`InputSampleRate` accepts another positive input rate. `Voice` is optional.
The automatic restart interval defaults to 540 seconds. `Params` contains
native **camelCase setup fields**, including `generationConfig`; these fields
recursively override defaults and direct session settings. This is the raw wire
equivalent of Python GenAI SDK configuration, rather than Python keyword names.
Immutable config updates apply to the next connection; active audio metadata
continues to describe the active connection.

Start waits for `setupComplete` before sending history or accepting live input.
Fresh history follows Python's text-only seed behavior, mapping assistant to
`model`; nontext history is omitted. Vertex sends that `clientContent` history
without Developer-only `historyConfig`; explicitly supplying `Params.historyConfig`
in Vertex mode fails before connection. Live user messages support text and inline
images, audio deltas use `realtimeInput`, and grouped tool results support text
and JSON. Tool parameters use Google's structured Schema type enums, including
nested properties/items, as in the pinned GenAI formatter. Unsupported sources/results fail before any batch write. Pending
tool names survive restart. A tracked resumable handle avoids history replay;
a rejected handle falls back to a fresh connection and text history, matching
the source. This fallback cannot recreate nontext provider context.

Response, reasoning, visible text, audio, input/output transcript and usage
events follow native turn boundaries. `generationComplete` closes audio;
`turnComplete` closes the response. Interruption emits barge-in while preserving
a newer user utterance. GoAway requests a connection restart. Cancellation
checks also apply between normalized events from one frame. Opaque part
metadata remains available as detached `BidiNativeEvent` diagnostics. A native
tool cancellation is surfaced and its later result is rejected; individual
already-running local tool bodies are not canceled by that notification.
Setup acknowledgement defaults to a thirty-second deadline; JSON events use
the configurable 16MiB bound and shutdown uses the five-second close timeout.

The protocol is documented in Google's [Live WebSocket reference](https://ai.google.dev/api/live)
and [WebSocket guide](https://ai.google.dev/gemini-api/docs/live-api/get-started-websocket).

## Agent composition

`BidiAgent` starts a background model reader, records user messages and completed
text/reasoning/transcripts, runs tool batches concurrently, sends real results,
and exposes bounded streaming output through `ReceiveAsync`. It composes a
supplied real core `Agent` through `BidiAgentOptions.ToolAgent`; the duplex
model remains separate. The core agent initializes its configured tools and
provides actual schema validation, tool selection/input/result hooks, rich
results, and the shared streaming-tool runner. Its ordinary chat model is never
invoked by the bidirectional agent. Constructing a standalone default tool host
remains pending a proper shared host contract.

`StartAsync` retains the caller's invocation-state dictionary by reference and
adds no SDK entries. Progress events retain the effective hook-selected tool
call while preserving the original provider identifier. Tool interruptions are
refused through the core direct-tool policy, matching the pinned bidi limitation.
The core tool hooks remain separate from the bidirectional lifecycle registry.

The agent automatically recovers from `BidiConnectionTimeoutException` unless
the model's current `AutoRestart` policy is false. A positive `RestartAfter`
also schedules a warning ten seconds before replacement. The deadline waits
up to ten seconds for an active or unanswered turn to finish. A forced swap
sets `BidiConnectionRestartEvent.TurnInterrupted`; its `Reason` distinguishes
timeouts from scheduled swaps. The warning remains ordered before replacement
even when the receiver is backpressured. Null or nonpositive deadlines disable
the proactive timer while retaining reactive recovery.

Connection replacement serializes with sends, passes detached history and tool
specifications, and keeps the same caller-state object. It uses
`IRestartableBidiModel.RestartAsync` when available, otherwise stop/start.
Generation checks fence superseded readers and their expected transport errors;
an old reader has a two-second close grace period before cancellation. Real
iterator-disposal failures remain observable. In-flight tools continue without
replaying their bodies, and their results wait behind the connection swap.
Unsent result slots are omitted from replay until the real output has been
transmitted; local paired history remains intact. A failed swap unblocks queued
sends with the original failure. Stop cancels and joins the timer and any swap.

History reserves one assistant/result pair per batch and replaces its pending
result slot when tools settle. This deliberately avoids the pinned loop's
second duplicate-call exchange: completed side effects remain in one
committed history pair, including when shutdown cancels a sibling. Incomplete text and
unavailable transcripts carry explicit source-style metadata.

Both model and tool-agent object lifetimes remain borrowed by default; the
conversation still stops the connection it started. `OwnsModel` and
`OwnsToolAgent` explicitly transfer disposal. Stop cancels and joins running
work before completing the receiver; cancellation after stop admission cannot
skip connection cleanup. Disposal is terminal/idempotent, attempts all
transferred resources, and retains secondary failures under
`Exception.Data["Strands.CleanupErrors"]`. Initialization, model lifecycle, and
tool callbacks cannot await lifecycle operations on their own conversation.
One receiver consumes the bounded output queue at a time.

## Lifecycle hooks

`BidiAgent.Hooks` provides typed, awaited callbacks for the pinned Python
notifications: `BidiAgentStopEvent`, `BidiResponseStopHookEvent`,
`BidiBargeInHookEvent`, `BidiBeforeConnectionRestartEvent`, and
`BidiAfterConnectionRestartEvent`. The `HookEvent` suffix distinguishes the
response and barge-in notifications from public model output records. An
optional `BidiAgentOptions.Hooks` supplies a borrowed application registry;
disposal leaves its registrations intact. Registration handles remove only
their own callbacks, and each dispatch observes a stable registration snapshot.

Lower priorities run first. Stop reverses registration order within each
priority and attempts every callback after model, reader, and tool cleanup.
Other notifications retain registration order and stop at the first failure.
A response-stop hook requires an actual model completion; connection failure,
EOF, and shutdown do not invent one. Barge-in and response-stop callbacks run
before their corresponding public output is queued.

Before-restart callbacks see the source reason and original timeout before
any native swap. A failure prevents the swap and releases queued sends with
that failure. After-restart callbacks run after an attempted swap, including
failure; a native failure remains primary if its callback also fails. Stop and
after-restart callbacks use `CancellationToken.None` so admitted cleanup is
attempted; other notifications receive the conversation's cancellation token.
Callbacks cannot await their own lifecycle or receive loop. Restart callbacks
also cannot await sends on their own gated connection. These checks prevent
self-await deadlocks without transferring callback or dependency ownership.

## Snapshots and latest-session persistence

`BidiAgent.State` holds independent application JSON state. `SessionId` is an
opaque key, generated as eight hexadecimal characters when omitted.
`InitializeAsync` restores a configured `ISessionStore` once and then awaits
`BidiInitializedEvent` before any model connection. Initialization failure is
cached. A failed session read cannot overwrite that unread session. Store and
callback objects remain borrowed, and disposal joins unfinished physical
initialization even when the caller has already canceled its wait.

`TakeSnapshotAsync(SnapshotOptions)` and `LoadSnapshotAsync(AgentSnapshot)` reuse
the core JSON envelope. The Session preset selects messages and application
state; include `SnapshotFields.SystemPrompt` to select the structured prompt too.
Explicit null clears a restored prompt. Absent fields retain their current
values; unknown fields and AppData are ignored on load. The whole detached
snapshot is validated before initialization I/O or mutation. Capture is allowed
during a live conversation, while load requires a stopped connection and
completed initialization. Model-state and interruption fields are not Bidi
snapshot capabilities and cannot be selected.

`BidiAgentOptions.SystemPromptContent` accepts text and cache-point blocks,
mutually exclusive with a nonnull `SystemPrompt`. The agent copies the blocks;
`SystemPromptContent` returns a detached list. `SetSystemPrompt(string?)` and
`SetSystemPromptContent(IReadOnlyList<ContentBlock>?)` replace the prompt, with
null explicitly clearing it. Text blocks are joined with newlines for
`SystemPrompt`; an empty or cache-only list has no text projection, while an
empty text block projects to an empty string. A mutable prompt change takes
effect on the next provider start or restart. Providers receive that text
projection, as in the pinned Python Bidi implementation; retaining cache points
does not enable provider-side prompt caching.

New snapshots retain the typed block array, and load also accepts earlier
string/null prompts. Native `SessionSnapshot` keeps the existing string and
presence marker alongside optional `SystemPromptContent`; a structured field
requires the marker and a matching string projection. This preserves old files
and rejects conflicting stored representations before applying any fields.
The ordinary core `Agent` does not gain structured-prompt configuration through
this persistence field.

```csharp
await using var conversation = new BidiAgent(new()
{
    Model = liveModel, ToolAgent = toolAgent,
    SessionStore = new FileSessionStore("./sessions"), SessionId = "conversation-1"
});
await conversation.InitializeAsync(ct);
conversation.State.Set("tenant", "example");
var snapshot = await conversation.TakeSnapshotAsync(new()
{
    Preset = SnapshotPreset.Session, Include = SnapshotFields.SystemPrompt
}, ct);
await conversation.SaveSessionAsync(ct);
```

`BidiMessageAddedEvent` and `BidiMessageUpdatedEvent` observe accepted history
mutations through the same awaited registry. Messages are detached; an update
retains the original tracking identifier. A tool batch reserves its complete
assistant/result pair atomically before callbacks and body execution. Text,
reasoning and transcription reserve empty pending messages and later replace
them with complete or explicitly incomplete content. Notifications finish
before their corresponding public output. An observer failure retains the
accepted mutation and still attempts its automatic save.

`BidiSessionSaveStrategy.Message` is the default: save on message addition or
replacement, plus stop and before connection restart. `Stop` saves after stop
callbacks and after before-restart callbacks, before the actual swap.
Response completion alone is not a Stop-policy save point. Explicit
`SaveSessionAsync` works under either policy. Writes are serialized and capture
the latest history/state after admission; no cross-process compare-and-swap or
transactional provider-delivery guarantee is implied.

Restored pending content becomes incomplete before connecting. An abandoned
tool request or dispatch receives an explicit error with an unknown outcome;
its body is never resumed. Duplicate identifiers in separate valid historical
tool batches are retained, while a model cannot execute an identifier already
present in restored history. These native safeguards do not recover active
tool bodies, sockets, or provider resumption handles.

## Immutable snapshot catalogs

`BidiSnapshotSessionManager` adds a latest snapshot and immutable checkpoint
catalog through an explicit, borrowed `IStorage`. Configure it through
`BidiAgentOptions.SessionManager`; configuring `SessionStore` at the same time
fails before initialization. Its `SessionId` defaults to `default-session` and
the agent's `AgentId` defaults to `default`. These portable path components
accept ASCII letters, digits, underscores, hyphens and dots, excluding dot
segments. A configured agent SessionId must match its manager.

```csharp
var sessions = new BidiSnapshotSessionManager(
    new LocalFileStorage("./conversation-checkpoints"), "conversation",
    BidiSessionSaveStrategy.Trigger,
    snapshotTrigger: agent => agent.State.Get<bool>("checkpoint"));
// Supply sessions as BidiAgentOptions.SessionManager.
var handle = await sessions.SaveSnapshotAsync(agent, isLatest: false, ct);
var page = await sessions.ListSnapshotIdsAsync(agent, limit: 20, startAfter: null, ct);
await sessions.RestoreSnapshotAsync(agent, handle, ct); // agent must be stopped
```

The manager's `saveLatestOn` policy defaults to `Message`; `Stop` saves after
stop callbacks and before-restart callbacks. `Trigger` skips automatic latest
writes unless the callback fires or fails. The callback runs only at those
stop/restart boundaries, after application callbacks. Response completion
does not evaluate it. A true callback captures once, writes the immutable
snapshot first, then writes the identical bytes to latest. An immutable-write
failure leaves latest unchanged; a latest-write failure can leave a valid
immutable entry. Trigger exceptions skip immutable creation and still attempt
latest, with best-effort diagnostics. Storage failures remain observable.

`SaveSnapshotAsync(isLatest: false)` returns an opaque canonical UUIDv7 handle;
it does not replace latest. `isLatest: true` explicitly replaces latest and
returns null. Listing is oldest first, with an exclusive `startAfter` cursor
and optional limit. Invalid handles fail before storage access. Missing
snapshots return false. Restores validate the complete detached envelope
before changing history, state or prompt. `DeleteSessionAsync` deletes the
selected session's catalog across its agent scopes. Raw storage is namespaced
under `session`; an existing namespaced view is used directly.

Catalog writes drain before agent disposal and remain serialized per agent.
The manager and its storage are borrowed. These are native snapshots, with
no cross-language serialization or cross-process transaction guarantee.
Context stash persistence, active tool-body recovery, provider resumption,
and executable tool serialization are outside this catalog's scope.

## Running borrowed input and output streams

`RunAsync(inputs, outputs, invocationState, cancellationToken)` connects the
conversation and manages `IBidiInputStream` and `IBidiOutputStream` lifecycles.
Their shared `IBidiStream` contract provides `StartAsync(agent, token)` and
`StopAsync(token)`. Inputs return one `IBidiInput` from each `ReadAsync` call;
outputs consume one `IAgentStreamEvent` through `WriteAsync`. `BidiInputStream`
and `BidiOutputStream` adapt application callables with optional start/stop
callbacks. No audio device or foreign runtime is required by these contracts.

Inputs read concurrently. Output consumers run concurrently for each event;
the run awaits all consumers before advancing to the next event. It reserves
the conversation's receiver before starting I/O. Automatic connection restarts
keep the same stream lifecycles and caller-state object. Model EOF ends the run;
source/sink failure or caller cancellation stops its remaining work. Null or
unsupported inputs fail explicitly rather than acting as an undocumented EOF.

Stream startup follows input then output list order. Entered lifecycles,
including a stream whose start failed, stop in reverse order with
`CancellationToken.None`. Unentered streams and preexisting conversations are
left intact after failed admission. Streams remain borrowed: the run calls
their lifecycle methods and does not dispose their objects.

Cancellation requests alone do not complete the run. Stream stop callbacks
must unblock pending I/O, or callbacks must honor their cancellation token.
The agent joins physical I/O before stop hooks, external stop, or disposal
complete. It preserves the original failure and retains additional cleanup
failures in `Exception.Data["Strands.CleanupErrors"]`. Caller cancellation
retains the exact caller token. Stream callbacks cannot await their own agent
lifecycle or receive loop, and native model-send callbacks cannot recursively
send or stop their own connection.

## Scope and validation

Agent composition uses an explicitly supplied, borrowed core `ToolAgent` so the
application controls its model, tools and ownership. Device input/output is
available through the optional [Bidi Audio package](../Strands.Agents.Bidi.Audio/README.md).
[Nova Sonic](../Strands.Agents.Bedrock/README.md) is provided by the Bedrock package.
OpenAI reconnect restores supported local history; provider session-handle
resumption is not exposed by that adapter.

The independent `Strands.Bidi.slnx` scope contains real TCP WebSocket,
core-tool, snapshot and file-session cases. Source-specific test results,
platforms, normal .NET Standard asset selection and package consumers are
recorded in [validation results](../../docs/VALIDATION_RESULTS.md). A local
loopback proof does not establish authenticated provider or audio-device
execution. Release acceptance remains a separate contribution gate.

Repeat packed consumption with
`validation/Test-Package.ps1 -PackageDirectory <feed>`. The script builds all
three consumer targets, runs modern targets on every OS and Framework on
Windows, uses an isolated sibling package cache, and retains diagnostics.
