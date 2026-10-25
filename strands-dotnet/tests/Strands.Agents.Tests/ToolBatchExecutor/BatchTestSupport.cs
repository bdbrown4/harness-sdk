using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.ToolBatchExecutor;

internal static class BatchTestSupport
{
    internal static JsonElement Parse(string value = "{}") => JsonSerializer.Deserialize<JsonElement>(value);
    internal static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Ready(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        await task;
    }
    internal static ToolUseBlock Call(string id, string name = "work", string input = "{}") => new(id, name, Parse(input));
    internal static FunctionTool Tool(Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> body, string name = "work") =>
        new(new(name, "Execute test work", Parse("{\"type\":\"object\"}")), body);
    internal static Agent Agent(ITool tool, IToolBatchExecutor? executor, int calls = 3, int parallel = 2) =>
        new(new() { Model = new Model(Enumerable.Range(0, calls).Select(i => Call(i.ToString())).ToArray()), Tools = [tool], ToolBatchExecutor = executor, MaxParallelTools = parallel, MaxTurns = 1 });
    internal sealed class Scheduler(Func<ToolBatchExecutionContext, CancellationToken, ValueTask> callback) : IToolBatchExecutor, IDisposable
    {
        internal int Calls;
        internal bool Disposed;
        public ValueTask ExecuteAsync(ToolBatchExecutionContext execution, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return callback(execution, cancellationToken); }
        public void Dispose() => Disposed = true;
    }
    internal sealed class Model(params ToolUseBlock[] calls) : IModel
    {
        internal int Requests;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = Interlocked.Increment(ref Requests) == 1 && calls.Length > 0;
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            if (first)
            {
                for (var i = 0; i < calls.Length; i++)
                {
                    yield return new ModelContentBlockStartEvent(i, calls[i]);
                    yield return new ModelContentBlockDeltaEvent(i, ToolInput: calls[i].Input.GetRawText());
                    yield return new ModelContentBlockStopEvent(i);
                }
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
                yield return new ModelContentBlockStopEvent(0);
            }
            yield return new ModelMessageStopEvent(first ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
}
