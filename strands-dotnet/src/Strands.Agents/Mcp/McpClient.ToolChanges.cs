using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Strands.Agents.Mcp;

public sealed partial class McpClient
{
    private static readonly AsyncLocal<ToolChangeCallbackScope?> ToolChangeCallback = new();
    private readonly ToolChangeWireState _toolChangeWire;
    private ToolChangeChannel? _toolChangeChannel;

    /// <summary>Observes tool-change notifications and accepts complete bounded discoveries for one scope.</summary>
    /// <param name="onChanged">The borrowed observer invoked after each refreshed snapshot is accepted.</param>
    /// <param name="discovery">The copied prefix, filter, and discovery bounds.</param>
    /// <param name="options">The notification scheduling profile and acknowledgement bound.</param>
    /// <param name="cancellationToken">Cancellation for observer creation.</param>
    /// <returns>The owned observer, whose initial discovery is available in Current.</returns>
    public ValueTask<McpToolChangeSubscription> WatchToolChangesAsync(
        Func<McpToolsChanged, CancellationToken, ValueTask> onChanged,
        McpToolListOptions? discovery = null, McpToolChangeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(onChanged, nameof(onChanged));
        RefuseToolChangeCallbackReentry();
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = CopyToolChangeOptions(options);
        var copiedDiscovery = CopyToolChangeDiscovery(discovery);
        return new(CreateToolChangeSubscriptionAsync(onChanged, copiedDiscovery, configuration, cancellationToken));
    }

