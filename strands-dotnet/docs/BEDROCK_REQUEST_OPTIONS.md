# Bedrock request options

The native provider exposes guardrail request settings, optional additional response-field paths, invocation-log metadata, managed-prompt text variables, performance latency, service tier and explicit tool-result status selection. These values map to the official AWS SDK request; credentials, endpoint and retry policy remain properties of the supplied AWS client. Availability and service-specific request constraints are validated by Bedrock.

```csharp
using var model = new BedrockModel(new BedrockModelOptions
{
    Guardrail = new()
    {
        Identifier = "application-guardrail",
        Version = "1",
        GuardLatestUserMessage = true
    },
    ServiceTier = "priority",
    AdditionalModelResponseFieldPaths = ["/stop_sequence"],
    RequestMetadata = new Dictionary<string, string> { ["application"] = "assistant" }
});
```

Guardrail trace defaults to `enabled`. Optional processing mode accepts `sync` or `async`; trace accepts `enabled`, `disabled` or `enabled_full`. Latest-user selection finds the last user message with direct text/image content, so subsequent tool-result messages do not move the selection. It wraps nonblank text and inline PNG/JPEG images, preserving explicit guard qualifiers and ordinary unsupported image formats/sources. Blank text remains ordinary content, following Python's avoidance of invalid blank guard blocks. Unsupported automatic wrapping produces a trace warning. Conversation snapshots and the caller's media objects remain unchanged.

`IncludeToolResultStatus` is nullable: `true` always includes status, `false` omits it, and `null` retains automatic Claude detection. Forced `Any`/named tool choice removes the incompatible `thinking` request field from a detached copy. Other fields remain present; an otherwise empty object is omitted. The configured thinking settings remain available for subsequent automatic tool selection. Collections are detached and read-only when published or returned by `GetConfig`; invalid updates leave the old configuration intact.

When no inference option is configured, the native request omits `inferenceConfig`; this permits managed-prompt identifiers and `PromptVariables` without adding an otherwise forbidden empty object. A test inspects the official AWS marshaller's emitted JSON. Explicit inference/system/tool settings remain explicit request settings, and Bedrock validates combinations with managed prompts.

This document covers native request projection. [Non-streaming Converse](BEDROCK_NON_STREAMING.md), [blocked-content redaction](GUARDRAIL_REDACTION.md), [strict tools](BEDROCK_STRICT_TOOLS.md) and [structured system prompts](BEDROCK_SYSTEM_PROMPTS.md) have their own implemented contracts and tests. Additional native fields retain the documented managed-field precedence; arbitrary source-language parameter-object behavior is not promised. Selecting a guardrail does not by itself select a local redaction policy.

Twenty-two request-option and seventeen non-streaming scenarios pass .NET8/.NET10 and actual local Framework x64/x86. The full Bedrock suite passes 148 cases per modern target. Seven initial request-option scenarios and three peer-review cases produced runnable failures before implementation; twelve cases were passing conformance/regression checks. Both bounded provider changes received independent review. These newer working-tree changes are outside checkpoint `fab9392` and await clean Framework/macOS execution.

References: [pinned TypeScript Bedrock provider](../../strands-ts/src/models/bedrock.ts), [pinned Python Bedrock provider](../../strands-py/src/strands/models/bedrock.py), [AWS ConverseStream request](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_ConverseStream.html), [AWS guardrail streaming configuration](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_GuardrailStreamConfiguration.html).
