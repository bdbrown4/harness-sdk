using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed partial class AgentTests
{
    internal static ModelStreamEvent[] Text(string text, Usage? usage = null) =>
    [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0),
        new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0),
        new ModelMessageStopEvent(StopReasons.EndTurn), new ModelMetadataEvent(usage ?? new())];

    internal static ModelStreamEvent[] Call(params (string Id, string Name, string Json)[] calls)
    {
        List<ModelStreamEvent> events = [new ModelMessageStartEvent()];
        for (var i = 0; i < calls.Length; i++)
        {
            var call = calls[i];
            events.Add(new ModelContentBlockStartEvent(i, new(call.Id, call.Name, JsonSerializer.SerializeToElement(new { }))));
            events.Add(new ModelContentBlockDeltaEvent(i, ToolInput: call.Json));
            events.Add(new ModelContentBlockStopEvent(i));
        }
        events.Add(new ModelMessageStopEvent(StopReasons.ToolUse));
        return events.ToArray();
    }

    internal sealed class ScriptedModel(params ModelStreamEvent[][] turns) : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var item in turns[Requests.Count - 1])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }
}
