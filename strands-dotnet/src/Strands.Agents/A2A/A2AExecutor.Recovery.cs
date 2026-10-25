using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AExecutor
{
    private static void BindRestoredPause(A2AContextPool.Lease lease, A2ARequestContext context)
    {
        if (lease.PendingTaskId is not null || context.CurrentTask is not { } task) return;
        if (lease.Agent.InterruptState is not { } pending) throw RecoveryMismatch();
        ValidateRestoredPause(pending, task, context.TaskId, context.ContextId);
        lease.PendingTaskId = context.TaskId;
    }
    private static void ValidateRestoredPause(InterruptState pending, JsonElement task, string taskId, string contextId)
    {
        if (A2ATaskRecord.Validate(task) != taskId || task.GetProperty("contextId").GetString() != contextId ||
            task.GetProperty("status").GetProperty("state").GetString() != "input-required") throw RecoveryMismatch();
        var answered = new HashSet<string>(pending.Responses.Select(value => value.InterruptId), StringComparer.Ordinal);
        var completed = new HashSet<string>(pending.CompletedToolResults.Select(value => value.ToolUseId), StringComparer.Ordinal);
        var outstanding = pending.Interrupts.Where(value => !answered.Contains(value.Id) && (value.IsBatch || !completed.Contains(value.ToolUseId))).ToDictionary(value => value.Id, StringComparer.Ordinal);
        if (outstanding.Count == 0) throw RecoveryMismatch();
        var status = task.GetProperty("status");
        if (!status.TryGetProperty("message", out var message) || !message.TryGetProperty("parts", out var parts)) throw RecoveryMismatch();
        var published = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var part in parts.EnumerateArray())
        {
            if (A2AJson.RequiredString(part, "kind") != "data" || !part.GetProperty("data").TryGetProperty("interrupts", out var questions)) continue;
            if (questions.ValueKind != JsonValueKind.Array) throw RecoveryMismatch();
            foreach (var question in questions.EnumerateArray())
            {
                var id = A2AJson.RequiredString(question, "interruptId");
                if (published.ContainsKey(id)) throw RecoveryMismatch();
                published.Add(id, question);
            }
        }
        if (published.Count != outstanding.Count) throw RecoveryMismatch();
        foreach (var question in outstanding.Values)
        {
            if (!published.TryGetValue(question.Id, out var previous) || A2AJson.RequiredString(previous, "name") != question.Name) throw RecoveryMismatch();
            var reason = previous.TryGetProperty("reason", out var supplied) && supplied.ValueKind != JsonValueKind.Null ? supplied : (JsonElement?)null;
            var expectedReason = question.Reason is { ValueKind: not JsonValueKind.Null } expected ? expected : (JsonElement?)null;
            if (reason.HasValue != expectedReason.HasValue || reason is { } actual && !JsonElement.DeepEquals(actual, expectedReason!.Value)) throw RecoveryMismatch();
        }
    }
    private static A2ARpcException RecoveryMismatch() => new(-32004, "The persisted task does not match the restored native paused execution.");
    private static OperationCanceledException RestoreCallerCancellation(OperationCanceledException error, CancellationToken cancellationToken)
    {
        var canceled = new OperationCanceledException("The A2A execution was canceled.", error, cancellationToken);
        if (error.Data["Strands.CleanupErrors"] is List<Exception> cleanup) canceled.Data["Strands.CleanupErrors"] = new List<Exception>(cleanup);
        return canceled;
    }
}
