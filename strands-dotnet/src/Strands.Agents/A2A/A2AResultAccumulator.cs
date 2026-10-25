using System.Text;
using System.Text.Json;

namespace Strands.Agents.A2A;

internal sealed class A2AResultAccumulator(A2AAgentOptions options)
{
    private readonly Dictionary<string, List<IReadOnlyList<JsonElement>>> _artifacts = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
    private IReadOnlyList<JsonElement> _fallback = [];
    private long _size, _fallbackSize;
    private JsonElement _final;
    private string? _taskId, _contextId, _state;

    internal void Observe(JsonElement value)
    {
        _final = value;
        var kind = A2AJson.RequiredString(value, "kind");
        if (kind == "message") { SetFallback(Parts(value)); _taskId = A2AJson.OptionalString(value, "taskId"); _contextId = A2AJson.OptionalString(value, "contextId"); return; }
        _taskId = A2AJson.RequiredString(value, kind == "task" ? "id" : "taskId");
        _contextId = A2AJson.RequiredString(value, "contextId");
        if (kind == "artifact-update") { AddArtifact(A2AJson.RequiredObject(value, "artifact"), A2AJson.OptionalBoolean(value, "append")); return; }
        var status = A2AJson.RequiredObject(value, "status");
        _state = A2AJson.RequiredString(status, "state");
        SetFallback(status.TryGetProperty("message", out var message) ? Parts(message) : []);
        if (kind != "task" || !value.TryGetProperty("artifacts", out var artifacts)) return;
        if (artifacts.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("A2A task artifacts must be an array.");
        _artifacts.Clear(); _order.Clear(); _sizes.Clear(); _size = 0;
        foreach (var artifact in artifacts.EnumerateArray())
        {
            if (_artifacts.ContainsKey(A2AJson.RequiredString(artifact, "artifactId"))) throw new A2AProtocolException("An A2A task snapshot contains duplicate artifact identifiers.");
            AddArtifact(artifact, false);
        }
    }
    private void AddArtifact(JsonElement artifact, bool append)
    {
        var id = A2AJson.RequiredString(artifact, "artifactId");
        var parts = Parts(artifact);
        var size = Size(parts);
        _sizes.TryGetValue(id, out var previous);
        var overhead = 64L + Encoding.UTF8.GetByteCount(id);
        var artifactSize = (append && previous > 0 ? previous : overhead) + size;
        var next = _size - previous + artifactSize;
        CheckSize(next + _fallbackSize);
        if (!_artifacts.TryGetValue(id, out var current)) { _artifacts.Add(id, current = []); _order.Add(id); }
        if (!append) current.Clear();
        if (parts.Count > 0) current.Add(parts);
        _size = next; _sizes[id] = artifactSize;
    }
    private void SetFallback(IReadOnlyList<JsonElement> parts)
    { var size = Size(parts); CheckSize(_size + size); _fallback = parts; _fallbackSize = size; }
    private void CheckSize(long size)
    { if (size > options.MaxResultBytes) throw new A2AProtocolException("The retained A2A result exceeds MaxResultBytes."); }
    private static long Size(IReadOnlyList<JsonElement> parts) => parts.Count == 0 ? 0 : 32 + parts.Sum(part => 16L + Encoding.UTF8.GetByteCount(part.GetRawText()));
    private static IReadOnlyList<JsonElement> Parts(JsonElement value)
    {
        A2AJson.RequireObject(value);
        if (!value.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("A2A parts must be an array.");
        return parts.EnumerateArray().Select(part => part.Clone()).ToArray();
    }
    internal A2AResultEvent Build(IReadOnlyDictionary<string, object?> invocationState)
    {
        var content = new List<ContentBlock>();
        if (A2AJson.RequiredString(_final, "kind") == "status-update")
        {
            var statusContent = A2AParts.FromParts(_fallback, options.ContentMode);
            if (options.ContentMode != A2AContentMode.TextOnly || statusContent.OfType<TextBlock>().Any(block => block.Text.Length > 0)) content.AddRange(statusContent);
        }
        if (content.Count == 0) foreach (var id in _order)
        {
            var artifact = _artifacts[id].SelectMany(chunk => A2AParts.FromParts(chunk, options.ContentMode)).ToArray();
            if (artifact.Length == 0) continue;
            if (content.Count > 0) content.Add(new TextBlock("\n"));
            content.AddRange(artifact);
        }
        if (content.Count == 0) content.AddRange(A2AParts.FromParts(_fallback, options.ContentMode));
        var stop = options.TaskStateMapping == A2ATaskStateMapping.Protocol && _state is "input-required" or "auth-required" ? StopReasons.Interrupt : StopReasons.EndTurn;
        var metadata = JsonSerializer.SerializeToElement(new { a2a = new { taskState = _state, taskId = _taskId, contextId = _contextId, finalEvent = _final } });
        return new(new(stop, new Message(Role.Assistant, content, metadata: metadata), new(), 0, invocationState), _final, _state, _taskId, _contextId);
    }
}
