# Bedrock tool history without current tools

The Bedrock request mapper supplies a private `noop` tool specification when the
current tool list is absent or empty and a conversation contains a top-level
tool-use or tool-result block. The specification uses the pinned SDK description,
`This is a fake tool that MUST be completely ignored.`, and an empty object input
schema. It is request metadata only: it does not register a callable tool with an
Agent or change stored messages.

A nonempty current tool list takes precedence, including an application tool
named `noop`. Existing ordering, strict-schema mapping, tool cache checkpoints,
and explicit tool choices continue through the ordinary mapper. A missing tool
choice remains omitted, matching the pinned TypeScript adapter; an explicit
`ToolChoiceMode.Auto` also expresses Python's explicit automatic choice.

`ToolChoiceMode.None` keeps its existing omission of `toolConfig`. The pinned
upstream choice unions and AWS ToolChoice DTO have no equivalent prohibition.
The mapper therefore does not weaken `None` by adding a selectable tool. Whether
the real Bedrock service accepts tool-bearing history with this native choice
remains a provider limitation; localhost request-mapping tests do not establish
service acceptance.

Both official Converse and ConverseStream requests use this mapper, as does
native CountTokens when enabled. Rich history, result content, media ownership,
and cancellation use the existing paths. The presence check matches typed
history blocks; it does not inspect words inside text or JSON strings. Detecting
an isolated historical block is not a claim that the service accepts an invalid
conversation.
