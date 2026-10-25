# Attributed plugins

`PluginFactory.FromObject(instance, name, initialize)` creates an `IPlugin` from explicitly marked public instance methods. The target remains caller-owned. Existing explicit `IPlugin` implementations and `ToolFactory.FromObject` keep their current behavior.

```csharp
sealed class AuditPlugin
{
    [Hook]
    public void Before(BeforeInvocationEvent value)
        => value.Agent.State.Set("invocationObserved", true);

    [Tool("Echoes the supplied text.", Name = "echo")]
    public string Echo(string text) => text;
}

var plugin = PluginFactory.FromObject(new AuditPlugin(), "application.audit");
var agent = new Agent(new AgentOptions { Model = model, Plugins = [plugin] });
```

The optional initializer is awaited for each agent after session restoration and before automatic registration. Hook methods accept one typed class/interface implementing `IAgentStreamEvent`, optionally followed by `CancellationToken`. Supported returns are synchronous `void`, `Task` and `ValueTask`; asynchronous callbacks are awaited. Async void, open generic methods, ref/out parameters, incompatible signatures, ambiguous overload names, duplicate tool names and methods marked as both hook and tool fail during discovery. Unmarked, private and static methods are outside instance discovery. Original callback exceptions and cancellation tokens are preserved.

`HookAttribute.Order` uses the ordinary hook priority policy. Lower priorities run first; equal-priority after hooks unwind in reverse registration order. Within the native class hierarchy, discovery uses parent metadata declaration slots first and binds each name to its most-derived public method. A parent slot is retained even when only the child method is marked. An unmarked child override suppresses its parent's marker. This supplies the Python parent-first/child-bound discovery capability through C# single inheritance; it does not claim Python multiple-inheritance or source-text ordering for partial classes. Existing ToolFactory discovery ordering is unchanged.

One declaration can bind to several agents. Each agent owns its automatic hook registrations; disposing one does not remove sibling or application registrations. A failed agent initialization removes this adapter's automatic hooks and admitted tools, including when a later plugin fails. User initializer side effects and manually registered hooks are not transactional. The adapter does not initialize or dispose the borrowed target outside the supplied delegate. Shared targets and initializer delegates must support their application's concurrency.

The pinned ordinary-Agent Python and TypeScript registries initialize plugins during agent setup. This adapter does not introduce an unrelated runtime plugin add/remove API. Tool methods use the existing ordinary rich/streaming ToolFactory binding and schema rules.
