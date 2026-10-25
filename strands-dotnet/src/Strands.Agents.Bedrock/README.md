# Strands.Agents.Bedrock

Amazon Bedrock Converse and Nova Sonic bidirectional support for Strands Agents. Converse and modern Nova use the official AWS SDK; the Windows .NET Standard Nova transport uses packaged AWS CRT native assets.

The package targets .NET Standard 2.0, .NET 8, and .NET 10. Framework 4.8 applications use the .NET Standard asset.

Pass a caller-owned `IAmazonBedrockRuntime` to `BedrockModel` to use application AWS configuration. The convenience constructor creates a client lazily through the AWS credential chain; dispose the model when that owned client is no longer needed. Provider requests are not replayed after streamed output.

The adapter translates text, fragmented tool calls, rich tool results, signed and opaque reasoning, citations, images, audio, documents, video, and explicit cache boundaries. Supported media can use inline bytes or S3 locations. It preserves reported usage and stop reasons, propagates cancellation, enforces response bounds, and maps throttling/context failures to the core exception types. Unsupported content raises an explicit error.

`BedrockModelOptions` exposes nonstreaming Converse, optional native CountTokens, guardrail configuration and redaction, strict tool schemas, and managed cache profiles with model-family selection, section-specific durations and checkpoint placement. Opt-in reasoning/history policies support stale-thinking recovery, tool-result turn recovery and Nova tool-result JSON projection. Their defaults and limits are documented in the [API proposal](../../docs/API.md).

Server-side streamed tool-result blocks remain unsupported and fail explicitly. Strict tool schemas do not expose a separate provider-native structured-output configuration. Model-specific parameters can be supplied through `AdditionalModelRequestFields`; callers can also use their AWS client for operations outside this adapter.

`BedrockNovaSonicModel` supplies text/audio/tool conversations, bounded queues, usage and reconnect behavior. The borrowed `IAmazonBedrockRuntime` constructor requires .NET 8 or later. The explicit `AWSCredentials` and `RegionEndpoint` constructor uses an owned official AWS client on modern .NET and the packaged Windows x86/x64 native transport for the .NET Standard asset, including Framework 4.8. Credentials remain borrowed. See [Nova Sonic conversations](../../docs/BEDROCK_NOVA_SONIC.md) for construction and lifecycle details.

[Validation results](../../docs/VALIDATION_RESULTS.md) record the source, platform, native-protocol and fresh-package profiles separately. Deterministic provider and localhost transport checks do not establish live AWS compatibility or completion of the remaining release gates.
