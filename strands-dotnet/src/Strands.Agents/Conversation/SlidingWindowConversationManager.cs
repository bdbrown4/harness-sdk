using System.Text.Json;
using Strands.Agents.Context;

namespace Strands.Agents.Conversation;

/// <summary>Controls Python classic sliding-window history management.</summary>
public sealed record SlidingWindowConversationManagerOptions
{
    /// <summary>Maximum retained message target; zero removes all unpinned messages. Defaults to forty.</summary>
    public int WindowSize { get; init; } = 40;
    /// <summary>Whether reactive overflow first truncates the oldest tool-result message.</summary>
    public bool ShouldTruncateResults { get; init; } = true;
    /// <summary>Reactive truncation policy only; Python is the default. This does not select TypeScript lifecycle or user-anchor behavior.</summary>
    public ClassicToolResultTruncationPolicy ToolResultTruncationPolicy { get; init; }
    /// <summary>Fallback tool-pair boundary policy only; Python remains the default. This does not select lifecycle or rich-result truncation behavior.</summary>
    public ClassicToolPairBoundaryPolicy ToolPairBoundaryPolicy { get; init; }
    /// <summary>Manage before every N model calls; null disables per-turn management and one corresponds to Python true.</summary>
    public int? PerTurn { get; init; }
    /// <summary>Permanently pin the first N messages on the first reduction.</summary>
    public int? PinFirst { get; init; }
    /// <summary>Optional proactive context utilization threshold in (0,1]; 0.7 corresponds to Python true.</summary>
    public double? ProactiveCompressionThreshold { get; init; }
}

