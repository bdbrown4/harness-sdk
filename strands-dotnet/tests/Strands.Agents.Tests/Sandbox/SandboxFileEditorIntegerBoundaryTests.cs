using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorIntegerBoundaryTests
{
    [Theory]
    [InlineData("SEI-E01", "2147483647.0")]
    [InlineData("SEI-E02", "-2147483648e0")]
    [InlineData("SEI-E08", "21474836470e-1")]
    [InlineData("SEI-E09", "-21474836480e-1")]
    [InlineData("SEI-E11", "1e1")]
    public async Task ExactInt32ValuesReachSemanticBoundsAfterSelectedRead(string id, string line)
    {
        Assert.StartsWith("SEI-E", id);
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, Insert(line), caller.Token));
        Assert.Equal("line", error.ParamName);
        Assert.StartsWith("insert_line must be within [0, 2].", error.Message);
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
        Assert.Equal("a\nb", selected.Files["/work/a.txt"]);
    }

    [Theory]
    [InlineData("SEI-E03", "2147483648e0")]
    [InlineData("SEI-E04", "-2147483649e0")]
    [InlineData("SEI-E07", "1000e-4")]
    public async Task ExactRangeAndFractionDecisionsRejectBeforeCallbacks(string id, string line)
    {
        Assert.StartsWith("SEI-E", id);
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, Insert(line)));
        Assert.Equal("input", error.ParamName);
        Assert.StartsWith("insert_line must be an integer.", error.Message);
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
        Assert.Equal("a\nb", selected.Files["/work/a.txt"]);
    }

    [Theory]
    [InlineData("SEI-E05", "0.00100e3", 1)]
    [InlineData("SEI-E06", "1000e-3", 1)]
    [InlineData("SEI-E10", "0.000e+999999999999999999999999999999999999", 0)]
    public async Task CanceledScalesAndAllZeroMantissasKeepExactPositions(string id, string line, int expectedLine)
    {
        Assert.StartsWith("SEI-E", id);
        var selected = File();
        var other = new EditorFakeSandbox { Forbidden = true };
        await using var agent = EditorTestContext.Agent(other);
        using var caller = new CancellationTokenSource();
        var result = await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, Insert(line), caller.Token);
        Assert.Equal(expectedLine == 0 ? "X\na\nb" : "a\nX\nb", selected.Files["/work/a.txt"]);
        Assert.Contains(expectedLine == 0 ? "     1  X\n     2  a\n     3  b\n" : "     1  a\n     2  X\n     3  b\n", result);
        Assert.Single(selected.Writes);
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt", "write:/work/a.txt");
        Assert.Empty(other.Calls);
        Assert.Equal(0, selected.Disposals);
    }

    [Fact]
    public async Task ParsedNegativeZeroStillFailsOneBasedViewBoundsAfterRead()
    {
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        const string input = "{\"command\":\"view\",\"path\":\"/work/a.txt\",\"view_range\":[-0.0,2]}";
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await InvokeRaw(Core.SandboxFileEditor.CreateForAgent(), agent, input, caller.Token));
        Assert.Equal("range", error.ParamName);
        Assert.StartsWith("view_range must be within the file's lines", error.Message);
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
        Assert.Equal("a\nb", selected.Files["/work/a.txt"]);
    }

    private static EditorFakeSandbox File()
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = "a\nb";
        return selected;
    }

    private static string Insert(string line) => "{\"command\":\"insert\",\"path\":\"/work/a.txt\",\"insert_line\":" + line + ",\"new_str\":\"X\"}";
    private static ValueTask<string> InvokeRaw(ITool tool, Agent agent, string json, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(json);
        var input = document.RootElement.Clone();
        return tool.InvokeAsync(input, new(agent, new("boundary-integer-editor", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken);
    }

    private static void AssertCalls(EditorFakeSandbox selected, CancellationToken token, params string[] calls)
    {
        Assert.Equal(calls, selected.Calls.Select(call => call.Operation + ":" + call.Path));
        Assert.All(selected.Calls, call => Assert.Equal(token, call.Token));
    }
}
