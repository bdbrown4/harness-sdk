# Bedrock reasoning replay

## Streaming opaque reasoning

Bedrock response chunks expose opaque reasoning as
`ModelContentBlockDeltaEvent.RedactedReasoningBytes` as soon as each native
fragment arrives. The first fragment produces an indexed block start; the block
stop carries the completed `RedactedReasoningBlock`. Agent accumulation appends
the byte fragments in order and validates that the completed block agrees.
Unclosed responses still expose their received fragments before failing with
`ModelException`; they do not commit an incomplete assistant message.

The byte payload copies both assigned and returned arrays. `null` omits the
payload from ordinary JSON, while an empty array represents an opaque fragment.
The existing five-argument delta constructor and deconstruction remain available.
Opaque and signed/text/tool/citation content cannot share one block. The existing
aggregate `MaximumBufferedContentBytes` limit applies before each fragment is
released, and closing a block releases its parser budget. Cancellation and early
consumer disposal close the response and retain a borrowed AWS client.

## Request history filtering

`BedrockModelOptions.FilterRejectedReasoningContent` defaults to `false`.
The default preserves the native provider's existing projection and the pinned
TypeScript provider's reasoning replay behavior. Python applies its model-family
filter automatically; this native default is an explicit compatibility choice.

Set the option to `true` to apply the reasoning policy from Python at source
commit `3bd98e618320f0f286acbfb2252f72388030421b`. The policy operates on top-level
`ReasoningBlock` and `RedactedReasoningBlock` in conversation history, before the
accepted blocks are mapped to the official AWS request. It preserves their order
and the caller's messages. It applies to streaming Converse, nonstreaming
Converse, and enabled native CountTokens; the local counting heuristic continues
to use its existing input.

The predicate has this order:

1. A model ID containing `deepseek` discards every reasoning block, including
   redacted content. This rule applies even inside an ARN or bare profile ID.
2. Redacted content is otherwise opaque and retained byte for byte.
3. An ID containing `anthropic` or `claude` retains reasoning text only when its
   signature is nonempty. Whitespace is nonempty; signatures are not trimmed.
4. Other IDs starting with `arn:` or containing no period retain signed and
   unsigned reasoning. They can hide the model's provider.
5. The pinned Python list rejects reasoning text for `amazon.nova-lite`,
   `amazon.nova-pro`, `google.gemma`, `meta.llama`, `mistral.devstral`,
   `mistral.ministral`, `mistral.mistral-large`, `nvidia.nemotron-nano-9b`,
   `nvidia.nemotron-super`, `openai.gpt-5`, `openai.gpt-6`, `qwen.qwen3-235b`,
   `qwen.qwen3-coder`, `writer.palmyra`, and `xai.grok` substrings.
6. Other dotted model IDs retain unsigned reasoning and discard signed reasoning.

Native matching uses ordinal case-insensitive comparisons against these ASCII
patterns. Arbitrary Unicode model-ID case folding is not promised to equal
Python's `lower()`. Reasoning text, signatures, and retained opaque bytes are not
normalized. The filter does not discover live model capabilities or verify a
signature, and the pinned list does not establish acceptance by every service
version.

When enabled, messages with no accepted content are omitted, including an
originally empty message. An empty `TextBlock` still counts as content. An empty
signature on retained reasoning text is omitted from the native request. The
disabled path preserves its existing message and signature projection. Existing
cache management runs afterward; it keeps its own behavior, including a
cache-only message becoming empty when an unusable checkpoint is removed.

The projection uses the options captured for that call. `UpdateConfig` affects
future calls. It adds no retry or learned state. Existing stale-thinking and
tool-result-turn recovery, when separately enabled, reuse the already filtered
graph. Existing guard selection, cache placement, history-tool fallback, typed
content authority, and owned media-stream cleanup continue through the same
mapper. Invalid retained native base64 still fails; excluded DeepSeek redacted
content is not decoded.

This option does not filter tool-result JSON or response events, widen the native
content union, add a debug-logging API, or implement Nova JSON-to-text projection.
The last behavior and other provider policies are separate parity work. It does
not change borrowed AWS client configuration or ownership.
