using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class NotebookTests
{
    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task CreateCommitsTheDefaultAndNamedNotebook(NotebookBehavior behavior)
    {
        await using var agent = NewAgent();
        var tool = Notebook.Create(new() { Behavior = behavior });
        Assert.Equal("Created notebook 'notes' with specified content", await Invoke(tool, agent, Input(behavior, "create", "notes", "hello")));
        var state = agent.State.Get("notebooks")!.Value;
        Assert.Equal("", state.GetProperty("default").GetString());
        Assert.Equal("hello", state.GetProperty("notes").GetString());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task RejectedUtf8GrowthLeavesTheWholeStateUnchanged(NotebookBehavior behavior)
    {
        await using var agent = NewAgent();
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "é", ["unrelated"] = "keep" });
        var before = agent.State.Get("notebooks")!.Value.GetRawText();
        var tool = Notebook.Create(new() { Behavior = behavior, MaximumNotebookSizeBytes = 3 });
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, Input(behavior, "write", "notes", "é")));
        Assert.Contains("would exceed maximum of 3 bytes", error.Message);
        Assert.Equal(before, agent.State.Get("notebooks")!.Value.GetRawText());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task AppendUsesOneSeparatorAndReplacementIsFirstAndLiteral(NotebookBehavior behavior)
    {
        await using var agent = NewAgent();
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A\n" });
        var tool = Notebook.Create(new() { Behavior = behavior });
        Assert.Equal("Appended text to notebook 'notes'", await Invoke(tool, agent, Input(behavior, "write", "notes", "A")));
        Assert.Equal("A\nA", agent.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
        var input = Input(behavior, "write", "notes", "$&"); input[Key(behavior, "oldStr", "old_str")] = "A";
        Assert.Equal("Replaced text in notebook 'notes'", await Invoke(tool, agent, input));
        Assert.Equal("$&\nA", agent.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task ProfileRangeLengthAndNullConventionsAreExplicit(NotebookBehavior behavior)
    {
        await using var agent = NewAgent();
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A\nB\nC" });
        var tool = Notebook.Create(new() { Behavior = behavior });
        var input = Input(behavior, "read", "notes"); input[Key(behavior, "readRange", "read_range")] = new[] { 2, 3, 99 };
        if (behavior == NotebookBehavior.TypeScript) Assert.Equal("2: B\n3: C", await Invoke(tool, agent, input));
        else await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, input));
        input[Key(behavior, "readRange", "read_range")] = null;
        if (behavior == NotebookBehavior.TypeScript) await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, input));
        else Assert.Equal("A\nB\nC", await Invoke(tool, agent, input));
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task NumericAndTextInsertionUseSourceLinePositions(NotebookBehavior behavior)
    {
        await using var agent = NewAgent();
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A\nB" });
        var tool = Notebook.Create(new() { Behavior = behavior });
        var input = Input(behavior, "write", "notes", "P"); input[Key(behavior, "insertLine", "insert_line")] = 0;
        Assert.Equal("Inserted text at line 1 in notebook 'notes'", await Invoke(tool, agent, input));
        input[Key(behavior, "insertLine", "insert_line")] = "A"; input[Key(behavior, "newStr", "new_str")] = "X";
        Assert.Equal("Inserted text at line 3 in notebook 'notes'", await Invoke(tool, agent, input));
        Assert.Equal("P\nA\nX\nB", agent.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task EmptyRangeFormatsNegativeZeroAsTheSourceIntegerZero(NotebookBehavior behavior)
    {
        await using var agent = NewAgent();
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "A" });
        var input = Input(behavior, "read", "notes");
        input[Key(behavior, "readRange", "read_range")] = new[] { -0.0, 0.0 };
        Assert.Equal("No lines found in range [0, 0]. Notebook 'notes' has 1 line(s).", await Invoke(Notebook.Create(new() { Behavior = behavior }), agent, input));
    }

    internal static string Key(NotebookBehavior behavior, string ts, string py) => behavior == NotebookBehavior.Python ? py : ts;
    internal static Dictionary<string, object?> Input(NotebookBehavior behavior, string mode, string name, string? text = null)
    {
        var input = new Dictionary<string, object?> { ["mode"] = mode, ["name"] = name };
        if (text is not null) input[Key(behavior, "newStr", "new_str")] = text;
        return input;
    }
    internal static Agent NewAgent() => new(new() { Model = new EmptyModel() });
    internal static async Task<string> Invoke(ITool tool, Agent agent, object input, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToElement(input);
        return await tool.InvokeAsync(json, new(agent, new("notebook-test", tool.Spec.Name, json), new Dictionary<string, object?>()), cancellationToken);
    }
    private sealed class EmptyModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; yield return new ModelMessageStartEvent(); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
