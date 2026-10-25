using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Strands.Agents.Telemetry;

namespace Strands.Harness;

internal static class HarnessTelemetry
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> WarnedSelectors = new(StringComparer.Ordinal);
    private static TracerProvider? _provider;
    private static bool _configured;
    private static bool _configuring;

    internal static void EnsureConfigured()
    {
        var warnings = new List<string>();
        try
        {
            lock (Gate)
            {
                if (_configured || _configuring) return;
                var selectors = (Environment.GetEnvironmentVariable("OTEL_TRACES_EXPORTER") ?? "")
                    .Split(',').Select(value => value.Trim().ToLowerInvariant()).Where(value => value.Length != 0).ToArray();
                if (selectors.Length == 0 || selectors.Contains("none", StringComparer.Ordinal)) return;
                foreach (var selector in selectors)
                    if (selector is not ("otlp" or "console") && WarnedSelectors.Add(selector))
                        warnings.Add("OTEL_TRACES_EXPORTER names '" + selector + "', which the harness does not support; skipping it. Supported values: otlp, console, none.");
                var otlp = selectors.Contains("otlp", StringComparer.Ordinal);
                var console = selectors.Contains("console", StringComparer.Ordinal);
                if (!otlp && !console) return;
                _configuring = true;
                try
                {
                    var provider = Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(new StrandsTelemetryOptions
                    {
                        OtlpExporter = otlp,
                        ConsoleExporter = console
                    }).Build();
                    try { AppDomain.CurrentDomain.ProcessExit += ProcessExit; }
                    catch { DisposeProvider(provider); throw; }
                    _provider = provider;
                    _configured = true;
                }
                finally { _configuring = false; }
            }
        }
        finally { foreach (var warning in warnings) Warn(warning); }
    }

    private static void ProcessExit(object? sender, EventArgs args)
    {
        TracerProvider? provider;
        lock (Gate) { provider = _provider; _provider = null; }
        DisposeProvider(provider);
    }

    private static void DisposeProvider(TracerProvider? provider)
    {
        if (provider is null) return;
        try { provider.Shutdown(5000); }
        catch (Exception error) { Warn("Harness telemetry shutdown failed: " + error.GetType().Name); }
        finally
        {
            try { provider.Dispose(); }
            catch (Exception error) { Warn("Harness telemetry disposal failed: " + error.GetType().Name); }
        }
    }

    private static void Warn(string message)
    {
        try { HarnessLogging.Warning("{Message}", message); }
        catch (Exception) { /* A diagnostic listener cannot fail agent construction or cleanup. */ }
    }

    internal static void ResetForTests()
    {
        TracerProvider? provider;
        lock (Gate)
        {
            AppDomain.CurrentDomain.ProcessExit -= ProcessExit;
            provider = _provider;
            _provider = null;
            _configured = false;
            _configuring = false;
            WarnedSelectors.Clear();
        }
        DisposeProvider(provider);
    }
}
