using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorIntegerSpellingTests
{
    [Theory]
    [InlineData("SEI-R01", "[1.0,2e0]", false)]
    [InlineData("SEI-R02", "[1e0,2.0]", false)]
    [InlineData("SEI-R03", "[1.0,-1e0]", true)]
    public async Task MathematicalViewRangesRetainExactOutputAndSelectedCalls(string id, string range, bool throughEnd)
    {
        Assert.StartsWith("SEI-R", id);
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = "a\nb\nc";
        var other = new EditorFakeSandbox { Forbidden = true };
        await using var agent = EditorTestContext.Agent(other);
        using var caller = new CancellationTokenSource();
        var result = await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, View(range), caller.Token);
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n" + (throughEnd ? "     3  c\n" : ""), result);
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(other.Calls);
        Assert.Empty(selected.Writes);
        Assert.Equal("a\nb\nc", selected.Files["/work/a.txt"]);
    }

    [Theory]
    [InlineData("SEI-R04", "1.0")]
    [InlineData("SEI-R05", "1e0")]
    public async Task RegisteredMathematicalInsertWritesExactlyOnce(string id, string line)
    {
        Assert.StartsWith("SEI-R", id);
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        var tool = Core.SandboxFileEditor.CreateForAgent();
        agent.AddTool(tool);
        var input = Parse(Insert(line));
        var result = await agent.InvokeToolAsync(new("integer-insert", tool.Spec.Name, input));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("The file /work/a.txt has been edited.", result.Content);
        Assert.Equal("a\nX\nb", selected.Files["/work/a.txt"]);
        Assert.Single(selected.Writes);
        Assert.Equal(new[] { "list:/work", "read:/work/a.txt", "write:/work/a.txt" }, selected.Calls.Select(call => call.Operation + ":" + call.Path));
        Assert.Equal(0, selected.Disposals);
    }

    [Theory]
    [InlineData("SEI-R06", "-0.0")]
    [InlineData("SEI-R07", "-0.0e-999999999999999999999999999999999999")]
    public async Task ExactZeroSpellingsInsertAtStartWithoutExponentExpansion(string id, string line)
    {
        Assert.StartsWith("SEI-R", id);
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        var result = await InvokeRaw(Core.SandboxFileEditor.CreateForAgent(), agent, Insert(line), caller.Token);
        Assert.Equal("X\na\nb", selected.Files["/work/a.txt"]);
        Assert.Contains("Here's the result of running `cat -n` on a snippet of the edited file:\n     1  X\n     2  a\n     3  b\n", result);
        Assert.Single(selected.Writes);
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt", "write:/work/a.txt");
        Assert.Equal(0, selected.Disposals);
    }

    [Fact]
    public async Task CanceledCoefficientAndExponentStillRepresentExactOne()
    {
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, Insert("1000000000000000000000000000000e-30"));
        Assert.Equal("a\nX\nb", selected.Files["/work/a.txt"]);
        Assert.Single(selected.Writes);
    }

    [Fact]
    public async Task LexicalViewRetainsExistingExactPositiveBehavior()
    {
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        var result = await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, View("[1,2]"), caller.Token);
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n", result);
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task RegisteredLexicalInsertRetainsExistingPositiveBehavior()
    {
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        var tool = Core.SandboxFileEditor.CreateForAgent();
        agent.AddTool(tool);
        var result = await agent.InvokeToolAsync(new("lexical-insert", tool.Spec.Name, Parse(Insert("1"))));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("a\nX\nb", selected.Files["/work/a.txt"]);
        Assert.Single(selected.Writes);
    }

    [Theory]
    [InlineData("SEI-C03", "1.5")]
    [InlineData("SEI-C04", "1e-1000")]
    [InlineData("SEI-C15", "1.00000000000000000000000000001")]
    public async Task FractionalValuesCannotRoundOrUnderflowIntoIntegers(string id, string line)
    {
        Assert.StartsWith("SEI-C", id);
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
    [InlineData("SEI-C05", "2147483648")]
    [InlineData("SEI-C06", "-2147483649.0")]
    [InlineData("SEI-C07", "1e999999999999999999999999999999999999")]
    public async Task OutOfInt32MagnitudesRejectBeforeSelectedCallbacks(string id, string line)
    {
        Assert.StartsWith("SEI-C", id);
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, Insert(line)));
        Assert.Equal("input", error.ParamName);
        Assert.StartsWith("insert_line must be an integer.", error.Message);
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("SEI-C08", "\"1\"")]
    [InlineData("SEI-C09", "true")]
    [InlineData("SEI-C10", "null")]
    public async Task RegisteredWrongKindsRemainSchemaErrorsBeforeCallbacks(string id, string line)
    {
        Assert.StartsWith("SEI-C", id);
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        var tool = Core.SandboxFileEditor.CreateForAgent();
        agent.AddTool(tool);
        var result = await agent.InvokeToolAsync(new("wrong-kind", tool.Spec.Name, Parse(Insert(line))));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Contains("schema", result.Content);
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("SEI-C11", "3")]
    [InlineData("SEI-C14", "-2147483648")]
    public async Task ParsedIntegersRetainSemanticLineBoundsAfterSelectedRead(string id, string line)
    {
        Assert.StartsWith("SEI-C", id);
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, Insert(line), caller.Token));
        Assert.Equal("line", error.ParamName);
        Assert.StartsWith("insert_line must be within [0, 2].", error.Message);
        AssertCalls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task OriginalRequestedCancellationPreemptsParseAndSelectedCalls()
    {
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, Insert("1.0"), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task IndependentBackendFailureRetainsIdentityAndBorrowedAuthority()
    {
        var failure = new IOException("selected listing failed");
        var selected = File();
        selected.ListFailure = _ => failure;
        var other = new EditorFakeSandbox { Forbidden = true };
        await using var agent = EditorTestContext.Agent(other);
        using var caller = new CancellationTokenSource();
        var caught = await Record.ExceptionAsync(async () => await InvokeRaw(Core.SandboxFileEditor.Create(selected), agent, Insert("1"), caller.Token));
        Assert.Same(failure, caught);
        AssertCalls(selected, caller.Token, "list:/work");
        Assert.Empty(other.Calls);
        Assert.Empty(selected.Writes);
        Assert.Equal(0, selected.Disposals);
    }

    private static EditorFakeSandbox File()
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = "a\nb";
        return selected;
    }

    private static string View(string range) => "{\"command\":\"view\",\"path\":\"/work/a.txt\",\"view_range\":" + range + "}";
    private static string Insert(string line) => "{\"command\":\"insert\",\"path\":\"/work/a.txt\",\"insert_line\":" + line + ",\"new_str\":\"X\"}";
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ValueTask<string> InvokeRaw(ITool tool, Agent agent, string json, CancellationToken cancellationToken = default)
    {
        var input = Parse(json);
        return tool.InvokeAsync(input, new(agent, new("raw-integer-editor", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken);
    }

    private static void AssertCalls(EditorFakeSandbox selected, CancellationToken token, params string[] calls)
    {
        Assert.Equal(calls, selected.Calls.Select(call => call.Operation + ":" + call.Path));
        Assert.All(selected.Calls, call => Assert.Equal(token, call.Token));
    }
}
