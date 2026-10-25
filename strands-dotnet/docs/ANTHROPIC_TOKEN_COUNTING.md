# Anthropic native token counting

`AnthropicModel` implements `IModelTokenCounter`. `AnthropicModelOptions.UseNativeTokenCount` defaults to `false`: counting uses the existing local input estimate and sends no HTTP request. The false value is omitted from default options JSON; the existing options constructor is unchanged.

Enable the option when constructing the model, then call `CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)`. The caller continues to own the supplied `HttpClient`. An entered count retains its captured configuration; later counts observe successful `UpdateConfig` changes.

Native counting sends one POST to the resolved Messages endpoint with `/count_tokens` appended to its path. Existing base-address resolution is retained, trailing slashes are normalized, and an explicit endpoint's query is preserved. The request uses the ordinary API key, version and optional beta headers. There is no implicit retry or generation request.

The count body reuses the adapter's request formatting, then includes only `model`, `messages`, `system`, `tools` and `tool_choice` when present. Output limits, sampling fields and unrelated native parameters are excluded. Existing rich-content, cache and function/server-tool mapping policies still apply. This five-field projection follows the pinned TypeScript/Python count paths; it does not promise identical foreign client construction or every service extension.

A successful JSON response must contain a nonnegative Int64 integer `input_tokens`; zero is valid. The complete response body is limited to 64 KiB. Recoverable formatting, transport, HTTP, content-type, JSON and count-value failures fall back to the local input estimate. Caller cancellation, independent cancellation and owned response-cleanup failures remain errors rather than successful estimates. A primary failure retains attached independent cleanup diagnostics.

Focused fake-HTTP cases cover request fields and headers, URI resolution, default JSON, configuration detachment, zero/malformed/oversize counts, fallback without retry, cancellation and causal cleanup. These checks do not establish live API authorization, model availability or billing accuracy. The count is input-only; output allowance metadata is unchanged.
