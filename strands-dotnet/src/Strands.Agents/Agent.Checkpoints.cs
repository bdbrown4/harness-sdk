namespace Strands.Agents;

public sealed partial class Agent
{
    private void ValidateCheckpointResume(string? prompt, InvocationOptions options)
    {
        if (options.CheckpointResume is not { } checkpoint) return;
        if (_options.CheckpointBehavior == CheckpointBehavior.Disabled)
            throw new InvalidOperationException("Checkpoint resumption requires an agent with checkpoint behavior enabled.");
        if (prompt is not null || options.InputMessage is not null || options.InputMessages.Count != 0 || options.InterruptResponses.Count != 0)
            throw new ArgumentException("Checkpoint resumption cannot also append input or answer a human interrupt.", nameof(options));
        if (checkpoint.Position == CheckpointPosition.AfterTools && checkpoint.CycleIndex == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "The next checkpoint cycle index exceeds Int32.MaxValue.");
    }

    private Message? PrepareCheckpointReplay(InvocationOptions options)
    {
        if (options.CheckpointResume is not { Position: CheckpointPosition.AfterModel } ||
            _options.CheckpointBehavior != CheckpointBehavior.PythonReplay) return null;
        if (InterruptState is not null)
            throw new InvalidOperationException("Answer the pending human interrupt before resuming a cycle checkpoint.");
        lock (_historyGate)
        {
            var message = _messages.LastOrDefault();
            if (message is not { Role: Role.Assistant } || !message.Content.OfType<ToolUseBlock>().Any())
                throw new InvalidOperationException("An after-model replay requires its retained assistant tool request in the conversation.");
            return message;
        }
    }

    private static AgentResult PauseAtCheckpoint(InvocationProgress progress, CheckpointPosition position)
    {
        progress.Checkpoint = new(position, progress.CheckpointCycleIndex);
        return progress.Result(StopReasons.Checkpoint);
    }
}
