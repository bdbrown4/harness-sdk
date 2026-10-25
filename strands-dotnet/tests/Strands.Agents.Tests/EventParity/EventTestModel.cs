using System.Runtime.CompilerServices;

namespace Strands.Agents.Tests.EventParity;

internal sealed class EventTestModel(params ModelStreamEvent[][] turns) : IModel
{
    internal List<ModelRequest> Requests { get; } = [];
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        foreach (var item in turns[Requests.Count - 1])
        {
            cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield();
        }
    }
    internal static ModelStreamEvent[] Text(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
}
