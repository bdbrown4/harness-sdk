using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorProfileTests
{
    [Theory]
    [InlineData("EP-T01", "[1.5,2.5]", "a\nb\nc", "   1.5  a\n   2.5  b\n")]
    [InlineData("EP-T02", "[2.5,-1]", "a\nb\nc\nd", "   2.5  b\n   3.5  c\n   4.5  d\n")]
    [InlineData("EP-T03", "[1.5,1.5]", "a\nb", "   1.5  a\n")]
    public async Task FractionalViewSlicesKeepOriginalNumberLabels(string id, string range, string text, string expectedRows)
    {
        Assert.StartsWith("EP-T", id);
        var selected = File(text);
        var other = new EditorFakeSandbox { Forbidden = true };
        await using var agent = EditorTestContext.Agent(other);
        using var caller = new CancellationTokenSource();
        var result = await Invoke(Tool(selected), agent, View(range), caller.Token);
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n" + expectedRows, result);
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(other.Calls);
        Assert.Empty(selected.Writes);
        Assert.Equal(text, selected.Files["/work/a.txt"]);
    }

    [Theory]
    [InlineData("[3.5,-1]")]
    [InlineData("[1,3.5]")]
    public async Task ViewBoundsAreCheckedBeforeSliceTruncation(string range)
    {
        var selected = File("a\nb\nc");
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Invoke(Tool(selected), agent, View(range), caller.Token));
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("0.5", "a\nb", "X\na\nb", "     1  X\n     2  a\n     3  b\n")]
    [InlineData("4.5", "a\nb\nc\nd\ne\nf\ng\nh", "a\nb\nc\nd\nX\ne\nf\ng\nh", "   1.5  a\n   2.5  b\n   3.5  c\n   4.5  d\n   5.5  X\n   6.5  e\n   7.5  f\n   8.5  g\n   9.5  h\n")]
    public async Task FractionalInsertRetainsIndependentSliceAndSnippetArithmetic(string line, string original, string expected, string expectedRows)
    {
        var selected = File(original);
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        var result = await Invoke(Tool(selected), agent, Insert(line), caller.Token);
        Assert.Equal(expected, selected.Files["/work/a.txt"]);
        Assert.Equal("The file /work/a.txt has been edited. Here's the result of running `cat -n` on a snippet of the edited file:\n" + expectedRows + "Review the changes and make sure they are as expected (correct indentation, no duplicate lines, etc). Edit the file again if necessary.", result);
        Assert.Single(selected.Writes);
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt", "write:/work/a.txt");
    }

    [Theory]
    [InlineData("-0.5")]
    [InlineData("3.5")]
    public async Task InsertBoundsUseOriginalNumberBeforeTruncation(string line)
    {
        var selected = File("a\nb\nc");
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Invoke(Tool(selected), agent, Insert(line), caller.Token));
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
        Assert.Equal("a\nb\nc", selected.Files["/work/a.txt"]);
    }

    [Fact]
    public async Task NumberRoundingOccursBeforeTypeScriptRangeArithmetic()
    {
        var selected = File("a\nb");
        await using var agent = EditorTestContext.Agent(selected);
        var result = await Invoke(Tool(selected), agent, View("[1.00000000000000000000000000001,2]"));
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n", result);
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("1e-1000")]
    [InlineData("-0.0")]
    public async Task UnderflowAndSignedZeroSelectTheFirstInsertionPosition(string line)
    {
        var selected = File("a\nb");
        await using var agent = EditorTestContext.Agent(selected);
        var result = await Invoke(Tool(selected), agent, Insert(line));
        Assert.Equal("X\na\nb", selected.Files["/work/a.txt"]);
        Assert.Contains("\n     1  X\n     2  a\n     3  b\n", result);
        Assert.Single(selected.Writes);
    }

    [Theory]
    [InlineData("1e10000")]
    [InlineData("\"1\"")]
    [InlineData("true")]
    public async Task NonfiniteAndNonNumberPositionsRejectBeforeSelectedIo(string line)
    {
        var selected = File("a\nb");
        await using var agent = EditorTestContext.Agent(selected);
        await Assert.ThrowsAsync<ArgumentException>(async () => await Invoke(Tool(selected), agent, Insert(line)));
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task TypeScriptOptionalRangeDoesNotAcceptNullOrEmptyTuple(string range)
    {
        var selected = File("a\nb");
        await using var agent = EditorTestContext.Agent(selected);
        await Assert.ThrowsAsync<ArgumentException>(async () => await Invoke(Tool(selected), agent, View(range)));
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("file_text")]
    [InlineData("old_str")]
    [InlineData("new_str")]
    [InlineData("insert_line")]
    public async Task IrrelevantOptionalValuesStillRejectExplicitNull(string name)
    {
        var selected = File("a\nb");
        await using var agent = EditorTestContext.Agent(selected);
        var raw = "{\"command\":\"view\",\"path\":\"/work/a.txt\",\"" + name + "\":null}";
        await Assert.ThrowsAsync<ArgumentException>(async () => await Invoke(Tool(selected), agent, raw));
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmittedOrEmptyReplacementDeletesTheUniqueLiteral(bool explicitEmpty)
    {
        var selected = File("before X after");
        await using var agent = EditorTestContext.Agent(selected);
        var raw = "{\"command\":\"str_replace\",\"path\":\"/work/a.txt\",\"old_str\":\"X\"" + (explicitEmpty ? ",\"new_str\":\"\"" : "") + "}";
        var result = await Invoke(Tool(selected), agent, raw);
        Assert.Equal("before  after", selected.Files["/work/a.txt"]);
        Assert.Single(selected.Writes);
        Assert.Contains("has been edited", result);
    }

    private static ITool Tool(EditorFakeSandbox selected) => Core.SandboxFileEditor.Create(selected, new() { InputProfile = Core.SandboxFileEditorInputProfile.TypeScript });
    private static EditorFakeSandbox File(string text)
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = text;
        return selected;
    }
    private static string View(string range) => "{\"command\":\"view\",\"path\":\"/work/a.txt\",\"view_range\":" + range + "}";
    private static string Insert(string line) => "{\"command\":\"insert\",\"path\":\"/work/a.txt\",\"insert_line\":" + line + ",\"new_str\":\"X\"}";
    private static ValueTask<string> Invoke(ITool tool, Agent agent, string raw, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(raw);
        var input = document.RootElement.Clone();
        return tool.InvokeAsync(input, new(agent, new("editor-profile", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken);
    }
    private static void AssertCalls(EditorFakeSandbox selected, CancellationToken token, params string[] expected)
    {
        Assert.Equal(expected, selected.Calls.Select(call => call.Operation + ":" + call.Path));
        Assert.All(selected.Calls, call => Assert.Equal(token, call.Token));
        Assert.Equal(0, selected.Disposals);
    }
}
