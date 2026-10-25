# Stateless sandbox shell tool

`SandboxShell.Create(selected, options)` borrows a selected sandbox;
`Create()` and `CreateForAgent(options)` resolve the invocation Agent's sandbox.
The default name is `shell`; a custom name, description and detached input
schema are supported. Each invocation forwards the literal command and exact
operation token with a default 120-second timeout, or a positive finite timeout
within the native timer range.

The returned JSON contains `output`, `error` and `exit_code`. A nonzero exit is
an ordinary result. The tool consumes the first terminal result and physically
joins iterator disposal before returning. A timeout preserves the same
`SandboxTimeoutException` and appends partial-result JSON with exit code 124.
Cancellation retains the same exception/token. Other failures become
`SandboxShellException` with the original cause. Independent cleanup failures
are retained once by reference without replacing the primary failure.

This is the pinned stateless sandbox shell behavior, distinct from a persistent
Bash session. Existing `Sandbox.ExecuteAsync` behavior is unchanged. Selected
sandboxes are borrowed, and the factory never selects an ambient host fallback.
The component proof uses fake streams; it does not establish actual shell,
Docker, SSH, credential or remote-service execution.
