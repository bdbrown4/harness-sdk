namespace Strands.Agents.Context;

/// <summary>An immutable truncate, drop, or summarize policy. When returns a new policy and never mutates an existing one.</summary>
public sealed class OffloadStrategy : ContextStrategy
{
    private readonly OffloadKind _kind;
    private readonly OffloadTarget _target;
    private readonly TruncateOptions _truncate;
    private readonly SummarizeOptions _summarize;
    private readonly OffloadConditions _conditions;
    internal OffloadStrategy(OffloadKind kind, OffloadTarget target, TruncateOptions truncate, SummarizeOptions summarize, OffloadConditions conditions)
    {
        _kind = kind; _target = target; _truncate = truncate; _summarize = summarize; _conditions = conditions;
        ContextReductionMethods.Validate(truncate);
        if (conditions.Threshold is < 0 || conditions.Utilization is { } utilization && (!Guard.IsFinite(utilization) || utilization < 0) || !Guard.IsFinite(conditions.PreserveRecent) || conditions.PreserveRecent < 0) throw new ArgumentOutOfRangeException(nameof(conditions));
        if (kind == OffloadKind.Truncate && conditions.Threshold is { } threshold && threshold <= truncate.PreviewTokens) throw new ArgumentException("A truncation threshold must exceed the preview allowance to ensure convergence.", nameof(conditions));
    }
    /// <inheritdoc />
    public override string Name => "offload:" + _kind.ToString().ToLowerInvariant();
    /// <summary>Creates a policy with the supplied conditions.</summary>
    /// <param name="conditions">Block threshold, utilization trigger, and recent-match protection.</param>
    /// <returns>A new immutable policy.</returns>
    public OffloadStrategy When(OffloadConditions conditions)
    {
        Guard.NotNull(conditions, nameof(conditions));
        return new(_kind, _target, _truncate, _summarize, conditions);
    }
    /// <inheritdoc />
    public override ValueTask InitializeAsync(Agent agent, Stash? stash = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        if (_conditions.Utilization is not null || _conditions.PreserveRecent > 0) return default;
        agent.Hooks.Register<MessageAddedEvent>(async (value, token) =>
        {
            var messages = value.Agent.Messages.ToList();
            var index = messages.FindLastIndex(message => ReferenceEquals(message, value.Message) || message.TrackingId == value.Message.TrackingId);
            if (index < 0) return;
            var context = new ContextState(value.Agent, messages, 0, false, stash);
            if (await TransformMessageAsync(context, index, token).ConfigureAwait(false))
                await value.Agent.ReplaceContextAsync(messages, token).ConfigureAwait(false);
        });
        return default;
    }
    /// <inheritdoc />
    public override async ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (_conditions.Utilization is { } utilization)
        {
            if (!context.Overflow && context.Utilization < utilization) return false;
            return await ApplyBatchAsync(context, cancellationToken).ConfigureAwait(false);
        }
        var acted = false;
        foreach (var messageIndex in OldestMatches(context.Messages))
            acted |= await TransformMessageAsync(context, messageIndex, cancellationToken).ConfigureAwait(false);
        return acted;
    }

    private IEnumerable<int> OldestMatches(IReadOnlyList<Message> messages)
    {
        var matching = Enumerable.Range(0, messages.Count).Where(index => MatchesMessage(messages, index)).ToArray();
        var preserve = _conditions.PreserveRecent;
        var count = preserve >= matching.Length ? matching.Length : preserve > 0 && preserve < 1 ? (int)Math.Ceiling(matching.Length * preserve) : (int)preserve;
        return matching.Take(matching.Length - count);
    }

    private bool MatchesMessage(IReadOnlyList<Message> messages, int index) => _target.Kind == OffloadTarget.TargetKind.All || messages[index].Content.Any(block => MatchesBlock(block, messages, index, false));

    private async ValueTask<bool> ApplyBatchAsync(ContextState context, CancellationToken cancellationToken)
    {
        var eligible = new List<int>();
        var oldest = new HashSet<int>(OldestMatches(context.Messages));
        foreach (var index in oldest.OrderBy(index => index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == 0 || index == context.Messages.Count - 1 || MessagePins.IsPinned(context.Messages, index)) continue;
            if (_conditions.Threshold is null) { eligible.Add(index); continue; }
            foreach (var block in context.Messages[index].Content)
            {
                if (!MatchesBlock(block, context.Messages, index, context.Stash is not null)) continue;
                if (await context.Agent.Model.CountTokensAsync([new Message(context.Messages[index].Role, [block])], cancellationToken: cancellationToken).ConfigureAwait(false) <= _conditions.Threshold.Value) continue;
                eligible.Add(index);
                break;
            }
        }
        var preserved = new HashSet<int>(Enumerable.Range(0, context.Messages.Count).Where(index => MatchesMessage(context.Messages, index) && !oldest.Contains(index)));
        var selected = OffloadHistory.SafeRemoval(context.Messages, eligible, preserved);
        if (selected.Count == 0) return false;
        var before = context.Messages.ToArray();
        var count = selected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var noun = selected.Count == 1 ? "message" : "messages";
        var marker = _kind == OffloadKind.Drop ? $"[Dropped: {count} {noun}]" : $"[... {count} {noun} elided ...]";
        if (_kind == OffloadKind.Summarize)
        {
            var originals = selected.Select(index => context.Messages[index]).ToArray();
            var model = _summarize.Model ?? context.Agent.AuxModel;
            var summary = await ContextReductionMethods.SummarizeAsync(ContextReductionMethods.FlattenForSummary(originals), model, _summarize, cancellationToken).ConfigureAwait(false);
            if (summary is null) return false;
            var tokens = await model.CountTokensAsync(originals, cancellationToken: cancellationToken).ConfigureAwait(false);
            marker = ContextReductionMethods.FormatSummary(count + " " + noun, tokens, summary);
        }
        if (!ContextReductionMethods.IsSmallerProjection(selected.SelectMany(index => before[index].Content), [new TextBlock(marker)])) return false;
        var replacement = before.ToList();
        OffloadHistory.RemoveAndMark(replacement, selected, marker);
        var beforeTokens = await context.Agent.Model.CountTokensAsync(before, cancellationToken: cancellationToken).ConfigureAwait(false);
        var afterTokens = await context.Agent.Model.CountTokensAsync(replacement, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (afterTokens >= beforeTokens) return false;
        context.Messages.Clear(); context.Messages.AddRange(replacement);
        return true;
    }

    private async ValueTask<bool> TransformMessageAsync(ContextState context, int messageIndex, CancellationToken cancellationToken)
    {
        if (MessagePins.IsPinned(context.Messages, messageIndex)) return false;
        var message = context.Messages[messageIndex];
        var blocks = message.Content.ToArray();
        var changed = false;
        for (var blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = blocks[blockIndex];
            if (!MatchesBlock(block, context.Messages, messageIndex, context.Stash is not null)) continue;
            var tokens = await context.Agent.Model.CountTokensAsync([new Message(message.Role, [block])], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (tokens <= (_conditions.Threshold ?? 0)) continue;
            IReadOnlyList<string> references = [];
            if (context.Stash is { } stash)
            {
                // Role repair may have synthesized new tracking-id/block-index positions.
                // Persist that representation before publishing references to it.
                var retrievalIds = new HashSet<string>(message.Content.OfType<ToolResultBlock>().Where(result => ToolName(context.Messages, messageIndex, result.ToolUseId) == ContextRetrievalTool.ToolName).Select(result => result.ToolUseId), StringComparer.Ordinal);
                await stash.StoreMessageAsync(message, retrievalIds, cancellationToken).ConfigureAwait(false);
                references = stash.GetReferences(block, message, blockIndex);
            }
            var replacement = await ReplaceBlockAsync(block, references, context.Agent, tokens, cancellationToken).ConfigureAwait(false);
            if (replacement is null || ReferenceEquals(replacement, block) || !ContextReductionMethods.IsSmaller(block, replacement)) continue;
            blocks[blockIndex] = replacement;
            changed = true;
        }
        if (changed) context.Messages[messageIndex] = new(message.Role, blocks, message.TrackingId, message.Metadata);
        return changed;
    }

    private async ValueTask<ContentBlock?> ReplaceBlockAsync(ContentBlock block, IReadOnlyList<string> references, Agent agent, long tokens, CancellationToken cancellationToken)
    {
        if (_kind == OffloadKind.Summarize)
        {
            var summary = await ContextReductionMethods.SummarizeAsync([block], agent.AuxModel, _summarize, cancellationToken).ConfigureAwait(false);
            if (summary is null) return block is TextBlock or ToolResultBlock or JsonBlock ? null : new TextBlock(OffloadedMarker(tokens) + FormatReferences(references));
            var description = block is ToolResultBlock ? "tool result" : block is TextBlock or JsonBlock ? "text block" : "media block";
            var summaryText = new TextBlock(ContextReductionMethods.FormatSummary(description, tokens, summary) + FormatReferences(references));
            return block is ToolResultBlock summarized
                ? new ToolResultBlock(summarized.ToolUseId, new ContentBlock[] { summaryText }.Concat(ContextReductionMethods.ResultContent(summarized).Where(item => item is not (TextBlock or JsonBlock))).ToArray(), summarized.Status, summarized.StructuredContent)
                : summaryText;
        }
        if (_kind == OffloadKind.Drop)
        {
            var marker = new TextBlock("[Dropped]" + FormatReferences(references));
            return block is ToolResultBlock dropped
                ? new ToolResultBlock(dropped.ToolUseId, new ContentBlock[] { marker }.Concat(ContextReductionMethods.ResultContent(dropped).Where(item => item is not (TextBlock or JsonBlock))).ToArray(), dropped.Status, dropped.StructuredContent)
                : marker;
        }
        if (_kind != OffloadKind.Truncate) return null;
        var annotation = FormatReferences(references);
        if (block is ToolResultBlock result)
        {
            var truncated = ContextReductionMethods.TruncateToolResult(result, _truncate);
            if (ReferenceEquals(truncated, result) || annotation.Length == 0) return truncated;
            var content = truncated.ContentBlocks.ToArray();
            var index = Array.FindIndex(content, item => item is TextBlock);
            if (index < 0) return truncated;
            content[index] = new TextBlock(((TextBlock)content[index]).Text + "\n\n[Stashed]" + annotation);
            return new ToolResultBlock(result.ToolUseId, content, result.Status, result.StructuredContent);
        }
        if (block is TextBlock text)
        {
            var preview = ContextReductionMethods.BuildPreview(text.Text, options: _truncate);
            return ReferenceEquals(preview, text.Text) ? block : new TextBlock(preview + (annotation.Length == 0 ? "" : "\n\n[Stashed]" + annotation));
        }
        if (block is JsonBlock json)
        {
            var source = json.Json.GetRawText();
            var preview = ContextReductionMethods.BuildPreview(source, options: _truncate);
            return ReferenceEquals(source, preview) ? block : new TextBlock(preview + annotation);
        }
        return new TextBlock(OffloadedMarker(tokens) + annotation);
    }

    private bool MatchesBlock(ContentBlock block, IReadOnlyList<Message> messages, int messageIndex, bool hasStash) => block switch
    {
        TextBlock => _target.MatchesText(messages[messageIndex].Role),
        ToolResultBlock result => !(hasStash && ToolName(messages, messageIndex, result.ToolUseId) == ContextRetrievalTool.ToolName) && _target.MatchesTool(result, ToolName(messages, messageIndex, result.ToolUseId)),
        JsonBlock or ImageBlock or AudioBlock or DocumentBlock or ResourceLinkBlock => _target.Kind == OffloadTarget.TargetKind.All,
        _ => false
    };

    private static string? ToolName(IReadOnlyList<Message> messages, int messageIndex, string id) => messageIndex > 0 && messages[messageIndex - 1].Role == Role.Assistant ? messages[messageIndex - 1].Content.OfType<ToolUseBlock>().FirstOrDefault(call => call.ToolUseId == id)?.Name : null;
    private static string FormatReferences(IReadOnlyList<string> references) => references.Count switch { 0 => "", 1 => " [ref: " + references[0] + "]", _ => " [refs: " + string.Join(", ", references) + "]" };
    private static string OffloadedMarker(long tokens) => "[Offloaded: ~" + tokens.ToString(System.Globalization.CultureInfo.InvariantCulture) + " tokens]";
}
