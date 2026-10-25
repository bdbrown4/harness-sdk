# Explicit Vertex-compatible Google transport

`GoogleModel` can use a caller-configured Vertex-compatible REST endpoint and authentication transport:

```csharp
var options = new GoogleModelOptions("gemini-model")
{
    UseVertexAi = true,
    Endpoint = applicationStreamingEndpoint
};
var model = new GoogleModel(authenticatedHttpClient, options);
```

The application supplies the complete streaming endpoint and configures authentication through its borrowed `HttpClient` or handlers. The adapter does not discover credentials, construct project/location URLs, refresh tokens, or create a Google authentication client. It does not inspect `Authorization` during construction because a handler may add or refresh it when sending.

`UseVertexAi` defaults to `false`. The original `GoogleModelOptions(string modelId, string apiKey)` constructor and `ApiKey` property remain available. The additive one-argument constructor supplies an empty key; it does not select Vertex mode automatically. Developer mode still requires a nonblank API key and uses its existing endpoint defaults. `GoogleModelOptions` was already a non-positional record; this increment does not add or remove a `Deconstruct` method. The original two-argument constructor is explicitly marked for JSON deserialization so adding an overload does not make existing serialized options ambiguous. Previous null and missing-field deserialization behavior remains unchanged; constructing a usable model still performs its existing validation.

Vertex mode requires an explicit absolute HTTP or HTTPS `Endpoint`, even when `HttpClient.BaseAddress` is set. This prevents an implicit Developer API route. The application selects the appropriate service resource or gateway; the adapter does not infer the service from the hostname. HTTP remains available for local fixtures and caller-selected transports. No live Vertex request is part of the component evidence.

In Vertex mode, the adapter omits its request-local `x-goog-api-key` header. It leaves all caller defaults, including `Authorization` or an explicitly supplied default API-key header, unchanged. The model never owns the client or its handlers. Replacing model configuration does not acquire or dispose another transport.

With both function declarations and native built-in tools, Developer mode retains its automatic `includeServerSideToolInvocations` flag. Vertex mode omits only that automatic addition. An explicit `Params.toolConfig`, including its flag and other fields, remains authoritative even if the remote service would reject the selected policy. Existing native preservation of an explicit JSON `null` flag is unchanged; pinned TypeScript's nullish default and Python's `setdefault` differ here.

Streaming, native counting and direct structured output use the same entered mode and authentication policy. Explicit `TokenCountEndpoint` and `StructuredOutputEndpoint` override derivation. Otherwise existing suffix derivation applies: `:streamGenerateContent` becomes `:countTokens` with its query cleared, or `:generateContent` with only `alt` removed. Supply an explicit counting endpoint if its query values are required. A custom stream URL without the expected suffix requires an explicit endpoint for the corresponding native operation. Counting still uses the existing native opt-in and heuristic fallback policy; this mode does not change that default.

`GetConfig` retains the mode and existing detached native configuration. `UpdateConfig` validates a replacement model before publishing it. An already-entered operation uses its captured configuration; later calls use a successfully published replacement. Request parsing, response bounds, cancellation and cleanup behavior remain the existing Google adapter contracts. The accepted typed HTTP image URL formatter, inline media ordering and unsupported typed tool-result URL/S3 behavior remain intact.

The pinned source is `3bd98e618320f0f286acbfb2252f72388030421b`. TypeScript `models/google/model.ts:99-113` reuses a configured client and otherwise requires an API key before constructing a client with `clientConfig`; `:353-363` omits the automatic mixed-tools flag when that client reports `vertexai`. Python `models/gemini.py:139-155` borrows an injected client or constructs one from `client_args`; `:379-385` applies the same mode distinction for the automatic flag. Their mocked Vertex cases are TypeScript `google.test.ts:1009-1030` and Python `test_gemini.py:1388-1415`. These boundaries establish configured-client behavior, not complete authentication SDK, ADC, endpoint-builder or live-service equivalence.

The initial 37-case selection has five observed runtime REDs followed by GREEN and 32 first-green conformance cases. A separately retained constructor compatibility correction adds six observed JSON REDs and one first-green null-reference control, for 44 cases in total. An unchanged consumer compiled against the previous package characterizes the original complete, nullable and missing-field JSON inputs before and after the change. Component evidence is separate from final portable, package, consumer or combined checkpoint acceptance. Partial function argument assembly was not established as a gap in the pinned adapters; newer service extensions and complete Google/Vertex service parity are not claimed by this increment.
