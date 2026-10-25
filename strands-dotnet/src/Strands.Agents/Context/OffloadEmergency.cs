namespace Strands.Agents.Context;

internal sealed class EmergencyTruncateStrategy : ContextStrategy
{
    public override string Name => "offload:emergency-truncate";
    public override async ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Messages.Count <= 3 || !context.Overflow && context.Utilization < 1) return false;
        var count = Math.Max(1, (context.Messages.Count - 1) / 5);
        var candidates = Enumerable.Range(1, context.Messages.Count - 2).Where(index => !MessagePins.IsPinned(context.Messages, index)).Take(count);
        var selected = OffloadHistory.SafeRemoval(context.Messages, candidates);
        if (selected.Count == 0) return false;
        var replacement = context.Messages.ToList();
        var countText = selected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OffloadHistory.RemoveAndMark(replacement, selected, $"[... {countText} {(selected.Count == 1 ? "message" : "messages")} elided ...]");
        var before = await context.Agent.Model.CountTokensAsync(context.Messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var after = await context.Agent.Model.CountTokensAsync(replacement, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (after >= before) return false;
        context.Messages.Clear(); context.Messages.AddRange(replacement);
        return true;
    }
}
