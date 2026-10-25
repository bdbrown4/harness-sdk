# Direct OpenAI Responses structured output

`OpenAIResponsesModel.InvokeStructuredAsync<T>(request, options, cancellationToken)`
uses the native nonstreaming Responses HTTP endpoint and the existing
`StructuredOutputOptions<T>` JSON-schema validator/deserializer. It borrows the
supplied `HttpClient`; each request, response and body remains operation-owned.
`MaxResponseBytes` defaults to 16 MiB and must be positive. Calls capture the
entered configuration, so a concurrent `UpdateConfig` affects later calls.

This direct helper is distinct from `Agent.InvokeStructuredAsync`: it performs
one provider request without the agent tool loop, hooks, validation feedback or
automatic retries. Supplied local `ModelRequest.Tools`/`ToolChoice` are omitted;
configured native `Params.tools` and native tool choice remain provider inputs.
The helper does not execute tools or return their arguments as the typed result.

## Parsing contract

The pinned Python Responses module requires OpenAI Python SDK 2.0.0 or later
(below 3.0). Its direct helper calls `responses.parse` and then reads
`output_parsed`. The official 2.0.0 parser independently validates **every**
`output_text` in every output message, in wire order. It selects the first parsed
value only after the entire traversal succeeds. A later invalid JSON/schema or
deserializer failure defeats earlier valid output. JSON fragments are never
concatenated, and no single-message/cardinality restriction is imposed.

Refusal, reasoning, server-tool and unknown output items are skipped for typed
selection. Refusal-only/no-text responses fail. A mixed refusal and valid text
can succeed. Response/message status, `incomplete_details` and embedded response
error fields are not extra completion gates: valid text in an incomplete
HTTP-success response can parse successfully, matching the Python helper.
Callers needing completion-status inspection should use the ordinary native
Responses stream. This method is not a completed-only response policy.

For matching configured ordinary function tools with `strict: true`, function
arguments must be JSON syntax, even when a valid text result preceded the call.
The function's parameter schema is not evaluated. Unmatched/non-strict function
arguments are not parsed. Ordinary JSON Chat-Completions function wrappers are
rejected; they cannot represent Python SDK Pydantic tool objects.

The native generic API selects the first **non-null** validated `T`. Python's
selection uses the truthiness of a Pydantic `BaseModel` instance. Native `false`
and zero values are valid results when the supplied schema permits them. A
custom native deserializer receives detached JSON, and null results fail under
the shared native validator. No Python/Pydantic runtime or schema generator is
emulated. Callers supply a schema compatible with OpenAI's strict schema subset;
native validation alone does not establish that a live service accepts it.

## Request and ownership boundaries

The request contains `text.format` with `type: json_schema`, a name, the supplied
schema and `strict: true`. Other configured text siblings are preserved. An
explicit `Params.text` that is null, false, numeric zero, an empty string or an
empty array normalizes to an empty text configuration before the typed format is
added, matching the Python parser wrapper. Empty objects already work; truthy
nonobject values still fail before credentials or HTTP. An
existing `TextFormat` or `Params.text.format` (including explicit null) conflicts
with the supplied typed format and fails before credentials or HTTP.

The helper is standalone even on a stateful adapter. Nonempty managed
`ModelRequest.ModelState` is rejected before credentials/HTTP. It keeps native
storage configuration but does not update provider cursors, acknowledge server
history, persist an agent session, or mutate caller state. Existing explicit raw
provider parameters retain their ordinary native formatting semantics.

All text validation and owned HTTP cleanup complete before the typed value is
returned. Requested caller cancellation propagates with its caller token,
including cancellation observed during cleanup. Primary parse/transport errors
retain their causes, and independent cleanup failures remain secondary; cleanup
failure after otherwise successful parsing remains observable. Borrowed client
lifetime is unchanged. No live-service execution or full Responses-provider
parity is claimed by the bounded HTTP/TCP fixtures.

Primary source anchors: [pinned Strands helper](https://github.com/strands-agents/harness-sdk/blob/3bd98e618320f0f286acbfb2252f72388030421b/strands-py/src/strands/models/openai_responses.py#L510-L546),
[official parser](https://github.com/openai/openai-python/blob/v2.0.0/src/openai/lib/_parsing/_responses.py#L53-L176),
[first parsed result](https://github.com/openai/openai-python/blob/v2.0.0/src/openai/types/responses/parsed_response.py#L89-L97),
and [format conflict/request](https://github.com/openai/openai-python/blob/v2.0.0/src/openai/resources/responses/responses.py#L2508-L2610).
