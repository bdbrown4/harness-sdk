using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace Strands.Agents.Telemetry;

/// <summary>Opt-in provider/exporter settings for native .NET OpenTelemetry builders.</summary>
public sealed class StrandsTelemetryOptions
{
    /// <summary>An explicit service name, or null to use the standard environment and Strands default.</summary>
    public string? ServiceName { get; init; }
    /// <summary>An explicit service namespace, or null to use environment and the Strands default.</summary>
    public string? ServiceNamespace { get; init; }
    /// <summary>An explicit application version, or null to use the SDK assembly's informational version.</summary>
    public string? ServiceVersion { get; init; }
    /// <summary>An explicit deployment environment, or null to use environment and the development default.</summary>
    public string? DeploymentEnvironment { get; init; }
    /// <summary>Additional resource attributes, copied when configuring a builder.</summary>
    public IReadOnlyDictionary<string, object> ResourceAttributes { get; init; } = new Dictionary<string, object>();
    /// <summary>Enables console export of the selected signal.</summary>
    public bool ConsoleExporter { get; init; }
    /// <summary>Enables OTLP export of the selected signal. HTTP/protobuf is the default on every supported target.</summary>
    public bool OtlpExporter { get; init; }
    /// <summary>Optional native OTLP configuration, applied after exporter defaults and environment settings.</summary>
    public Action<OtlpExporterOptions>? ConfigureOtlpExporter { get; init; }
    /// <summary>Optional native console-exporter configuration.</summary>
    public Action<ConsoleExporterOptions>? ConfigureConsoleExporter { get; init; }
    /// <summary>Optional native reader configuration, independently applied to each enabled metric exporter.</summary>
    public Action<MetricReaderOptions>? ConfigureMetricReader { get; init; }
    /// <summary>Copies every baggage entry onto started spans when no key predicate is supplied.</summary>
    public bool CaptureBaggage { get; init; }
    /// <summary>Enables baggage copying for keys accepted by this predicate.</summary>
    public Func<string, bool>? BaggageKeyPredicate { get; init; }
}
