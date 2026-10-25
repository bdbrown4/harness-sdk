using System.Collections.ObjectModel;
using System.Text.Json;

namespace Strands.Agents;

internal static class AgentToolInterruptJournal
{
    internal const string TurnsKey = "subAgentInterruptedTurns";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Prefix(string callId) => "agent_as_tool:" + Uri.EscapeDataString(callId) + ":";
    internal static bool IsForwarded(Interrupt question) => question.Source == InterruptSource.Tool &&
        (question.Id.StartsWith(Prefix(question.ToolUseId), StringComparison.Ordinal) ||
         question.Id.StartsWith("background:", StringComparison.Ordinal) && question.Id.IndexOf(":" + Prefix(question.ToolUseId), StringComparison.Ordinal) >= 0);

    internal static IReadOnlyDictionary<string, JsonElement> Copy(IReadOnlyDictionary<string, JsonElement> values, InterruptState? owner = null)
    {
        if (values is null) throw new InvalidDataException("Interrupt context cannot be null.");
        var copied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in values)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Interrupt context requires nonempty keys and defined JSON values.");
            copied.Add(entry.Key, entry.Value.Clone());
        }
        if (copied.TryGetValue(TurnsKey, out var turns))
        {
            if (turns.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Interrupted child turns must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var turn in turns.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(turn.Name) || !names.Add(turn.Name)) throw new InvalidDataException("Interrupted child turns require unique tool-call identifiers.");
                var snapshot = turn.Value.Deserialize<AgentSnapshot>(Json) ?? throw new InvalidDataException("An interrupted child turn requires a checkpoint.");
                Agent.ValidateSnapshotData(snapshot);
                if (!snapshot.Data.ContainsKey("messages") || !snapshot.Data.TryGetValue("interruptState", out var state) || state.ValueKind == JsonValueKind.Null)
                    throw new InvalidDataException("An interrupted child turn requires its conversation and pending input.");
                var child = state.Deserialize<InterruptState>(Json) ?? throw new InvalidDataException("An interrupted child turn requires pending input.");
                if (owner is null) continue;
                if (!owner.AssistantMessage.Content.OfType<ToolUseBlock>().Any(call => call.ToolUseId == turn.Name) || owner.CompletedToolResults.Any(result => result.ToolUseId == turn.Name))
                    throw new InvalidDataException("An interrupted child turn must belong to an incomplete parent call.");
                var answered = new HashSet<string>(owner.Responses.Select(answer => answer.InterruptId), StringComparer.Ordinal);
                var questions = owner.Interrupts.Where(question => question.ToolUseId == turn.Name && IsForwarded(question)).ToArray();
                if (questions.Length == 0 || questions.Where(question => !answered.Contains(question.Id)).Any(question => !child.Interrupts.Any(nested => Matches(question, nested))))
                    throw new InvalidDataException("An interrupted child turn must match its forwarded input requests.");
            }
        }
        return new ReadOnlyDictionary<string, JsonElement>(copied);
    }

    private static bool Matches(Interrupt question, Interrupt child)
    {
        if (question.Name != child.Name || question.Reason.HasValue != child.Reason.HasValue ||
            question.Reason is { } reason && !JsonElement.DeepEquals(reason, child.Reason!.Value)) return false;
        var prefix = Prefix(question.ToolUseId);
        if (question.Id.StartsWith(prefix, StringComparison.Ordinal)) return question.Id.Substring(prefix.Length) == child.Id;
        var position = question.Id.IndexOf(":" + prefix, StringComparison.Ordinal);
        return question.Id.StartsWith("background:", StringComparison.Ordinal) && position >= 0 && question.Id.Substring(position + prefix.Length + 1) == child.Id;
    }
}

// The same concrete JSON journal belongs to a foreground batch, a background execution, or a direct-call refusal scope.
internal sealed class AgentToolCallContext(object gate, Dictionary<string, JsonElement> journal, Action ensureOpen,
    Func<Interrupt, bool, JsonElement> resolve, Func<bool> hasForwarded, Action requireAnyAnswer)
{
    internal bool HasForwarded { get { lock (gate) { ensureOpen(); return hasForwarded(); } } }
    internal void RequireAnyAnswer() { lock (gate) { ensureOpen(); requireAnyAnswer(); } }
    internal JsonElement Resolve(Interrupt question) { lock (gate) { ensureOpen(); return resolve(question, false); } }
    internal void Reopen(IReadOnlyList<Interrupt> questions)
    {
        lock (gate)
        {
            ensureOpen();
            var reopened = new List<Interrupt>();
            foreach (var question in questions)
                try { _ = resolve(question, true); }
                catch (InterruptException pause) { reopened.AddRange(pause.Interrupts); }
            if (reopened.Count == 0) throw new InvalidOperationException("An interrupted child turn has no input requests to reopen.");
            throw new InterruptException(reopened);
        }
    }
    internal AgentSnapshot? GetTurn(string callId)
    {
        lock (gate)
        {
            ensureOpen();
            return journal.TryGetValue(AgentToolInterruptJournal.TurnsKey, out var turns) && turns.TryGetProperty(callId, out var turn)
                ? turn.Deserialize<AgentSnapshot>(AgentToolInterruptJournal.Json) : null;
        }
    }
    internal void SetTurn(string callId, AgentSnapshot? snapshot)
    {
        lock (gate)
        {
            ensureOpen();
            var turns = journal.TryGetValue(AgentToolInterruptJournal.TurnsKey, out var stored)
                ? stored.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.Clone(), StringComparer.Ordinal) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (snapshot is null) turns.Remove(callId);
            else turns[callId] = JsonSerializer.SerializeToElement(snapshot, AgentToolInterruptJournal.Json);
            if (turns.Count == 0) journal.Remove(AgentToolInterruptJournal.TurnsKey);
            else journal[AgentToolInterruptJournal.TurnsKey] = JsonSerializer.SerializeToElement(turns, AgentToolInterruptJournal.Json);
        }
    }
}
