using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.Cancellation;

internal sealed class CancellationTestModel(params ModelStreamEvent[][] turns) : IModel
{
    internal List<ModelRequest> Requests { get; } = [];
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        foreach (var value in turns[Requests.Count - 1])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    internal static ModelStreamEvent[] Text(string text) =>
    [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0),
        new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];

    internal static ModelStreamEvent[] Call(params (string Id, string Name, string Json)[] calls)
    {
        List<ModelStreamEvent> events = [new ModelMessageStartEvent()];
        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            events.Add(new ModelContentBlockStartEvent(index, new(call.Id, call.Name, JsonSerializer.SerializeToElement(new { }))));
            events.Add(new ModelContentBlockDeltaEvent(index, ToolInput: call.Json));
            events.Add(new ModelContentBlockStopEvent(index));
        }
        events.Add(new ModelMessageStopEvent(StopReasons.ToolUse));
        return events.ToArray();
    }

    internal static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback) =>
        new(new(name, name, JsonSerializer.SerializeToElement(new { type = "object" })), callback);

    internal static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Wait(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000)));
        await task;
    }
}
