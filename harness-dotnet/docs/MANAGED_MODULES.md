# Managed harness modules

`HarnessAssemblyLoader` is an opt-in loader for trusted C# assemblies. Portable configuration continues to require explicit application bindings by default. A specific `HarnessConfigBindings` resolver wins over the loader; an explicitly supplied `ModuleResolver` also remains unchanged by `Bind`.

```csharp
var loader = new HarnessAssemblyLoader("./trusted-modules");
var config = HarnessAgentConfig.Parse("""
{
  "tools": [{ "module": "ProjectTools.dll", "language": "csharp" }],
  "builtinTools": [],
  "builtinPlugins": [],
  "skills": false,
  "memory": false,
  "contextManager": false,
  "session": false
}
""");
var options = config.ToOptions(loader.Bind(new HarnessConfigBindings
{
    ModelResolver = _ => applicationModel,
}));
await using var agent = await Harness.CreateAsync(options);
```

The selected directory is application configuration, never a model-authored tool argument. References must identify `.dll` files beneath that directory. Absolute paths outside it, traversal outside it, and symbolic links/reparse points at or below the selected directory are rejected. These checks do not provide isolation or protect against a concurrent filesystem replacement. Assembly initializers, exported getters, and factory code run with the application's process permissions. Only load assemblies that the application trusts.

## Export values and tools

Reference the same compatible Strands packages as the host and deploy the assembly's normal .NET dependencies. The loader uses the runtime's ordinary assembly loading and shared host SDK types. It does not install dependencies or add global assembly-resolution handlers. A precompiled .NET Standard 2.0 fixture is exercised on Framework 4.8, .NET 8 and .NET 10, including SDK, harness, Bedrock and Monty options initialized by that extension.

```csharp
using Strands.Agents;
using Strands.Harness;

[HarnessExport] // export name "default"
public static class ProjectTools
{
    [Tool("Read an application catalog", Name = "catalog_lookup")]
    public static string Lookup(string productId) => Catalog.Read(productId);
}
```

`[HarnessExport("name")]` may name a public static field/property, a public static method marked `[Tool]`, or a public type containing static `[Tool]` methods. Static tool discovery uses `ToolFactory`; it constructs schemas without executing tool bodies. A reference's `export` defaults to `default`. An explicit `Namespace.Type::Member` can select a public static field, property or uniquely named method without a `HarnessExport` attribute. Overloaded member selections require a unique attributed export name. Names are case-sensitive; duplicate attributed export names are rejected before any exported value is read.

Exports for `tools`, `plugins`, `subagents` and `memoryStores` may contain one matching native object or an `IReadOnlyList<T>` of matching nonnull objects. Configuration order and list order are retained. Intervention module exports use `HarnessInterventionValue` or an ordered `IReadOnlyList<HarnessInterventionValue>`. Model and sandbox exports contain one `IModel` or `ISandbox`. `agentConfigModules` exports contain `Func<AgentOptions, AgentOptions>` and keep the existing model-identity/ownership guards and explicit core-options precedence.

Only subagent exports may be parameterless synchronous factory methods or delegates; they return an `Agent` or an ordered list of agents. Factories run when configuration is bound, and specialists are exposed through ordinary `Agent.AsTool`. Other method exports require `[Tool]`; arbitrary factory methods are not invoked. Exported components remain borrowed. The loader neither disposes them nor transfers their resources to the harness; an application that creates disposable exported components remains responsible for their lifetime. Existing explicit core ownership settings remain available.

Invalid or null exports fail before native provider allocation. All loaded tools subsequently pass through the ordinary agent's filtering, schema/name validation and atomic registration. No assembly export bypasses tool selection or approval handling.

## Declared-file root generations

Use `loader.ResolveOptions(config, bindings, baseDirectory)` to opt into configuration-time generations. A nonempty `files` declaration activates this behavior for references resolved by that loader. Existing `Bind`, `Load`, and configurations without declared files retain ordinary process caching. Explicit component resolvers and an existing fallback `ModuleResolver` keep their precedence; the loader does not inspect inputs they resolve.

```csharp
var config = HarnessAgentConfig.Parse("""
{
  "tools": [{ "module": "ProjectTools.dll", "files": ["source-version.txt"] }],
  "session": false, "skills": false, "memory": false, "contextManager": false
}
""");
var options = loader.ResolveOptions(config, new() { ModelResolver = _ => applicationModel });
```

One generation hashes every selected root DLL and declared path before any export is read. Paths are relative to the trusted loader directory; `baseDirectory` retains its existing configuration/session role. Declared paths cannot escape that directory or traverse links. Directory hashing is recursive and ordered, excluding `.git`, `.venv` and `node_modules`. Missing declared paths have a stable marker, so creating one invalidates the generation. Rewriting identical bytes retains the same identity. Concurrent filesystem replacement is not an atomic snapshot or an authorization boundary; publish coherent trusted inputs.

Changing a declared input causes a fresh root image even when the DLL bytes and assembly identity stay unchanged. Unchanged generations reuse their static values on the same loader; exported getters and factories still run on each resolution. Root bytes are captured before callbacks and used directly through `Assembly.Load(byte[])`. A runtime that substitutes a physical/GAC assembly instead of an in-memory image fails before export discovery. Old options and borrowed exports remain usable. No existing Agent is updated.

This bounded feature reimports root assemblies, not their private dependency graphs. Dependencies must resolve from the host deployment, sharing the host SDK assemblies. Merely listing a dependency DLL in `files` invalidates the root; it does not reload that dependency. Byte-loaded root assemblies have an empty `Assembly.Location`, so application code must receive any required resource directory explicitly. The loader retains generation caches, and returned objects or the runtime may retain assemblies for the process lifetime. There is no unload guarantee, collectible scope, directory watcher, or generation disposal API.

## Relationship to the pinned loaders

The reference baseline is `3bd98e618320f0f286acbfb2252f72388030421b`. Both [TypeScript `loadMany`/`loadReference`](https://github.com/strands-agents/harness-sdk/blob/3bd98e618320f0f286acbfb2252f72388030421b/harness-ts/src/config.ts#L613) and [Python `_load_many`/`_load_reference`](https://github.com/strands-agents/harness-sdk/blob/3bd98e618320f0f286acbfb2252f72388030421b/harness-py/src/strands_harness/config.py#L497) preserve one-or-list exports, select named/default exports, and invoke callable specialist-agent exports. Portable agent-config overlays still precede the convenience model/name fields and explicitly supplied native core options.

The native loader deliberately loads compiled C# code. `language: "csharp"` is a native schema extension; shared Python/TypeScript language metadata still round-trips, and this loader rejects foreign declarations. An explicit application binding can interpret such metadata itself. Python's optional `agent` export fallback, package imports, asynchronous TS specialist factories, and foreign dependency installation are not emulated. `dependency` metadata remains inert. Trimming/AOT discovery preservation is the application's responsibility.

Ordinary `Bind`/`Load` rejects a same-identity assembly already loaded from another origin before reading exports; select its original path or a distinct name. Framework shadow copies retain their original URI for that comparison. The explicit root-generation method is a separate native contract: TS computes generation hashes even without a `files` declaration and propagates identity into local ESM/CJS/JSON imports. Native root generations do not claim that transitive behavior. Python's harness caches imports; its SDK's separately opt-in `./tools` watcher is also unimplemented here. These limits are separate from overall harness parity.
