# Strands.Agents.OpenTelemetry

Optional native .NET OpenTelemetry provider and exporter configuration for Strands tracing and metrics. The package targets `netstandard2.0`, `net8.0` and `net10.0`; the standard asset supports Framework 4.8 consumers. It uses the official OpenTelemetry .NET exporters. The core SDK's `ActivitySource` and `Meter` instruments work independently of this package.

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Strands.Agents.Telemetry;

var options = new StrandsTelemetryOptions
{
    ServiceName = "orders",
    OtlpExporter = true
};
using var traces = Sdk.CreateTracerProviderBuilder()
    .AddStrandsInstrumentation(options).Build();
using var metrics = Sdk.CreateMeterProviderBuilder()
    .AddStrandsInstrumentation(options).Build();
```

The application owns both providers and their shutdown. The extensions preserve existing resource attributes and application identity, with explicit Strands options taking precedence. Additional native processors, readers, samplers and resource configuration can be composed on the same builders. No separate global provider registry or automatic process-exit handler is installed.

`ConsoleExporter` and `OtlpExporter` are independently opt-in. Console traces use the native simple processor; metrics use native periodic readers. Named native registrations keep exporter and reader options separate from each other and from the application's default registrations. OTLP uses HTTP/protobuf by default on every target. Generic `OTEL_EXPORTER_OTLP_*` settings use the native exporter configuration. Standard trace/metric endpoint, protocol, headers, timeout and compression settings override their generic equivalents; an explicit callback runs last. Signal-specific endpoints include the complete `/v1/traces` or `/v1/metrics` path. Invalid signal-specific endpoints, protocols, timeouts or compression values fail during startup with the setting name. `ConfigureOtlpExporter`, `ConfigureConsoleExporter` and `ConfigureMetricReader` expose the official option types, so applications can configure endpoints, authentication, timeouts, clients and reader behavior directly.

`CreateResourceBuilder` supplies explicit Strands defaults independently of a provider: service name `strands-agents`, namespace `strands`, development environment, SDK assembly version and native OpenTelemetry .NET identification. Standard resource environment attributes override defaults; explicit options override environment. Resource dictionaries and array values are copied during configuration.

Set `CaptureBaggage` to copy every ambient OpenTelemetry baggage entry onto started spans, or supply `BaggageKeyPredicate` to select keys. Baggage copying is disabled by default. A predicate failure omits that entry and preserves the application operation. Message content and redaction remain controlled by the core tracer.

Core agents instrument invocation, model and tool activity and expose metrics and traces on their results. The Harness supports environment-based telemetry configuration. `SdkLogging` and `HarnessLogging` separately accept borrowed `Microsoft.Extensions.Logging` loggers; these trace/metric exporter extensions do not replace application logging configuration. Core tracing controls message capture and redaction.

See the [telemetry contract](../../docs/TELEMETRY.md) and [validation results](../../docs/VALIDATION_RESULTS.md) for supported instruments, collector/exporter checks, package consumers and source-specific platform evidence. Historical component passes and localhost collector checks do not close the remaining composed release gates or establish live collector interoperability.
