namespace Strands.Agents.Tests;

internal sealed class AttributedPluginTestModel : IModel
{
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Attributed plugin fixtures must not acquire a model.");
}
