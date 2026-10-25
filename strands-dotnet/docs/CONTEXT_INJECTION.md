# Ephemeral context injection

`ContextInjector` renders text through model input middleware and appends it to the most recent user message for that model call. The injected block never enters the agent's durable history or session.

```csharp
using Strands.Agents;
using Strands.Agents.Injection;

var injector = new ContextInjector(
    (context, cancellationToken) =>
        new ValueTask<string?>(context.AppState.Get<string>("working-context")));

await using var agent = new Agent(new AgentOptions
{
    Model = model,
    Plugins = new[] { injector }
});
```

The default `InjectionTrigger.UserTurn` runs only when the latest model-visible message is a user message containing no tool result. Set `Trigger = InjectionTrigger.EveryTurn` to render before every model call, including tool-result turns. An optional `Predicate` replaces the built-in trigger and receives the same context as the renderer.

`InjectionContext.Messages` is a detached, read-only view of the current model input. `Agent` and `AppState` are borrowed references; the injector adds no application or invocation state keys. One immutable injector instance can be shared among agents. Distinct injectors require distinct plugin names and execute in plugin registration order. A later renderer sees context appended by an earlier injector.

The renderer's nonempty text is used verbatim. A separator of two newlines is prepended when the target already has content and the rendered text does not start with a newline. Text is appended after existing content, preserving tool-result ordering, typed media/JSON, message metadata and tracking identity. When the target is the final message, `DynamicTrailingBlocks` is incremented so automatic provider cache placement precedes all ephemeral suffix blocks. Injecting into an earlier user message does not change the last message's suffix count. If there is no user message, no new message is invented.

Null, empty or whitespace-only output skips injection. Trigger and renderer exceptions also skip injection, with best-effort diagnostics. Requested caller cancellation takes precedence: it is checked before and after the callbacks, including when a callback throws another exception. An unrelated `OperationCanceledException` is treated as an ordinary callback failure. The injector borrows its callbacks and owns no clients or resources.

Rendered text is a prompt-injection surface. The application renderer is responsible for escaping or delimiting untrusted fields; the plugin does not treat arbitrary retrieved text as trusted instructions. Model middleware remains an application extension point, not a security isolation boundary.

The implementation follows `strands-ts/src/injection/{types,message-injection}.ts` and `strands-ts/src/vended-plugins/context-injector/plugin.ts` at baseline `3bd98e618320f0f286acbfb2252f72388030421b`. The native API uses a cancellation-aware renderer constructor plus enum/predicate options. The source's generic injection primitive is internal; this increment exposes the vended plugin and its callback context. Existing memory retrieval/injection behavior is separate and unchanged.

The first increment has four observed incremental failing scenarios followed by fixes and sixteen additional cases that passed on first execution. Portable `net48` execution uses the locally installed .NET Framework 4.8.1 runtime; clean 4.8 and other operating systems remain combined checkpoint gates.
