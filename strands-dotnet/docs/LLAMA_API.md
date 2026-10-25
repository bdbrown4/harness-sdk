# Llama API

`Strands.Agents.Models.LlamaApiModel` implements Meta's native streaming event protocol in `Strands.Agents`. The [pinned Python adapter](https://github.com/strands-agents/harness-sdk/blob/3bd98e618320f0f286acbfb2252f72388030421b/strands-py/src/strands/models/llamaapi.py) marks the Meta service deprecated and plans removal of that adapter in v2. This implementation preserves its source contracts for parity; verification uses deterministic native HTTP fixtures and makes no live-service availability claim.

```csharp
using System.Net.Http;
using Strands.Agents;
using Strands.Agents.Models;

var apiKey = Environment.GetEnvironmentVariable("LLAMA_API_KEY")
    ?? throw new InvalidOperationException("Supply a credential through your host.");
using var http = new HttpClient();
var model = new LlamaApiModel(http,
    new LlamaApiModelOptions("Llama-4-Maverick-17B-128E-Instruct-FP8", apiKey)
    {
        MaxCompletionTokens = 1024
    });
await using var agent = new Agent(new AgentOptions { Model = model });
var result = await agent.InvokeAsync("Describe this task.");
Console.WriteLine(result.Message?.Text);
```

The host supplies a fixed key or a borrowed `Func<CancellationToken, ValueTask<string>>` credential callback. `HttpClient` remains caller-owned. `Endpoint` overrides the complete request URI; otherwise the client's base address supplies `chat/completions`, falling back to `https://api.llama.com/v1/chat/completions`.

`ModelId`, `RepetitionPenalty`, `Temperature`, `TopP`, `MaxCompletionTokens`, and `TopK` map to the pinned model settings. Optional generation settings are omitted when unset. `GetConfig()` returns an immutable typed snapshot; `UpdateConfig(current => current with { ... })` validates a replacement before atomically selecting it for future calls. An already-entered stream keeps its selected configuration. `ToString()` redacts credentials.

Two explicit policies expose source differences:

- `HonorToolChoice` defaults to `false`: a supplied choice produces a warning and is omitted, matching Python. Setting it to `true` enables the [vendor's documented](https://github.com/meta-llama/llama-api-typescript/blob/main/src/resources/chat/completions.ts) auto, required, none, and named-function wire forms through the common `ToolChoice` API.
- `AssistantContentPolicy` defaults to `ConcatenatedText`, preserving all assistant text blocks. `PythonFirstBlock` validates every ordinary assistant block, then retains only the first text or inline-image block, including an empty text block. Later supported blocks are discarded; tool calls remain separately correlated. This reproduces the pinned source projection. Its assistant-image form exceeds the [vendor's published typed assistant schema](https://github.com/meta-llama/llama-api-python/blob/main/src/llama_api_client/types/completion_message_param.py), so selecting it does not establish live-service compatibility.

User content supports text and inline images. Tool results preserve ordered text, JSON rendered as text, and inline images. Reasoning, audio, video, documents, guard selections, cache blocks, HTTP image URLs, and S3 image locations are rejected when they cannot be represented. Ordinary location-source images are rejected rather than following Python's skip-and-warn filtering. Validation precedes credential callbacks and transport.

The stream preserves the first tool-argument fragment, validates correlated tools, maps native stop reasons, and accepts clean EOF after a complete response without an OpenAI terminator. Reported token metrics must be exact nonnegative integral `Int64` values even though the vendor schema calls them floats. Missing usage stays unreported; timing is not invented. Native response IDs and raw metrics remain detached message metadata.

Cancellation preserves the requested caller token. HTTP, protocol, and read failures retain their primary cause when cleanup also fails; runtime cleanup diagnostics remain on the exception or its reachable cancellation cause. Early stream disposal closes the owned response while preserving the borrowed HTTP client. The pinned provider-native `structured_output` method is unimplemented; this adapter adds no provider-native structured-output helper.

The [64-case evidence summary](evidence/llama-api64.json) records exact hashes, the fixed Core dependency commit, and six passing test runs: 64 cases on each Windows/Linux modern target and 65 on each local Framework x64/x86 target, including the architecture assertion. Fresh NuGet-only consumers passed on Windows `net48`, `net8.0`, and `net10.0`, and Linux `net8.0` and `net10.0`; all three library assets passed package API validation. Framework execution used the installed 4.8.1-or-later in-place runtime. This increment has no clean-4.8-only, macOS, or live Meta verification. Paths inside the summary identify retained local logs and TRX artifacts, rather than additional committed files.

The [source-scope metadata](llama-api-source-scope.json) distinguishes observed product RED→GREEN tests, other conformance cases, fixture corrections, and native choices. Vercel's `LanguageModelV3` and LiteLLM's library/router integrations remain separate gaps. This increment does not establish full SDK parity or PR readiness. Framework applications consume the `netstandard2.0` asset; see [API.md](API.md) for compiler, `System.Net.Http`, and binding-redirect requirements.
