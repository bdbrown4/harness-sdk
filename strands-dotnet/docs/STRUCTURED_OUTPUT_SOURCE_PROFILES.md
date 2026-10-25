# Structured output source profiles

`StructuredOutputOptions<T>.Behavior` selects `Native` (the unchanged default),
`TypeScript`, or `Python`. These are explicit workflow and accepted-result profiles
for the native API. They do not execute foreign schemas or reproduce every Zod or
Pydantic coercion, class-derived tool name, or error format. The native temporary
tool name remains `strands_structured_output`; it is not added to the public registry.

Native preserves the existing hybrid: an ordinary end-turn response is retained,
then the force prompt is appended and the next request selects the structured tool
among the available tools. TypeScript drops a non-tool response, adds no force
prompt, and selects the structured tool while retaining all available tool specs.
Python forces only after `end_turn`, retains the plain response and force prompt,
and advertises only the structured tool during the forced request. A fresh public
invocation starts with automatic choice, not a persisted forcing phase. The native
finite validation budget and ordinary invocation limits remain available.

TypeScript extracts the first matching assistant call's final successful tool
result only when its first content block is JSON. That final hook JSON is
authoritative, including explicit null and a hook replacement of validation feedback.
An unchanged emitted value reuses its typed cache without repeating converters or
custom validation. Changed JSON is cloned directly for `JsonElement`; another T is
decoded with its configured System.Text.Json settings. This post-hook decoding
does not rerun the schema or custom `Deserializer`. Incompatible native T fails
with `StructuredOutputException` and its conversion cause after the accepted
history is retained. This language boundary is not another model retry.

Python extracts the first successfully validated matching cached value before
`AfterTools`, separately from final hook display content or status. The cached JSON
also accompanies an `AfterTools` end-turn, requested cancellation or sibling
interrupt. Those stop reasons, pending human requests, accepted side effects and
physical cleanup retain their precedence; the payload does not turn them into a
successful tool-use stop. Native and TypeScript keep their existing preempted-result
payload rules. The native cancellation checkpoint policy remains separately
configured. Provider/tool cleanup errors and the original hard caller token keep
their existing authority.

The native typed convenience method still requires a tool-use completion; a
non-tool-use stop throws even when an ordinary Python-profile result can carry
cached data. Native continues to require an accepted successful result and validated
body. Legacy direct `ITool.InvokeAsync` keeps its existing validation string; rich
results carry source-profile JSON or validation text.

Public `IStructuredOutputTool` implementations registered as ordinary tools do not
select themselves into this profile slot. Supported default, per-call, typed and
AgentNode paths create their own native temporary tool. The reserved-name collision
is rejected before a model call. Models, tools and storage remain borrowed.

## Stateful discarded responses

A real completed TypeScript discard can consume a retained User-ended prefix.
The legacy `ModelHistoryCount` remains an assistant-ended cursor. A separate
immutable `ModelInputReceipt` records consumed message count, retained boundary
tracking ID, and omitted response tracking ID. It is a structurally validated
local fact, not authenticated provider attestation. Synthetic or failed provider
completion cannot create it. A later accepted assistant clears it.

Receipt and provider state travel as a complete pair. Whole sessions use inner
schema 2 only with a receipt; ordinary sessions remain inner schema 1. Public,
goal and typed-child snapshots use `AgentSnapshot` 2.0 for the pair and 1.0 when
absent. Catalog envelopes remain version 1. Indexed journals use native mode 3
with inner schema 2 while the fact exists, then mode 2/inner 1 after acceptance;
storage envelope and active-row projection versions stay 1. Existing row IDs and
assistant-ended cursor validation are preserved. The accepted User tool-result
row stays pending input until an actual provider consumes it.

Restoration validates detached complete history before publishing live fields.
Catalog Invocation policy saves on completion; a failed latest write retains the
live receipt/state, and a later completion saves the newer accepted provider state.
It does not promise a pre-provider retry. Accepted storage work physically joins
even when cancellation races it. Late cancellation during completion does not
replace an already accepted terminal result. These receipts are separate from an
experimental checkpoint pause marker and do not claim upstream wire compatibility.

The component's installed Windows Framework checks qualify 4.8.1. Genuine 4.8,
Linux, macOS, cloud providers and the latest composed Main require separate gates.
