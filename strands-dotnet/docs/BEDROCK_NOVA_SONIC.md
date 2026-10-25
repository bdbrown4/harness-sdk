# Nova Sonic conversations

`BedrockNovaSonicModel` implements native bidirectional text, mono PCM16 audio, transcripts, tool calls/results, usage and interruption events. It implements `IRestartableBidiModel`, `IAudioBidiModel`, `IModelMetadataProvider` and `IConfigurableModel<BedrockNovaSonicModelOptions>`. The borrowed `IAmazonBedrockRuntime` constructor uses the official AWS duplex operation on .NET 8 and later and rejects that route on the .NET Standard target. The `AWSCredentials`/`RegionEndpoint` constructor refreshes borrowed credentials for each connection and owns its transport: the official AWS client on modern .NET, or the static-CRT Windows x86/x64 native transport for Framework 4.8 consumers of the .NET Standard 2.0 asset. The [recorded genuine Framework localhost TLS/model and package-ABI checks](VALIDATION_RESULTS.md#native-protocol-and-audio-evidence) retain their bounded source/runtime qualifications; final full-pipeline acceptance and live AWS execution are separate.

Pass a caller-owned `IAmazonBedrockRuntime` and an explicit model identifier:

```csharp
using Strands.Agents.Bedrock;
using Strands.Agents.Bidi;

await using var model = new BedrockNovaSonicModel(bedrockClient, new()
{
    ModelId = "amazon.nova-2-sonic-v1:0",
    VoiceId = "matthew"
});
```

The example uses the [official Nova 2 Sonic model identifier](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-2-sonic.html). The AWS client supplies credentials, region and HTTP configuration. Keep it alive until model disposal completes; the model never disposes it. Supply the model to `BidiAgent` using the existing bidirectional APIs. Console helpers and asynchronous input sequences are supported. Audio devices are a separate optional integration.

Options select model, voice, input/output rate, inference settings and bounded queues. Supported PCM16 rates are 8000, 16000 and 24000 Hz, with defaults of 16000 input and 24000 output. `MaxTokens` defaults to 1024, `Temperature` to 0.7 and `TopP` to 0.9. Input/output queues default to 32/64 events and wait under pressure. A complete input batch or initial session is limited to 1 MiB by default, including JSON envelopes. Invalid or oversized batches fail before their first event is published.

`UpdateConfig` validates a complete replacement for the next connection. An active connection retains its captured settings, audio formats and metadata. `GetConfig` returns the next connection's configuration.

The default connection policy enables reconnect after seven minutes, before the service's eight-minute limit. Set `Connection.AutoRestart` to false to disable scheduled reconnect; a custom positive interval must precede eight minutes. `BidiAgent` supplies retained text and completed tool exchanges during reconnect. Tool history becomes explicit text context and does not execute tool bodies again. Pending tools, active responses, in-progress sends and unsupported history reject before a healthy connection is closed. Direct model callers must supply retained conversation history to `RestartAsync` after traffic.

Caller cancellation after partial publication of a finite text/tool content group invalidates that connection. Subsequent sends reject until a new connection is established. Stop cancels transport work and joins its reader and publisher; graceful closing events are best effort under queue pressure. Receive cancellation preserves the caller token. Cumulative service usage becomes additive native usage events, and service interruption markers become barge-in events.

Deterministic tests use the official AWS operation and event-stream decoder with a controlled transport. They verify protocol messages and ownership but do not establish live AWS service connectivity. See [recorded validation](VALIDATION_RESULTS.md) for platform and package acceptance limits.
