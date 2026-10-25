# Google typed image URLs

An ordinary `ImageBlock` with an HTTP or HTTPS `Url` maps to Google's `fileData` part. The request carries `fileUri` and the supplied `MediaType`; the adapter does not download the image. The existing `Uri.AbsoluteUri` representation is used, including its escaping and normalization.

This implements the typed image URL path in the [pinned TypeScript adapter](../../strands-ts/src/models/google/adapters.ts). It uses the existing native content API and adds no public types, options or dependencies. The source baseline is `3bd98e618320f0f286acbfb2252f72388030421b`.

The same message formatter serves streaming generation, native token counting and structured output. Mixed text, inline image bytes and image URLs retain their input order. Inline bytes continue to use `inlineData`.

Message validation still rejects contradictory byte, URL and S3 sources. The typed URL source remains limited to absolute HTTP/HTTPS URLs. S3 images and image URLs inside function responses remain unsupported by this adapter. A raw `GoogleContentBlock` is still available for an explicitly supplied provider part and is detached at the message boundary.

The increment's ten contracts include six cases that fail against the unchanged formatter: three URL forms, mixed part order, token counting and structured output. Four controls cover function responses, S3 rejection, contradictory sources and raw Google part retention. Selected existing provider tests cover streaming, cancellation and cleanup as separate regression checks. Component test, package and runtime evidence is recorded separately from a complete SDK/harness matrix.

This increment does not implement Vertex authentication/routing, future partial function argument assembly or every Google service feature. The pinned Python and TypeScript adapters serialize each complete function-call argument object; their source does not establish partial argument assembly as a missing pinned contract.
