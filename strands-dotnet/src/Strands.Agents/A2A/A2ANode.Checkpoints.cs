using System.Text;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.A2A;

public sealed partial class A2ANode : INodeCheckpointValidator
{
    private static readonly JsonSerializerOptions CheckpointJson = new(JsonSerializerDefaults.Web);
    private sealed record RemoteQuestion(string Id, string? RemoteId);
    private sealed record RemotePause(string Scope, string SchemaVersion, string NodeId, A2AEndpointBinding Binding, string TaskId, string ContextId, JsonElement FinalEvent, IReadOnlyList<RemoteQuestion> Questions, bool UncertainGenericSend = false);
    private sealed record WireQuestion(string? Id, string Name, JsonElement? Reason);

    IReadOnlyList<Interrupt> INodeCheckpointValidator.ReadPendingInterrupts(JsonElement snapshot) => Questions(ReadPause(snapshot));
    void INodeCheckpointValidator.ValidateResponses(JsonElement snapshot, IReadOnlyList<InterruptResponse> responses) => _ = ResponseContent(ReadPause(snapshot), responses);

    private RemotePause ReadPause(JsonElement snapshot)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(snapshot.GetRawText()) > Options.MaxCheckpointBytes) throw new InvalidDataException("The remote node checkpoint exceeds its byte limit.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            if (bytes.Length > Options.MaxCheckpointBytes) throw new InvalidDataException("The serialized remote node checkpoint exceeds its byte limit.");
            using var document = JsonDocument.Parse(bytes);
            A2AJson.ValidateUniqueProperties(document.RootElement);
            var pause = document.RootElement.Deserialize<RemotePause>(CheckpointJson) ?? throw new InvalidDataException("The remote node checkpoint is missing.");
            if (pause.Scope != "a2aNode" || pause.SchemaVersion != "1.0" || pause.NodeId != Id || pause.Binding is null || !_client.MatchesConfiguration(pause.Binding)
                || !IsBindingHash(pause.Binding.Endpoint) || string.IsNullOrWhiteSpace(pause.TaskId) || string.IsNullOrWhiteSpace(pause.ContextId))
                throw new InvalidDataException("The remote node checkpoint identity, version or endpoint configuration does not match this node.");
            var actual = ReadWireQuestions(pause.FinalEvent, out var task, out var context);
            if (pause.TaskId != task || pause.ContextId != context || pause.Questions is null || pause.Questions.Count != actual.Count
                || pause.Questions.Any(question => question is null || !IsLocalId(question.Id)) || pause.Questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count() != pause.Questions.Count
                || !pause.Questions.Select(question => question.RemoteId).SequenceEqual(actual.Select(question => question.Id)))
                throw new InvalidDataException("Remote node questions must uniquely match their captured paused task.");
            return pause with { FinalEvent = pause.FinalEvent.Clone(), Questions = Array.AsReadOnly(pause.Questions.ToArray()) };
        }
        catch (Exception error) when (error is JsonException or A2AProtocolException or ArgumentException or InvalidOperationException or NotSupportedException)
        { throw new InvalidDataException("The remote node checkpoint is malformed.", error); }
    }
    private static bool IsLocalId(string id) => id is not null && id.StartsWith("remote_", StringComparison.Ordinal) && Guid.TryParseExact(id.Substring(7), "N", out _);
    private static bool IsBindingHash(string? value) => value is not null && value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private RemotePause CapturePause(JsonElement finalEvent, A2AEndpointBinding binding, RemotePause? previous, bool preserveGeneric = true)
    {
        var actual = ReadWireQuestions(finalEvent, out var task, out var context);
        var questions = actual.Select(question => new RemoteQuestion(previous?.Questions.FirstOrDefault(known => known.RemoteId == question.Id && (question.Id is not null || preserveGeneric))?.Id ?? "remote_" + Guid.NewGuid().ToString("N"), question.Id)).ToArray();
        var candidate = new RemotePause("a2aNode", "1.0", Id, binding, task, context, finalEvent.Clone(), Array.AsReadOnly(questions));
        return ReadPause(JsonSerializer.SerializeToElement(candidate, CheckpointJson));
    }
    private IReadOnlyList<Interrupt> Questions(RemotePause pause)
    {
        var wire = ReadWireQuestions(pause.FinalEvent, out _, out _);
        return Array.AsReadOnly(wire.Select((question, index) => new Interrupt(pause.Questions[index].Id, question.Name, Id, question.Reason, InterruptSource.RemoteAgent)).ToArray());
    }
    private void RetainPause(NodeState state, RemotePause pause)
    {
        state.InterruptedNodeSnapshot = JsonSerializer.SerializeToElement(pause, CheckpointJson);
        state.PendingInterrupts = Questions(pause);
        var outstanding = new HashSet<string>(pause.Questions.Select(question => question.Id), StringComparer.Ordinal);
        state.DeferredResponses = Array.AsReadOnly(state.DeferredResponses.Where(response => outstanding.Contains(response.InterruptId)).ToArray());
    }
    private IReadOnlyList<WireQuestion> ReadWireQuestions(JsonElement finalEvent, out string task, out string context)
    {
        _ = new A2AStreamState().Observe(finalEvent);
        var kind = A2AJson.RequiredString(finalEvent, "kind");
        if (kind is not ("task" or "status-update")) throw new A2AProtocolException("A remote pause requires a task status.");
        task = A2AJson.RequiredString(finalEvent, kind == "task" ? "id" : "taskId"); context = A2AJson.RequiredString(finalEvent, "contextId");
        var status = A2AJson.RequiredObject(finalEvent, "status"); var state = A2AJson.RequiredString(status, "state");
        if (state is not ("input-required" or "auth-required")) throw new A2AProtocolException("Only a stable remote input or authentication pause can be checkpointed.");
        var result = new List<WireQuestion>();
        if (status.TryGetProperty("message", out var message) && message.TryGetProperty("parts", out var parts))
            foreach (var part in parts.EnumerateArray())
                if (A2AJson.RequiredString(part, "kind") == "data" && part.GetProperty("data") is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("interrupts", out var interrupts))
                {
                    if (interrupts.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("Remote interrupts must be an array.");
                    foreach (var question in interrupts.EnumerateArray())
                    {
                        if (result.Count >= Options.MaxQuestions) throw new A2AProtocolException("The remote task exceeds the configured question limit.");
                        result.Add(new(A2AJson.RequiredString(question, "interruptId"), A2AJson.RequiredString(question, "name"), question.TryGetProperty("reason", out var reason) ? reason.Clone() : null));
                    }
                }
        if (result.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count() != result.Count) throw new A2AProtocolException("Remote question identifiers must be unique within a task.");
        if (result.Count == 0) result.Add(new(null, state == "auth-required" ? "remote-authentication" : "remote-input", status.TryGetProperty("message", out var prompt) ? prompt.Clone() : null));
        return Array.AsReadOnly(result.ToArray());
    }
    private IReadOnlyList<ContentBlock> ResponseContent(RemotePause pause, IReadOnlyList<InterruptResponse> responses)
    {
        var parts = new List<ContentBlock>();
        foreach (var response in responses)
        {
            var question = pause.Questions.SingleOrDefault(value => value.Id == response.InterruptId) ?? throw new ArgumentException("The response does not target this remote pause.", nameof(responses));
            if (question.RemoteId is { } remoteId) parts.Add(new JsonBlock(JsonSerializer.SerializeToElement(new { interruptResponse = new { interruptId = remoteId, response = response.Response } })));
            else if (response.Response.ValueKind == JsonValueKind.String) parts.Add(new TextBlock(response.Response.GetString()!));
            else throw new ArgumentException("An unstructured remote input or authentication request requires a text response.", nameof(responses));
        }
        return parts;
    }
}