/// <summary>Python classic sliding-window management with durable removal and model-call counters.</summary>
public sealed class SlidingWindowConversationManager : ConversationManager
{
    private readonly SlidingWindowConversationManagerOptions _options;
    private bool _pinFirstApplied;
    private long _modelCallCount;
    /// <summary>Creates a classic reducer with copied options.</summary>
    public SlidingWindowConversationManager(SlidingWindowConversationManagerOptions? options = null) : base(options?.ProactiveCompressionThreshold)
    {
        _options = options is null ? new() : options with { };
        if (_options.WindowSize < 0 || _options.PerTurn is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(typeof(ClassicToolResultTruncationPolicy), _options.ToolResultTruncationPolicy)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown classic tool-result truncation policy.");
        if (!Enum.IsDefined(typeof(ClassicToolPairBoundaryPolicy), _options.ToolPairBoundaryPolicy)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown classic tool-pair boundary policy.");
    }
    /// <summary>The number of model calls observed while per-turn management is enabled.</summary>
    public long ModelCallCount { get => Interlocked.Read(ref _modelCallCount); private set => Interlocked.Exchange(ref _modelCallCount, value); }
    /// <inheritdoc />
    protected override async ValueTask BeforeModelCallAsync(BeforeModelCallEvent value, CancellationToken cancellationToken)
    {
        if (_options.PerTurn is not { } frequency) return;
        long count;
        lock (StateGate) count = ModelCallCount = checked(ModelCallCount + 1);
        if (count % frequency != 0) return;
        await ApplyManagementCoreAsync(value.Agent, cancellationToken).ConfigureAwait(false);
        value.Request = value.Request with { Messages = value.Agent.Messages };
    }
    /// <inheritdoc />
    protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken)
    {
        CheckAgent(agent, cancellationToken);
        return agent.Messages.Count <= _options.WindowSize ? default : ReduceContextCoreAsync(agent, null, cancellationToken);
    }
    /// <inheritdoc />
    protected override async ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken)
    {
        CheckAgent(agent, cancellationToken);
        var original = agent.Messages;
        var messages = original.ToList();
        if (!_pinFirstApplied && _options.PinFirst is > 0) MessagePins.ApplyPinFirst(messages, _options.PinFirst.Value);
        if (error is not null && _options.ShouldTruncateResults && _options.WindowSize != 0)
        {
            var oldest = -1;
            for (var index = 0; index < messages.Count; index++)
                if (!MessagePins.IsPinned(messages, index) && messages[index].Content.OfType<ToolResultBlock>().Any()) { oldest = index; break; }
            if (oldest >= 0 && ClassicToolResultTruncator.Truncate(messages[oldest], _options.ToolResultTruncationPolicy) is { } truncated)
            {
                messages[oldest] = truncated;
                await agent.CommitClassicContextAsync(original, messages, () => _pinFirstApplied = true, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        var initialStart = messages.Count <= _options.WindowSize ? 2 : messages.Count - _options.WindowSize;
        var start = _options.WindowSize == 0 ? messages.Count : initialStart;
        int? fallbackUserAnchor = null;
        var typeScriptFallback = false;
        if (_options.WindowSize != 0)
        {
            while (start < messages.Count && (messages[start].Role != Role.User || messages[start].Content.OfType<ToolResultBlock>().Any())) start++;
            if (start >= messages.Count)
            {
                start = messages.Count;
                for (var index = initialStart; index + 1 < messages.Count; index++)
                    if ((_options.ToolPairBoundaryPolicy != ClassicToolPairBoundaryPolicy.TypeScript || messages[index].Role == Role.Assistant) && messages[index].Content.OfType<ToolUseBlock>().Any() && messages[index + 1].Role == Role.User && messages[index + 1].Content.OfType<ToolResultBlock>().Any()) { start = index; break; }
                if (start < messages.Count && _options.ToolPairBoundaryPolicy == ClassicToolPairBoundaryPolicy.TypeScript)
                {
                    typeScriptFallback = true;
                    fallbackUserAnchor = FindToolPairUserAnchor(messages, start);
                    if (fallbackUserAnchor is null) { DeclineTypeScriptFallback(error); return; }
                }
                if (start >= messages.Count) { CannotReduce(error); return; }
            }
        }
        var selected = new HashSet<int>(Enumerable.Range(0, Math.Min(start, messages.Count)).Where(index => index != fallbackUserAnchor && !MessagePins.IsPinned(messages, index)));
        if (selected.Count == 0) { if (typeScriptFallback) DeclineTypeScriptFallback(error); else CannotReduce(error); return; }
        var removed = checked(RemovedMessageCount + selected.Count);
        var replacement = messages.Where((_, index) => !selected.Contains(index)).ToArray();
        await agent.CommitClassicContextAsync(original, replacement, () => { lock (StateGate) { RemovedMessageCount = removed; _pinFirstApplied = true; } }, cancellationToken).ConfigureAwait(false);
    }

    private static int? FindToolPairUserAnchor(IReadOnlyList<Message> messages, int toolPairIndex)
    {
        var pinned = Enumerable.Range(0, toolPairIndex).Where(index => MessagePins.IsPinned(messages, index)).ToArray();
        if (pinned.Length != 0)
        {
            var first = messages[pinned[0]];
            if (first.Role != Role.User || first.Content.OfType<ToolResultBlock>().Any()) return null;
            for (var index = 1; index < pinned.Length; index++)
                if (messages[pinned[index]].Role == messages[pinned[index - 1]].Role) return null;
            var last = messages[pinned[pinned.Length - 1]];
            return last.Role == Role.User && !last.Content.OfType<ToolUseBlock>().Any() ? pinned[pinned.Length - 1] : null;
        }
        for (var index = toolPairIndex - 1; index >= 0; index--)
            if (messages[index].Role == Role.User && !messages[index].Content.Any(block => block is ToolUseBlock or ToolResultBlock)) return index;
        return null;
    }

    private static void DeclineTypeScriptFallback(Exception? error)
    {
        CannotReduce(error);
        throw new InvalidOperationException("Classic tool-pair fallback cannot reduce history while retaining a valid user anchor.");
    }
    /// <inheritdoc />
    protected override void WriteState(IDictionary<string, object?> state) => state["model_call_count"] = ModelCallCount;
    /// <inheritdoc />
    protected override Action PrepareAdditionalState(JsonElement state)
    {
        var calls = ReadCounter(state, "model_call_count", optional: true);
        return () => ModelCallCount = calls;
    }
    internal override ConversationManager CreateForAgent() => new SlidingWindowConversationManager(_options);

}
