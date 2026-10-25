using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorConformanceTests
{
    [Theory]
    [InlineData("omitted")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("delete")]
    public async Task RegisteredOptionalValuesReachTheSelectedSourceOperation(string profile)
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = "abcXdef";
        var tool = Core.SandboxFileEditor.CreateForAgent();
        await using var agent = new Agent(new() { Model = new NoModel(), Sandbox = selected, Tools = [tool] });
        var arguments = new Dictionary<string, object?> { ["command"] = profile == "delete" ? "str_replace" : "view", ["path"] = "/work/a.txt" };
        if (profile == "null") arguments["view_range"] = null;
        if (profile == "empty") arguments["view_range"] = Array.Empty<int>(); // Python's explicit falsey-list path; TS tuple rejects this profile.
        if (profile == "delete") { arguments["old_str"] = "X"; arguments["new_str"] = null; }
        using var caller = new CancellationTokenSource();
        var result = await agent.InvokeToolAsync(new("registered-" + profile, tool.Spec.Name, JsonSerializer.SerializeToElement(arguments)), cancellationToken: caller.Token);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains(profile == "delete" ? "has been edited" : "abcXdef", result.Content);
        Assert.Equal(profile == "delete" ? "abcdef" : "abcXdef", selected.Files["/work/a.txt"]);
        Assert.All(selected.Calls, call => Assert.Equal(caller.Token, call.Token));
        if (profile != "delete") Assert.Empty(selected.Writes);
    }

    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Registered editor calls must not execute a model, process or interpreter.");
    }
}
