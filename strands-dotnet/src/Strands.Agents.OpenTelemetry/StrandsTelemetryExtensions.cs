using System.Globalization;
using System.Reflection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Strands.Agents.Telemetry;

/// <summary>Configures caller-owned OpenTelemetry builders for Strands instruments.</summary>
public static class StrandsTelemetryExtensions
{
    /// <summary>Adds Strands tracing, resource metadata and optional exporters to a caller-owned builder.</summary>
    /// <param name="builder">The application's tracing builder.</param>
    /// <param name="options">Optional Strands configuration.</param>
    /// <returns>The same builder for continued native configuration.</returns>
    public static TracerProviderBuilder AddStrandsInstrumentation(this TracerProviderBuilder builder, StrandsTelemetryOptions? options = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        options ??= new();
        var metadata = CreateResourceBuilderCore(options, out var selected).Build();
        builder.AddSource(AgentMeter.MeterName).ConfigureResource(resource =>
            resource.AddAttributes(metadata.Merge(resource.Build()).Merge(selected).Attributes));
        if (options.CaptureBaggage || options.BaggageKeyPredicate is not null) builder.AddProcessor(new BaggageSpanProcessor(options.BaggageKeyPredicate));
        if (options.ConsoleExporter) builder.AddConsoleExporter("Strands.Traces.Console", console => options.ConfigureConsoleExporter?.Invoke(console));
        if (options.OtlpExporter) builder.AddOtlpExporter("Strands.Traces.Otlp", exporter => ConfigureOtlp(exporter, options, "TRACES"));
        return builder;
    }

    /// <summary>Adds Strands metrics, resource metadata and optional exporters to a caller-owned builder.</summary>
    /// <param name="builder">The application's metrics builder.</param>
    /// <param name="options">Optional Strands configuration.</param>
    /// <returns>The same builder for continued native configuration.</returns>
    public static MeterProviderBuilder AddStrandsInstrumentation(this MeterProviderBuilder builder, StrandsTelemetryOptions? options = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        options ??= new();
        var metadata = CreateResourceBuilderCore(options, out var selected).Build();
        builder.AddMeter(AgentMeter.MeterName).ConfigureResource(resource =>
            resource.AddAttributes(metadata.Merge(resource.Build()).Merge(selected).Attributes));
        if (options.ConsoleExporter) builder.AddConsoleExporter("Strands.Metrics.Console", (console, reader) =>
        {
            options.ConfigureConsoleExporter?.Invoke(console);
            options.ConfigureMetricReader?.Invoke(reader);
        });
        if (options.OtlpExporter) builder.AddOtlpExporter("Strands.Metrics.Otlp", (exporter, reader) =>
        {
            ConfigureOtlp(exporter, options, "METRICS");
            options.ConfigureMetricReader?.Invoke(reader);
        });
        return builder;
    }

    /// <summary>Creates standard Strands resource metadata with native .NET SDK identification.</summary>
    /// <param name="options">Optional resource configuration.</param>
    /// <returns>A resource builder the caller may further configure.</returns>
    public static ResourceBuilder CreateResourceBuilder(StrandsTelemetryOptions? options = null) => CreateResourceBuilderCore(options, out _);

    private static ResourceBuilder CreateResourceBuilderCore(StrandsTelemetryOptions? options, out Resource selected)
    {
        options ??= new();
        if (options.ResourceAttributes is null) throw new ArgumentException("Resource attributes cannot be null.", nameof(options));
        var assembly = typeof(Agent).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        var defaults = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["service.name"] = "strands-agents",
            ["service.namespace"] = EnvironmentValue("OTEL_SERVICE_NAMESPACE", "strands"),
            ["service.version"] = version,
            ["deployment.environment"] = EnvironmentValue("OTEL_DEPLOYMENT_ENVIRONMENT", "development")
        };
        var explicitAttributes = new Dictionary<string, object>(StringComparer.Ordinal);
        AddIdentity(explicitAttributes, "service.name", options.ServiceName);
        AddIdentity(explicitAttributes, "service.namespace", options.ServiceNamespace);
        AddIdentity(explicitAttributes, "service.version", options.ServiceVersion);
        AddIdentity(explicitAttributes, "deployment.environment", options.DeploymentEnvironment);
        foreach (var attribute in options.ResourceAttributes)
        {
            if (string.IsNullOrWhiteSpace(attribute.Key) || attribute.Value is null) throw new ArgumentException("Resource attributes require a name and value.", nameof(options));
            explicitAttributes[attribute.Key] = attribute.Value is Array array ? array.Clone() : attribute.Value;
        }
        selected = new Resource(explicitAttributes);
        return ResourceBuilder.CreateEmpty().AddTelemetrySdk().AddAttributes(defaults)
            .AddEnvironmentVariableDetector().AddAttributes(explicitAttributes);
    }

    private static string EnvironmentValue(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return value is null or "" ? fallback : value;
    }

    private static void ConfigureOtlp(OtlpExporterOptions exporter, StrandsTelemetryOptions options, string signal)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")))
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        // Native AddOtlpExporter reads generic OTLP settings only. Apply the
        // standard per-signal overrides before the caller's final configuration.
        var prefix = "OTEL_EXPORTER_OTLP_" + signal + "_";
        var protocol = SignalSetting(prefix, "PROTOCOL");
        if (protocol is not null)
        {
            exporter.Protocol = protocol.ToLowerInvariant() switch
            {
                "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
#pragma warning disable CS0618 // Honor explicitly requested native gRPC configuration.
                "grpc" => OtlpExportProtocol.Grpc,
#pragma warning restore CS0618
                _ => throw InvalidSetting(prefix + "PROTOCOL")
            };
        }
        var endpoint = SignalSetting(prefix, "ENDPOINT");
        if (endpoint is not null)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw InvalidSetting(prefix + "ENDPOINT");
            exporter.Endpoint = uri;
        }
        var headers = SignalSetting(prefix, "HEADERS");
        if (headers is not null) exporter.Headers = headers;
        var timeout = SignalSetting(prefix, "TIMEOUT");
        if (timeout is not null)
        {
            if (!int.TryParse(timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds <= 0)
                throw InvalidSetting(prefix + "TIMEOUT");
            exporter.TimeoutMilliseconds = milliseconds;
        }
        var compression = SignalSetting(prefix, "COMPRESSION");
        if (compression is not null)
        {
            exporter.Compression = compression.ToLowerInvariant() switch
            {
                "gzip" => OtlpExportCompression.GZip,
                "none" => OtlpExportCompression.None,
                _ => throw InvalidSetting(prefix + "COMPRESSION")
            };
        }
        options.ConfigureOtlpExporter?.Invoke(exporter);
    }

    private static string? SignalSetting(string prefix, string setting)
    {
        var value = Environment.GetEnvironmentVariable(prefix + setting)?.Trim();
        return value is null or "" ? null : value;
    }

    private static ArgumentException InvalidSetting(string name) => new("Invalid OpenTelemetry environment setting: " + name + ".");

    private static void AddIdentity(Dictionary<string, object> attributes, string name, string? value)
    {
        if (value is null) return;
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Resource identity cannot be empty.", "options");
        attributes[name] = value;
    }
}
