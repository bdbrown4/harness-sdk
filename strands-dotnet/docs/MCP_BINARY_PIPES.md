# MCP stdio binary input

The convenience-owned MCP transport uses the existing internal owned-child-process implementation shared with sandbox clients and the native Monty worker. Its legacy Windows input is a raw anonymous pipe. Obtaining the protocol input stream does not construct a text writer or emit its encoding preamble.

The higher-level Windows CMD release protocol is unchanged: attach the process to its owned job, explicitly write the release line, await the release acknowledgement, and only then start the official MCP stream transport. The process handle exposed to the job helper is borrowed; the child-process facade retains ownership. Existing cancellation, argument/environment handling, stderr draining, protocol dispatch and physical process joins remain in place. Modern assets retain explicit UTF-8 without a preamble and the existing Unix termination path.

Three new private-console profiles execute the existing ordinary stdio/environment test, finalized-task test and legacy-task elicitation test. Each original test retains its native assertions and owned-process cleanup; the wrapper also checks that the caller console encoding is unchanged. All eleven pre-existing binary-pipe rows remain intact. The trusted fixture allowlist includes the new MCP scenario.

The three profile rows passed on the unchanged producer runtime and are conformance coverage, not a newly reproduced transport failure. A separate retained actual clean .NET Framework 4.8 experiment demonstrates that a BOM emitted before an explicitly separated release write can prematurely acknowledge the CMD gate and expose the release line as protocol input. That controlled experiment does not establish the cause of every earlier full-suite failure. Final validation receipts separately identify local installed Framework 4.8.1, actual clean 4.8, modern assets, source hashes and affected suites.

This is an internal reuse by three real binary-protocol consumers; no new public process framework or tool-protocol implementation is introduced. The original binary-pipe implementation and its source/reference documentation remain prerequisites. Native release-package requirements are unchanged.
