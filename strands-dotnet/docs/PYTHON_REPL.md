# Python source through a selected sandbox

This isolated first increment adds Core source execution and an explicitly bound
optional `PythonRepl.Create(sandbox)` tool. It borrows that selected environment;
it does not resolve an Agent environment or choose a host fallback.

The pinned TypeScript/Python tool starts a fresh interpreter for each call,
defaults to `python3` and 120 seconds, and returns `output`, `error`, `exit_code`.
Nonzero interpreter exits are ordinary results. Timeout errors carry partial
JSON with exit code 124; caller cancellation remains an exception.

Agent/default-local, Harness selected-environment routing and sandbox-vended
tool registration remain subsequent reviewed stages of the full-parity plan.
