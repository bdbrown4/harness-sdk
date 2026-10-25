using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AExecutor
{
    /// <summary>Settles an inactive paused task without replaying its tools, then publishes its canceled state. Active executions must first be cooperatively canceled by their host.</summary>
    public Task CancelAsync(string taskId, string contextId, IA2AEventSink sink, CancellationToken cancellationToken = default)
        => CancelCoreAsync(taskId, contextId, null, sink, cancellationToken);
    internal Task CancelRestoredAsync(string taskId, string contextId, JsonElement currentTask, IA2AEventSink sink, CancellationToken cancellationToken)
        => CancelCoreAsync(taskId, contextId, currentTask, sink, cancellationToken);
    private async Task CancelCoreAsync(string taskId, string contextId, JsonElement? currentTask, IA2AEventSink sink, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId)); Guard.NotNullOrWhiteSpace(contextId, nameof(contextId)); Guard.NotNull(sink, nameof(sink));
        if (ContainsScope(this)) throw new InvalidOperationException("An A2A executor cannot recursively cancel itself.");
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try
        {
            using var lease = currentTask is null
                ? await _contexts.AcquireExistingAsync(contextId, cancellationToken).ConfigureAwait(false)
                : await _contexts.AcquireAsync(contextId, cancellationToken).ConfigureAwait(false);
            if (lease.PendingTaskId is null && lease.Agent.InterruptState is { } pending && currentTask is { } task)
            {
                ValidateRestoredPause(pending, task, taskId, contextId);
                lease.PendingTaskId = taskId;
            }
            if (lease.PendingTaskId != taskId || lease.Agent.InterruptState is null) throw new A2ARpcException(-32002, "The context has no matching paused task.");
            Exception? primary = null;
            try { await lease.Agent.AbortInterruptsAsync("Remote task canceled.", lease.CancellationToken).ConfigureAwait(false); }
            catch (Exception error) { if (lease.Agent.InterruptState is not null) throw; primary = error; }
            lease.PendingTaskId = null;
            try { await PublishAsync(sink, new { kind = "status-update", taskId, contextId, status = new { state = "canceled" }, final = true }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { if (primary is null) throw; A2AHttp.AddCleanup(primary, cleanup); }
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken != cancellationToken)
        { throw RestoreCallerCancellation(error, cancellationToken); }
        finally { Scope.Value = previous; }
    }
    private static IReadOnlyList<InterruptResponse>? ReadInterruptResponses(JsonElement message)
    {
        var parts = message.GetProperty("parts").EnumerateArray().ToArray();
        var hasResponses = parts.Any(part => part.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("interruptResponse", out _));
        if (!hasResponses) return null;
        var responses = new List<InterruptResponse>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (!part.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "data" || !part.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("interruptResponse", out var answer) || answer.ValueKind != JsonValueKind.Object ||
                !answer.TryGetProperty("interruptId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
                !answer.TryGetProperty("response", out var response) || !ids.Add(id.GetString()!))
                throw new A2ARpcException(-32602, "Interrupt responses must be well-formed, unique data parts without ordinary message content.");
            responses.Add(new(id.GetString()!, response));
        }
        return responses;
    }
    private static void ValidateResume(InterruptState? pending, string? pendingTaskId, string taskId, IReadOnlyList<InterruptResponse>? responses)
    {
        if (pending is null)
        {
            if (responses is not null) throw new A2ARpcException(-32602, "The context has no pending interrupt responses.");
            return;
        }
        if (responses is null || pendingTaskId != taskId) throw new A2ARpcException(-32602, "The context requires interrupt responses for its original task.");
        var unanswered = new HashSet<string>(pending.Interrupts.Select(question => question.Id), StringComparer.Ordinal);
        unanswered.ExceptWith(pending.Responses.Select(response => response.InterruptId));
        if (responses.Any(response => !unanswered.Contains(response.InterruptId))) throw new A2ARpcException(-32602, "An interrupt response does not identify an outstanding question.");
    }
    private static ValueTask PublishInputRequiredAsync(IA2AEventSink sink, A2ARequestContext context, IReadOnlyList<Interrupt> interrupts, CancellationToken cancellationToken)
    {
        var text = interrupts.Count == 0 ? "Agent requires additional input to continue." : "Agent requires input:\n" + string.Join("\n", interrupts.Select(question => "- " + question.Name));
        var parts = new List<object> { new { kind = "text", text } };
        if (interrupts.Count > 0) parts.Add(new { kind = "data", data = new { interrupts = interrupts.Select(question => new { interruptId = question.Id, name = question.Name, reason = question.Reason }).ToArray() } });
        return PublishAsync(sink, new
        {
            kind = "status-update",
            taskId = context.TaskId,
            contextId = context.ContextId,
            status = new { state = "input-required", message = new { kind = "message", messageId = Guid.NewGuid().ToString("N"), role = "agent", taskId = context.TaskId, contextId = context.ContextId, parts } },
            final = true
        }, cancellationToken);
    }
}
