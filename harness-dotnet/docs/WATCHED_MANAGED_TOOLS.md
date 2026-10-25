# Watched managed tool declarations

`HarnessAssemblyLoader.CreateToolProvider(configuration)` creates a borrowed
`IToolProviderUpdates` from the configuration's `tools` declarations. At least one
tool declaration must contain a nonempty `files` list. Only tool exports are
resolved. Model, plugin, memory, subagent, and other configuration factories are
not called by this provider.

Attach the returned provider through the ordinary typed options:

```csharp
var provider = loader.CreateToolProvider(toolConfiguration);
await using var agent = await Harness.CreateAsync(new HarnessOptions
{
    AgentOptions = new AgentOptions
    {
        Model = applicationModel,
        ToolProviders = [provider],
        EnableToolProviderUpdates = true,
    },
});
```

The application supplies the loader, parsed tool configuration, and model in this
example. The tool configuration is a separate declaration input; resolving its
same tools into `HarnessOptions.Tools` as well would create duplicate registrations.
There is no implicit portable JSON watcher. With the ordinary
`EnableToolProviderUpdates = false` default, an agent loads its initial tools once
and allocates no listener for that consumer. A different live consumer may still
update the shared provider cache.

## Fixed source authority

Creation snapshots the normalized tool references and performs lexical checks.
Creation and `AddConsumerAsync` do not access the filesystem or execute exports.
The loader's application-selected directory, module paths, and declared inputs
remain fixed for the provider's lifetime. Loading performs the existing complete
path/link checks. These checks use process filesystem permissions and do not
provide a sandbox.

Each export must be `ITool` or `IReadOnlyList<ITool>`, including an empty list.
Membership is copied, null entries and incompatible exports reject the complete
candidate, and declaration order is retained. Original capability objects remain
intact: rich, streaming, structured, and agent-tool capabilities are not wrapped.
The SDK publisher applies the consuming agent's filter, captured specification,
compiled schema, collision checks, and invocation safe points. An application
getter can execute arbitrary trusted code; rejected publication cannot roll back
its external effects.

One source worker captures all declared roots and hashes their explicit inputs.
It checks another capture before getters and a final capture afterward, together
with the notification sequence, before accepting a candidate. An observed change
during this work discards the candidate. This is not an OS-atomic multi-file
transaction and cannot freeze unrelated files read by application getters.
Unchanged content retains cached tool identity. Returning to an earlier content
hash can reuse that retained root generation.

## Events and failures

The first live consumer arms one recursive `FileSystemWatcher` before its final
initial capture. Changed, created, deleted, and renamed events invalidate only
selected paths or their affected ancestors/descendants. Rename never changes the
declared module path. Declared directories are recursive; `.git`, `.venv`, and
`node_modules` are excluded during directory hashing. Missing declared side
inputs retain the existing missing-input fingerprint and can appear later.
This provider does not discover arbitrary new assemblies as tool declarations.

A partial image, missing module, invalid export, or throwing getter after a
successful load retains the last accepted batch and reports a warning. A later
relevant event can recover it. Initial failures are returned to the caller and a
later load can retry. There is no blind retry timer or public refresh operation.
Monitoring errors, including buffer overflow, terminate that source generation:
the listener closes, admitted work joins, and each subscriber receives the same
terminal error independently. Further loads/watches return that error until all
consumers leave; remove and re-add after repairing the source starts a listener
generation again. The agent retains its last published table.

## Consumer lifetime

Consumer IDs have idempotent set semantics. Repeated declarations for one agent
can own multiple independent subscriptions; removing that consumer closes all
of them. A source cache is accepted before application callbacks run. Subscriber
workers are serialized independently and coalesce pending full snapshots, so a
slow subscriber does not block the source or another subscriber.
Pending loads and watches retain their original admission receipt. Removing and
re-adding the same consumer ID cannot revive an operation from the old admission.

The watch token controls establishment only. A canceled caller stops its wait
without canceling another admitted consumer's source work. Failed establishment
closes its provisional subscription. Lease disposal joins its admitted callback;
the last consumer removal cancels and physically joins source capture, listener
cleanup, and deliveries. Callbacks/getters must return cooperatively. There is
no timeout that reports complete while owned work remains active. Own-lifecycle
reentry from an active getter, observer, diagnostic, or cleanup callback is refused.
This also applies when a synchronous callback restores an older captured execution
context. Once callbacks return, their captured contexts and escaped tasks do not
prevent later independent lifecycle operations.
Tools, the provider, loader, and application models remain borrowed. Original
independent failure identity precedes cleanup errors, which use the existing
`Strands.CleanupErrors` diagnostic convention.

## Language and integration limits

All three library targets use the accepted root-image generation loader and host
dependency deployment. There is no private dependency graph, collectible context,
unload guarantee, foreign-source interpreter, or automatic replacement of the
outer Harness delegation/Bidi catalogs. Running and queued background work retain
their originally admitted SDK bindings; a removed name is not a physical-drain
signal for disposing old module tools.

At pinned upstream commit `3bd98e618320f0f286acbfb2252f72388030421b`, Python's
`tools/watcher.py:52-67,98-135` watches opt-in top-level tool directories and
reloads modified `.py` paths, with class-global observer/registry fan-out. It does
not define this fixed-declaration create/delete/rename or joined-lease contract.
Python's loader explicitly retains cached sibling imports
(`tools/loader.py:35-37`). TypeScript Harness file generations are option-rebuild
imports (`harness-ts/src/config.ts:398-455,686-694` and
`config-module-loader.ts:112-137`), rather than a running-agent watcher. Native
live publication uses `IToolProviderUpdates`; it does not create a second registry.
