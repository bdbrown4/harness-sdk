using System.Text.Json;

namespace Strands.Agents.SessionRepositories;

// Numeric identities come only from the journal's accepted append/initialization/abort receipts.
// This describes the active local projection, independently of the complete audit archive.
internal sealed class RepositoryStatefulProjection
{
    private readonly List<Entry> _active = [];

    internal void Append(Message message, long id) => _active.Add(new(message, id));

    internal void Replace(Message original, Message replacement)
    {
        for (var index = 0; index < _active.Count; index++)
            if (ReferenceEquals(_active[index].Source, original)) _active[index] = _active[index] with { Source = replacement };
    }

    internal JsonElement Capture(SessionSnapshot snapshot, Message? additional = null, long? additionalId = null)
    {
        var entries = additional is null ? _active.ToArray() : _active.Concat(new[] { new Entry(additional, additionalId!.Value) }).ToArray();
        ValidateSources(snapshot.Messages, entries);
        ValidateCursor(snapshot);
        return JsonSerializer.SerializeToElement(new { version = 1, activeMessageIds = entries.Select(entry => entry.Id).ToArray() });
    }

    internal RepositoryHistoryClearReceipt PrepareClear(IReadOnlyList<Message> expected)
    {
        ValidateSources(expected, _active);
        // All validation precedes the live clear. Accept invokes no application code and cannot
        // reject a prepared receipt after history has already changed.
        return new(() => _active.Clear());
    }

    internal void Restore(IReadOnlyList<Message> messages, IReadOnlyList<SessionMessageRecord> rows)
    {
        _active.Clear();
        for (var index = 0; index < rows.Count; index++) Append(messages[index], rows[index].MessageId);
    }

    internal static SessionMessageRecord[] SelectRows(JsonElement marker, IReadOnlyList<SessionMessageRecord> confirmedRows)
    {
        if (marker.ValueKind != JsonValueKind.Object || !marker.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
            !marker.TryGetProperty("activeMessageIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The indexed stateful projection is malformed.");
        var available = confirmedRows.ToDictionary(row => row.MessageId);
        var selected = new List<SessionMessageRecord>();
        long? previous = null;
        foreach (var value in ids.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id < 0 ||
                previous is { } prior && id <= prior || !available.TryGetValue(id, out var row))
                throw new InvalidDataException("Stateful active identities must be ordered, distinct confirmed rows belonging to this agent.");
            selected.Add(row);
            previous = id;
        }
        if (!selected.Select(row => row.MessageId).SequenceEqual(confirmedRows.Skip(confirmedRows.Count - selected.Count).Select(row => row.MessageId)))
            throw new InvalidDataException("Stateful active history must be a confirmed archive suffix.");
        return selected.ToArray();
    }

    internal static PreparedRepositoryHistory PrepareHistory(IReadOnlyList<Message> messages)
    {
        ConversationHistory.Validate(messages);
        return new(messages, Array.Empty<(Message, Message)>());
    }

    internal static void ValidateCursor(SessionSnapshot snapshot)
    {
        if (snapshot.ModelHistoryCount is null) throw new InvalidDataException("Indexed stateful history requires its acknowledgement cursor.");
        snapshot.ValidateModelHistory();
        snapshot.ValidateModelInputReceipt();
        if (snapshot.ModelHistoryCount > 0 && snapshot.ModelState is not { Count: > 0 })
            throw new InvalidDataException("Acknowledged indexed history requires provider conversation state.");
    }

    private static void ValidateSources(IReadOnlyList<Message> messages, IReadOnlyList<Entry> entries)
    {
        if (messages.Count != entries.Count || messages.Where((message, index) => !ReferenceEquals(message, entries[index].Source)).Any())
            throw new InvalidDataException("Stateful active history must match its accepted indexed row receipts.");
    }

    private sealed record Entry(Message Source, long Id);
}

internal sealed class RepositoryHistoryClearReceipt(Action accept)
{
    private int _accepted;
    internal void Accept() { if (Interlocked.Exchange(ref _accepted, 1) == 0) accept(); }
}
