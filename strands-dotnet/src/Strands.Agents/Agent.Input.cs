namespace Strands.Agents;

public sealed partial class Agent
{
    private static InvocationOptions IncludeAutomaticInvocationInput(InvocationOptions options, IReadOnlyList<Message>? automaticInputs)
        => automaticInputs is null ? options : options with { InputMessages = CombineContinuationInput(automaticInputs, options.InputMessages) };

    private static IReadOnlyList<Message> SnapshotManualInvocationInput(string? prompt, InvocationOptions options)
    {
        var messages = new List<Message>();
        if ((options.InputMessage ?? (prompt is not null ? Message.User(prompt) : null)) is { } input) messages.Add(input);
        messages.AddRange(options.InputMessages);
        return SnapshotInvocationInput(messages);
    }

    private static IReadOnlyList<Message> SnapshotInvocationInput(IReadOnlyList<Message> messages)
        => Array.AsReadOnly(messages.Select(message => message is null ? throw new InvalidDataException("Invocation input messages cannot be null.") :
            new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray());

    private IReadOnlyList<Message> PrepareInvocationInput(IReadOnlyList<Message> manualInputs, IReadOnlyList<Message>? automaticInputs, InvocationOptions options)
    {
        var accepted = SnapshotInvocationInput(CombineContinuationInput(automaticInputs, manualInputs));
        // Responses were accepted before the hook; this second pass validates only incoming input.
        var validationOptions = options with { InputMessage = null, InputMessages = accepted, InterruptResponses = Array.Empty<InterruptResponse>() };
        ValidateInterruptResume(null, _backgroundTasks?.ForegroundOptions(validationOptions, null, false) ?? validationOptions);
        lock (_historyGate) ConversationHistory.Validate(_messages.Concat(accepted).ToArray(), _messages.Count == 0 ? ModelHistoryBehavior.Native : _options.ModelHistoryBehavior);
        return accepted;
    }

    /// <summary>Runs with an ordered user message containing text, media, or JSON. Content is copied before initialization; tool protocol blocks are rejected.</summary>
    public Task<AgentResult> InvokeContentAsync(IReadOnlyList<ContentBlock> content, InvocationOptions? options = null,
        CancellationToken cancellationToken = default) => InvokeAsync(null, WithContent(content, options), cancellationToken);

    /// <summary>Streams an invocation with typed user content. The input is copied when this method is called, before enumeration begins.</summary>
    public IAsyncEnumerable<IAgentStreamEvent> StreamContentAsync(IReadOnlyList<ContentBlock> content, InvocationOptions? options = null,
        CancellationToken cancellationToken = default) => StreamAsync(null, WithContent(content, options), cancellationToken);

    /// <summary>Runs with typed user content and a temporary schema-validated response tool.</summary>
    public Task<StructuredOutputResult<T>> InvokeStructuredContentAsync<T>(IReadOnlyList<ContentBlock> content, StructuredOutputOptions<T> options,
        InvocationOptions? invocationOptions = null, CancellationToken cancellationToken = default) =>
        InvokeStructuredAsync(null, options, WithContent(content, invocationOptions), cancellationToken);

    private static InvocationOptions WithContent(IReadOnlyList<ContentBlock> content, InvocationOptions? options)
    {
        Guard.NotNull(content, nameof(content));
        var message = new Message(Role.User, content);
        if (message.Content.Any(block => block is ToolUseBlock or ToolResultBlock))
            throw new ArgumentException("User input cannot contain tool requests or tool results. The agent manages tool protocol exchanges.", nameof(content));
        return (options ?? new()) with { InputMessage = message };
    }
}
