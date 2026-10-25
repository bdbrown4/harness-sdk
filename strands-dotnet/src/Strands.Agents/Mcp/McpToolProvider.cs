using System.Runtime.ExceptionServices;

namespace Strands.Agents.Mcp;

/// <summary>Provides one lazily connected MCP generation to its admitted agent consumers.</summary>
public sealed partial class McpToolProvider : IToolProviderUpdates
{
    private static readonly AsyncLocal<CallbackScope?> ActiveCallback = new();
    [ThreadStatic] private static SynchronousCallbackScope? _synchronousCallback;
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<McpClient>> _connector;
    private readonly bool _continueOnError;
    private readonly McpToolListOptions _discovery;
    private readonly Dictionary<string, object> _consumers = new(StringComparer.Ordinal);
    private Generation? _current;
    private Generation? _closing;

    /// <summary>Creates a borrowed provider whose connector transfers a fresh owned MCP client.</summary>
    /// <remarks>The provider never owns the consuming agents or caller-supplied HTTP clients. Connector and cleanup callbacks must cooperate with their lifetime token; physical cleanup is joined without a timeout.</remarks>
    public McpToolProvider(Func<CancellationToken, Task<McpClient>> connector, McpToolProviderOptions? options = null)
    {
        Guard.NotNull(connector, nameof(connector));
        options ??= new();
        if (options.Name is not null) Guard.NotNullOrWhiteSpace(options.Name, nameof(options));
        var discovery = options.Discovery ?? new();
        if (discovery.MaximumPages < 1 || discovery.MaximumTools < 1) throw new ArgumentOutOfRangeException(nameof(options), "Discovery bounds must be positive.");
        Name = options.Name;
        ConfigureToolChanges(options);
        _connector = connector;
        _continueOnError = options.ContinueOnError;
        _discovery = new()
        {
            Prefix = discovery.Prefix,
            ToolFilter = discovery.ToolFilter is { } filter ? name =>
            {
                using var synchronous = new SynchronousCallbackScope(this);
                return filter(name);
            }
            : null,
            Progress = discovery.Progress,
            ContinueOnError = discovery.ContinueOnError || options.ContinueOnError,
            MaximumPages = discovery.MaximumPages,
            MaximumTools = discovery.MaximumTools,
        };
    }

    /// <summary>The optional application-provided name; this standalone provider does not register a model-facing tool under that name.</summary>
    public string? Name { get; }

