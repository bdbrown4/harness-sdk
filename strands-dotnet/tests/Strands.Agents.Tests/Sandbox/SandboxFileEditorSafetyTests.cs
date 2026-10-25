using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorSafetyTests
{
    [Fact]
    public async Task NonMissingProbeFailureRemainsTheOriginalCause()
    {
        var failure = new IOException("Known permission or transport cause, not absence.");
        var selected = new EditorFakeSandbox { ListFailure = _ => failure };
        await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "view", path = "/work/a.txt" }));
        Assert.Same(failure, caught);
        Assert.Equal("list", Assert.Single(selected.Calls).Operation);
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf8ReadLimitAcceptsExactlyOneMiBAndRejectsTheNextByte(bool over)
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = new string('\u00e9', 524288) + (over ? "a" : "");
        await using var agent = EditorTestContext.Agent(selected);
        var tool = Core.SandboxFileEditor.Create(selected);
        if (over)
        {
            var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(tool, agent, new { command = "view", path = "/work/a.txt" }));
            Assert.NotNull(caught);
            Assert.Contains("exceeds maximum allowed size", caught!.Message);
        }
        else
        {
            var result = await EditorTestContext.Invoke(tool, agent, new { command = "view", path = "/work/a.txt" });
            Assert.Contains("     1  \u00e9\u00e9", result);
        }
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("relative/file")]
    [InlineData("/work/../file")]
    public async Task RelativeOrTraversalPathsFailBeforeAnySelectedOperation(string path)
    {
        var selected = new EditorFakeSandbox { Forbidden = true };
        await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "view", path }));
        Assert.IsAssignableFrom<ArgumentException>(caught);
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }
}
