namespace Strands.Agents;

/// <summary>An optional borrowed provider capability for ordered full tool snapshots.</summary>
public interface IToolProviderUpdates : IToolProvider
{
    /// <summary>Establishes a consumer subscription and delivers its initial full snapshot before returning its owned subscription lease.</summary>
    /// <remarks>The token governs establishment only. The provider owns provisional cleanup on failure. Disposing the returned lease stops and physically joins delivery; tools and the provider remain borrowed.</remarks>
    ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer, CancellationToken cancellationToken = default);
}
