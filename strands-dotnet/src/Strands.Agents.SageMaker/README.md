# Strands.Agents.SageMaker

An optional native AWS SageMaker Runtime adapter based on the Python
`SageMakerAIModel` at source baseline
`3bd98e618320f0f286acbfb2252f72388030421b`. It uses the official
`AWSSDK.SageMakerRuntime` 4.0.101.13 package (Apache-2.0), including AWS signing,
credential resolution, and event-stream decoding. Container payloads follow the
OpenAI-compatible chat format supported by the pinned SageMaker implementation;
requests go through SageMaker's AWS API operations.

The library targets .NET Standard 2.0, .NET 8, and .NET 10. Framework 4.8
applications consume the Standard asset, using a current compiler for the public
required/init options. No package has been published.

```csharp
using Strands.Agents;
using Strands.Agents.SageMaker;

using var model = new SageMakerAIModel(new()
{
    EndpointName = "my-deployed-endpoint",
    Region = "us-east-1",
    Payload = new() { MaxTokens = 1024, Temperature = 0.7 },
});
await using var agent = new Agent(new() { Model = model });
var result = await agent.InvokeAsync("Hello");
```

The convenience constructor creates its AWS client lazily, using the AWS
credential chain and region precedence `Region > AWS_REGION > us-west-2`.
It disables SDK retries; an explicit retry model or a supplied AWS client can
provide the application's retry policy. The constructor accepting
`IAmazonSageMakerRuntime` borrows that client's credentials, endpoint, retry
settings, region, and lifetime. Disposing the model cancels its active operations
and disposes only a client it created.

`Payload.Stream` defaults to true and selects `InvokeEndpointWithResponseStream`.
False selects `InvokeEndpoint`. The streaming parser handles arbitrary AWS
`PayloadPart` boundaries, fragmented UTF-8, SSE data lines, and bare JSON,
including pretty-printed JSON. The default malformed-event policy warns and skips
malformed SSE lines, matching Python; `SageMakerMalformedEventPolicy.Throw`
rejects them. Incomplete completions fail explicitly. Trailing and choice-local
usage are retained; cumulative usage becomes additive native usage deltas.
`MaximumResponseBytes` defaults to 16 MiB and bounds each event, buffered tool
data, and complete non-streaming responses.

Messages retain assistant text beside tool calls. Rich tool results become text
with JSON rendered as Unicode; `ToolResultsAsUserMessages` optionally adds the
source's described user-message representation. Text, JSON, citations, images,
audio, documents, inline video, and resource links have explicit formatting.
Video uses the pinned raw `video_url.url` byte-string contract; HTTP image URLs
are retained. Reasoning and
cache blocks in request history are skipped with warnings, matching the inherited
Python chat formatter. S3 input and selectively guarded content are rejected,
including rich tool results, rather than sending empty bytes or unwrapping guard
semantics. Provider-specific opaque input fails explicitly. Output supports text,
reasoning, tools, and usage.

Requested per-call tool choice warns and is ignored. Available tools select
`auto`; empty tool lists omit tool selection. Cache settings round-trip in
configuration but warn and send no cache fields. These are pinned SageMaker
limitations.

`GetConfig()` returns detached JSON and read-only list snapshots.
`UpdateConfig(...)` atomically publishes validated endpoint and payload options
for future calls; an entered stream keeps its captured options. Region changes
require another transport instance. Payload `AdditionalArguments` override typed
payload fields. Endpoint `AdditionalArguments` accept the actual AWS string
fields `EndpointName`, `ContentType`, `Accept`, `InferenceComponentName`,
`TargetModel`, `TargetVariant`, `TargetContainerHostname`, `InferenceId`,
`CustomAttributes`, `SessionId`, `PrefixAwareId`, `EnableExplanations`, and `Body`.
Unknown fields fail before publication. The streaming operation does not support
`TargetModel` or `EnableExplanations`. Session headers are explicit pass-through
settings; the adapter does not claim server-managed conversation history.

`InvokeStructuredAsync<T>(ModelRequest, StructuredOutputOptions<T>, token)` uses
`InvokeEndpoint` with strict container `response_format.json_schema`, then the
common native schema validator and custom/System.Text.Json deserializer. It
forces non-streaming/schema fields after explicit body overrides and leaves
published configuration unchanged. Incomplete, filtered, non-assistant, and
malformed completions cannot become typed successes. The deployed container must implement that
response format. Requested cancellation retains the caller token. Original AWS
causes remain attached to typed model errors; secondary cleanup failures are in
`Exception.Data["Strands.CleanupErrors"]`.

[Validation results](../../docs/VALIDATION_RESULTS.md) record source tests,
package/API validation and fresh consumers by source and runtime. Historical
installed Framework 4.8.1 checkpoints remain separate from genuine Framework 4.8
results and the pending composed workflow gate. Official-SDK and localhost tests
do not establish live AWS or deployed-container compatibility.

The repeatable package-consumption check is
`validation/Test-Package.ps1 -PackageDirectory <packed-core-and-sagemaker-feed>`.
It uses only the packed SageMaker dependency graph, retains an isolated scratch
consumer/cache for diagnostics, builds every target, and runs net48 on Windows.
