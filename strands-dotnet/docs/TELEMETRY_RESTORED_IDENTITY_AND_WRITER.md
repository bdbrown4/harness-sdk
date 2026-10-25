# Restored identity and Writer usage accounting

When lazy initialization restores a stable agent identity, the existing invocation activity receives that committed identity before plugin initialization. Completed exported activities therefore correlate with the persisted agent, including when a later plugin fails. Explicit initialization continues to use the restored identity when the invocation activity starts. Activity creation order, initialization failure behavior and public APIs are unchanged.

Writer retains its existing terminal zero-valued metadata event when the response omits usage. That placeholder now sets `UsageReported = false`. An actual provider usage object, including explicitly reported zero counters, replaces the placeholder with a reported measurement. Existing message/metadata ordering remains intact, while Agent metrics distinguish missing measurement from measured zero.

Five contracts cover lazy and explicit restoration, plugin failure after restoration, and absent versus reported-zero Writer usage. Three cases have recorded runtime red-to-green evidence and two are first-pass conformance checks. The local regression matrix passes 884 executions across six TRX files: all telemetry and existing Writer contracts through both modern Core and compatibility targets, plus physical Framework x64/x86 compatibility hosts. The Framework runs use the installed release 534296; they do not certify a clean 4.8-only installation. Bounded independent review found no actionable P1/P2 findings.

This is a separate increment after the usage-presence capture. Its evidence does not imply integrated verification, exhaustive provider measurement detection, specialized telemetry parity or current macOS execution.
