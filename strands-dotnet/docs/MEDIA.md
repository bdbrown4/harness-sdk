# Native media and selective guard content

The pinned acceptance sources are `strands-ts/src/types/media.ts`, `strands-ts/src/types/messages.ts`, `strands-ts/src/models/bedrock.ts`, `strands-py/src/strands/types/media.py`, `strands-py/src/strands/types/content.py` and their provider formatters at repository commit `3bd98e618320f0f286acbfb2252f72388030421b`.

Existing base64 constructors remain available. `ImageBlock`, `AudioBlock`, `DocumentBlock` and `VideoBlock` also accept an immutable `S3Location`; images additionally accept an absolute HTTP/HTTPS `Uri`. Remote-source constructors leave `Data` empty. Nonempty bytes and remote sources cannot coexist: message construction, rich tool results and snapshot restoration validate that boundary. Empty inline byte content remains representable. Snapshots retain the original S3 URI, optional owner, image URL, document options and ordered typed blocks.

```csharp
var source = new S3Location("s3://media-bucket/clip.mp4", "123456789012");
var message = new Message(Role.User, [
    new TextBlock("Summarize this clip."),
    new VideoBlock("video/mp4", source),
    new GuardContentBlock("Selected user text", qualifiers: [GuardQualifier.GuardContent])
]);
```

S3 references follow the documented [Bedrock URI/owner contract](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_S3Location.html). Validation preserves the original string instead of URL-normalizing object keys. This is stricter than upstream constructors that defer validation to the provider.

Bedrock maps byte/S3 image, audio, document and video sources into the official AWS SDK union, with no empty-byte substitute for S3. `DocumentBlock.FromText` retains UTF-8 source text; `FromContent` retains copied, ordered text segments. Bedrock converts text to bytes following TypeScript and maps segments to native document content. OpenAI Chat/Responses project those document forms to text; Google retains UTF-8 inline text documents and projects segments to ordered native text parts, including rich results. Video supports the [native formats](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_VideoBlock.html), including upstream `3gp`/`3gpp`/`3g2` aliases for `three_gp`. Native video also survives rich tool results. Bedrock HTTP image URLs fail explicitly.

`GuardContentBlock` selects exactly one text or inline PNG/JPEG image. Text qualifiers are copied and retain declaration order; their wire names are `grounding_source`, `query` and `guard_content`. Images cannot carry text qualifiers or remote sources. Bedrock maps these selections to [GuardrailConverseContentBlock](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_GuardrailConverseContentBlock.html). A selection alone does not configure or enable a guardrail. Structured system instructions, automatic guard selection and the complete guardrail options surface remain separate gaps.

Google accepts inline video through native `inlineData`. OpenAI Chat/Responses, Anthropic, Mistral, Writer's vision model and llama.cpp retain HTTP image sources using their native URL shape. Every HTTP adapter explicitly rejects unsupported S3 references, including nested rich results. Mistral/Writer retain their documented upstream behavior for other unsupported ordinary byte blocks; the explicit S3 rejection is a native extension that avoids losing a newly introduced remote source. Other unrepresentable content follows each adapter's documented rejection/projection policy. The SDK does not download referenced media implicitly.

The reviewed media increment has 90 portable cases: 62 HTTP-source/guard cases, 13 native mapping/snapshot cases, three document ownership/union cases and 12 document-provider cases. Initial runnable failures established missing polymorphic serialization, native mapping, remote-source handling and URL/video translation before their fixes. Document failures and independent guard-projection findings also went through runnable red/green cycles. Additional validation cases are conformance evidence. Local Framework execution uses the installed 4.8.1 runtime; clean 4.8 and fresh cloud execution remain separate gates. Source-specific execution is recorded in the validation results.

The supported media routes and each provider's explicit projection/rejection policies are documented above. [Guarded ordinary system prompts](BEDROCK_SYSTEM_PROMPTS.md) preserve selective guard authority through native Agent/session operations. These contracts do not promise arbitrary cross-provider replay or live-service acceptance; consult [current validation results](VALIDATION_RESULTS.md) for source/runtime qualification.
