# Google authentication

`GoogleCredentialHttpClient` creates caller-owned `ConfigurableHttpClient` instances using Google.Apis.Auth 1.77.0. Supply an existing `GoogleCredential` to `CreateVertexClient`, or explicitly request official application-default credential discovery with `CreateApplicationDefaultVertexClientAsync`.

The supplied credential is borrowed. When the official SDK reports that explicit scopes are required, the helper creates its scoped copy with `https://www.googleapis.com/auth/cloud-platform`; existing explicit scopes and quota configuration are retained. Google’s initializer applies authentication, quota headers, refresh and retries through its own HTTP pipeline. The helper does not implement OAuth or hold a separate token cache.

Dispose the returned client after its users finish. Core `GoogleModel` borrows that client: select `UseVertexAi = true` and supply an explicit `Endpoint`. Model construction and `CreateVertexClient` do not send service requests. The helper does not select a project, location or endpoint, install global handlers, or change process environment variables.

Cancellation of official discovery or refresh can cancel a caller’s wait while shared SDK work continues. The helper does not own or drain those private SDK tasks. It checks cancellation before discovery and before allocating the returned transport. A client successfully created after admission remains caller-owned. HTTP cancellation exception/token details also follow the selected SDK/runtime stack.

Keep the client’s owner outside a borrowed `AgentOptions.Model` or `ModelFactory` delegate. Explicit model injection can use this bridge; it does not reproduce every foreign SDK client argument or automatic Harness environment/project/location routing rule.
