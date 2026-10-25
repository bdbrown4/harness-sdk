using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Strands.Agents.Monty.Tests;

[CollectionDefinition("Monty logging", DisableParallelization = true)]
public sealed class MontyLoggingCollection { }

[Collection("Monty logging")]
public sealed class MontyLoggingTests : IDisposable
{
    private readonly ILogger _saved = SdkLogging.Logger;
    public void Dispose() => SdkLogging.ConfigureLogging(_saved);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAliasWarningHonorsHostCancellationBeforeAnyWorkerAdmission(bool loggerFails)
    {
        using var caller = new CancellationTokenSource();
        var logger = new HostLogger(caller, loggerFails); SdkLogging.ConfigureLogging(logger);
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var tools = new[] { new FunctionTool(new("a-_b", "First", schema), (_, _, _) => new ValueTask<string>("first")), new FunctionTool(new("a_-b", "Second", schema), (_, _, _) => new ValueTask<string>("second")) };
        var workerStarted = false;
        var tool = ProgrammaticToolCaller.Create(new() { WorkerPath = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N")), ProcessStarted = _ => workerStarted = true });
        await using var agent = new Agent(new() { Model = new NoModel(), Tools = tools });
        var input = JsonSerializer.SerializeToElement(new { code = "print(1)" });
        var failure = await Record.ExceptionAsync(async () => await ((IRichTool)tool).InvokeResultAsync(input, new(agent, new("call", tool.Spec.Name, input), new Dictionary<string, object?>()), caller.Token));
        Assert.Equal("programmatic_tool_caller: multiple tools normalize to 'a__b', no alias injected.", Assert.Single(logger.Messages));
        Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(failure).CancellationToken);
        Assert.False(workerStarted);
    }
    private sealed class HostLogger(CancellationTokenSource cancellation, bool fails) : ILogger
    {
        internal List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, error)); cancellation.Cancel(); if (fails) throw new IOException("host logger"); }
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No provider call is allowed.");
    }
}
