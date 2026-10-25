# Bidirectional implementation notes

The reference baseline is `3bd98e618320f0f286acbfb2252f72388030421b`.
`strands-py/src/strands/bidi/models/model.py` defines a persistent duplex model
separately from ordinary model streaming. `models/openai.py` supplies native
Realtime wire handling and acknowledgement/response/tool scheduling.
`agent/loop.py` supplies background reading, content history, concurrent tools,
restart generations, and lifecycle hooks. `agent/_blocks.py` defines pending,
complete, and incomplete content history. There is no baseline TypeScript bidi
surface.

The native adapter uses `ClientWebSocket` directly. Its independent TCP test
peer performs the real RFC6455 HTTP upgrade, masking and frame fragmentation;
provider-format assertions do not call an internal encoder to produce expected
events. The package consumer repeats actual text/tool/audio exchanges through
the packed public API. No paid API or foreign runtime is involved in these
offline tests.

The initial agent composes a caller-supplied real core agent for tools. A narrow
internal dispatch entry preserves the public direct-tool admission, schema,
hook, cancellation, rich-result and interrupt-refusal behavior while retaining
the bidi caller-state reference and forwarding progress. The public core
`InvokeToolAsync` still copies its invocation-state dictionary and has no
streamed update callbacks. Existing direct-tool/interrupt-abort tests cover
this compatibility boundary. No placeholder chat model is created in production.

Explicit native differences from the pinned Python code are:

- Unsupported content fails instead of being discarded, including inline-image
  history replay preservation and ambiguous truncated call-ID rejection.
- A full input/result batch is validated before any item is transmitted.
- A failed reconnect retains outstanding tool calls for the next restart.
- Duplicate tool IDs and duplicate JSON fields are rejected before execution.
- Model JSON has a configurable 16 MiB event bound, and close-output is bounded.
- Caller-state dictionaries receive no `request_state` SDK entry.
- One reserved result message is replaced by actual settled results, rather than
  recording a second repeated-ID tool exchange after a dispatch placeholder.
- Successful sibling effects are retained in paired history during cancellation.
- Pending or completed-but-unsent result slots are excluded from reconnect
  replay until the actual output is transmitted. This prevents a dispatch
  placeholder from becoming a fabricated provider result.
- Stop/disposal reject reentrant callback cycles and attempt all cleanup stages.
- Lifecycle stop callbacks attempt every registration while retaining failures;
  the Python registry stops at its first ordinary callback error. Native restart
  callbacks preserve the swap's primary error and release failed send waiters.
- Superseded transport errors are fenced, while independent iterator disposal
  errors remain observable instead of being treated as expected socket closure.

The restart supervisor follows `agent/loop.py` and `_restart_timer.py`: reactive
timeouts consult the current automatic-restart policy; positive deadlines warn
ten seconds before replacement; turn alignment waits at most ten seconds; an
old reader gets two seconds to close before cancellation. One supervisor
serializes reactive and scheduled triggers. A connection gate orders both user
sends and tool results against the swap, without re-executing tool bodies.
Warning delivery is ordered before replacement, and its backpressure does not
restart the deadline countdown. The test clock is internal; public behavior
uses cancellable native .NET delays.

Lifecycle notifications follow `bidi/hooks/events.py` and `bidi/agent/loop.py`:
stop callbacks follow attempted cleanup; actual response completion and barge-in
notifications precede public output; before/after restart surround the native
attempt. Stop reverses registration order within each priority, matching
`hooks/registry.py`. Direct sealed records and an event interface avoid an
extensible record hierarchy across .NET Standard and modern runtime assets.
The bidirectional registry carries a real `BidiAgent`; it cannot reuse the core
registry's chat-agent-specific `HookEvent` without inventing an unrelated host.

Run orchestration follows `bidi/types/io.py` and `bidi/agent/agent.py`: connect,
start inputs then outputs, repeatedly read concurrent inputs, and fan each
output event out to concurrent consumers before processing the next event.
Explicit interface lifecycle methods work on Framework without default
interface implementations; callable adapters retain the source's ergonomic
callable form. Neither stream type transfers object disposal ownership.

Native cleanup unwinds entered stream lifecycles in reverse, keeps the actual
primary exception, and joins physical I/O before agent stop hooks or disposal.
The Python implementation stops all configured streams in forward order and
replaces stop failures with a formatted `RuntimeError`. Native failed admission
also leaves an already active conversation intact. A captured conversation
receipt and drain/stop completion signals prevent old-run teardown from
stopping a new connection or returning before canceled work actually exits.

The ordinary session manager, standalone default host, OpenAI server-side
resumption, device I/O and Nova Sonic remain separate work. These gaps are not represented by inactive
configuration switches.

Google Live follows `bidi/models/google.py` and its source tests: setup defaults,
text-only fresh history, native client/realtime/tool messages, per-reader turn
state, transcription boundaries and preserved pending tool names. Its GenAI
SDK configuration is expressed as raw camelCase setup JSON. No Python client
or ordinary chat-model proxy is involved. The native adapter waits for the
actual setup acknowledgement, resumes with a tracked handle, and falls back
to fresh text history after resume refusal. Fresh start clears tracked handles.
The native connection snapshot is held through old-connection shutdown so a
concurrent sender cannot invalidate its pending-tool snapshot.

Unlike the source formatter's unexamined `message.data`, native PCM output
validates any declared sample rate/channel count before publishing audio.
Native combined-message state commits only after all fields validate; malformed
usage cannot reserve a tool call that was never emitted. Unknown native part
metadata is retained without translating a thought signature into visible text.
The native tool-cancellation notification is retained, but the current common
agent has no per-call cancellation bridge; this remains explicit rather than
pretending to cancel an already-running local side effect.

Relevant current primary protocol documentation is OpenAI's
[Realtime WebSocket guide](https://developers.openai.com/api/docs/guides/voice-websockets),
[conversation guide](https://developers.openai.com/api/docs/guides/realtime-conversations),
and [client events](https://developers.openai.com/api/reference/resources/realtime/client-events).
The Realtime protocol is not replaced with the distinct GPT-Live sessions API.
