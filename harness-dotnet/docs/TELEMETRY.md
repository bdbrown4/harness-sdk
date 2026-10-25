# Harness environment telemetry

The harness uses the existing .NET OpenTelemetry integration when `OTEL_TRACES_EXPORTER` explicitly selects `console`, `otlp`, or both. An unset or empty selector leaves exporter setup disabled. An endpoint alone does not enable it. Names are trimmed, case-insensitive and deduplicated; `none` takes precedence over every other name. Unknown names produce one diagnostic warning per name, following the TypeScript warning policy.

```csharp
Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "console");
await using var agent = await Harness.CreateAsync(new HarnessOptions { Model = model });
await agent.InvokeAsync("Explain this project");
```

Successful setup installs one process-owned tracing provider. Concurrent harness construction and subagents share it. Disposing an agent leaves that provider active. Changing the selector after successful setup does not reconfigure it; setup failures remain retryable. At process exit, the harness requests a bounded shutdown and disposes its provider. An application that already configures OpenTelemetry can set `UseEnvironmentTelemetry = false` and register `AddStrandsInstrumentation` on its own provider builder.

The `Strands.Agents.OpenTelemetry` dependency configures resource identity and the ordinary `OTEL_EXPORTER_OTLP_*` settings, including per-trace endpoint, protocol, headers, timeout and compression overrides. This is a direct native dependency: disabled setup does not construct a provider or exporter, but the package still references the integration assembly. It does not emulate TypeScript's dynamic module import. Exporter diagnostics cannot replace agent-construction or cleanup failures.

The behavioral references are the pinned [TypeScript setup](../../harness-ts/src/telemetry.ts) and [Python setup](../../harness-py/src/strands_harness/telemetry.py) at repository commit `3bd98e618320f0f286acbfb2252f72388030421b`. Python repeats unknown-selector warnings; the .NET default follows TypeScript's deduplication. Provider lifetime and the caller-managed opt-out use native .NET ownership. The core SDK remains independent of the assembled harness.

Twenty focused contracts cover selector behavior, successful retry, disabled setup, simultaneous and reentrant construction, agent-independent provider lifetime, diagnostics, and real console/OTLP export. The OTLP test sends a real harness trace over loopback TCP with configured endpoint and headers, then flushes through the same shutdown/disposal helper used on process exit. It does not claim that the operating system delivered a process-exit notification. Component evidence includes both modern targets, source-linked .NET Standard execution under Framework x64/x86, all three library assets and cross-target API package validation. The local Framework installation is 4.8.1-or-later; current clean 4.8 and macOS execution remain separate gates. The standalone harness package was built; a complete fresh-consumer package feed still requires genuine Monty assets for all supported RIDs.
