using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents.Context;

namespace Strands.Agents.Conversation;

/// <summary>Controls Python classic summary replacement.</summary>
public sealed record SummarizingConversationManagerOptions
{
    /// <summary>Fraction of oldest messages to summarize; clamped to [0.1,0.8], default 0.3.</summary>
    public double SummaryRatio { get; init; } = 0.3;
    /// <summary>Minimum recent message count retained before adjusting complete tool boundaries. Defaults to ten.</summary>
    public int PreserveRecentMessages { get; init; } = 10;
    /// <summary>An optional borrowed dedicated summarization agent; otherwise the managed agent's auxiliary model is used directly.</summary>
    public Agent? SummarizationAgent { get; init; }
    /// <summary>An optional summarizer instruction override, mutually exclusive with a dedicated agent.</summary>
    public string? SummarizationSystemPrompt { get; init; }
    /// <summary>Permanently pin the first N messages on the first successful reduction.</summary>
    public int? PinFirst { get; init; }
    /// <summary>Optional proactive context utilization threshold in (0,1]; 0.7 corresponds to Python true.</summary>
    public double? ProactiveCompressionThreshold { get; init; }
}

/// <summary>Replaces old unpinned history with actual model-generated summaries and checkpoints its previous summary.</summary>
public sealed class SummarizingConversationManager : ConversationManager
{
    private readonly SummarizingConversationManagerOptions _options;
    private Message? _summary;
    private bool _pinFirstApplied;
    /// <summary>Creates a classic summarizer with copied policy and borrowed dependencies.</summary>
    public SummarizingConversationManager(SummarizingConversationManagerOptions? options = null) : base(options?.ProactiveCompressionThreshold)
    {
        _options = options is null ? new() : options with { SummaryRatio = Math.Max(0.1, Math.Min(0.8, options.SummaryRatio)) };
        if (!Guard.IsFinite(_options.SummaryRatio) || _options.PreserveRecentMessages < 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.SummarizationAgent is not null && _options.SummarizationSystemPrompt is not null)
            throw new ArgumentException("A dedicated summarization agent already owns its instructions.", nameof(options));
    }
    /// <inheritdoc />
    protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken) => default;
    /// <inheritdoc />
    protected override async ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken)
    {
        CheckAgent(agent, cancellationToken);
        try
        {
            var original = agent.Messages;
            var messages = original.ToList();
            var split = Math.Min(Math.Max(1, (int)(messages.Count * _options.SummaryRatio)), messages.Count - _options.PreserveRecentMessages);
            if (split <= 0) throw new ContextWindowOverflowException("Cannot summarize: insufficient messages for summarization.");
            while (split < messages.Count && (messages[split].Content.OfType<ToolResultBlock>().Any() || messages[split].Content.OfType<ToolUseBlock>().Any() && split + 1 < messages.Count && !messages[split + 1].Content.OfType<ToolResultBlock>().Any())) split++;
            if (split >= messages.Count && split != Math.Min(Math.Max(1, (int)(messages.Count * _options.SummaryRatio)), messages.Count - _options.PreserveRecentMessages))
                throw new ContextWindowOverflowException("Cannot summarize: no complete tool boundary.");
            if (!_pinFirstApplied && _options.PinFirst is > 0) MessagePins.ApplyPinFirst(messages, _options.PinFirst.Value);
            var preserved = Enumerable.Range(0, split).Where(index => MessagePins.IsPinned(messages, index)).Select(index => messages[index]).ToArray();
            var eligible = Enumerable.Range(0, split).Where(index => !MessagePins.IsPinned(messages, index)).Select(index => messages[index]).ToArray();
            if (eligible.Length == 0) throw new ContextWindowOverflowException("Cannot summarize: all messages in the summarize range are pinned.");
            Message? previous; lock (StateGate) previous = _summary;
            var response = _options.SummarizationAgent is { } summarizer
                ? await GenerateWithAgentAsync(agent, summarizer, eligible, cancellationToken).ConfigureAwait(false)
                : await GenerateWithModelAsync(agent.AuxModel, eligible, _options.SummarizationSystemPrompt, cancellationToken).ConfigureAwait(false);
            var summary = AsUserSummary(response);
            var replacingSummary = previous is not null && eligible.Any(message => message.TrackingId == previous.TrackingId && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(message), JsonSerializer.SerializeToElement(previous)));
            var removed = checked(RemovedMessageCount + eligible.Length - (replacingSummary ? 1 : 0));
            var replacement = preserved.Concat(new[] { summary }).Concat(messages.Skip(split)).ToArray();
            await agent.CommitClassicContextAsync(original, replacement, () => { lock (StateGate) { RemovedMessageCount = removed; _summary = summary; _pinFirstApplied = true; } }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (error is null && !cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <inheritdoc />
    protected override void WriteState(IDictionary<string, object?> state) => state["summary_message"] = _summary;
    /// <inheritdoc />
    protected override Action PrepareAdditionalState(JsonElement state)
    {
        var summary = ReadSummary(state);
        return () => _summary = summary;
    }
    internal override IReadOnlyList<Message> PrepareRepositoryPrepend(JsonElement state)
        => ReadSummary(state) is { } summary ? new[] { summary } : Array.Empty<Message>();
    private static Message? ReadSummary(JsonElement state)
    {
        Message? summary = null;
        if (state.TryGetProperty("summary_message", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            summary = value.Deserialize<Message>(StateJson) ?? throw new InvalidDataException("Summary message cannot be null.");
            if (summary.Role != Role.User || summary.Content.Count == 0 || summary.Content.Any(block => block is not TextBlock { Text: not null }))
                throw new InvalidDataException("A previous classic summary must contain only user-role text.");
            summary = new(summary.Role, summary.Content, summary.TrackingId, summary.Metadata);
        }
        return summary;
    }
    /// <inheritdoc />
    protected override IReadOnlyList<Message>? GetRepositoryPrepend()
    { lock (StateGate) return _summary is null ? null : new[] { new Message(_summary.Role, _summary.Content, _summary.TrackingId, _summary.Metadata) }; }
    internal override ConversationManager CreateForAgent() => new SummarizingConversationManager(_options);

    private static Message AsUserSummary(Message response)
    {
        var content = response.Content.Select(block => block switch
        {
            TextBlock text => text,
            CitationsBlock cited => new TextBlock(cited.Text),
            Models.GoogleContentBlock google when google.Text.Length > 0 => new TextBlock(google.Text),
            Models.AnthropicContentBlock anthropic when anthropic.Text.Length > 0 => new TextBlock(anthropic.Text),
            Models.ResponsesContentBlock value when value.Text.Length > 0 => new TextBlock(value.Text),
            _ => null
        }).Where(block => block is not null).Cast<ContentBlock>().ToArray();
        if (content.Length == 0) throw new ModelException("Failed to generate summary: model response contained no text.");
        return new(Role.User, content);
    }
    private static async ValueTask<Message> GenerateWithModelAsync(IModel model, IReadOnlyList<Message> messages, string? systemPrompt, CancellationToken cancellationToken)
    {
        var request = new ModelRequest(messages.Concat(new[] { Message.User("Please summarize this conversation.") }).ToArray(), systemPrompt ?? DefaultSummarizationPrompt);
        var accumulator = new ModelStreamAccumulator();
        var attempt = new Models.ModelAttempt(model, request, cancellationToken);
        try { while (await attempt.MoveNextAsync().ConfigureAwait(false)) accumulator.Process(attempt.Current); }
        finally { await attempt.DisposeAsync().ConfigureAwait(false); }
        if (attempt.Error is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
        cancellationToken.ThrowIfCancellationRequested();
        var result = accumulator.Complete();
        if (result.StopReason is StopReasons.MaxTokens or StopReasons.ContentFiltered) throw new ModelException("The summarizer did not complete its response.");
        return result.Message;
    }
    private static async ValueTask<Message> GenerateWithAgentAsync(Agent owner, Agent summarizer, IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(owner, summarizer)) throw new InvalidOperationException("A managed agent cannot reenter itself for summarization.");
        var saved = await summarizer.TakeSnapshotAsync(new() { Include = SnapshotFields.Messages | SnapshotFields.ModelState }, cancellationToken).ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            await summarizer.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement(messages), ["modelState"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>()) }), cancellationToken).ConfigureAwait(false);
            var result = await summarizer.InvokeAsync("Please summarize this conversation.", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Message is null || result.StopReason is StopReasons.Cancelled or StopReasons.MaxTokens or StopReasons.ContentFiltered)
                throw new ModelException("The dedicated summarization agent did not complete its response.");
            return result.Message;
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try { await summarizer.RestoreClassicSummarizerAsync(saved).ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null)
            {
                const string key = "Strands.CleanupErrors";
                if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
                errors.Add(cleanup);
            }
        }
    }
    /// <summary>The pinned Python classic summarizer's default instructions.</summary>
    public const string DefaultSummarizationPrompt = "You are a conversation summarizer. Provide a concise summary of the conversation history.\n\nFormat Requirements:\n- You MUST create a structured and concise summary in bullet-point format.\n- You MUST NOT respond conversationally.\n- You MUST NOT address the user directly.\n- You MUST NOT comment on tool availability.\n\nAssumptions:\n- You MUST NOT assume tool executions failed unless otherwise stated.\n\nTask:\nYour task is to create a structured summary document:\n- It MUST contain bullet points with key topics and questions covered\n- It MUST contain bullet points for all significant tools executed and their results\n- It MUST contain bullet points for any code or technical information shared\n- It MUST contain a section of key insights gained\n- It MUST format the summary in the third person\n\nExample format:\n\n## Conversation Summary\n* Topic 1: Key information\n* Topic 2: Key information\n\n## Tools Executed\n* Tool X: Result Y";
}
