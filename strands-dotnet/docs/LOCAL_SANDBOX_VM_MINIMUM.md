# Root-only local environment minimum: historical scaffold

This document preserves the original four-case compile scaffold and its Root-only execution contract. The original author built net8 without executing tests, interpreters, VMs or WSL. The local implementation and selected routing were subsequently completed and integrated with separate component evidence; current real-interpreter outcomes are recorded in [validation results](VALIDATION_RESULTS.md).

Root selects only `Strands.Agents.Tests.Sandbox.NotASandboxLocalEnvironmentTests`
in its owned VM, binds `STRANDS_PYTHON_EXECUTABLE` to the exact staged absolute
CPython path, and prepends that directory to the testhost's process-local PATH.
No Bash/sh path is added. Root also supplies `PYTHONNOUSERSITE`,
`PYTHONDONTWRITEBYTECODE`, and UTF8 `PYTHONIOENCODING`. Each code test asserts the
actual `sys.executable`; no ambient interpreter or conditional skip is accepted.

The four cases cover UTF8 source delivery, entered cwd/environment, fresh
interpreter state with persistent files and stdin EOF, and native file byte/text
roundtrip/list/delete. Temporary directories are unique, below the runner's
chosen TEMP, and removed only after awaited operations. Cleanup preserves an
existing primary failure.

The file test follows existing native IFileSystem: missing delete is a no-op, text reads retain native bounds/UTF8 policy and byte writes retain native atomic replacement/link policy. Source directory metadata/size and missing-unlink differences remain open. The four cases now pass modern Windows, Linux .NET8/.NET10 and genuine Framework4.8 x64/x86; those bounded results do not establish complete filesystem, stateless shell, remote interpreter or full sandbox parity.