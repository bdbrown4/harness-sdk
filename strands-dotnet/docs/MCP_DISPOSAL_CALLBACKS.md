# MCP disposal callbacks

`McpClient.DisposeAsync` publishes one shared cleanup task before it cancels task
input. A synchronous task-input cancellation callback may capture that already
published task and return. Later disposal calls observe the same task, including
its original failure. This preserves the existing task-client contract.

That observation does not grant provider or watcher lifecycle reentry. Those
operations still refuse the owning client's synchronous task-shutdown callback.
The synchronous prefixes of protocol and owned transport disposal also continue
to refuse direct disposal of the same client. The private owner stack is restored
on exit and is never retained across an `await`; other clients are unaffected.

A cancellation callback must return before the cleanup it participates in can
settle. Synchronously waiting on its own shared disposal task can deadlock. A
normal `Task` cannot reveal whether the caller will save or block on it, so this
compatibility contract cannot promise universal self-wait prevention. Capture
the task, return from the callback, then join it outside that callback.

Caller-supplied HTTP clients remain borrowed. Cleanup still attempts all owned
resources and preserves primary and distinct secondary failure identities. This
change adds no transport ownership policy, public task wrapper, or guarantee for
arbitrary user callbacks after an asynchronous disposal implementation yields.
