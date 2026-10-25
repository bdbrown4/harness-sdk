# MCP diagnostic logging

MCP discovery and task diagnostics use `SdkLogging`; harness server configuration and connection diagnostics use the separate `HarnessLogging` facade. Both use the existing default Trace/console bridge until an application supplies its own standard `ILogger` or logger factory. Supplied logging resources remain borrowed.

The migration covers overlong prefixed tool names, failed cooperative task cancellation, failed progress observers, skipped server configurations/connections, and ignored client-credentials fields. Existing rendered messages are retained. Caught configuration, connection, progress and cancellation failures are passed as the original exception to the logger. Ignored authentication fields report their names, without serializing credential values.

These warnings are per occurrence. They do not add a warning-once cache or reset the existing SDK-wide warning-once cache. The shared task warning helper also retains its one-argument shape for legacy task diagnostics.

Logger failures are isolated by the existing dispatcher. A failed progress observer does not replace a completed tool result; failed cooperative cancellation does not replace the original caller cancellation. `ContinueOnError` still controls whether invalid configurations, failed connections or overlong prefixed names are skipped. Logging does not change transport ownership, task budgets, native protocol selection, stdio cleanup or cancellation-token contracts.

Validation uses real MCP discovery/task protocol exchanges, native notification handling, and TCP-loopback initialization failures. The exact source is the accepted `1cd5d26` archive plus the named immutable logging13 and stdio15 prerequisites. This is a diagnostic migration; it does not claim new MCP protocol coverage or clean Framework 4.8/cloud execution.
