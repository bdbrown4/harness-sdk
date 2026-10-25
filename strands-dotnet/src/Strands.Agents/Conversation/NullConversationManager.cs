namespace Strands.Agents.Conversation;

/// <summary>A classic manager that retains history and cannot recover from overflow.</summary>
public sealed class NullConversationManager : ConversationManager
{
    /// <inheritdoc />
    protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken) => default;
    /// <inheritdoc />
    protected override ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken) { CannotReduce(error); return default; }
    internal override ConversationManager CreateForAgent() => new NullConversationManager();
}
