using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit.Abstractions;

namespace Strands.Agents.Monty.Tests;

public sealed class ProgrammaticToolCallerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ActualMontyCallsARegisteredToolAndUnwrapsItsJsonResult()
    {
        var effects = 0;
        var echo = new FunctionTool(new("echo", "Echo a value", JsonSerializer.SerializeToElement(new { type = "object" })), (input, _, _) => { effects++; return new(JsonSerializer.Serialize(new { answer = input.GetProperty("value").GetInt32() })); });
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = new Agent(new() { Model = new NoModel(), Tools = new[] { echo, caller } });
        var result = await Invoke(caller, agent, "value = await echo(value=21)\nprint(value['answer'] * 2)\n'not printed'");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("42", result.Content);
        Assert.Equal(1, effects);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task ReturnValuesStayInsideTheVmUnlessPrinted()
    {
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = new Agent(new() { Model = new NoModel(), Tools = new[] { caller } });
        var result = await Invoke(caller, agent, "sum(x * x for x in range(5))");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("(no output)", result.Content);
    }

    [Theory]
    [InlineData("if :", "Syntax error:")]
    [InlineData("print('before')\n1/0", "ZeroDivisionError")]
    [InlineData("open('/etc/passwd').read()", "PermissionError")]
    public async Task GenuineVmErrorsReturnCorrelatedResults(string code, string text)
    {
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = new Agent(new() { Model = new NoModel(), Tools = new[] { caller } });
        var result = await Invoke(caller, agent, code);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal("ptc-call", result.ToolUseId);
        Assert.Contains(text, result.Content);
        if (code.StartsWith("print", StringComparison.Ordinal)) Assert.StartsWith("before", result.Content);
    }

    [Fact]
    public async Task NativeAllocatorExitDuringAHostResultIsReportedAsMemoryError()
    {
        var large = new FunctionTool(new("large", "Large result", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new(new string('x', 12 * 1024 * 1024)));
        var caller = ProgrammaticToolCaller.Create(new() { MaxMemoryBytes = 4 * 1024 * 1024 });
        await using var agent = new Agent(new() { Model = new NoModel(), Tools = new[] { large, caller } });
        var result = await Invoke(caller, agent, "print('before')\nvalue = await large()\nprint(len(value))");
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Contains("MemoryError", result.Content);
        Assert.StartsWith("before", result.Content);
    }

    [Fact]
    public async Task NativeHandshakeProvesThePinnedEngineAndActualProcessArchitecture()
    {
        var pointer = 0;
        var process = 0;
        var caller = ProgrammaticToolCaller.Create(new() { WorkerReady = (id, bits) => { process = id; pointer = bits; } });
        await using var agent = new Agent(new() { Model = new NoModel(), Tools = new[] { caller } });
        Assert.Equal("native", (await Invoke(caller, agent, "print('native')")).Content);
        Assert.True(process > 0);
        Assert.Equal(IntPtr.Size * 8, pointer);
        var architecture = RuntimeInformation.ProcessArchitecture.ToString();
        var expected = Environment.GetEnvironmentVariable("STRANDS_MONTY_EXPECTED_ARCHITECTURE");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, architecture);
        output.WriteLine("MontyEngine:0.0.23;ABI:1;ProcessArchitecture:" + architecture + ";HostPointerSize:" + IntPtr.Size + ";WorkerPointerBits:" + pointer + ";WorkerProcessId:" + process);
    }

    private static ValueTask<ToolResultBlock> Invoke(ITool caller, Agent agent, string code, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.SerializeToElement(new { code });
        return ((IRichTool)caller).InvokeResultAsync(input, new(agent, new("ptc-call", caller.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken);
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Programmatic calls do not invoke the model.");
    }
}
