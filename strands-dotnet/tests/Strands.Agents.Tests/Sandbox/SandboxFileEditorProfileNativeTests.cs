using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorProfileNativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAndExplicitNativeKeepExactIntegerAndFractionRules(bool explicitNative)
    {
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        var tool = explicitNative ? Core.SandboxFileEditor.Create(selected, new() { InputProfile = Core.SandboxFileEditorInputProfile.Native }) : Core.SandboxFileEditor.Create(selected);
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n", await Invoke(tool, agent, View("[1.0,2e0]")));
        selected.Calls.Clear();
        await Assert.ThrowsAsync<ArgumentException>(async () => await Invoke(tool, agent, View("[1.5,2]")));
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"view_range\":null")]
    [InlineData(",\"view_range\":[]")]
    public async Task NativeKeepsAbsentNullAndEmptyRangeAsWholeFile(string range)
    {
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        var raw = "{\"command\":\"view\",\"path\":\"/work/a.txt\"" + range + "}";
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n", await Invoke(Core.SandboxFileEditor.Create(selected), agent, raw));
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task TypeScriptNameAndWalkPolicyDoNotSelectTypeScriptInputs()
    {
        var selected = File();
        await using var agent = EditorTestContext.Agent(selected);
        var tool = Core.SandboxFileEditor.Create(selected, new() { Name = "fileEditor", Description = "custom", DirectoryErrorPolicy = Core.SandboxDirectoryErrorPolicy.SkipNonCancellationFailures });
        Assert.Equal("fileEditor", tool.Spec.Name);
        Assert.Equal("custom", tool.Spec.Description);
        await Assert.ThrowsAsync<ArgumentException>(async () => await Invoke(tool, agent, View("[1.5,2]")));
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public void ExplicitTypeScriptSchemaDoesNotConstructOrConsultAnEnvironment()
    {
        var selected = new EditorFakeSandbox { Forbidden = true };
        var tool = Core.SandboxFileEditor.Create(selected, new() { InputProfile = Core.SandboxFileEditorInputProfile.TypeScript, Name = "custom_editor", Description = "custom" });
        var properties = tool.Spec.InputSchema.GetProperty("properties");
        Assert.Equal("array", properties.GetProperty("view_range").GetProperty("type").GetString());
        Assert.Equal("number", properties.GetProperty("view_range").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("number", properties.GetProperty("insert_line").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("new_str").GetProperty("type").GetString());
        Assert.Equal("custom_editor", tool.Spec.Name);
        Assert.Equal("custom", tool.Spec.Description);
        Assert.Empty(selected.Calls);
        Assert.Equal(0, selected.Disposals);
    }

    private static EditorFakeSandbox File()
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = "a\nb";
        return selected;
    }
    private static string View(string range) => "{\"command\":\"view\",\"path\":\"/work/a.txt\",\"view_range\":" + range + "}";
    private static ValueTask<string> Invoke(ITool tool, Agent agent, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var input = document.RootElement.Clone();
        return tool.InvokeAsync(input, new(agent, new("editor-native-profile", tool.Spec.Name, input), new Dictionary<string, object?>()));
    }
}
