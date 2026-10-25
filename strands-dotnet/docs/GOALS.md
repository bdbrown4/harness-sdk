# Iterative goals

`Strands.Agents.Goals.GoalLoop` evaluates a completed agent response and supplies corrective input until a validator accepts it or its evaluation budget expires. It supports both an application callback and a natural-language goal judged through the SDK's real structured-output path.

```csharp
using Strands.Agents;
using Strands.Agents.Goals;

var goal = new GoalLoop(
    (response, agent, cancellationToken) =>
        new ValueTask<ValidationOutcome>(new ValidationOutcome(
            response.Text.Length <= 200,
            "Keep the answer within 200 characters.")),
    new GoalLoopOptions { MaxAttempts = 3 });

await using var agent = new Agent(new AgentOptions
{
    Model = model,
    Plugins = new[] { goal }
});

var response = await agent.InvokeAsync("Explain the result concisely.");
GoalResult? summary = goal.LastResult(agent);
```

Use `new GoalLoop("Answer every question and cite the supplied evidence.", options)` for natural-language evaluation. Each attempt creates a fresh judge agent. Its borrowed model is selected in this order: `JudgeModel`, the host's auxiliary model, then the host's main model. `JudgeSystemPrompt` can replace the shared strict evaluation instructions. The judge does not inherit the host's tools, plugins or application state, and disposing it does not dispose the borrowed model. `GoalJudge` exposes the outcome schema, instructions and transcript builder for custom validators.

## Execution and limits

`MaxAttempts` and `Timeout` default to null, matching the unbounded goal budgets in the pinned implementations. A warning is emitted once per plugin name when both are absent. The ordinary agent invocation limits still apply. Attempt limits must be positive; timeouts must be positive `TimeSpan` values. The timeout is checked between attempts before validation. It does not cancel an in-flight model call or validator.

The default retry preserves conversation history and inserts corrective feedback. `ResumePromptTemplate` can return typed continuation content, retaining media and JSON without converting them to strings. An ordinary validator exception becomes failed validation feedback. Requested caller cancellation propagates through the validation path, and failed, denied, interrupted or partially consumed agent attempts are not silently retried or judged.

Set `PreserveContext = false` to restore the first model-visible session checkpoint before each retry. This restores messages, system prompt, provider state and the stateful acknowledgement cursor. It preserves application state and accepted external effects; it cannot undo a tool's side effects. The checkpoint remains internal to the active invocation, so this feature does not relax the public snapshot API's active-invocation guard. Active background work can reject a rewind through the existing snapshot safety guard.

One `GoalLoop` instance can be shared by multiple agents; their attempts and summaries are separate. Attaching two goal loops to the same agent is rejected. A new outer invocation starts a fresh result ledger even if a different hook cleared the previous continuation. The invocation receipt is private runtime state and never enters caller `InvocationState` or serialized snapshots.

For stateful providers that clear acknowledged history, the judge uses a detached current-run transcript when the host history is empty. After a context reset, prior failed attempts are also removed from that judge transcript. Visible text and tool summaries are included; opaque reasoning and media are omitted. Individual tool input/output summaries are bounded to 500 characters plus a length annotation.

## Compatibility and assurance boundaries

The baseline is commit `3bd98e618320f0f286acbfb2252f72388030421b`:

- TypeScript: `strands-ts/src/vended-plugins/goal/plugin.ts`, `judge.ts` and `__tests__/plugin.test.ts`.
- Python: `strands-py/src/strands/vended_plugins/goal/plugin.py` and its goal tests.

The native options use `TimeSpan` instead of language-specific millisecond/second values and null for unbounded budgets. A private outer-admission receipt prevents separate invocations from sharing an unfinished goal ledger when hooks replace a continuation or a consumer stops enumeration at the after-invocation boundary. This is a tested correction to the ambiguity of using a mutable resumed flag alone.

Goal attempts, elapsed time, judge transcripts and retry checkpoints are not serialized into a durable session. Restoring a paused agent into a fresh process does not restore that private goal ledger. Rewinding a conversation does not independently restore a context manager's internal caches. Judge transcript projection is intentionally textual and does not promise media understanding. Decision-model and context-injection families are separate work; this increment does not claim to implement them.

The increment has 30 executable contract cases: 13 incremental behavioral scenarios produced 15 observed failing executions before their fixes, while 15 additional cases passed on their first run and serve as conformance coverage. The same cases run against the core modern assets and the portable consumer project. Local `net48` execution uses the installed .NET Framework 4.8.1 runtime; a clean 4.8 runner remains a separate checkpoint gate.
