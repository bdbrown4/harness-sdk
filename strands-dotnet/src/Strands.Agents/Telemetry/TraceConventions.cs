namespace Strands.Agents.Telemetry;

internal sealed class TraceConventions
{
    private readonly HashSet<string> _unredacted = new(StringComparer.Ordinal);
    private readonly List<string> _prefixes = [];
    private readonly bool _redactionEnabled;

    internal TraceConventions(bool usePythonToolErrorStatus = false)
    {
        UsePythonToolErrorStatus = usePythonToolErrorStatus;
        var tokens = (Environment.GetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN") ?? "")
            .Split(',').Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        Latest = tokens.Contains("gen_ai_latest_experimental", StringComparer.Ordinal);
        IncludeToolDefinitions = tokens.Contains("gen_ai_tool_definitions", StringComparer.Ordinal);
        UseLatestInvocationTokens = tokens.Contains("gen_ai_use_latest_invocation_tokens", StringComparer.Ordinal);
        Langfuse = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANGFUSE_BASE_URL"))
            || new[] { "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT" }
                .Any(name => (Environment.GetEnvironmentVariable(name) ?? "").Contains("langfuse"));
        AttributesOnly = Langfuse || tokens.Contains("gen_ai_span_attributes_only", StringComparer.Ordinal);
        var allowlist = tokens.FirstOrDefault(value => value.StartsWith("gen_ai_unredacted_attributes=", StringComparison.Ordinal));
        _redactionEnabled = allowlist is not null;
        if (allowlist is null) return;
        foreach (var raw in allowlist.Substring(allowlist.IndexOf('=') + 1).Split(';'))
        {
            var value = raw.Trim();
            if (value.Length == 0) continue;
            if (value.EndsWith("*", StringComparison.Ordinal)) _prefixes.Add(value.Substring(0, value.Length - 1));
            else _unredacted.Add(value);
        }
    }

    internal bool Latest { get; }
    internal bool IncludeToolDefinitions { get; }
    internal bool AttributesOnly { get; }
    internal bool Langfuse { get; }
    internal bool UsePythonToolErrorStatus { get; }
    internal bool UseLatestInvocationTokens { get; }
    internal string Content(string canonicalName, Func<string> value) => !_redactionEnabled || _unredacted.Contains(canonicalName)
        || _prefixes.Any(prefix => canonicalName.StartsWith(prefix, StringComparison.Ordinal)) ? value() : "[REDACTED]";
}
