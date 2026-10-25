using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private readonly HashSet<Task> _pushOperations = [];
    private readonly Queue<string> _retiredPushTasks = [];
    private readonly SemaphoreSlim _pushConfigurationChanges = new(1, 1);
    private static readonly AsyncLocal<PushChangeScope?> PushChanges = new();

    private static bool IsPushMethod(string? method) => method is "tasks/pushNotificationConfig/set" or "tasks/pushNotificationConfig/get" or "tasks/pushNotificationConfig/list" or "tasks/pushNotificationConfig/delete";

    private async Task<JsonElement?> AuthorizeMessagePushAsync(HttpRequestMessage request, JsonElement parameters, CancellationToken cancellationToken)
    {
        var configuration = OptionalObject(parameters, "configuration");
        if (configuration is not { } settings || !settings.TryGetProperty("pushNotificationConfig", out var push)) return null;
        return await RunPushOperationAsync(token => AuthorizePushAsync(request, push, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> AuthorizePushAsync(HttpRequestMessage request, JsonElement configuration, CancellationToken cancellationToken)
    {
        var options = _options.PushNotifications ?? throw new A2ARpcException(-32003, "Push notifications are not supported.");
        configuration = NormalizePushConfiguration(configuration, null);
        if (!await options.AuthorizeConfiguration(request, configuration, cancellationToken).ConfigureAwait(false))
            throw new A2ARpcException(-32602, "The push notification configuration was not authorized.");
        cancellationToken.ThrowIfCancellationRequested();
        return configuration;
    }

    private async Task<JsonElement> HandlePushRequestAsync(HttpRequestMessage request, string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        var options = _options.PushNotifications ?? throw new A2ARpcException(-32003, "Push notifications are not supported.");
        var id = A2AJson.RequiredString(parameters, method == "tasks/pushNotificationConfig/set" ? "taskId" : "id");
        _ = await GetTaskAsync(JsonSerializer.SerializeToElement(new { id }), cancellationToken).ConfigureAwait(false);
        if (method == "tasks/pushNotificationConfig/set")
        {
            var configuration = await AuthorizePushAsync(request, A2AJson.RequiredObject(parameters, "pushNotificationConfig"), cancellationToken).ConfigureAwait(false);
            configuration = NormalizePushConfiguration(configuration, id);
            await ChangePushConfigurationsAsync(async () =>
            {
                if (_options.TaskStore is null) lock (_gate) _ = FindTask(id);
                await options.ConfigStore.SetAsync(id, configuration, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return PushRecord(id, configuration);
        }
        if (method == "tasks/pushNotificationConfig/delete")
        {
            await options.ConfigStore.DeleteAsync(id, A2AJson.RequiredString(parameters, "pushNotificationConfigId"), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement<object?>(null);
        }
        var configurations = await options.ConfigStore.ListAsync(id, cancellationToken).ConfigureAwait(false);
        if (method == "tasks/pushNotificationConfig/list") return JsonSerializer.SerializeToElement(configurations.Select(configuration => PushRecord(id, configuration)).ToArray());
        var selectedId = A2AJson.OptionalString(parameters, "pushNotificationConfigId");
        foreach (var configuration in configurations)
            if (selectedId is null || A2AJson.RequiredString(configuration, "id") == selectedId) return PushRecord(id, configuration);
        throw new A2ARpcException(-32602, "The push notification configuration was not found.");
    }

    private static JsonElement PushRecord(string id, JsonElement configuration) => JsonSerializer.SerializeToElement(new { taskId = id, pushNotificationConfig = configuration });

    private async Task PrepareExecutionPushAsync(Execution execution)
    {
        if (_options.PushNotifications is not { } options) return;
        var token = execution.Cancellation.Token;
        await ChangePushConfigurationsAsync(async () =>
        {
            while (true)
            {
                string retired;
                lock (_gate)
                {
                    if (_retiredPushTasks.Count == 0) break;
                    retired = _retiredPushTasks.Peek();
                }
                var configurations = await options.ConfigStore.ListAsync(retired, token).ConfigureAwait(false);
                foreach (var configuration in configurations)
                    await options.ConfigStore.DeleteAsync(retired, A2AJson.RequiredString(configuration, "id"), token).ConfigureAwait(false);
                lock (_gate) _retiredPushTasks.Dequeue();
            }
            if (execution.PushConfiguration is { } current)
            {
                await options.ConfigStore.SetAsync(execution.Entry.Id, NormalizePushConfiguration(current, execution.Entry.Id), token).ConfigureAwait(false);
                execution.PushConfigurationStored = true;
            }
        }, token).ConfigureAwait(false);
    }

    private async Task ChangePushConfigurationsAsync(Func<Task> change, CancellationToken cancellationToken)
    {
        for (var scope = PushChanges.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Server, this)) throw new InvalidOperationException("A push configuration store callback cannot recursively change its own server's configurations.");
        await _pushConfigurationChanges.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previous = PushChanges.Value; PushChanges.Value = new(this, previous);
        try { await change().ConfigureAwait(false); }
        finally { PushChanges.Value = previous; _pushConfigurationChanges.Release(); }
    }

    private static JsonElement NormalizePushConfiguration(JsonElement configuration, string? defaultId)
    {
        A2AJson.RequireObject(configuration); A2AJson.ValidateUniqueProperties(configuration);
        if (!Uri.TryCreate(A2AJson.RequiredString(configuration, "url"), UriKind.Absolute, out var url)) throw new A2AProtocolException("A push destination must be an absolute HTTP or HTTPS URI.");
        A2AHttp.ValidateUri(url, nameof(configuration));
        if (url.UserInfo.Length != 0 || url.Fragment.Length != 0) throw new A2AProtocolException("A push destination cannot contain user information or a fragment.");
        var id = A2AJson.OptionalString(configuration, "id") ?? defaultId;
        if (configuration.TryGetProperty("token", out _)) _ = A2AJson.OptionalString(configuration, "token");
        if (configuration.TryGetProperty("authentication", out var authentication))
        {
            A2AJson.RequireObject(authentication);
            if (!authentication.TryGetProperty("schemes", out var schemes) || schemes.ValueKind != JsonValueKind.Array || schemes.GetArrayLength() == 0 || schemes.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                throw new A2AProtocolException("Push authentication requires nonempty scheme names.");
            if (authentication.TryGetProperty("credentials", out _)) _ = A2AJson.OptionalString(authentication, "credentials");
        }
        var members = configuration.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
        if (id is not null) members["id"] = id;
        return JsonSerializer.SerializeToElement(members);
    }

    private async Task<T> RunPushOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken shutdown;
        lock (_gate)
        {
            if (_disposeTask is not null) throw new ObjectDisposedException(nameof(A2AServer));
            shutdown = _shutdown.Token; _pushOperations.Add(completion.Task);
        }
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
            var result = await operation(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("The push operation was canceled.", error, cancellationToken); }
        finally
        {
            Scope.Value = previous;
            lock (_gate) _pushOperations.Remove(completion.Task);
            completion.TrySetResult(true);
        }
    }

    private async ValueTask PushAsync(JsonElement task, CancellationToken cancellationToken)
    {
        if (_options.PushNotifications is not { } options || _shutdown.IsCancellationRequested) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var id = A2AJson.RequiredString(task, "id");
        IReadOnlyList<JsonElement> configurations;
        try { configurations = await options.ConfigStore.ListAsync(id, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); return; }
        catch (Exception error) { ReportPushFailure(id, error); return; }
        foreach (var configuration in configurations)
        {
            if (linked.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); return; }
            try { await options.SendAsync(task.Clone(), configuration.Clone(), linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); return; }
            catch (Exception error) { ReportPushFailure(id, error); }
        }
    }

    private void ReportPushFailure(string taskId, Exception error)
    {
        try
        {
            if (_options.PushNotifications!.DeliveryFailed is { } observer) observer(taskId, error);
            else System.Diagnostics.Trace.TraceWarning("A2A push delivery failed ({0}).", error.GetType().Name);
        }
        catch (Exception observerError) { System.Diagnostics.Trace.TraceWarning("A2A push failure observer failed ({0}).", observerError.GetType().Name); }
    }
    private sealed record PushChangeScope(A2AServer Server, PushChangeScope? Parent);
}
