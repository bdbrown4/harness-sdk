using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorPythonProfileTests
{
    [Theory]
    [InlineData("")]
    [InlineData(",\"view_range\":null")]
    [InlineData(",\"view_range\":[]")]
    public async Task PythonNullableAndEmptyRangesKeepFullRegisteredView(string suffix)
    {
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        using var caller = new CancellationTokenSource();
        var result = await EditorProfileAssertions.Registered(agent, tool, "{\"command\":\"view\",\"path\":\"/work/a.txt\"" + suffix + "}", caller.Token);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n", result.Content);
        EditorProfileAssertions.Calls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("[1,2,3]", true)]
    [InlineData("[1]", false)]
    public async Task PythonListLengthIsAppliedAfterSelectedFileRead(string range, bool accepted)
    {
        var selected = EditorProfileAssertions.File("a\nb\nc");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        using var caller = new CancellationTokenSource();
        var result = await EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.View(range), caller.Token);
        Assert.Equal(accepted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        if (accepted) Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n", result.Content);
        EditorProfileAssertions.Calls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
        Assert.Equal("a\nb\nc", selected.Files["/work/a.txt"]);
    }

    [Fact]
    public async Task EveryPythonListMemberIsBoundBeforeIoEvenWhenTheHandlerWouldIgnoreIt()
    {
        var selected = EditorProfileAssertions.File("a\nb\nc");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        var result = await EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.View("[1,2,\"invalid\"]"));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("file_text")]
    [InlineData("old_str")]
    [InlineData("new_str")]
    [InlineData("insert_line")]
    public async Task PythonNullableIrrelevantFieldsRemainAcceptedByRegisteredSchema(string name)
    {
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        var result = await EditorProfileAssertions.Registered(agent, tool, "{\"command\":\"view\",\"path\":\"/work/a.txt\",\"" + name + "\":null}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n", result.Content);
        EditorProfileAssertions.Calls(selected, default, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("true")]
    public async Task PythonNumericSpellingsAndBooleanBindToIntegerOne(string line)
    {
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        using var caller = new CancellationTokenSource();
        var result = await EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.Insert(line), caller.Token);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("a\nX\nb", selected.Files["/work/a.txt"]);
        Assert.Contains("has been edited", result.Content);
        Assert.Single(selected.Writes);
        EditorProfileAssertions.Calls(selected, caller.Token, "list:/work", "read:/work/a.txt", "write:/work/a.txt");
    }

    [Theory]
    [InlineData("\"1.0\"", true)]
    [InlineData("\"1e0\"", false)]
    [InlineData("1.5", false)]
    public async Task PythonStringDecimalAndFractionAdmissionUseCapturedBinderGrammar(string line, bool accepted)
    {
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        var result = await EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.Insert(line));
        Assert.Equal(accepted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        if (accepted)
        {
            Assert.Equal("a\nX\nb", selected.Files["/work/a.txt"]);
            Assert.Single(selected.Writes);
            EditorProfileAssertions.Calls(selected, default, "list:/work", "read:/work/a.txt", "write:/work/a.txt");
        }
        else { Assert.Empty(selected.Calls); Assert.Empty(selected.Writes); Assert.Equal("a\nb", selected.Files["/work/a.txt"]); }
    }

    [Theory]
    [InlineData("9223372036854775807", true)]
    [InlineData("9223372036854775807.0", false)]
    [InlineData("-9223372036854775808", true)]
    [InlineData("-9223372036854775808.0", false)]
    public async Task PythonRawIntegerAndFloatingI64EndpointsHaveDifferentAdmission(string line, bool reachesRead)
    {
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        var result = await EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.Insert(line));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        if (reachesRead) EditorProfileAssertions.Calls(selected, default, "list:/work", "read:/work/a.txt");
        else Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
        Assert.Equal("a\nb", selected.Files["/work/a.txt"]);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("floating")]
    [InlineData("underflow")]
    public async Task PythonHugeRawIntegersFloatingOverflowAndUnderflowRemainDistinct(string kind)
    {
        var line = kind == "raw" ? new string('7', 400) : kind == "floating" ? new string('7', 400) + "e0" : "1e-400";
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        var result = await EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.Insert(line));
        Assert.Equal(kind == "underflow" ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        if (kind == "floating") { Assert.Empty(selected.Calls); Assert.Empty(selected.Writes); }
        else if (kind == "raw") { EditorProfileAssertions.Calls(selected, default, "list:/work", "read:/work/a.txt"); Assert.Empty(selected.Writes); }
        else
        {
            Assert.Equal("X\na\nb", selected.Files["/work/a.txt"]);
            Assert.Single(selected.Writes);
            EditorProfileAssertions.Calls(selected, default, "list:/work", "read:/work/a.txt", "write:/work/a.txt");
        }
        if (kind != "underflow") Assert.Equal("a\nb", selected.Files["/work/a.txt"]);
    }

    [Theory]
    [InlineData("short-underscore", false)]
    [InlineData("long-underscore", true)]
    [InlineData("4300", true)]
    [InlineData("4301", false)]
    public async Task PythonStringByteDispatchAndMaximumRetainCapturedReferenceBranches(string kind, bool accepted)
    {
        var text = kind switch
        {
            "short-underscore" => "0" + new string('_', 17),
            "long-underscore" => "0" + new string('_', 18),
            "4300" => new string('0', 4299) + "1",
            _ => new string('0', 4300) + "1"
        };
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        var result = await EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.Insert(JsonSerializer.Serialize(text)));
        Assert.Equal(accepted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        if (accepted)
        {
            Assert.Equal(kind == "long-underscore" ? "X\na\nb" : "a\nX\nb", selected.Files["/work/a.txt"]);
            Assert.Single(selected.Writes);
            EditorProfileAssertions.Calls(selected, default, "list:/work", "read:/work/a.txt", "write:/work/a.txt");
        }
        else { Assert.Empty(selected.Calls); Assert.Empty(selected.Writes); Assert.Equal("a\nb", selected.Files["/work/a.txt"]); }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("insert")]
    public async Task PythonNullableRequiredValuesStillRejectBeforeSelectedIo(string command)
    {
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        var raw = command == "create" ? "{\"command\":\"create\",\"path\":\"/work/new.txt\",\"file_text\":null}"
            : "{\"command\":\"insert\",\"path\":\"/work/a.txt\",\"insert_line\":null,\"new_str\":\"X\"}";
        var result = await EditorProfileAssertions.Registered(agent, tool, raw);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task PrecanceledPythonRegisteredCallPreservesTokenWithoutSelectedIo()
    {
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Tool();
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.Insert("true"), caller.Token));
        Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task PythonConvertedRangeRemainsDetachedWhileSelectedReadIsHeld()
    {
        var selected = new EditorProfileReadGate();
        var tool = Core.SandboxFileEditor.Create(selected, new() { InputProfile = Core.SandboxFileEditorInputProfile.Python });
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        using var owner = JsonDocument.Parse(EditorProfileAssertions.View("[\"1.0\",\"2.0\"]"));
        var pending = EditorProfileAssertions.Direct(tool, agent, owner.RootElement, caller.Token);
        Exception? primary = null;
        try
        {
            await EditorProfileAssertions.Started(selected.ReadStarted.Task, pending);
            owner.Dispose();
            Assert.False(pending.IsCompleted);
            selected.Release.TrySetResult(true);
            Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n", await pending);
            Assert.True(selected.ReadCompleted);
            Assert.Equal(new[] { "list:/work", "read:/work/a.txt" }, selected.Calls.Select(call => call.Operation + ":" + call.Path));
            Assert.All(selected.Calls, call => Assert.Equal(caller.Token, call.Token));
            Assert.Empty(selected.Writes);
            Assert.Equal(0, selected.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await EditorProfileAssertions.ReleaseAndJoin(selected.Release, pending, primary); }
    }

    private static ITool Tool() => Core.SandboxFileEditor.CreateForAgent(new() { InputProfile = Core.SandboxFileEditorInputProfile.Python });
}
