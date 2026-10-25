# Official Google credential transport bridge

The optional `Strands.Agents.GoogleAuth` assembly references Google.Apis.Auth 1.77.0 and has no Core dependency. Its public methods accept a borrowed official `GoogleCredential` or explicitly discover official application-default credentials, returning an owned `ConfigurableHttpClient`. Core and Harness can consume that transport through their existing explicit model injection APIs.

Credential construction remains an SDK responsibility. Python’s pinned `GeminiModel` borrows an injected client or delegates `client_args` to `genai.Client`; the pinned TypeScript model borrows an injected client and otherwise still requires an API key. The Harness factories delegate credential construction to those SDK defaults and do not expose a distinct ADC selector. This native bridge is an explicit authentication capability. It does not claim to reproduce all foreign environment/project/location selection or client arguments.

The helper uses official scoping and initialization. It adds the cloud-platform scope only when `IsCreateScopedRequired` is true, preserving the original borrowed credential and existing explicit scopes/quota. It obtains the client before invoking its credential initializer, so initialization failure can dispose that client while preserving the original exception and independent cleanup errors. Successfully returned clients belong to the caller; credentials and their shared discovery/refresh work remain borrowed.

Official ADC and refresh may share work. Cancellation cancels the caller’s wait where the SDK supports it; the helper does not promise physical completion of private SDK tasks. The bridge checks the caller token before invoking discovery and before transport allocation, preserves independent resolver errors, and does not substitute cancellation tokens. Installed Framework HTTP cancellation was observed to differ from modern .NET in Phase0; a fake resolver’s token assertions do not erase that SDK/runtime qualification.

`GoogleModel` continues to require explicit Vertex selection and an explicit endpoint, and borrows its `HttpClient`. Neither the helper nor model chooses project/location resources, modifies process environment, or silently acquires a client inside a borrowed model factory. The returned official `ConfigurableMessageHandler` remains caller-configurable through Google’s API.

All task verification uses synthetic credentials, closed fake flows and in-process HTTP handlers. Actual ADC/user credential files, metadata endpoints and Google services are not invoked. These proofs do not establish live credential/service behavior or all-platform/full-composition acceptance.

## Vertex AI Live WebSockets

`GoogleGeminiLiveModel` owns a `ClientWebSocket` independently of the REST
transport bridge. Set `UseVertexAi = true`, an explicit regional WebSocket
endpoint and a fully qualified model resource. Compose the existing official
credential API through the borrowed callback; Bidi adds no Google SDK dependency.
For example, with `Google.Apis.Auth` referenced by the application:

```csharp
using Google.Apis.Auth.OAuth2;
using Strands.Agents.Bidi.Models;

var credential = await GoogleCredential.GetApplicationDefaultAsync(ct);
if (credential.IsCreateScopedRequired)
    credential = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
ITokenAccess tokens = credential;
const string serviceAuthUri = "https://us-central1-aiplatform.googleapis.com/";
await using var live = new GoogleGeminiLiveModel(new()
{
    UseVertexAi = true,
    Endpoint = new Uri("wss://us-central1-aiplatform.googleapis.com/ws/google.cloud.aiplatform.v1.LlmBidiService/BidiGenerateContent"),
    ModelId = "projects/your-project/locations/us-central1/publishers/google/models/your-live-model",
    AccessTokenProvider = token => tokens.GetAccessTokenForRequestAsync(serviceAuthUri, token)
});
await live.StartAsync(cancellationToken: ct);
```

Choose a model and region available to the application. The callback is invoked
for every actual connection attempt, including resumption and fresh fallback;
the official credential remains responsible for caching and refreshing tokens.
Use the HTTPS service URI appropriate to the configured endpoint when the
credential implementation uses that URI as its JWT audience.
The model borrows the callback and credential. Vertex ignores `ApiKey` and
Developer key environment variables. Bearer tokens are validated without
including their value in validation errors, sent only in the authorization
header, and not retained in configuration or model metadata. The callback is
excluded from JSON serialization. Application exceptions retain their original
identity and content, so credential implementations control their own diagnostics.

The callback runs outside the model state lock and before a healthy connection
is closed. A credential error is not a rejected service resumption and does not
trigger fallback. Its cancellation token links caller cancellation, disposal and
the setup deadline. The adapter physically awaits that callback, even if it
ignores cancellation, and checks cancellation again before opening a socket.
Consequently `SetupTimeout` is cooperative for credential resolution; it does
not abandon credential work. A callback cannot await lifecycle methods on its
own model. Credentials used internally by an official SDK can have separate
shared-task lifetimes; joining this callback does not claim to join those private
SDK tasks.

Vertex uses the same acknowledged `clientContent` text-history seed. It omits
Developer-only `historyConfig` and rejects an explicit `Params.historyConfig`,
including null, before I/O. Other native setup defaults and parameter overrides
remain available. Local tests use real loopback WebSocket handshakes and synthetic
tokens; they do not establish authenticated Google service or ADC execution.

The route, bearer header and full model resource follow Google's
[Live session guide](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/live-api/start-manage-session).
The pinned official [GenAI Live implementation](https://github.com/googleapis/python-genai/blob/v2.0.0/google/genai/live.py)
and [Vertex setup converter](https://github.com/googleapis/python-genai/blob/v2.0.0/google/genai/_live_converters.py)
also define the native history distinction.
