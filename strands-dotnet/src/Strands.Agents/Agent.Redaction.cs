namespace Strands.Agents;

public sealed partial class Agent
{
    private async ValueTask ApplyInputRedactionAsync(string? replacement, CancellationToken cancellationToken)
    {
        if (replacement is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        Message[] history;
        lock (_historyGate)
        {
            if (_messages.Count == 0 || _messages[^1].Role != Role.User) return;
            history = _messages.ToArray();
        }
        var original = history[^1];
        var results = original.Content.OfType<ToolResultBlock>()
            .Select(result => (ContentBlock)new ToolResultBlock(result.ToolUseId, [new TextBlock(replacement)], result.Status) { Metadata = result.Metadata }).ToArray();
        var redacted = new Message(Role.User, results.Length == 0 ? [new TextBlock(replacement)] : results, original.TrackingId);
        history[^1] = redacted;
        var write = _options.SessionStore is null ? null : await PrepareSessionWriteAsync(history, InterruptState, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Once storage accepts the redaction, commit the same projection even if caller cancellation races it.
        if (write is not null)
        {
            await CommitRepositoryRedactionAsync(original, redacted).ConfigureAwait(false);
            await _options.SessionStore!.SaveAsync(SessionId!, write.Snapshot, CancellationToken.None).ConfigureAwait(false);
            _savedStateVersion = write.StateVersion;
            _savedModelStateVersion = write.ModelStateVersion;
        }
        lock (_historyGate) _messages[^1] = redacted;
    }
}