    /// <inheritdoc />
    public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context));
        RefuseCallbackReentry();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            if (!_consumers.ContainsKey(context.ConsumerId)) _consumers.Add(context.ConsumerId, new object());
        return default;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
        => LoadToolsAsync(context, cancellationToken, null);

    private ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken, object? requiredAdmission)
    {
        Guard.NotNull(context, nameof(context));
        RefuseCallbackReentry();
        cancellationToken.ThrowIfCancellationRequested();
        Generation generation;
        LoadAttempt attempt;
        var start = false;
        lock (_gate)
        {
            if (!_consumers.TryGetValue(context.ConsumerId, out var admission) ||
                requiredAdmission is not null && !ReferenceEquals(requiredAdmission, admission))
                throw new InvalidOperationException("The MCP tool provider consumer has not been admitted.");
            if (_closing is { } closing) return new(WaitForCloseThenLoadAsync(closing, context, cancellationToken, requiredAdmission));
            generation = _current ??= new();
            if (generation.Watch?.Completion is { IsCompleted: true } terminal) terminal.GetAwaiter().GetResult();
            if (generation.Tools is { } tools) return new(tools);
            if (generation.ConnectionFailed) return new(Array.Empty<ITool>());
            if (generation.Loading is null) { generation.Loading = new(); start = true; }
            attempt = generation.Loading;
            attempt.Waiters++;
            generation.Waiters++;
        }
        var completion = new TaskCompletionSource<IReadOnlyList<ITool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CompleteCallerAsync(generation, attempt, cancellationToken, completion);
        if (start) _ = CompleteLoadAsync(generation, attempt);
        return new(completion.Task);
    }

    /// <inheritdoc />
    public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context));
        RefuseCallbackReentry();
        cancellationToken.ThrowIfCancellationRequested();
        Generation? closing;
        var start = false;
        lock (_gate)
        {
            _consumers.Remove(context.ConsumerId);
            // Preserve the existing owner-set semantics; each independent watch for this owner retires together.
            var subscriptions = (_current ?? _closing)?.UpdateSubscriptions.Where(value => value.ConsumerId == context.ConsumerId).ToArray()
                ?? Array.Empty<UpdateSubscription>();
            var closes = subscriptions.Select(value => value.CloseAsync()).ToArray();
            if (_consumers.Count != 0) return closes.Length == 0 ? default : new(Task.WhenAll(closes));
            closing = _closing;
            if (closing is null && _current is { } current)
            {
                closing = _closing = current;
                _current = null;
                current.Closing = true;
                if (current.Waiters == 0) current.WaitersDrained.TrySetResult(true);
                start = true;
            }
        }
        // Publish the shared task before cancellation callbacks, connector continuations or disposal.
        if (start) _ = CompleteCloseAsync(closing!);
        return closing is null ? default : new(closing.CloseCompletion.Task);
    }

    private async Task<IReadOnlyList<ITool>> WaitForCloseThenLoadAsync(Generation closing, ToolProviderContext context, CancellationToken token, object? requiredAdmission)
    {
        await TaskCancellation.WaitAsync(closing.CloseCompletion.Task, token).ConfigureAwait(false);
        return await LoadToolsAsync(context, token, requiredAdmission).ConfigureAwait(false);
    }

    private async Task CompleteLoadAsync(Generation generation, LoadAttempt attempt)
    {
        var scope = new CallbackScope(this, generation);
        try
        {
            if (generation.Client is null)
            {
                try
                {
                    Task<McpClient> connecting;
                    using (var synchronous = new SynchronousCallbackScope(this)) connecting = _connector(generation.Lifetime.Token);
                    var client = await connecting.ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The MCP provider connector returned null.");
                    generation.Client = client;
                    generation.Lifetime.Token.ThrowIfCancellationRequested();
                    if (!client.IsConnected) throw new InvalidOperationException("The MCP provider connector must transfer a fresh connected client.");
                }
                catch (Exception error) when (_continueOnError && error is not OperationCanceledException && !generation.Lifetime.IsCancellationRequested)
                {
                    lock (_gate) generation.ConnectionFailed = true;
                    using (var synchronous = new SynchronousCallbackScope(this))
                        SdkLogging.Warning("provider=<{0}>, error_type=<{1}> | MCP provider failed to connect, continuing with no tools", Name ?? nameof(McpToolProvider), error.GetType().Name);
                    attempt.Completion.TrySetResult(Array.Empty<ITool>());
                    return;
                }
            }
            // Deliberately outside the startup-failure catch: listing failures are retryable, not sticky.
            IReadOnlyList<ITool> tools;
            if (_toolChanges is null)
            {
                var discovered = await generation.Client.ListToolsAsync(_discovery, generation.Lifetime.Token).ConfigureAwait(false);
                generation.Lifetime.Token.ThrowIfCancellationRequested();
                tools = Array.AsReadOnly(discovered.Cast<ITool>().ToArray());
                lock (_gate) generation.Tools = tools;
            }
            else tools = await LoadToolChangesAsync(generation).ConfigureAwait(false);
            attempt.Completion.TrySetResult(tools);
        }
        catch (Exception error)
        {
            lock (_gate) attempt.Failure = error;
            attempt.Completion.TrySetException(error);
            _ = attempt.Completion.Task.Exception; // Observe even when every caller stopped waiting.
        }
        finally
        {
            lock (_gate)
            {
                attempt.Completed = true;
                if (ReferenceEquals(generation.Loading, attempt)) generation.Loading = null;
                RetainUnobservedFailure(generation, attempt);
            }
            scope.Dispose();
            attempt.PhysicalCompletion.TrySetResult(true);
        }
    }

    private async Task CompleteCallerAsync(Generation generation, LoadAttempt attempt, CancellationToken token,
        TaskCompletionSource<IReadOnlyList<ITool>> completion)
    {
        try
        {
            await TaskCancellation.WaitAsync(attempt.Completion.Task, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            completion.TrySetResult(await attempt.Completion.Task.ConfigureAwait(false));
        }
        catch (Exception error)
        {
            lock (_gate) if (ReferenceEquals(error, attempt.Failure)) attempt.Reported = true;
            var failure = token.IsCancellationRequested && (error is not OperationCanceledException canceled || canceled.CancellationToken != token)
                ? new OperationCanceledException("The MCP provider load wait was cancelled.", error, token) : error;
            // Fault rather than TrySetCanceled so Framework retains the original cause and token.
            completion.TrySetException(failure);
        }
        finally
        {
            lock (_gate)
            {
                attempt.Waiters--;
                generation.Waiters--;
                RetainUnobservedFailure(generation, attempt);
                if (generation.Closing && generation.Waiters == 0) generation.WaitersDrained.TrySetResult(true);
            }
        }
    }

    private static void RetainUnobservedFailure(Generation generation, LoadAttempt attempt)
    {
        if (!attempt.Completed || attempt.Waiters != 0 || attempt.Settled) return;
        attempt.Settled = true;
        if (attempt.Failure is { } error && !attempt.Reported &&
            !(error is OperationCanceledException cancelled && generation.Lifetime.IsCancellationRequested &&
                cancelled.CancellationToken == generation.Lifetime.Token)) generation.UnobservedFailures.Add(error);
    }

    private async Task CompleteCloseAsync(Generation generation)
    {
        var scope = new CallbackScope(this, generation);
        Exception? failure = null;
        try
        {
            generation.IsCancelling = true;
            try { using var synchronous = new SynchronousCallbackScope(this); generation.Lifetime.Cancel(); }
            catch (Exception error) { AppendFailure(ref failure, error); }
            finally { generation.IsCancelling = false; }
            LoadAttempt? loading;
            lock (_gate) loading = generation.Loading;
            if (loading is not null) await loading.PhysicalCompletion.Task.ConfigureAwait(false);
            await generation.WaitersDrained.Task.ConfigureAwait(false);
            UpdateSubscription[] subscriptions;
            lock (_gate) subscriptions = generation.UpdateSubscriptions.ToArray();
            foreach (var subscription in subscriptions)
                try { await subscription.CloseAsync().ConfigureAwait(false); } catch (Exception error) { AppendFailure(ref failure, error); }
            lock (_gate) foreach (var error in generation.UnobservedFailures) AppendFailure(ref failure, error);
            if (generation.Watch is { } watch)
                try { await watch.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { AppendFailure(ref failure, error); }
            if (generation.Client is { } client)
                try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { AppendFailure(ref failure, error); }
        }
        catch (Exception error) { AppendFailure(ref failure, error); }
        finally
        {
            try { generation.Lifetime.Dispose(); } catch (Exception error) { AppendFailure(ref failure, error); }
            lock (_gate)
            {
                generation.Closed = true;
                if (ReferenceEquals(_closing, generation)) _closing = null;
            }
            scope.Dispose();
            if (failure is null) generation.CloseCompletion.TrySetResult(true);
            else generation.CloseCompletion.TrySetException(failure);
            _ = generation.CloseCompletion.Task.Exception;
        }
    }

    private static void AppendFailure(ref Exception? primary, Exception secondary)
    {
        if (primary is null) { primary = secondary; return; }
        if (ReferenceEquals(primary, secondary)) return;
        const string key = "Strands.CleanupErrors";
        var errors = primary.Data[key] is IEnumerable<Exception> existing ? existing.ToList() : new List<Exception>();
        if (!errors.Any(error => ReferenceEquals(error, secondary))) errors.Add(secondary);
        primary.Data[key] = errors;
    }

    private void RefuseCallbackReentry()
    {
        for (var scope = _synchronousCallback; scope is not null; scope = scope.Previous)
            if (ReferenceEquals(scope.Owner, this))
                throw new InvalidOperationException("An MCP provider callback cannot enter its own provider lifecycle.");
        for (var scope = ActiveCallback.Value; scope is not null; scope = scope.Previous)
            if (ReferenceEquals(scope.Owner, this) && (scope.Active || scope.Generation.IsCancelling))
                throw new InvalidOperationException("An MCP provider callback cannot enter its own provider lifecycle.");
        McpClient? current; McpClient? closing;
        lock (_gate) { current = _current?.Client; closing = _closing?.Client; }
        current?.RefuseToolChangeCallbackReentry();
        if (!ReferenceEquals(current, closing)) closing?.RefuseToolChangeCallbackReentry();
    }

    private sealed class Generation
    {
        internal CancellationTokenSource Lifetime { get; } = new();
        internal TaskCompletionSource<bool> CloseCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> WaitersDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal McpClient? Client;
        internal IReadOnlyList<ITool>? Tools;
        internal McpToolChangeSubscription? Watch;
        internal long WatchVersion = -1;
        internal LoadAttempt? Loading;
        internal bool ConnectionFailed;
        internal bool Closing;
        internal volatile bool Closed;
        internal volatile bool IsCancelling;
        internal int Waiters;
        internal List<Exception> UnobservedFailures { get; } = [];
        internal List<UpdateSubscription> UpdateSubscriptions { get; } = [];
        internal bool UpdatesTerminated;
        internal Exception? UpdatesError;
    }

    private sealed class LoadAttempt
    {
        internal TaskCompletionSource<IReadOnlyList<ITool>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> PhysicalCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Failure;
        internal int Waiters;
        internal bool Completed;
        internal bool Reported;
        internal bool Settled;
    }

    private sealed class CallbackScope : IDisposable
    {
        internal CallbackScope(McpToolProvider owner, Generation generation)
        { Owner = owner; Generation = generation; Previous = ActiveCallback.Value; ActiveCallback.Value = this; }
        internal McpToolProvider Owner { get; }
        internal Generation Generation { get; }
        internal CallbackScope? Previous { get; }
        internal volatile bool Active = true;
        public void Dispose() { Active = false; ActiveCallback.Value = Previous; }
    }

    private sealed class SynchronousCallbackScope : IDisposable
    {
        internal SynchronousCallbackScope(McpToolProvider owner)
        { Owner = owner; Previous = _synchronousCallback; _synchronousCallback = this; }
        internal McpToolProvider Owner { get; }
        internal SynchronousCallbackScope? Previous { get; }
        public void Dispose() => _synchronousCallback = Previous;
    }
}

/// <summary>Immutable connection-failure and discovery settings for an MCP tool provider.</summary>
public sealed record McpToolProviderOptions
{
    /// <summary>An optional application name for identifying this provider.</summary>
    public string? Name { get; init; }
    /// <summary>Whether noncancellation connection failures yield an empty sticky result until all consumers leave. Defaults to false. Also enables discovery's existing overlong-prefix skip policy; other discovery errors remain fatal.</summary>
    public bool ContinueOnError { get; init; }
    /// <summary>Ordered discovery, prefix, filtering and bounds. Values are copied at construction.</summary>
    public McpToolListOptions? Discovery { get; init; }
    /// <summary>Opt-in notification rediscovery. Null retains the original cached generation.</summary>
    public McpToolChangeOptions? ToolChanges { get; init; }
    /// <summary>An optional borrowed observer after the provider's refreshed cache is accepted. Requires ToolChanges.</summary>
    public Func<McpToolsChanged, CancellationToken, ValueTask>? OnToolsChanged { get; init; }
}
