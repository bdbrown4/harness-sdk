using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AClient
{
    /// <summary>Retrieves a task and optional bounded history from the remote JSON-RPC endpoint.</summary>
    public Task<JsonElement> GetTaskAsync(string taskId, int? historyLength = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId));
        if (historyLength < 0) throw new ArgumentOutOfRangeException(nameof(historyLength));
        var parameters = new Dictionary<string, object?> { ["id"] = taskId };
        if (historyLength is { } length) parameters["historyLength"] = length;
        return TaskCallAsync("tasks/get", taskId, JsonSerializer.SerializeToElement(parameters), cancellationToken);
    }
    /// <summary>Requests cooperative remote cancellation. This is separate from cancelling a local HTTP wait.</summary>
    public Task<JsonElement> CancelTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId));
        return TaskCallAsync("tasks/cancel", taskId, JsonSerializer.SerializeToElement(new { id = taskId }), cancellationToken);
    }
    /// <summary>Reconnects to an existing task's event stream without resending its input.</summary>
    public IAsyncEnumerable<JsonElement> ResubscribeAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId));
        return StreamTaskAsync(taskId, cancellationToken);
    }
    private async Task<JsonElement> TaskCallAsync(string method, string taskId, JsonElement parameters, CancellationToken cancellationToken)
    {
        var result = await CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        ValidateTaskCorrelation(result, taskId);
        _ = new A2AStreamState().Observe(result);
        return result;
    }
    private async IAsyncEnumerable<JsonElement> StreamTaskAsync(string taskId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var iterator = StreamCallAsync("tasks/resubscribe", JsonSerializer.SerializeToElement(new { id = taskId }), cancellationToken).GetAsyncEnumerator(cancellationToken);
        Exception? primary = null;
        try
        {
            while (true)
            {
                JsonElement value;
                try
                {
                    if (!await iterator.MoveNextAsync().ConfigureAwait(false)) break;
                    value = iterator.Current;
                    ValidateTaskCorrelation(value, taskId);
                }
                catch (Exception error) { primary = error; throw; }
                yield return value;
            }
        }
        finally
        {
            try { await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { if (primary is null) throw; A2AHttp.AddCleanup(primary, cleanup); }
        }
    }
    private static void ValidateTaskCorrelation(JsonElement value, string taskId)
    {
        var kind = A2AJson.RequiredString(value, "kind");
        if (kind is not ("task" or "status-update" or "artifact-update") || A2AJson.RequiredString(value, kind == "task" ? "id" : "taskId") != taskId)
            throw new A2AProtocolException("The A2A server returned a different task than requested.");
    }
}
