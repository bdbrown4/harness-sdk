using System.Text;
using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private sealed class HostedTask(string id, string contextId, int maximum)
    {
        private readonly object _gate = new();
        private readonly List<JsonElement> _history = [];
        private readonly List<Dictionary<string, object?>> _artifacts = [];
        private readonly Dictionary<string, JsonElement> _extensions = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _publication = new(1, 1);
        private JsonElement _status = JsonSerializer.SerializeToElement(new { state = "submitted" });
        internal string Id { get; } = id;
        internal string ContextId { get; } = contextId;
        internal bool Active { get; set; }
        internal bool Started { get; private set; }
        internal long LastUsed { get; set; }
        internal Execution? Execution { get; set; }
        internal bool Terminal { get { lock (_gate) return _status.GetProperty("state").GetString() is "completed" or "canceled" or "failed" or "rejected"; } }
        internal void Publish(JsonElement update, JsonElement request, EventBuffer events)
        { lock (_gate) { Apply(update, request); events.Publish(update); } }
        internal async ValueTask PublishAsync(JsonElement update, JsonElement request, EventBuffer events, IA2ATaskStore? store, CancellationToken cancellationToken)
        {
            if (store is null) { Publish(update, request, events); return; }
            await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                events.Validate(update);
                var candidate = Restore(Snapshot(null), maximum); candidate.Apply(update, request);
                await PersistTaskAsync(store, candidate.Snapshot(null), cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _status = candidate._status; _history.Clear(); _history.AddRange(candidate._history);
                    _artifacts.Clear(); _artifacts.AddRange(candidate._artifacts); Started = true;
                    events.Publish(update);
                }
            }
            finally { _publication.Release(); }
        }
        internal static HostedTask Restore(JsonElement task, int maxBytes)
        {
            A2AJson.RequireObject(task);
            if (Encoding.UTF8.GetByteCount(task.GetRawText()) > maxBytes) throw new A2AProtocolException("The stored task exceeded MaxTaskBytes.");
            var id = A2ATaskRecord.Validate(task);
            task = task.Clone();
            var entry = new HostedTask(id, task.GetProperty("contextId").GetString()!, maxBytes) { _status = task.GetProperty("status"), Started = true };
            if (task.TryGetProperty("history", out var history)) entry._history.AddRange(history.EnumerateArray());
            if (task.TryGetProperty("artifacts", out var artifacts))
                foreach (var artifact in artifacts.EnumerateArray())
                {
                    var fields = artifact.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
                    fields["artifactId"] = artifact.GetProperty("artifactId").GetString()!;
                    fields["parts"] = artifact.GetProperty("parts").EnumerateArray().ToList();
                    entry._artifacts.Add(fields);
                }
            foreach (var property in task.EnumerateObject())
                if (property.Name is not ("kind" or "id" or "contextId" or "status" or "artifacts" or "history")) entry._extensions.Add(property.Name, property.Value);
            return entry;
        }
        internal (JsonElement Task, long Cursor) SnapshotWithCursor(EventBuffer events)
        { lock (_gate) return (Snapshot(null), events.NextCursor); }
        internal void Apply(JsonElement update, JsonElement request)
        {
            lock (_gate)
            {
                var previousStatus = _status; var previousHistory = _history.Count;
                var previousArtifacts = _artifacts.Select(artifact => new Dictionary<string, object?>(artifact, StringComparer.Ordinal)).ToArray();
                try
                {
                    switch (A2AJson.RequiredString(update, "kind"))
                    {
                        case "task": _status = update.GetProperty("status").Clone(); _history.Add(request.Clone()); break;
                        case "status-update":
                            _status = update.GetProperty("status").Clone();
                            if (_status.TryGetProperty("message", out var message)) _history.Add(message.Clone());
                            break;
                        case "artifact-update":
                            var artifact = update.GetProperty("artifact"); var artifactId = A2AJson.RequiredString(artifact, "artifactId");
                            var existing = _artifacts.FindIndex(value => (string)value["artifactId"]! == artifactId);
                            var parts = artifact.GetProperty("parts").EnumerateArray().Select(part => part.Clone()).ToList();
                            if (existing >= 0 && A2AJson.OptionalBoolean(update, "append")) parts.InsertRange(0, (List<JsonElement>)_artifacts[existing]["parts"]!);
                            var replacement = new Dictionary<string, object?>(StringComparer.Ordinal) { ["artifactId"] = artifactId, ["parts"] = parts };
                            if (existing < 0) _artifacts.Add(replacement); else _artifacts[existing] = replacement;
                            break;
                        default: throw new A2AProtocolException("The executor published an unsupported task update.");
                    }
                    if (Encoding.UTF8.GetByteCount(Snapshot(null).GetRawText()) > maximum) throw new A2AProtocolException("The task exceeded MaxTaskBytes.");
                    Started = true;
                }
                catch
                {
                    _status = previousStatus; if (_history.Count > previousHistory) _history.RemoveRange(previousHistory, _history.Count - previousHistory);
                    _artifacts.Clear(); _artifacts.AddRange(previousArtifacts); throw;
                }
            }
        }
        internal JsonElement Snapshot(int? historyLength)
        {
            lock (_gate)
            {
                var history = _history.AsEnumerable(); if (historyLength is { } length) history = _history.Skip(Math.Max(0, _history.Count - length));
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = "task",
                    ["id"] = Id,
                    ["contextId"] = ContextId,
                    ["status"] = _status,
                    ["artifacts"] = _artifacts,
                    ["history"] = history.ToArray()
                };
                foreach (var extension in _extensions) fields.Add(extension.Key, extension.Value);
                return JsonSerializer.SerializeToElement(fields);
            }
        }
    }
}
