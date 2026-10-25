# Vended Swarm tool

`SwarmTool.Create()` creates an optional Harness tool that lets a model define a team of native agents. It returns `IRichTool` and also implements `IStreamingTool`. This helper uses the existing Core Swarm with its Python profile; it is separate from the Core orchestration primitive.

```csharp
var team = SwarmTool.Create();
await using var agent = new Agent(new AgentOptions
{
    Model = model,
    Tools = [team, readTool]
});
```

The model supplies a string `task`, an `agents` array and an optional `entry_point`. Agent names are trimmed, nonempty and distinct. An empty task is accepted. The first agent is the default entry point, and the default team cap is 20 agents. The helper forwards progress and returns each participating agent's latest final text, prefixed by its name in first-participation order. An incomplete team returns an error with available named partial output and its native cause.

`SwarmToolOptions` configures developer authority and optional limits. Instructions default to `Open`; tools and model default to `Inherit`, with no corresponding model-facing tool/model fields. `Fixed` hides an axis, `Choice` exposes only declared choices, and instruction/model choices resolve before preset fallbacks. Presets are detached when the helper is created. The first preset is the default unless ad hoc instructions suppress that implicit choice. Explicit developer limits override the Core Python profile's 20 handoffs, 20 iterations, 900-second execution timeout and 300-second node timeout.

The default builder borrows the parent's model and permitted tools/providers, creates fresh conversation, application and model state, and selects automatic context management for a stateless model. Other native child policies remain native policies; this does not claim Python constructor or foreign model-router equivalence. The helper removes other vended Swarm instances by identity and application capabilities using the reserved `handoff_to_agent` name. Its own Core handoff coordinator is identified through an internal Core predicate. Inherited developer filters remain authoritative and may intentionally deny that coordinator. A custom builder must transfer a fresh child and cannot retain undelegated or nonremovable reserved provider/plugin capabilities. The parent and borrowed models, tools and clients are not transferred to the helper.

SDK-owned fresh children created by this default builder and by the standard `subagent` helper explicitly use `CheckpointBehavior.Disabled`, so delegated work can run through to a correlated result instead of inheriting the parent checkpoint policy. The parent retains its configured checkpoint behavior. Manual `Agent.CreateChild`, borrowed `Agent.AsTool`, and caller-supplied custom builders retain their separate construction and ownership policies. This reset does not add durable checkpoint propagation or resume to generic wrapped tools, Graph or Swarm orchestration.

Invocation state is copied shallowly for a child team and the exact `request_state` entry is removed. Other state values retain their object identity. Child output whitespace is preserved. Native name and key comparisons use ordinal semantics; arbitrary Python Unicode normalization and foreign model types are not reproduced.

Human requests retain the owned team in memory under its calling parent. All outer answers are collected before that team resumes. Resume requires the saved outer question identity and original call ID, name and detached input; changing those values cannot borrow another paused team. A process restart cannot resume these in-memory children. Parent shutdown closes helper admission, joins accepted calls and disposes owned paused children.

Native cancellation is cooperative and physically joins accepted iterator and owned-child cleanup. The pinned Python helper explicitly excludes cancellation by the parent. A caller directly enumerating `IStreamingTool` can observe an error terminal and its original exception after cleanup. `InvokeResultAsync` and `InvokeAsync` use the existing native stream consumer: a requested original caller token remains hard cancellation before accepting a terminal result, including an error already converted by the helper. This boundary does not promise that a converted error result outranks caller cancellation. Independent exceptions and cleanup failures retain the existing native primary/cleanup handling. Early consumer disposal joins owned children; it does not dispose borrowed models or the parent.

This is a cooperative tool API, not an isolation or security boundary. A custom builder or borrowed client must obey its existing native ownership and concurrency contracts.
