# Closed-question decision models

`Strands.Agents.Decisions` answers a named set of boolean or string-choice questions over text, typed user content or conversation history. It follows the public TypeScript decision family at pinned upstream commit `3bd98e618320f0f286acbfb2252f72388030421b`. The pinned Python SDK has no equivalent public generic decision-model family.

```csharp
using Strands.Agents;
using Strands.Agents.Decisions;

static async ValueTask<DecisionResult> ClassifyAsync(IModel model, CancellationToken cancellationToken)
{
    var decisions = new LLMDecisionModel(model);
    return await decisions.AskAsync(
        "The fruit is a red apple.",
        new Dictionary<string, DecisionQuestion>
        {
            ["isFruit"] = new("Is the subject a fruit?") { AllowUncertain = false },
            ["color"] = new("What is its color?", new[] { "red", "green", "blue" })
        },
        cancellationToken);
}
```

`DecisionAnswer` carries a boolean, one string choice, or an `Uncertain` sentinel. JSON serialization writes the underlying value: `true`, `"red"`, or `{"uncertain":true,"reason":"insufficient evidence"}`. An absent uncertainty reason is omitted. `MinConfidence` must be finite and within `[0,1]`; the LLM implementation validates this extension point but does not enforce a confidence threshold, matching the pinned source. `AllowUncertain` controls whether the reserved `"uncertain"` value appears among allowed model answers.

The model is borrowed. Each ask makes one direct model stream operation with a dynamically generated `strands_structured_output` schema and forced tool choice. It does not construct an agent, execute a tool body, retry a response or dispose the provider. The provider must support forced tool choice. Unlike the TypeScript zero-configuration Bedrock default, the core .NET API requires an explicit model so the optional Bedrock package remains optional.

`AskAsync` wraps text in one user message. `AskContentAsync` preserves ordered typed content and wraps even empty content in one user message. `AskMessagesAsync` preserves a complete history; an empty history remains empty. Inputs and question collections are detached before an asynchronous provider operation begins. Input validation therefore fails before provider effects, and caller mutations cannot alter an admitted operation. No SDK coordination data is stored in caller state.

The advertised JSON schema rejects additional properties. Local parsing strips extra question keys and extra answer-entry fields, as the source Zod parser does, then validates required questions, string answer literals and optional string reasons. Missing, malformed, disallowed or incomplete answer sets fail with `StructuredOutputException`. Invalid model protocol, including a nonobject tool input or an incomplete stream, fails with `ModelException`. Neither path retries. Provider exceptions and caller cancellation retain their original identities; iterator disposal errors are attached to a primary exception under `Strands.CleanupErrors`.

`DecisionResult.Metadata.Latency` uses provider timing when reported and local elapsed time otherwise. `Usage` is absent when the model stream does not report usage; an explicitly reported zero remains a present `Usage`. `ModelMetadataEvent.UsageReported` defaults to true for existing providers. A metadata-only event sets it to false and carries zero usage while retaining performance metadata. Wire adapters must explicitly opt into that distinction when their API omits usage; this increment does not change every provider adapter. The common accumulator rejects contradictory nonzero unreported usage before updating its accounting or timing.

The base `DecisionModel` supplies the same prevalidation, detached inputs and operation tracing to custom implementations. The LLM helper records a `decision` span with question identifiers, typed answers, latency, reported usage and outcome. Observability callback failures do not replace the decision outcome. Mandatory untrusted-input and forced-output instructions are appended even when `LLMDecisionModelOptions.SystemPrompt` is supplied; those instructions do not create a security boundary against model prompt injection.

## Source and acceptance evidence

The source contracts are `strands-ts/src/decisions/{types,decision-model,llm-decision-model}.ts` and the decision test directory at the pinned commit. `tests/Strands.Agents.Tests/Decisions/DecisionModelTests.cs` is also compiled into the compatibility suite. The focused scope covers direct forced-tool requests, boolean/choice/uncertain conversion, schema and protocol errors, detached typed inputs, prevalidation, cancellation, cleanup error identity, tracing, custom implementations, usage presence and atomic metadata validation.

Eleven implementation scenarios were exercised as runnable failing assertions before their fixes. Nine further cases were added as conformance checks; their first run produced one error-classification expectation mismatch for top-level null tool input, corrected to the established common `ModelException` protocol contract without changing production code. Counts and target results belong to the accompanying scope manifest. Local `net48` execution uses the installed .NET Framework 4.8.1 runtime; a separate clean .NET Framework 4.8 gate remains necessary for this new increment. No live provider credentials or network model services are used by these tests.
