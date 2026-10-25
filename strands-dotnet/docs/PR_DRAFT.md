# Unposted PR draft

Suggested title: `feat: propose native .NET agent SDK and initial harness`

This draft is local and has not been submitted. Feature request [#5271](https://github.com/strands-agents/harness-sdk/issues/5271) is open; maintainer alignment and public API review remain pending. This contribution introduces an alpha subset; it does not complete Python/TypeScript parity. Use the repository's current PR template when submitting.

## Human Overview

<!-- Intentionally blank. The human author must write this section in their own words; the repository PR template prohibits an AI agent from filling it. -->

## Description

.NET applications need an in-process Strands agent loop with native async streams, tools, hooks, and cancellation. This proposal adds `Strands.Agents`, the optional `Strands.Agents.Bedrock` adapter, and companion `Strands.Harness` as `0.1.0-alpha.1` NuGet artifacts targeting `netstandard2.0;net8.0;net10.0`. Framework 4.8 consumers use the .NET Standard 2.0 asset; source builds use the modern SDK.

The implemented surface includes rich model/tool content, schema-validated structured output, bounded tool execution, lifecycle hooks/plugins, separate application and provider state, sessions, retry/routing decorators, automatic and agentic context management, memory extraction/retrieval, progressive skill activation, OpenAI Chat/Responses and Anthropic/Google adapters, native Bedrock streaming and MCP HTTP/stdio integration. The harness adds portable configuration with typed native bindings, shared prompts, bounded shell/file tools over selected sandbox storage, todos/environment plugins, default file memory and bounded subagent delegation. Its optional default model is a lazily owned Bedrock adapter; supplied models retain their documented ownership.

The RFC, source export inventory and parity ledger track the inclusive TS/Python capability target. The cloud-verified subset includes streaming tools, snapshots, typed input, middleware, harness context defaults, interrupt/resume, the first resumable Graph slice and web fetching. Later provider additions, Graph crash-recovery journals, invocation-local tools, harness intervention/module bindings, Cedar and Swarm need a fresh checkpoint. Remote sandboxes, programmatic tools, telemetry and remaining source-specific behavior still need implementation and evidence. Local shell execution retains host-process authority with documented cleanup/platform limits. This draft is not full parity or PR-ready. Package publication and maintainer adoption remain pending.

## Related Issues

Feature request: [#5271 — Native C#/.NET SDK and harness distributed through NuGet](https://github.com/strands-agents/harness-sdk/issues/5271).

Related community proposal: [#2231](https://github.com/strands-agents/harness-sdk/issues/2231). This reference does not imply maintainer approval or agreement on ownership.

## Public API Changes

The customer-facing entry points are the core `Agent` and its options, `IModel`, `ITool`, typed hook events, and session/context seams. The harness returns the same core agent type. The [API proposal](API.md) presents signatures/defaults, provider/harness examples, cancellation outcomes, cleanup diagnostics, hook order, session schema/writers, fresh child conversations, and window semantics. The [RFC](../../team/designs/0021-dotnet-sdk.md) and [parity ledger](PARITY.md) define scope and remaining gaps.

```csharp
using Strands.Agents;

static async Task<AgentResult> AskAsync(IModel model, CancellationToken cancellationToken)
{
    var agent = new Agent(new AgentOptions { Model = model, MaxTurns = 8 });
    return await agent.InvokeAsync("Explain this repository.", cancellationToken: cancellationToken);
}
```

## Documentation PR

Package README, API proposal, development guide, parity ledger, validation plan, and design RFC are included. Library XML documentation generation is enabled; an automated public API compatibility baseline/freeze remains pending. User-guide site integration depends on maintainer agreement about the alpha surface.

## Type of Change

New feature

## Testing

<!-- Do not check this template's hatch item unless that command actually ran. -->

Local Windows validation exercises .NET 8/10 and x64/x86 compatibility hosts using installed Framework 4.8.1-or-later; the tests assert actual pointer size. Release build, formatting, packing and fresh-cache three-package consumers have separate source-specific records in [Results](VALIDATION_RESULTS.md). Target execution counts must distinguish repeated targets/architectures from distinct test cases, including Framework-only cases.

Immutable cloud checkpoint `ff35aac068affaf0672217488597948ca1203766` passed **1,913 cases per modern target on Linux/macOS** and **643 compatibility cases on each clean Framework 4.8 architecture**: 8,938 passing cloud executions across 18 TRX records. Both hosts also passed build, formatting, package validation and fresh-cache NuGet consumption. [Results](VALIDATION_RESULTS.md) identify source snapshots and distinguish repeated executions from distinct cases; later source changes require a fresh checkpoint. Live APIs and API review remain unverified/pending; no publication occurred.

- [ ] I ran `hatch run prepare`

## Additional Details

<details>
<summary>Design and API review</summary>

The baseline is `3bd98e618320f0f286acbfb2252f72388030421b`. Design approval and API bar raising remain pending. The proposal needs maintainers to agree on language ownership, NuGet naming, target frameworks, initial parity threshold, and release policy. Apply `api/needs-review` when opening the implementation PR; only reviewers can establish that review is complete.

AI assisted this local contribution. The human author must review and understand every line, write the Human Overview, and attest to the repository checklist. No review approval or remote communication is implied by this draft.

</details>

## Checklist

- [ ] I have read the CONTRIBUTING document
- [ ] I have reviewed and understand every line of code in this PR, including any generated by AI tools, and I can explain why it works
- [ ] My change is focused and reasonably small; I have split unrelated work into separate PRs
- [ ] I have added any necessary tests that prove my fix is effective or my feature works
- [ ] I have updated the documentation accordingly
- [ ] I have added an appropriate example to the documentation to outline the feature, or no new docs are needed
- [ ] My changes generate no new warnings
- [ ] Any dependent changes have been merged and published

By submitting this pull request, I confirm that you can use, modify, copy, and redistribute this contribution, under the terms of your choice.
