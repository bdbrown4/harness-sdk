# Strands.Agents.Monty

Programmatic tool orchestration using the genuine, MIT-licensed Monty 0.0.23 interpreter. A dedicated Rust worker installs Monty's official bounded allocator; interpreter failures and memory-limit exits remain outside the .NET process. No CPython or shell interpreter runs guest code.

```csharp
var caller = ProgrammaticToolCaller.Create();
await using var agent = new Agent(new AgentOptions
{
    Model = model,
    Tools = new ITool[] { read, write, caller },
});
```

Each invocation starts a fresh native worker. Registered tools appear as async functions that accept keyword arguments. `await` and `asyncio.gather` work in guest code. A single run permits ten concurrent host calls. Structured results, a lone JSON block, and a lone object/array JSON text result become guest data; other text remains a string. Tool failures become catchable `RuntimeError`, while positional arguments raise `TypeError`. Only `print()` output is returned; the final expression stays inside the VM.

`AllowedTools` intersects the current agent registry. All programmatic callers are excluded, including callers registered under other names. Non-identifier tool names receive underscore aliases unless that would shadow a real name or create an ambiguous alias. Inner calls copy invocation state and run the agent's before/after tool hooks without adding conversation history. Unanswered human approval is refused even if a handler swallows its exception; explicit preemptive responses remain usable. This direct path follows the TypeScript baseline's hook path and does not execute ordinary tool middleware, retry policies, or streamed update callbacks.

Defaults are a 15-minute wall deadline, 60 seconds of cumulative guest compute excluding suspended tool time, 256 MiB of guest memory, 1,000 suspensions, recursion depth 1,000, and 200,000 UTF-16 output units. The dedicated worker installs Monty's official allocator; a native hard-limit exit becomes a memory error without terminating the .NET host. The framed JSON boundary additionally limits each frame to 32 MiB and JSON nesting to 64. These transport limits are native implementation policies. Recursion and allocator details follow the pinned engine. Guest filesystem, environment, network and process callbacks are denied; access requires an exposed host tool. Exposed tools keep their own permissions and are not isolated by the VM.

Cancellation terminates and reaps the worker, then cancels outstanding host calls. Arbitrary application tools may ignore cancellation. After a bounded five-second drain, an exception's `Data["Strands.CleanupTask"]` exposes a non-faulting `Task<IReadOnlyList<Exception>>` that completes when those calls and their owned synchronization resources finish. Already observed cleanup failures are attached at `Strands.CleanupErrors`. A caller must await deferred cleanup before assuming its uncooperative tools have stopped. Cancellation diagnostics are process-local and have no binary exception-serialization guarantee.

Package assets are intended for Windows 10+/Server 2016+ x86/x64, Linux x64 with glibc 2.31 or later, and macOS x64/ARM64. The Windows minimum follows the [Rust MSVC target requirements](https://doc.rust-lang.org/rustc/platform-support.html). The loader uses the current process architecture, including Framework shadowcopy, and never searches PATH for a Python or shell interpreter. `WorkerPath` is an explicitly trusted executable override. Source, native-asset and fresh NuGet/publish evidence for each platform is recorded in [validation results](../../docs/VALIDATION_RESULTS.md), separately from the remaining composed release gates. No live provider credentials are needed for its runtime tests.

Build each RID using `native/Build-Native.ps1`; Linux CI uses the pinned Ubuntu 20.04 image and `native/Build-Linux.sh`. Packing requires all five real worker assets and generated dependency notices. `native/Test-Package.ps1` restores an independent consumer into a fresh package cache and runs genuine guest/tool orchestration. The license export preserves originals and exact-source attribution; quote-use's absent publisher license file is explicitly distinguished from the canonical SPDX template supplied with its MIT declaration.
