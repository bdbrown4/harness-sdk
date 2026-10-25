using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness;
using Xunit.Abstractions;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class HarnessCompatibilityTests
{
    private readonly ITestOutputHelper _output;

    public HarnessCompatibilityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task TextToolsWriteEditAndReadRealFiles()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "nested", "note.txt");
            var sandbox = new LocalFileSandbox();
            var agent = await CreateAgentAsync();
            var write = await InvokeAsync(FileTools.CreateWrite(sandbox), new { path, content = "one\ntwo\nthree" }, agent);
            var edit = await InvokeAsync(FileTools.CreateEdit(sandbox), new { path, old_str = "two", new_str = "updated" }, agent);
            var read = await InvokeAsync(FileTools.CreateRead(sandbox), new { path, offset = 2, limit = 1 }, agent);

            Assert.Equal("Wrote 3 lines to " + path + ".", write);
            Assert.Equal("Edited " + path + ".", edit);
            Assert.Equal("     2\tupdated\n[Showing lines 2-2 of 3. Use offset/limit to read more.]", read);
            Assert.Equal("one\nupdated\nthree", File.ReadAllText(path));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Utf8ReadsRespectTheByteLimitAndRemoveTheBom()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "note.txt");
            var sandbox = new LocalFileSandbox(maxReadBytes: 8);
            await sandbox.WriteTextAsync(path, "ééé");
            Assert.Equal("ééé", await sandbox.ReadTextAsync(path));
            await sandbox.WriteTextAsync(path, "ééééé");
            await Assert.ThrowsAsync<IOException>(() => sandbox.ReadTextAsync(path).AsTask());
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, 0x61, 0x62, 0x63 });
            Assert.Equal("abc", await sandbox.ReadTextAsync(path));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task CancelledAndInvalidWritesPreserveTheDestination()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "note.txt");
            var sandbox = new LocalFileSandbox();
            await sandbox.WriteTextAsync(path, "original");
            using var source = new CancellationTokenSource();
            source.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.WriteTextAsync(path, "replacement", source.Token).AsTask());
            await Assert.ThrowsAsync<EncoderFallbackException>(() => sandbox.WriteTextAsync(path, new string('x', 20000) + "\uD800").AsTask());

            Assert.Equal("original", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ReadOnlyMetadataPreventsOverwrite()
    {
        var directory = TemporaryDirectory();
        var path = Path.Combine(directory, "note.txt");
        try
        {
            var sandbox = new LocalFileSandbox();
            await sandbox.WriteTextAsync(path, "original");
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sandbox.WriteTextAsync(path, "replacement").AsTask());

            Assert.Equal("original", File.ReadAllText(path));
            Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task TerminalSymbolicLinksAreRejectedWhenTheHostCanCreateThem()
    {
        var directory = TemporaryDirectory();
        try
        {
            var target = Path.Combine(directory, "target.txt");
            var link = Path.Combine(directory, "link.txt");
            var sandbox = new LocalFileSandbox();
            await sandbox.WriteTextAsync(target, "original");
            if (!TryCreateSymbolicLink(link, target))
            {
                _output.WriteLine("Symbolic-link assertions were not exercised: this host does not permit symbolic-link creation.");
                return;
            }

            var error = await Assert.ThrowsAsync<IOException>(() => sandbox.WriteTextAsync(link, "replacement").AsTask());

            Assert.Contains("symbolic link", error.Message);
            Assert.Equal("original", File.ReadAllText(target));
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task HarnessSessionRestoresToolJsonReasoningAndTrackingIds()
    {
        var directory = TemporaryDirectory();
        try
        {
            var input = JsonSerializer.SerializeToElement(new { value = "payload", nested = new { number = 7 } });
            var model = new ScriptedModel(ReasoningAndTool(input), Text("answer"));
            var echo = new FunctionTool(new ToolSpec("echo", "Echo a value", JsonSerializer.SerializeToElement(new { type = "object" })),
                (value, _, _) => new ValueTask<string>(value.GetProperty("value").GetString()!));
            var first = await HarnessFactory.CreateAsync(new HarnessOptions
            {
                BackgroundTasks = null,
                Model = model,
                BuiltinTools = Array.Empty<string>(),
                Tools = new[] { echo },
                Memory = new() { Enabled = false },
                Context = new() { Enabled = false },
                Session = new SessionOptions { Id = " Legacy Session ", Directory = directory },
            });
            await first.InvokeAsync("question");
            var ids = first.Messages.Select(message => message.TrackingId).ToArray();
            var resumedModel = new ScriptedModel(Text("follow-up answer"));
            var resumed = await HarnessFactory.CreateAsync(new HarnessOptions
            {
                BackgroundTasks = null,
                Model = resumedModel,
                BuiltinTools = Array.Empty<string>(),
                Memory = new() { Enabled = false },
                Context = new() { Enabled = false },
                Session = new SessionOptions { Id = "legacy-session", Directory = directory },
            });
            await resumed.InvokeAsync("follow-up");

            var request = Assert.Single(resumedModel.Requests);
            Assert.Equal(ids, request.Messages.Take(4).Select(message => message.TrackingId));
            var reasoning = Assert.IsType<ReasoningBlock>(request.Messages[1].Content[0]);
            Assert.Equal("thinking", reasoning.Text);
            Assert.Equal("signature", reasoning.Signature);
            var call = Assert.IsType<ToolUseBlock>(request.Messages[1].Content[1]);
            Assert.Equal("tool-id", call.ToolUseId);
            Assert.Equal("payload", call.Input.GetProperty("value").GetString());
            Assert.Equal(7, call.Input.GetProperty("nested").GetProperty("number").GetInt32());
            Assert.Equal("payload", Assert.IsType<ToolResultBlock>(request.Messages[2].Content[0]).Content);
            Assert.StartsWith("follow-up\n\n<system-reminder>", request.Messages[4].Text);
            Assert.Equal("follow-up", resumed.Messages[4].Text);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task DefaultHarnessCanBeExposedAsATool()
    {
        var agent = await CreateAgentAsync();
        Assert.Equal("strands_harness", agent.AsTool().Spec.Name);
    }

    private static Task<Agent> CreateAgentAsync() => HarnessFactory.CreateAsync(new HarnessOptions
    {
        BackgroundTasks = null,
        Model = new ScriptedModel(Text("unused")),
        BuiltinTools = Array.Empty<string>(),
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new SessionOptions { Enabled = false },
    });

    private static Task<string> InvokeAsync(ITool tool, object arguments, Agent agent)
    {
        var input = JsonSerializer.SerializeToElement(arguments);
        var call = new ToolUseBlock("test-call", tool.Spec.Name, input);
        return tool.InvokeAsync(input, new ToolContext(agent, call, new Dictionary<string, object?>())).AsTask();
    }

    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "strands-compatibility-" + Guid.NewGuid().ToString("N"));
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static bool TryCreateSymbolicLink(string path, string target)
    {
        if (Path.DirectorySeparatorChar == '\\')
            return CreateSymbolicLink(path, target, 2) || CreateSymbolicLink(path, target, 0);
#if NET8_0_OR_GREATER
        try { File.CreateSymbolicLink(path, target); return true; }
        catch (PlatformNotSupportedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
#else
        return false;
#endif
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLink(string path, string target, int flags);

    private static ModelStreamEvent[] Text(string value) => new ModelStreamEvent[]
    {
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: value),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn),
    };

    private static ModelStreamEvent[] ReasoningAndTool(JsonElement input) => new ModelStreamEvent[]
    {
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0),
        new ModelContentBlockDeltaEvent(0, Reasoning: "thinking", Signature: "signature"), new ModelContentBlockStopEvent(0),
        new ModelContentBlockStartEvent(1, new ToolUseBlock("tool-id", "echo", input)),
        new ModelContentBlockStopEvent(1), new ModelMessageStopEvent(StopReasons.ToolUse),
    };

    private sealed class ScriptedModel : IModel
    {
        private readonly ModelStreamEvent[][] _turns;
        public List<ModelRequest> Requests { get; } = new();
        public ScriptedModel(params ModelStreamEvent[][] turns) => _turns = turns;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            foreach (var item in _turns[Requests.Count - 1])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }
}
