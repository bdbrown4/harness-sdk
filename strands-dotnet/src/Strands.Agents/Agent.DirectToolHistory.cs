using System.Text.Json;
using Strands.Agents.Conversation;

namespace Strands.Agents;

public sealed partial class Agent
{
    private readonly HashSet<DirectToolOperation> _directToolOperations = [];

    /// <summary>Invokes one tool with explicitly selected direct-call history behavior.</summary>
    public ValueTask<ToolResultBlock> InvokeToolAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState, DirectToolCallOptions options, CancellationToken cancellationToken)
        => InvokeDirectToolAsync(toolUse, invocationState, true, null, cancellationToken, CopyDirectToolOptions(options));

    /// <summary>Streams one tool with explicitly selected direct-call history behavior.</summary>
    public IAsyncEnumerable<IToolStreamEvent> StreamToolAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState, DirectToolCallOptions options, CancellationToken cancellationToken)
        => StreamDirectToolAsync(toolUse, invocationState, CopyDirectToolOptions(options), cancellationToken);

    private static DirectToolCallOptions CopyDirectToolOptions(DirectToolCallOptions options)
    {
        Guard.NotNull(options, nameof(options));
        if (!Enum.IsDefined(typeof(DirectToolCallProfile), options.Profile)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown direct tool profile.");
        if (options.Profile == DirectToolCallProfile.TypeScript && options.UserMessageOverride is not null)
            throw new ArgumentException("A user-message override is available only for the Python direct-tool profile.", nameof(options));
        return options with { };
    }

    private void ValidateDirectToolHistoryAdmission(DirectToolCallOptions? options)
    {
        if (options is null) return;
        if (InterruptState is not null) throw new InvalidOperationException("A direct tool call cannot run while human input is pending.");
        if (options is { Profile: DirectToolCallProfile.Python, RecordHistory: false } && ClassicManager is not null &&
            _directToolOperations.Any(active => active.Options.RecordHistory))
            throw new ConcurrentInvocationException();
        if (options.RecordHistory && (Volatile.Read(ref _invoking) != 0 || _directToolCalls != 0 || _classicReductions != 0))
            throw new ConcurrentInvocationException();
    }

    private async ValueTask CompleteDirectToolAsync(DirectToolOperation operation, ToolUseBlock call,
        ToolResultBlock result, JsonElement? recordingSchema, IReadOnlyDictionary<string, object?> state, CancellationToken cancellationToken)
    {
        var options = operation.Options;
        if (options.RecordHistory)
        {
            var recorded = call;
            if (options.Profile == DirectToolCallProfile.Python)
            {
                if (call.Input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Python direct-tool recording requires object input.", nameof(call));
                if (recordingSchema is { } schema)
                {
                    if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException("Python direct-tool recording requires a registered schema with object properties.");
                    var filtered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var property in call.Input.EnumerateObject())
                        if (properties.TryGetProperty(property.Name, out _)) filtered.Add(property.Name, property.Value.Clone());
                    recorded = call with { Input = JsonSerializer.SerializeToElement(filtered) };
                }
            }
            var rows = new List<Message>();
            if (options.Profile == DirectToolCallProfile.Python)
            {
                var preface = new List<ContentBlock>();
                if (!string.IsNullOrEmpty(options.UserMessageOverride)) preface.Add(new TextBlock(options.UserMessageOverride + "\n"));
                preface.Add(new TextBlock("agent.tool." + recorded.Name + " direct tool call.\nInput parameters: " + recorded.Input.GetRawText() + "\n"));
                rows.Add(new(Role.User, preface));
            }
            rows.Add(new(Role.Assistant, [recorded]));
            rows.Add(new(Role.User, [result]));
            rows.Add(new(Role.Assistant, [new TextBlock("agent.tool." + recorded.Name + " was called.")]));
            ConversationHistory.Validate(Messages.Concat(rows).ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var message in rows) await AddMessageAsync(message, state, CancellationToken.None).ConfigureAwait(false);
        }
        if (options.Profile != DirectToolCallProfile.Python || ClassicManager is not { } manager) return;
        lock (_initializationGate)
        {
            if (!_directToolOperations.Contains(operation)) throw new InvalidOperationException("The direct tool operation is no longer active.");
            operation.Managing = true;
        }
        try { await manager.ApplyDirectToolManagementAsync(this, operation, cancellationToken).ConfigureAwait(false); }
        finally { lock (_initializationGate) operation.Managing = false; }
    }

    private void ValidateDirectToolManagement(DirectToolOperation operation, ConversationManager manager)
    {
        if (!ReferenceEquals(operation.Owner, this) || !_directToolOperations.Contains(operation) || !operation.Managing ||
            operation.Options.Profile != DirectToolCallProfile.Python || !ReferenceEquals(ClassicManager, manager))
            throw new InvalidOperationException("Classic direct-tool management requires this agent's live configured operation.");
        if (_abortingInterrupts || _initialization is { IsCompleted: false }) throw new ConcurrentInvocationException();
        if (operation.Options.RecordHistory && Volatile.Read(ref _invoking) != 0) throw new ConcurrentInvocationException();
    }

    internal sealed class DirectToolOperation(Agent owner, DirectToolCallOptions options)
    {
        internal Agent Owner { get; } = owner;
        internal DirectToolCallOptions Options { get; } = options;
        internal bool Managing { get; set; }
        internal void AttachCleanup(Exception primary, Exception cleanup) => AttachDirectStreamCleanup(primary, cleanup);
    }
}
