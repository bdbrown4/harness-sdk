# SageMaker provider contract

The baseline implementation is
`strands-py/src/strands/models/sagemaker.py`; its unit tests are
`strands-py/tests/strands/models/test_sagemaker.py` and integration tests are
`strands-py/tests_integ/models/test_model_sagemaker.py`. There is no corresponding
pinned TypeScript provider. This optional package keeps AWS transport separate
from the model-container chat payload.

The official [AWS streaming operation](https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_runtime_InvokeEndpointWithResponseStream.html)
returns AWS event-stream frames containing arbitrary payload bytes. The AWS .NET
SDK decodes and validates those frames; the native adapter then parses container
SSE/JSON. A plain HTTP chat endpoint would not provide this contract. The official
[.NET asynchronous operation](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/SageMakerRuntime/MSageMakerRuntimeInvokeEndpointWithResponseStreamAsyncInvokeEndpointWithResponseStreamRequestCancellationToken.html)
supports Standard 2.0 and Framework 4.7.2+, covering the package's Framework 4.8
asset selection.

The core's existing chat normalizer, chat decoder, schema parser, and provider
formatting helpers are reused through a narrowly named friend assembly. They
keep malformed tool identifiers/arguments and usage validation consistent. The
SageMaker layer adds reasoning blocks, source-order content indices, deferred tool
events, cumulative usage deltas, and the actual AWS transport lifecycle. The AWS
SDK client remains the signing/credential/retry extension point; no replacement
AWS HTTP or credential framework is introduced.

Explicit native differences preserve data and fit the existing runtime contract:

- Incremental UTF-8 decoding survives a code point split across AWS payload parts.
- All visible content survives, rather than the baseline's first-text selection.
- The optional tool-result-as-user representation updates the transmitted list;
  the source's local loop-variable assignment does not.
- Cumulative container metrics become additive native usage events, preventing
  double-counting when a final usage tail repeats prior counters.
- Complete events and buffered tools have a configurable byte budget, and missing
  terminal finish reasons fail instead of accepting partial completions.
- Payload parts are read through a fixed 8 KiB buffer after official SDK frame
  decoding. One frame may carry many bounded container events without an extra
  frame-sized allocation in this adapter. Post-finish output is rejected; empty
  final usage deltas remain supported.
- Schema formatting forces the non-streaming body field, including explicit AWS
  body overrides, before the non-streaming operation is invoked.
- Structured success additionally requires an unfiltered completed assistant
  response, common protocol validation, and an uncancelled model lifetime.
- Configuration publication detaches caller JSON/lists and rejects unsupported
  AWS request fields atomically. AWS client region/ownership remain fixed.

Behavioral tests exercise native AWS wire frames and the two actual SDK methods,
not a replacement OpenAI HTTP endpoint. Initial runtime failures cover the two
transport branches; subsequent failures cover Unicode preservation, complete
event/tool bounds, schema/body precedence, usage accounting, configuration
publication, reasoning fallback, and source non-streaming event order. Cancellation
tests use a body that ignores read tokens and require native output disposal to
unblock it. Cleanup tests retain primary failures and still release owned clients.

Inline video follows the source's raw byte-string `video_url` mapping. S3 media
and selectively guarded content fail explicitly, including nested rich results;
the inherited Python formatter silently skips unsupported location inputs.
Further AWS response-header projection and fresh
clean-4.8/cross-platform execution remain explicit follow-up scope. No live AWS
costs, credentials, SDK package publishing, or full-parity claim are involved.
