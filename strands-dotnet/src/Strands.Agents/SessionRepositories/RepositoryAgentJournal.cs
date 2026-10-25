using System.Text.Json;

namespace Strands.Agents.SessionRepositories;

internal sealed class RepositoryAgentJournal(RepositorySessionManager manager, ISessionRepository repository, Agent agent) : ISessionStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _receiptsGate = new();
    private readonly List<JournalRow> _pending = [];
    private readonly List<JournalRow> _held = [];
    private readonly RepositoryStatefulProjection? _stateful = agent.Model is IStatefulModel { Stateful: true } ? new() : null;
    private long _nextId;
    private long? _acceptedFrontier;
    private readonly HashSet<long> _excludedIds = [];
    private JournalRow? _provisionalAbort;
    private long _expectedRemovedCount;
    private Message? _summarySource;
    private bool _repairChanged;
    private SessionAgentRecord? _confirmed;
    private bool _loaded;
    private int _released;
    internal RepositorySessionManager Manager { get; } = manager;
    internal string BindingId { get; } = agent.Id;
    internal void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) Manager.Release(this);
    }

    internal RepositoryAppendReceipt PrepareAppend(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0) return new(static () => { });
        lock (_receiptsGate)
        {
            if (!_loaded) throw new InvalidOperationException("The indexed session has not initialized.");
            var next = _nextId;
            var rows = messages.Select(message => new JournalRow(message, new SessionMessageRecord(message, checked(next++)))).ToArray();
            return new(() =>
            {
                lock (_receiptsGate)
                {
                    if (_nextId != rows[0].Record.MessageId) throw new InvalidOperationException("The indexed append receipt was superseded.");
                    _nextId = next;
                    if (_provisionalAbort is { } abandoned) { _excludedIds.Add(abandoned.Record.MessageId); _provisionalAbort = null; }
                    _acceptedFrontier = rows[^1].Record.MessageId;
                    _pending.AddRange(rows);
                    _held.AddRange(rows);
                    foreach (var row in rows) _stateful?.Append(row.Source, row.Record.MessageId);
                }
            });
        }
    }

    public async ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSession(sessionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) throw new InvalidOperationException("The indexed binding has already loaded.");
            var created = await Manager.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            var stored = created ? null : await repository.ReadAgentAsync(sessionId, agent.Id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (stored is null)
            {
                var initial = await agent.CaptureRepositorySnapshotAsync(cancellationToken).ConfigureAwait(false);
                var initialRecord = ToRecord(_stateful is null ? initial : initial with { Messages = [] });
                await repository.CreateAgentAsync(sessionId, initialRecord, cancellationToken).ConfigureAwait(false);
                _confirmed = initialRecord;
                foreach (var message in initial.Messages)
                {
                    var row = new JournalRow(message, new SessionMessageRecord(message, checked(_nextId++)));
                    _pending.Add(row); _held.Add(row);
                    _stateful?.Append(row.Source, row.Record.MessageId);
                    _acceptedFrontier = row.Record.MessageId;
                }
                await FlushRowsAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _loaded = true;
                return null;
            }
            if (stored.AgentId != agent.Id) throw new InvalidDataException("The indexed agent identity does not match its binding.");
            var metadata = ReadSnapshot(stored, []);
            var statefulRecord = stored.InternalState.TryGetProperty("nativeSessionVersion", out var nativeVersion) && nativeVersion.GetInt32() is 2 or 3;
            if (statefulRecord != (_stateful is not null)) throw new InvalidDataException("The indexed session history mode does not match this model.");
            agent.ValidateRepositorySnapshot(metadata);
            var conversation = agent.PrepareRepositoryConversation(metadata.ConversationManagerState);
            // Read the complete numeric frontier even if the retained projection is empty.
            var allRows = await repository.ListMessagesAsync(sessionId, agent.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (allRows.Select(row => row.MessageId).Distinct().Count() != allRows.Count || !allRows.Select(row => row.MessageId).SequenceEqual(allRows.Select(row => row.MessageId).OrderBy(value => value)))
                throw new InvalidDataException("Indexed message rows must have unique ascending identifiers.");
            var frontier = ReadFrontier(stored, allRows);
            _acceptedFrontier = frontier.Last;
            foreach (var id in frontier.Excluded) _excludedIds.Add(id);
            var confirmedRows = allRows.Where(row => frontier.Last is { } last && row.MessageId <= last && !_excludedIds.Contains(row.MessageId)).ToArray();
            foreach (var row in allRows.Where(row => frontier.Last is null || row.MessageId > frontier.Last)) _excludedIds.Add(row.MessageId);
            if (_stateful is not null && !stored.InternalState.TryGetProperty("statefulProjection", out _))
                throw new InvalidDataException("Indexed stateful metadata requires its active-row projection.");
            var rows = _stateful is null ? confirmedRows.Skip(conversation.Offset).ToArray()
                : RepositoryStatefulProjection.SelectRows(stored.InternalState.GetProperty("statefulProjection"), confirmedRows);
            _nextId = allRows.Count == 0 ? 0 : checked(allRows[^1].MessageId + 1);
            var messages = rows.Select(row => row.ToMessage()).ToArray();
            var repaired = _stateful is null ? RepositoryHistoryRepair.Prepare(conversation.Prepend.Concat(messages).ToArray())
                : RepositoryStatefulProjection.PrepareHistory(messages);
            var snapshot = metadata with { Messages = repaired.Messages };
            if (_stateful is not null) RepositoryStatefulProjection.ValidateCursor(snapshot);
            snapshot.InterruptState?.ValidateHistory(snapshot.Messages);
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < rows.Length; index++) _held.Add(new(messages[index], rows[index]));
            _stateful?.Restore(messages, rows);
            foreach (var replacement in repaired.Replacements)
                foreach (var row in _held)
                    if (ReferenceEquals(row.Source, replacement.Original)) row.Source = replacement.Replacement;
            _expectedRemovedCount = conversation.Offset;
            _summarySource = conversation.Prepend.SingleOrDefault();
            _repairChanged = repaired.Replacements.Count > 0 || repaired.Messages.Count != conversation.Prepend.Count + messages.Length;
            _confirmed = stored;
            _loaded = true;
            return snapshot;
        }
        catch (Exception failure) when (cancellationToken.IsCancellationRequested && (failure is not OperationCanceledException canceled || canceled.CancellationToken != cancellationToken))
        { throw new RepositoryCancellationException("Indexed session restore was cancelled.", failure, cancellationToken); }
        finally { _gate.Release(); }
    }

    internal void RebindRestoredMessageCopies(IReadOnlyList<Message> expected, IReadOnlyList<Message> copies)
    {
        lock (_receiptsGate)
        {
            if (!_loaded || _stateful is null || _pending.Count != 0 || _provisionalAbort is not null ||
                expected.Count != copies.Count || _held.Count != expected.Count ||
                _held.Where((row, index) => !ReferenceEquals(row.Source, expected[index])).Any())
                throw new InvalidDataException("Detached indexed history must retain its accepted restore row identities.");
            var rows = _held.Select(row => row.Record).ToArray();
            _stateful.Restore(copies, rows);
            for (var index = 0; index < _held.Count; index++) _held[index].Source = copies[index];
        }
    }

    public async ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateSession(sessionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded) throw new InvalidOperationException("The indexed binding has not loaded.");
            var next = ToRecord(snapshot);
            await FlushRowsAsync(cancellationToken).ConfigureAwait(false);
            if (_confirmed is null || !JsonElement.DeepEquals(next.State, _confirmed.State) ||
                !JsonElement.DeepEquals(next.ConversationManagerState, _confirmed.ConversationManagerState) || !JsonElement.DeepEquals(next.InternalState, _confirmed.InternalState))
            {
                await repository.UpdateAgentAsync(sessionId, next, cancellationToken).ConfigureAwait(false);
                _confirmed = next;
            }
            lock (_receiptsGate) _held.RemoveAll(row => !snapshot.Messages.Any(message => ReferenceEquals(message, row.Source)));
        }
        catch (Exception failure) when (cancellationToken.IsCancellationRequested && (failure is not OperationCanceledException canceled || canceled.CancellationToken != cancellationToken))
        { throw new RepositoryCancellationException("Indexed session save was cancelled.", failure, cancellationToken); }
        finally { _gate.Release(); }
    }

    internal async ValueTask PersistAbortAsync(Message message, SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded) throw new InvalidOperationException("The indexed binding has not loaded.");
            await FlushRowsAsync(cancellationToken).ConfigureAwait(false);
            JournalRow row;
            lock (_receiptsGate)
            {
                var id = _provisionalAbort?.Record.MessageId ?? checked(_nextId++);
                row = _provisionalAbort = new(message, new SessionMessageRecord(message, id));
            }
            var next = ToRecord(snapshot, row.Record.MessageId, row.Source);
            await repository.CreateMessageAsync(Manager.SessionId, agent.Id, row.Record, cancellationToken).ConfigureAwait(false);
            await repository.UpdateAgentAsync(Manager.SessionId, next, cancellationToken).ConfigureAwait(false);
            lock (_receiptsGate)
            {
                _acceptedFrontier = row.Record.MessageId; _provisionalAbort = null; _held.Add(row);
                _stateful?.Append(row.Source, row.Record.MessageId);
            }
            _confirmed = next;
        }
        catch (Exception failure) when (cancellationToken.IsCancellationRequested && (failure is not OperationCanceledException canceled || canceled.CancellationToken != cancellationToken))
        { throw new RepositoryCancellationException("Indexed abort was cancelled.", failure, cancellationToken); }
        finally { _gate.Release(); }
    }

    internal RepositoryRedactionReceipt PrepareRedaction(Message original, Message replacement)
    {
        lock (_receiptsGate)
        {
            var latest = _held.LastOrDefault();
            if (latest is null || !ReferenceEquals(latest.Source, original))
                throw new InvalidOperationException("The latest live message has no indexed append receipt.");
            var rows = _held.Where(row => ReferenceEquals(row.Source, original))
                .Select(row => (Row: row, Record: new SessionMessageRecord(row.Record.Message, row.Record.MessageId, replacement, row.Record.CreatedAt))).ToArray();
            var next = 0;
            return new(async token =>
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await FlushRowsAsync(token).ConfigureAwait(false);
                    for (; next < rows.Length; next++)
                    {
                        var item = rows[next];
                        await repository.UpdateMessageAsync(Manager.SessionId, agent.Id, item.Record, token).ConfigureAwait(false);
                        item.Row.Record = item.Record;
                    }
                    lock (_receiptsGate)
                    {
                        foreach (var item in rows) item.Row.Source = replacement;
                        _stateful?.Replace(original, replacement);
                    }
                }
                catch (Exception failure) when (token.IsCancellationRequested && (failure is not OperationCanceledException canceled || canceled.CancellationToken != token))
                { throw new RepositoryCancellationException("Indexed redaction was cancelled.", failure, token); }
                finally { _gate.Release(); }
            });
        }
    }

    internal RepositoryReductionReceipt PrepareReduction(IReadOnlyList<Message> expected, IReadOnlyList<Message> replacement, bool summarizing)
    {
        lock (_receiptsGate)
        {
            if (!_loaded || _repairChanged) throw new NotSupportedException("Indexed classic reduction cannot encode a repaired history's removed-row offset. Use a whole-snapshot session.");
            var summary = _summarySource;
            var expectedRows = expected.Where(message => !ReferenceEquals(message, summary)).ToArray();
            if (expectedRows.Length != _held.Count || expectedRows.Where((message, index) => !ReferenceEquals(message, _held[index].Source)).Any())
                throw new NotSupportedException("Indexed history must retain its explicit ordered row bindings before classic reduction.");
            Message? newSummary = null;
            var suffix = replacement;
            if (summarizing)
            {
                if (replacement.Count == 0 || replacement[0].Role != Role.User || replacement[0].Content.Any(block => block is not TextBlock { Text: not null }) || expected.Any(message => ReferenceEquals(message, replacement[0])))
                    throw new NotSupportedException("Indexed summaries require one new text summary followed by a retained row suffix.");
                newSummary = replacement[0]; suffix = replacement.Skip(1).ToArray();
            }
            var cut = expected.Count - suffix.Count;
            if (cut < 0 || suffix.Where((message, index) => !ReferenceEquals(message, expected[cut + index])).Any() ||
                summary is not null && !expected.Take(cut).Any(message => ReferenceEquals(message, summary)))
                throw new NotSupportedException("Indexed classic sessions support prefix removal and summary replacement, not non-prefix pins or rich-result rewrites. Use a whole-snapshot session.");
            var removedRows = cut - (summary is null ? 0 : 1);
            var previousCount = _expectedRemovedCount;
            var nextCount = checked(previousCount + removedRows);
            return new(() =>
            {
                lock (_receiptsGate)
                {
                    if (_expectedRemovedCount != previousCount || !ReferenceEquals(_summarySource, summary)) throw new InvalidOperationException("The indexed classic reduction receipt was superseded.");
                    _held.RemoveRange(0, removedRows);
                    _expectedRemovedCount = nextCount; _summarySource = newSummary;
                }
            });
        }
    }

    private async ValueTask FlushRowsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            JournalRow? row;
            lock (_receiptsGate) row = _pending.FirstOrDefault();
            if (row is null) return;
            await repository.CreateMessageAsync(Manager.SessionId, agent.Id, row.Record, cancellationToken).ConfigureAwait(false);
            lock (_receiptsGate) _pending.Remove(row);
        }
    }

    internal RepositoryHistoryClearReceipt? PrepareAcknowledgedHistoryClear(IReadOnlyList<Message> expected)
    {
        lock (_receiptsGate)
        {
            var receipt = _stateful?.PrepareClear(expected);
            return receipt is null ? null : new(() => { lock (_receiptsGate) receipt.Accept(); });
        }
    }

    private SessionAgentRecord ToRecord(SessionSnapshot snapshot, long? abortFrontier = null, Message? abortMessage = null)
    {
        ValidateProjectionState(snapshot.ConversationManagerState);
        var metadata = snapshot with { Messages = [], State = null, ConversationManagerState = null };
        var state = JsonSerializer.SerializeToElement(snapshot.State ?? new Dictionary<string, JsonElement>());
        var conversation = snapshot.ConversationManagerState ?? JsonSerializer.SerializeToElement(new { });
        var last = abortFrontier ?? _acceptedFrontier;
        var excluded = _excludedIds.Where(id => last is { } frontier && id <= frontier).OrderBy(id => id).ToArray();
        if (_stateful is not null)
        {
            JsonElement projection;
            lock (_receiptsGate) projection = _stateful.Capture(snapshot, abortMessage, abortFrontier);
            return new(agent.Id, state, conversation, JsonSerializer.SerializeToElement(new { nativeSessionVersion = snapshot.ModelInputReceipt is null ? 2 : 3, snapshot = metadata, journalFrontier = new { lastMessageId = last, excludedMessageIds = excluded }, statefulProjection = projection }, SessionRecordCodec.Options));
        }
        return new(agent.Id, state, conversation, JsonSerializer.SerializeToElement(new { nativeSessionVersion = 1, snapshot = metadata, journalFrontier = new { lastMessageId = last, excludedMessageIds = excluded } }, SessionRecordCodec.Options));
    }

    private void ValidateProjectionState(JsonElement? state)
    {
        var removed = state is { } value ? Conversation.ConversationManager.ReadCounter(value, "removed_message_count") : 0;
        if (removed != _expectedRemovedCount) throw new InvalidDataException("Classic removed-row state must match an accepted indexed reduction receipt.");
        JsonElement summary = default;
        var hasSummary = state is { } managerState && managerState.TryGetProperty("summary_message", out summary) && summary.ValueKind != JsonValueKind.Null;
        if (hasSummary != (_summarySource is not null) || hasSummary && !JsonElement.DeepEquals(summary, JsonSerializer.SerializeToElement(_summarySource, SessionRecordCodec.Options)))
            throw new InvalidDataException("Classic summary state must match its accepted indexed summary receipt.");
    }

    private static (long? Last, long[] Excluded) ReadFrontier(SessionAgentRecord record, IReadOnlyList<SessionMessageRecord> rows)
    {
        if (!record.InternalState.TryGetProperty("journalFrontier", out var marker))
        {
            if (record.InternalState.TryGetProperty("nativeSessionVersion", out _)) throw new InvalidDataException("Versioned native journal metadata requires its confirmed row frontier.");
            return (rows.Count == 0 ? null : rows[^1].MessageId, []);
        }
        if (!record.InternalState.TryGetProperty("nativeSessionVersion", out _) || marker.ValueKind != JsonValueKind.Object ||
            !marker.TryGetProperty("lastMessageId", out var last) || !marker.TryGetProperty("excludedMessageIds", out var excluded) || excluded.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The indexed journal frontier is malformed.");
        long? frontier = last.ValueKind == JsonValueKind.Null ? null : last.ValueKind == JsonValueKind.Number && last.TryGetInt64(out var number) ? number : throw new InvalidDataException("The indexed frontier requires an Int64 or null.");
        var exclusions = new List<long>();
        foreach (var value in excluded.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || frontier is null || id >= frontier || exclusions.Count > 0 && exclusions[^1] >= id)
                throw new InvalidDataException("Indexed excluded identities must be distinct ascending rows below the confirmed frontier.");
            exclusions.Add(id);
        }
        if (frontier is { } selected && !rows.Any(row => row.MessageId == selected)) throw new InvalidDataException("The confirmed indexed frontier row is missing.");
        return (frontier, exclusions.ToArray());
    }

    private static SessionSnapshot ReadSnapshot(SessionAgentRecord record, IReadOnlyList<Message> messages)
    {
        try
        {
            SessionRecordCodec.ValidateJson(JsonSerializer.SerializeToUtf8Bytes(record, SessionRecordCodec.Options));
            SessionSnapshot metadata;
            if (record.InternalState.TryGetProperty("nativeSessionVersion", out var version))
            {
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number is not (1 or 2 or 3)) throw new InvalidDataException("Unsupported indexed native session version.");
                metadata = record.InternalState.GetProperty("snapshot").Deserialize<SessionSnapshot>(SessionRecordCodec.Options) ?? throw new InvalidDataException("Indexed native session metadata is null.");
                if (metadata.Messages is null || metadata.Messages.Count != 0) throw new InvalidDataException("Indexed metadata must contain an empty message list.");
                if (metadata.AgentId is not null && metadata.AgentId != record.AgentId) throw new InvalidDataException("Indexed native metadata identity does not match its owner.");
                if (number == 3 ? metadata.SchemaVersion != 2 || metadata.ModelInputReceipt is null
                    : metadata.SchemaVersion != 1 || metadata.ModelInputReceipt is not null)
                    throw new InvalidDataException("The indexed journal and inner session receipt versions do not match.");
            }
            else
            {
                if (record.InternalState.TryGetProperty("snapshot", out var unversioned) && unversioned.ValueKind == JsonValueKind.Object &&
                    unversioned.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var schemaVersion) && schemaVersion == 2 &&
                    unversioned.TryGetProperty("modelInputReceipt", out var inputReceipt) && inputReceipt.ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("A receipt-bearing native snapshot requires a supported indexed journal version.");
                metadata = new(1, []);
            }
            var state = record.State.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            return metadata with { Messages = messages, AgentId = record.AgentId, State = state, ConversationManagerState = record.ConversationManagerState.EnumerateObject().Any() ? record.ConversationManagerState : null };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        { throw new InvalidDataException("Indexed native session metadata is malformed.", error); }
    }

    private void ValidateSession(string sessionId)
    {
        if (Volatile.Read(ref _released) != 0) throw new ObjectDisposedException(nameof(RepositoryAgentJournal));
        if (sessionId != Manager.SessionId) throw new InvalidOperationException("The indexed session binding cannot change sessions.");
    }
    private sealed class JournalRow(Message source, SessionMessageRecord record)
    {
        internal Message Source { get; set; } = source;
        internal SessionMessageRecord Record { get; set; } = record;
    }
}

internal sealed class RepositoryRedactionReceipt(Func<CancellationToken, ValueTask> persist)
{
    internal ValueTask PersistAsync(CancellationToken cancellationToken) => persist(cancellationToken);
}

internal sealed class RepositoryReductionReceipt(Action accept)
{
    private int _accepted;
    internal void Accept()
    {
        if (Interlocked.Exchange(ref _accepted, 1) != 0) throw new InvalidOperationException("An indexed reduction receipt can be accepted only once.");
        accept();
    }
}

internal sealed class RepositoryCancellationException(string message, Exception cause, CancellationToken cancellationToken)
    : OperationCanceledException(message, cause, cancellationToken)
{
    // Framework's default exception dictionary rejects some runtime-only diagnostics. Retain the causal journal itself.
    public override System.Collections.IDictionary Data { get; } = cause.Data;
}

internal sealed class RepositoryAppendReceipt(Action accept)
{
    private int _accepted;
    internal void Accept()
    {
        if (Interlocked.Exchange(ref _accepted, 1) != 0) throw new InvalidOperationException("An indexed append receipt can be accepted only once.");
        accept();
    }
}
