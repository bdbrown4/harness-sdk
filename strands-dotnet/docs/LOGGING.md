# Native SDK and harness logging

`SdkLogging` (`Strands.Agents`) and `HarnessLogging` (`Strands.Harness`) accept the standard `Microsoft.Extensions.Logging.ILogger` or `ILoggerFactory`. Configure each facade explicitly; they have independent process-wide logger references and warning deduplication. Both assemblies directly reference `Microsoft.Extensions.Logging.Abstractions` 10.0.10, which supports the netstandard2.0, net8.0, and net10.0 assets.

```csharp
SdkLogging.ConfigureLogging(loggerFactory);
HarnessLogging.ConfigureLogging(loggerFactory);
```

Factories create the `Strands.Agents` and `Strands.Harness` categories respectively. Supplied loggers and factories are borrowed: replacing the logger, shutting down an agent, or disposing an agent never disposes host logging resources. `Logger` returns the current reference, which can be retained and passed back to restore a previous configuration. Replacement is atomic; a diagnostic already in progress may finish through its captured logger.

The default logger sends warnings and errors to standard error and retains the existing .NET Trace listener bridge. Information reaches Trace listeners, while debug/trace levels are disabled. The information bridge is a native compatibility behavior: the pinned TypeScript defaults make both debug and information no-ops. Configuring a host logger replaces this default routing; normal host filtering, formatting, scopes, and providers belong to that logger.

Internal warn-once helpers deduplicate exact messages separately for SDK and harness, for the process lifetime, across logger replacement. A successful disabled/no-op call consumes the message, matching the TypeScript set-after-call behavior. A thrown enabled check or emission remains retryable. A reservation suppresses concurrent or same-message reentrant calls without holding a lock during application callbacks. No public reset or new logging protocol is introduced.

Diagnostic templates use named structured fields. Values are formatted only after the host reports the level enabled; array values are copied before passing them to the logger. Existing rendered diagnostics are retained, including the sorted unknown-configuration-key warning. Where an original exception is available it is passed through `ILogger` as well as retaining its prior rendered message. Logger/listener exceptions are observational and do not replace parsing results, provider failures, approval decisions, or accepted background work. The default attempts Trace and console independently.

This increment routes eligible core model, skills, memory, goals, injection and intervention diagnostics and harness configuration, model, plugin and telemetry diagnostics. Active MCP, classic conversation/context/session work and Agent lifecycle partials remain separate owner-coordinated migrations; optional Bedrock/Bidi packages are outside this component. The scope JSON lists every owned path. This is a bounded logging integration, not a claim that every SDK family or optional package is complete.

The source contract is pinned at `3bd98e618320f0f286acbfb2252f72388030421b`: `strands-ts/src/logging/{logger,warn-once}.ts` and `harness-ts/src/logging.ts`. Python uses its standard module logging system; the native implementation uses the corresponding standard .NET abstraction. Microsoft's [library logging guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/library-guidance) supports host-configured `ILogger`/`ILoggerFactory` and structured logging. Scott Hanselman's [Worker service example](https://www.hanselman.com/blog/dotnet-new-worker-windows-services-or-linux-systemd-services-in-net-core) demonstrates host-supplied `ILogger<Worker>` in cross-platform services; it is an integration example, not an endorsement of this SDK or API.
