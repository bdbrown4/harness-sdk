namespace Strands.Agents.Mcp;

public sealed partial class McpToolProvider
{
    private McpToolChangeOptions? _toolChanges;
    private Func<McpToolsChanged, CancellationToken, ValueTask>? _onToolsChanged;

    private void ConfigureToolChanges(McpToolProviderOptions options)
    {
        if (options.ToolChanges is null)
        {
            if (options.OnToolsChanged is not null) throw new ArgumentException("An MCP provider change observer requires ToolChanges.", nameof(options));
            return;
        }
        var changes = options.ToolChanges;
        if (changes.Profile is not (McpToolChangeProfile.Python or McpToolChangeProfile.TypeScript) ||
            changes.Debounce < TimeSpan.Zero || changes.Debounce.TotalMilliseconds > int.MaxValue ||
            changes.AcknowledgementTimeout <= TimeSpan.Zero || changes.AcknowledgementTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options));
        _toolChanges = changes with { };
        _onToolsChanged = options.OnToolsChanged;
    }

    private async Task<IReadOnlyList<ITool>> LoadToolChangesAsync(Generation generation)
    {
        var watch = await generation.Client!.WatchToolChangesAsync(async (snapshot, token) =>
        {
            using var scope = new CallbackScope(this, generation);
            if (!AcceptToolChangeSnapshot(generation, snapshot)) return;
            if (_onToolsChanged is { } observer)
            {
                ValueTask pending;
                using (var synchronous = new SynchronousCallbackScope(this)) pending = observer(snapshot, token);
                await pending.ConfigureAwait(false);
            }
        }, _discovery, _toolChanges, generation.Lifetime.Token).ConfigureAwait(false);
        generation.Watch = watch;
        generation.Lifetime.Token.ThrowIfCancellationRequested();
        AcceptToolChangeSnapshot(generation, watch.Current);
        _ = ObserveToolChangeCompletionAsync(generation, watch.Completion);
        lock (_gate) return generation.Tools!;
    }

    private bool AcceptToolChangeSnapshot(Generation generation, McpToolsChanged snapshot)
    {
        var tools = Array.AsReadOnly(snapshot.Tools.Cast<ITool>().ToArray());
        lock (_gate)
        {
            if (generation.Closing || snapshot.Version <= generation.WatchVersion) return false;
            generation.Tools = tools;
            generation.WatchVersion = snapshot.Version;
            foreach (var subscription in generation.UpdateSubscriptions) subscription.EnqueueUnderGate(tools, snapshot.Version);
            return true;
        }
    }
}
