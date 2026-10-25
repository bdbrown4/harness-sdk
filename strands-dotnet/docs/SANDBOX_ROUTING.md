# Agent and Harness sandbox routing

`AgentOptions.Sandbox` selects a borrowed Core sandbox. Without an explicit
selection, each Agent has its own stable lazy `NotASandboxLocalEnvironment`;
constructing an Agent or reading this property starts no process. An inherited
unbound `PythonRepl.Create()` or `CreateForAgent(options)` resolves the invocation
Agent on each call. The existing `Create(sandbox, options)` stays bound and wins.
The distinct factory name preserves the existing single-null bound call without
overload ambiguity. A default child has an independent lazy local environment;
an explicit sandbox is shared unless the child's options replace or reset it.

An explicit `FileSystem` wins. Otherwise an explicit Core sandbox supplies the
Agent filesystem without materializing the local default; the previous null
filesystem behavior remains for an Agent with neither option. Local files retain
the native `LocalFileSystem` limits and link policy. This environment uses host
permissions and provides no OS isolation. Code starts a fresh interpreter with
UTF-8 source on stdin. Commands require `sh`; the shell executable option never
selects a Python interpreter.

Harness selects code authority from an explicit Core sandbox first, then its
selected Harness sandbox. `SandboxAdapter` preserves the exact Core instance.
Custom source execution is opt-in through `ICodeSandbox`; text-only or legacy
command-only selections fail closed for Python. Code options are validated and
detached before entering that callback; generated source stdin is not passed as
a separate stdin request. An old `ICommandSandbox` still receives its aggregate
command options and capture limit, and cannot represent per-call stdin, cwd, or
environment overrides. Unsupported operations fail rather than use host code or
files. Binary operations require the selected binary filesystem capability.

`LocalFileSandbox` advertises code through a lazy Core local environment with a
frozen cwd, environment, timeout, and capture policy. SDK filesystem authority
uses explicit Core `FileSystem`, then Core sandbox, then selected Harness files;
existing built-in file tools continue to use the selected Harness files. With an
explicit Core sandbox, child rebinding also changes the implicit SDK filesystem.
The older Harness profile that materializes a separate filesystem keeps that
filesystem when only a child's sandbox is replaced or reset. This known split is
not a claim of complete child authority parity.

Borrowed sandboxes are never disposed by Agent or Harness. Stream aggregation
physically joins iterator cleanup and retains a primary failure with independent
cleanup diagnostics. The focused conformance tests use fake backends only;
type/policy checks never enumerate a local stream. A prior owned Windows VM
proved four basic local cases on modern .NET 8; the newly configured local-policy
route still requires its own real-process rerun. These tests do not establish
real Framework, Linux, macOS, or `sh` execution. Sandbox-vended tool registration
and richer source filesystem metadata/delete semantics remain separate work.