    private async Task<McpToolChangeSubscription> CreateToolChangeSubscriptionAsync(
        Func<McpToolsChanged, CancellationToken, ValueTask> callback, McpToolListOptions discovery,
        McpToolChangeOptions options, CancellationToken caller)
    {
        ToolChangeChannel channel;
        McpToolChangeSubscription subscription;
        while (true)
        {
            Task? closing = null;
            var start = false;
            lock (_gate)
            {
                if (_disposal is not null) throw new ObjectDisposedException(nameof(McpClient));
                caller.ThrowIfCancellationRequested();
                if (_toolChangeChannel is { Closing: true } previous) closing = previous.Closed.Task;
                if (closing is null)
                {
                    channel = _toolChangeChannel ??= new();
                    subscription = new(this, channel, callback, discovery, options);
                    channel.Subscriptions.Add(subscription);
                    if (!channel.Started) { channel.Started = true; start = true; }
                }
                else { channel = null!; subscription = null!; }
            }
            if (closing is not null) { await TaskCancellation.WaitAsync(closing, caller).ConfigureAwait(false); continue; }
            if (start) StartToolChangeChannel(channel);
            break;
        }
        try
        {
            using (var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(caller))
            {
                acknowledgement.CancelAfter(options.AcknowledgementTimeout);
                try { await TaskCancellation.WaitAsync(channel.Ready.Task, acknowledgement.Token).ConfigureAwait(false); }
                catch (OperationCanceledException error) when (!caller.IsCancellationRequested && acknowledgement.IsCancellationRequested)
                { throw new TimeoutException("MCP tool-change subscription acknowledgement timed out.", error); }
            }
            using (var initialReceipt = new ToolChangeCallbackScope(this, subscription))
            using (var initial = CancellationTokenSource.CreateLinkedTokenSource(caller, subscription.LifetimeToken))
            {
                var tools = await ListToolsAsync(discovery, initial.Token).ConfigureAwait(false);
                initial.Token.ThrowIfCancellationRequested();
                subscription.Initialize(tools);
                caller.ThrowIfCancellationRequested();
            }
            subscription.CompleteInitial();
            return subscription;
        }
        catch (Exception error)
        {
            // Release initial physical ownership before failed-creation cleanup joins it.
            subscription.CompleteInitial();
            if (error is OperationCanceledException && !caller.IsCancellationRequested)
                lock (_gate) error = channel.Failure ?? error;
            var failure = caller.IsCancellationRequested && (error is not OperationCanceledException canceled || canceled.CancellationToken != caller)
                ? new OperationCanceledException("MCP tool-change observer creation was cancelled.", error, caller) : error;
            try { await subscription.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { AttachDisposalError(failure, cleanup); }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private void StartToolChangeChannel(ToolChangeChannel channel)
    {
        try
        {
            channel.Modern = _protocol.NegotiatedProtocolVersion == "2026-07-28";
            channel.Handlers.Add(_protocol.RegisterNotificationHandler("notifications/tools/list_changed", (notification, token) =>
            {
                HandleToolChange(channel, notification);
                return default;
            }));
            if (channel.Modern)
            {
                channel.Wire = _toolChangeWire.Register(channel.Id);
                channel.Handlers.Add(_protocol.RegisterNotificationHandler("notifications/subscriptions/acknowledged", (notification, token) =>
                {
                    HandleToolChangeAcknowledgement(channel, notification);
                    return default;
                }));
                channel.Handlers.Add(_protocol.RegisterNotificationHandler("notifications/cancelled", (notification, token) =>
                {
                    if (ToolChangeWireState.Read(notification).Id == channel.Id)
                        FailToolChangeChannel(channel, channel.Wire.Current.Failure ?? new OperationCanceledException("MCP server cancelled the tool-change subscription."));
                    return default;
                }));
                channel.ListenRequest = _protocol.SendRequestAsync(new JsonRpcRequest
                {
                    Id = new RequestId(channel.Id),
                    Method = "subscriptions/listen",
                    Params = new JsonObject { ["notifications"] = new JsonObject { ["toolsListChanged"] = true } }
                }, channel.Lifetime.Token);
                _ = ObserveToolChangeListenAsync(channel);
            }
            else channel.Ready.TrySetResult(true);
            _ = ObserveToolChangeSessionAsync(channel);
        }
        catch (Exception error) { FailToolChangeChannel(channel, error); }
        finally { channel.StartPhysical.TrySetResult(true); }
    }

    private void HandleToolChangeAcknowledgement(ToolChangeChannel channel, JsonRpcNotification notification)
    {
        var observation = ToolChangeWireState.Read(notification);
        if (observation.Id != channel.Id) return;
        Exception? failure = null;
        var close = false;
        McpToolChangeSubscription[] pending = [];
        lock (_gate)
        {
            if (channel.Closing) return;
            if (channel.Wire?.Current.Failure is { } wireFailure) failure = wireFailure;
            else if (channel.Acknowledged) failure = new InvalidDataException("MCP subscription acknowledgement was repeated.");
            else
            {
                if (!observation.Granted)
                    failure = new InvalidDataException("MCP server did not grant tools/list_changed notifications.");
                else
                {
                    channel.Acknowledged = true;
                    channel.Ready.TrySetResult(true);
                    if (channel.PendingChange) { channel.PendingChange = false; pending = channel.Subscriptions.ToArray(); }
                }
            }
            if (failure is not null) close = MarkToolChangeChannelFailure(channel, failure);
        }
        if (close) _ = CloseToolChangeChannelAsync(channel);
        else foreach (var subscription in pending) subscription.Notify();
    }

    private void HandleToolChange(ToolChangeChannel channel, JsonRpcNotification notification)
    {
        if (channel.Modern && !MatchesToolChangeChannel(channel, notification)) return;
        McpToolChangeSubscription[] subscribers;
        var close = false;
        lock (_gate)
        {
            if (channel.Closing) return;
            if (channel.Modern && channel.Wire?.Current.Failure is { } wireFailure)
            {
                close = MarkToolChangeChannelFailure(channel, wireFailure);
                subscribers = [];
            }
            else if (channel.Modern && !channel.Acknowledged) { channel.PendingChange = true; subscribers = []; }
            else subscribers = channel.Subscriptions.ToArray();
        }
        if (close) { _ = CloseToolChangeChannelAsync(channel); return; }
        foreach (var subscription in subscribers) subscription.Notify();
    }

    private static bool MatchesToolChangeChannel(ToolChangeChannel channel, JsonRpcNotification notification)
        => ToolChangeWireState.Read(notification).Id == channel.Id;

    private async Task ObserveToolChangeListenAsync(ToolChangeChannel channel)
    {
        Exception failure;
        try { await channel.ListenRequest!.ConfigureAwait(false); failure = new EndOfStreamException("MCP tool-change subscription ended."); }
        catch (Exception error) { failure = error; }
        lock (_gate) if (channel.Closing) return;
        FailToolChangeChannel(channel, failure);
    }

    private async Task ObserveToolChangeSessionAsync(ToolChangeChannel channel)
    {
        if (await Task.WhenAny(_protocol.Completion, channel.Closed.Task).ConfigureAwait(false) != _protocol.Completion) return;
        lock (_gate) if (channel.Closing) return;
        var completed = await _protocol.Completion.ConfigureAwait(false);
        FailToolChangeChannel(channel, completed.Exception ?? new EndOfStreamException("MCP session ended while observing tool changes."));
    }

    private void FailToolChangeChannel(ToolChangeChannel channel, Exception error)
    {
        bool close;
        lock (_gate) close = MarkToolChangeChannelFailure(channel, error);
        if (close) _ = CloseToolChangeChannelAsync(channel);
    }

    // Caller holds _gate. Only marks state; cancellation and handler cleanup run after releasing it.
    private static bool MarkToolChangeChannelFailure(ToolChangeChannel channel, Exception error)
    {
        if (channel.Closing) return false;
        channel.Closing = true;
        channel.Failure = error;
        channel.Ready.TrySetException(error);
        _ = channel.Ready.Task.Exception;
        return true;
    }

    internal Task? ReleaseToolChangeSubscription(ToolChangeChannel channel, McpToolChangeSubscription subscription)
    {
        var close = false;
        lock (_gate)
        {
            channel.Subscriptions.Remove(subscription);
            if (channel.Subscriptions.Count == 0 && !channel.Closing) { channel.Closing = true; close = true; }
        }
        if (close) _ = CloseToolChangeChannelAsync(channel);
        return channel.Closing ? channel.Closed.Task : null;
    }

    private async Task CloseToolChangeChannelAsync(ToolChangeChannel channel)
    {
        Exception? failure = channel.Failure;
        channel.Ready.TrySetException(failure ?? new OperationCanceledException("MCP tool-change observer was closed before acknowledgement.", channel.Lifetime.Token));
        _ = channel.Ready.Task.Exception;
        McpToolChangeSubscription[] subscribers;
        lock (_gate) subscribers = channel.Subscriptions.ToArray();
        foreach (var subscription in subscribers) subscription.Stop(channel.Failure);
        channel.OwnCloseCutover = true;
        try { channel.Lifetime.Cancel(); } catch (Exception error) { AppendToolChangeFailure(ref failure, error); }
        try
        {
            await channel.StartPhysical.Task.ConfigureAwait(false);
            foreach (var subscription in subscribers) await subscription.Drained.ConfigureAwait(false);
            if (channel.ListenRequest is { } listen)
                try { await listen.ConfigureAwait(false); }
                catch (OperationCanceledException) when (channel.Lifetime.IsCancellationRequested) { }
                catch (Exception error) when (IsOwnToolChangeCloseEof(channel, error)) { }
                catch (Exception error) { AppendToolChangeFailure(ref failure, error); }
        }
        catch (Exception error) { AppendToolChangeFailure(ref failure, error); }
        foreach (var handler in channel.Handlers)
            try { await handler.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { AppendToolChangeFailure(ref failure, error); }
        if (channel.Wire is { } wire) _toolChangeWire.Remove(wire);
        channel.Lifetime.Dispose();
        lock (_gate) if (ReferenceEquals(_toolChangeChannel, channel)) _toolChangeChannel = null;
        if (failure is null) channel.Closed.TrySetResult(true);
        else { channel.Closed.TrySetException(failure); _ = channel.Closed.Task.Exception; }
    }

    private static bool IsOwnToolChangeCloseEof(ToolChangeChannel channel, Exception error) =>
        // SDK2.2 collapses cancelled SSE IO into this exact synthetic EOF. Do not hide a
        // different protocol/transport error, or a terminal failure already observed before close.
        channel.OwnCloseCutover && channel.Lifetime.IsCancellationRequested && error.GetType() == typeof(ModelContextProtocol.McpException) &&
        error.InnerException is null && error.Message == "Streamable HTTP POST response completed without a reply to request with ID: " + channel.Id;

    private async Task DisposeToolChangesAsync()
    {
        ToolChangeChannel? channel;
        var close = false;
        lock (_gate)
        {
            channel = _toolChangeChannel;
            if (channel is not null && !channel.Closing) { channel.Closing = true; close = true; }
        }
        if (channel is null) return;
        if (close) _ = CloseToolChangeChannelAsync(channel);
        await channel.Closed.Task.ConfigureAwait(false);
    }

    private static McpToolChangeOptions CopyToolChangeOptions(McpToolChangeOptions? options)
    {
        options ??= new();
        if (options.Profile is not (McpToolChangeProfile.Python or McpToolChangeProfile.TypeScript)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Debounce < TimeSpan.Zero || options.Debounce.TotalMilliseconds > int.MaxValue ||
            options.AcknowledgementTimeout <= TimeSpan.Zero || options.AcknowledgementTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "Tool-change delays must fit the supported timer range.");
        return options with { };
    }

    private static McpToolListOptions CopyToolChangeDiscovery(McpToolListOptions? options)
    {
        options ??= new();
        if (options.MaximumPages < 1 || options.MaximumTools < 1) throw new ArgumentOutOfRangeException(nameof(options));
        return new()
        {
            Prefix = options.Prefix,
            ToolFilter = options.ToolFilter,
            Progress = options.Progress,
            MaximumPages = options.MaximumPages,
            MaximumTools = options.MaximumTools,
            ContinueOnError = options.ContinueOnError
        };
    }

    internal static void AppendToolChangeFailure(ref Exception? primary, Exception secondary)
    { if (primary is null) primary = secondary; else AttachDisposalError(primary, secondary); }

    internal void WarnToolChangeFailure(Exception error)
    {
        try { TryWarn("MCP tool-change refresh failed: " + error.Message, error); }
        catch (Exception) { /* Diagnostic text and observers cannot fault the refresh owner. */ }
    }

    internal void RefuseToolChangeCallbackReentry()
        => RefuseToolChangeCallbackReentry(observePublishedTaskShutdown: false);

    private void RefuseToolChangeCallbackReentry(bool observePublishedTaskShutdown)
    {
        for (var scope = _synchronousCleanup; scope is not null; scope = scope.Previous)
            if (ReferenceEquals(scope.Owner, this) &&
                !(observePublishedTaskShutdown && scope.TaskShutdown))
                throw new InvalidOperationException("An MCP client cleanup callback cannot wait on its own client or provider lifecycle.");
        _httpBridge?.RefuseLifecycleReentry();
        for (var scope = ToolChangeCallback.Value; scope is not null; scope = scope.Previous)
            if (ReferenceEquals(scope.Owner, this) &&
                (scope.Active || scope.Subscription.IsCancelling))
                throw new InvalidOperationException("An MCP tool-change callback cannot wait on its own watcher or client lifecycle.");
    }

    internal sealed class ToolChangeCallbackScope : IDisposable
    {
        internal ToolChangeCallbackScope(McpClient owner, McpToolChangeSubscription subscription)
        { Owner = owner; Subscription = subscription; Previous = ToolChangeCallback.Value; ToolChangeCallback.Value = this; }
        internal McpClient Owner { get; }
        internal McpToolChangeSubscription Subscription { get; }
        internal ToolChangeCallbackScope? Previous { get; }
        internal volatile bool Active = true;
        public void Dispose() { Active = false; ToolChangeCallback.Value = Previous; }
    }

    internal sealed class ToolChangeChannel
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal CancellationTokenSource Lifetime { get; } = new();
        internal TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> StartPhysical { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<IAsyncDisposable> Handlers { get; } = [];
        internal HashSet<McpToolChangeSubscription> Subscriptions { get; } = [];
        internal Task? ListenRequest;
        internal ToolChangeWireState.Entry? Wire;
        internal Exception? Failure;
        internal bool Started;
        internal bool Closing;
        internal bool Modern;
        internal bool Acknowledged;
        internal bool PendingChange;
        internal bool OwnCloseCutover;
    }
}
