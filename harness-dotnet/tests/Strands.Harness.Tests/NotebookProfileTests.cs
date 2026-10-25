using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;
using static Strands.Harness.Tests.NotebookTests;

namespace Strands.Harness.Tests;

public sealed class NotebookProfileTests
{
    [Theory]
    [InlineData(NotebookBehavior.TypeScript, "Available notebooks:\n- 2: 1 lines\n- 10: 1 lines\n- 01: 1 lines\n- words: 1 lines")]
    [InlineData(NotebookBehavior.Python, "Available notebooks:\n- 10: 1 lines\n- 01: 1 lines\n- 2: 1 lines\n- words: 1 lines")]
    public async Task NumericNameOrderingIsAnExplicitProfileProjection(NotebookBehavior behavior, string expected)
    {
        await using var agent = NewAgent();
        using (var document = JsonDocument.Parse("{\"10\":\"ten\",\"01\":\"not an index\",\"2\":\"two\",\"words\":\"last\"}")) agent.State.Set("notebooks", document.RootElement);
        var before = agent.State.Get("notebooks")!.Value.GetRawText();
        Assert.Equal(expected, await Invoke(Notebook.Create(new() { Behavior = behavior }), agent, new { mode = "list" }));
        Assert.Equal(before, agent.State.Get("notebooks")!.Value.GetRawText());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task NamesAreLiteralOrdinalKeysAndCreateCanOverwriteThem(NotebookBehavior behavior)
    {
        await using var agent = NewAgent(); var tool = Notebook.Create(new() { Behavior = behavior });
        foreach (var name in new[] { "", "../path/to/notes", "__proto__", "constructor", "A", "a" })
            await Invoke(tool, agent, Input(behavior, "create", name, name + "!"));
        await Invoke(tool, agent, Input(behavior, "create", "A", "overwritten"));
        Assert.Equal("overwritten", await Invoke(tool, agent, Input(behavior, "read", "A")));
        Assert.Equal("a!", await Invoke(tool, agent, Input(behavior, "read", "a")));
        Assert.Equal("__proto__!", await Invoke(tool, agent, Input(behavior, "read", "__proto__")));
        Assert.Equal("!", await Invoke(tool, agent, Input(behavior, "read", "")));
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task EmptyNeedleFirstReplacementAndEmptyAppendPreserveLiteralContent(NotebookBehavior behavior)
    {
        await using var agent = NewAgent(); var tool = Notebook.Create(new() { Behavior = behavior });
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "a\r\na" });
        Assert.Equal("No changes made to notebook 'notes'", await Invoke(tool, agent, Input(behavior, "write", "notes", "")));
        var input = Input(behavior, "write", "notes", "$&$1"); input[Key(behavior, "oldStr", "old_str")] = "";
        Assert.Equal("Replaced text in notebook 'notes'", await Invoke(tool, agent, input));
        Assert.Equal("$&$1a\r\na", agent.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
        input[Key(behavior, "oldStr", "old_str")] = "missing";
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, input));
        Assert.Equal("$&$1a\r\na", agent.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript, -1, "A\nB\nX", 3)]
    [InlineData(NotebookBehavior.Python, -1, "A\nB\nX", 3)]
    [InlineData(NotebookBehavior.TypeScript, -3, "X\nA\nB", 1)]
    [InlineData(NotebookBehavior.Python, -3, "X\nA\nB", 1)]
    [InlineData(NotebookBehavior.TypeScript, 3, "A\nB\nX", 4)]
    [InlineData(NotebookBehavior.Python, 3, "A\nB\nX", 4)]
    public async Task InsertEdgesPreserveSourceClampingAndReportedPosition(NotebookBehavior behavior, int anchor, string expected, int reported)
    {
        await using var agent = NewAgent(); agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A\nB" });
        var input = Input(behavior, "write", "notes", "X"); input[Key(behavior, "insertLine", "insert_line")] = anchor;
        Assert.Equal("Inserted text at line " + reported + " in notebook 'notes'", await Invoke(Notebook.Create(new() { Behavior = behavior }), agent, input));
        Assert.Equal(expected, agent.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task StringAnchorIsAFirstSubstringSearchAndInvalidWritesDoNotCommit(NotebookBehavior behavior)
    {
        await using var agent = NewAgent(); var tool = Notebook.Create(new() { Behavior = behavior });
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A2\nB\nA2" });
        var input = Input(behavior, "write", "notes", "X"); input[Key(behavior, "insertLine", "insert_line")] = "2";
        Assert.Equal("Inserted text at line 2 in notebook 'notes'", await Invoke(tool, agent, input));
        var before = agent.State.Get("notebooks")!.Value.GetRawText();
        input[Key(behavior, "oldStr", "old_str")] = "A2"; await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, input));
        input.Remove(Key(behavior, "oldStr", "old_str")); input[Key(behavior, "insertLine", "insert_line")] = -100;
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, input));
        input[Key(behavior, "insertLine", "insert_line")] = "missing"; await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, input));
        Assert.Equal(before, agent.State.Get("notebooks")!.Value.GetRawText());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task ReadRangesClampBeforeIterationAndPreserveTrailingEmptyLines(NotebookBehavior behavior)
    {
        await using var agent = NewAgent(); var tool = Notebook.Create(new() { Behavior = behavior });
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A\nB\n", ["empty"] = "" });
        var input = Input(behavior, "read", "notes"); input[Key(behavior, "readRange", "read_range")] = new[] { -2, -1 };
        Assert.Equal("2: B\n3: ", await Invoke(tool, agent, input));
        input[Key(behavior, "readRange", "read_range")] = JsonSerializer.Deserialize<JsonElement>("[-1e300,1e300]");
        Assert.Equal("1: A\n2: B\n3: ", await Invoke(tool, agent, input));
        input["name"] = "empty"; input[Key(behavior, "readRange", "read_range")] = new[] { 1, 1 };
        Assert.Equal("1: ", await Invoke(tool, agent, input));
        Assert.Equal("Notebook 'empty' is empty", await Invoke(tool, agent, Input(behavior, "read", "empty")));
    }

    [Theory]
    [InlineData("1.000000000000000000000000000000000000001")]
    [InlineData("1e-400")]
    [InlineData("1e400")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("\"2\"")]
    public async Task NativeIntegerContractRejectsRoundedFractionUnderflowAndCoercion(string raw)
    {
        await using var agent = NewAgent(); var tool = Notebook.Create();
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A\nB" });
        using var input = JsonDocument.Parse("{\"mode\":\"read\",\"name\":\"notes\",\"readRange\":[1," + raw + "]}");
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, input.RootElement));
        Assert.Equal("integer", tool.Spec.InputSchema.GetProperty("properties").GetProperty("readRange").GetProperty("items").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("2.0", "1: A\n2: B")]
    [InlineData("200e-2", "1: A\n2: B")]
    [InlineData("0e-400", "No lines found in range [1, 0]. Notebook 'notes' has 2 line(s).")]
    public async Task ExactIntegralJsonLexemesRemainUsable(string raw, string expected)
    {
        await using var agent = NewAgent(); agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A\nB" });
        using var input = JsonDocument.Parse("{\"mode\":\"read\",\"name\":\"notes\",\"readRange\":[1," + raw + "]}");
        Assert.Equal(expected, await Invoke(Notebook.Create(), agent, input.RootElement));
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task NativeJsonBoundaryRejectsAnEscapedLoneSurrogateWithoutCommitting(NotebookBehavior behavior)
    {
        await using var agent = NewAgent(); var key = Key(behavior, "newStr", "new_str");
        using var document = JsonDocument.Parse("{\"mode\":\"create\",\"" + key + "\":\"\\ud800\"}");
        var tool = Notebook.Create(new() { Behavior = behavior });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tool.InvokeAsync(document.RootElement, new(agent, new("surrogate", tool.Spec.Name, document.RootElement), new Dictionary<string, object?>())));
        Assert.Null(agent.State.Get("notebooks"));
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task RegisteredIntegerSchemaRefusesFractionalMutation(NotebookBehavior behavior)
    {
        var input = Input(behavior, "write", "notes", "X"); input[Key(behavior, "insertLine", "insert_line")] = 1.5;
        await using var agent = new Agent(new() { Model = new NotebookStateTests.SingleCallModel(input), Tools = [Notebook.Create(new() { Behavior = behavior })] });
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A" });
        await agent.InvokeAsync("invalid integer input");
        Assert.Equal(ToolResultStatus.Error, Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Status);
        Assert.Equal("A", agent.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task RegisteredNullableSchemaMatchesTheSelectedProfile(NotebookBehavior behavior)
    {
        var input = new Dictionary<string, object?> { ["mode"] = "read", ["name"] = null, [Key(behavior, "readRange", "read_range")] = null };
        await using var agent = new Agent(new() { Model = new NotebookStateTests.SingleCallModel(input), Tools = [Notebook.Create(new() { Behavior = behavior })] });
        agent.State.Set("notebooks", new Dictionary<string, string> { ["default"] = "content" });
        await agent.InvokeAsync("profile schema");
        var accepted = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal(behavior == NotebookBehavior.Python ? ToolResultStatus.Success : ToolResultStatus.Error, accepted.Status);
        if (behavior == NotebookBehavior.Python) Assert.Equal("content", accepted.Content);
        Assert.Equal("content", agent.State.Get("notebooks")!.Value.GetProperty("default").GetString());
    }
}
