using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Files;
using Strands.Agents.Skills;
using Strands.Harness;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class SkillsCompatibilityTests
{
    [Fact]
    public void RealYamlParserPreservesQuotedScalarsAndResolvesAliases()
    {
        var skill = Skill.FromContent("---\nname: pdf-processing\ndescription: Process PDFs\nmetadata:\n  original: &original\n    enabled: true\n    code: \"001\"\n  copy: *original\n---\nCafé ☕", strict: true);
        Assert.True(skill.Metadata.GetProperty("copy").GetProperty("enabled").GetBoolean());
        Assert.Equal("001", skill.Metadata.GetProperty("copy").GetProperty("code").GetString());
        Assert.Equal("Café ☕", skill.Instructions);
        Assert.Throws<ArgumentException>(() => Skill.FromContent("---\nname: pdf-processing\nname: duplicate\ndescription: Process PDFs\n---\nInstructions"));
    }

    [Fact]
    public async Task HostSkillsUseUtf8FilesAndRestoreActivationFromARealSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands skills compatibility " + Guid.NewGuid().ToString("N"));
        var skillDirectory = Path.Combine(directory, "pdf-processing");
        Directory.CreateDirectory(skillDirectory);
        try
        {
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: pdf-processing\ndescription: Process PDFs\n---\nRead Café ☕.", new UTF8Encoding(false, true));
            var plugin = new AgentSkills(new() { Skills = new SkillSource[] { directory }, Strict = true });
            var model = new Model(Call("pdf-processing"), Text("saved"));
            var store = new FileSessionStore(Path.Combine(directory, "sessions"));
            await using (var agent = new Agent(new() { Model = model, Plugins = new IPlugin[] { plugin }, SessionStore = store, SessionId = "skills" }))
            {
                Assert.Equal("saved", (await Bounded(agent.InvokeAsync("read the PDF"))).ToString());
                Assert.Contains("Read Café ☕.", Assert.IsType<ToolResultBlock>(model.Requests[1].Messages.Last().Content[0]).Content);
                Assert.Equal(new[] { "pdf-processing" }, plugin.GetActivatedSkills(agent));
            }
            var resumedModel = new Model(Text("resumed"));
            await using var resumed = new Agent(new() { Model = resumedModel, Plugins = new IPlugin[] { plugin }, SessionStore = store, SessionId = "skills" });
            Assert.Equal("resumed", (await Bounded(resumed.InvokeAsync("continue"))).ToString());
            Assert.Equal(new[] { "pdf-processing" }, plugin.GetActivatedSkills(resumed));
            Assert.Contains("<name>pdf-processing</name>", Assert.Single(resumedModel.Requests).SystemPrompt);
            Assert.Empty(await plugin.GetAvailableSkillsAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AgentFilesystemIsUsedOnlyOnInitializationAndIsInheritedByChildren()
    {
        var files = new SelectedFiles();
        var plugin = new AgentSkills(new() { Skills = new SkillSource[] { "/selected/pdf-processing" }, Strict = true });
        await using var parent = new Agent(new() { Model = new Model(Text("parent")), Plugins = new IPlugin[] { plugin }, FileSystem = files });
        Assert.Empty(await plugin.GetAvailableSkillsAsync(parent));
        Assert.Equal(0, files.Reads);
        await Bounded(parent.InitializeAsync());
        Assert.Equal(1, files.Reads);
        await using var child = parent.CreateChild(options => options with { Model = new Model(Call("pdf-processing"), Text("child")) });

        Assert.Equal("child", (await Bounded(child.InvokeAsync("read the PDF"))).ToString());

        Assert.Same(files, child.FileSystem);
        Assert.Equal(2, files.Reads);
        Assert.Empty(plugin.GetActivatedSkills(parent));
        Assert.Equal(new[] { "pdf-processing" }, plugin.GetActivatedSkills(child));
    }

    [Fact]
    public async Task HttpsSkillReadsSplitUtf8AndRetainsCallerOwnedClient()
    {
        using var stream = new OneByteStream(Encoding.UTF8.GetBytes("---\nname: pdf-processing\ndescription: Process PDFs\n---\nRead Café ☕."));
        using var handler = new Handler(stream);
        using var client = new HttpClient(handler);
        var skill = await Bounded(Skill.FromUrlAsync("https://skills.example.test/SKILL.md", client, strict: true));

        Assert.Equal("Read Café ☕.", skill.Instructions);
        Assert.True(stream.Disposed);
        Assert.False(handler.Disposed);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Skill.FromUrlAsync("https://skills.example.test/SKILL.md", client, cancellationToken: canceled.Token));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task DefaultHarnessSkillsResolveRelativePathsInTheSelectedSandbox()
    {
        var sandbox = new SelectedSandbox();
        var model = new Model(Call("pdf-processing"), Text("done"));
        await using var agent = await Bounded(HarnessFactory.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = model,
            Sandbox = sandbox,
            BuiltinTools = Array.Empty<string>(),
            BuiltinPlugins = Array.Empty<string>(),
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        }));

        Assert.Equal("done", (await Bounded(agent.InvokeAsync("read the PDF"))).ToString());

        Assert.Equal("skills", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal(1, sandbox.Reads);
        Assert.Contains("Selected remote filesystem instructions.", Assert.IsType<ToolResultBlock>(model.Requests[1].Messages.Last().Content[0]).Content);
        Assert.All(sandbox.Lists, path => Assert.StartsWith("/selected/.agent/skills", path));
    }

    private static async Task<T> Bounded<T>(Task<T> task)
    {
        if (!ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15))))) throw new TimeoutException("The skills compatibility operation did not complete.");
        return await task;
    }
    private static async Task Bounded(Task task)
    {
        if (!ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15))))) throw new TimeoutException("The skills compatibility operation did not complete.");
        await task;
    }
    private static ModelStreamEvent[] Text(string text) => new ModelStreamEvent[]
    {
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn),
    };
    private static ModelStreamEvent[] Call(string name) => new ModelStreamEvent[]
    {
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new ToolUseBlock("activate", "skills", JsonSerializer.SerializeToElement(new { skill_name = name }))), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
    };
    private sealed class Model(params ModelStreamEvent[][] turns) : IModel
    {
        public List<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            foreach (var item in turns[Requests.Count - 1]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }
    private sealed class SelectedFiles : IReadOnlyFileSystem
    {
        public int Reads { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("/selected/pdf-processing/SKILL.md", path); Reads++;
            return new("---\nname: pdf-processing\ndescription: Process PDFs\n---\nSelected filesystem instructions.");
        }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return path == "/selected/pdf-processing"
                ? new((IReadOnlyList<FileEntry>)new FileEntry[] { new("SKILL.md", false) })
                : throw new DirectoryNotFoundException("No resources exist in this selected filesystem.");
        }
    }
    private sealed class SelectedSandbox : IEnvironmentSandbox
    {
        public int Reads { get; private set; }
        public List<string> Lists { get; } = new();
        public ValueTask<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); return new("/selected");
        }
        public ValueTask<string?> GetPlatformAsync(CancellationToken cancellationToken = default) => new((string?)null);
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("/selected/.agent/skills/pdf-processing/SKILL.md", path); Reads++;
            return new("---\nname: pdf-processing\ndescription: Process PDFs\n---\nSelected remote filesystem instructions.");
        }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<SandboxEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Lists.Add(path);
            return path switch
            {
                "/selected/.agent/skills" => new((IReadOnlyList<SandboxEntry>)new SandboxEntry[] { new("pdf-processing", true) }),
                "/selected/.agent/skills/pdf-processing" => new((IReadOnlyList<SandboxEntry>)new SandboxEntry[] { new("SKILL.md", false) }),
                _ => throw new DirectoryNotFoundException("The selected sandbox has no such directory."),
            };
        }
    }
    private sealed class Handler(Stream stream) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("strands-agents-sdk", request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class OneByteStream(byte[] bytes) : Stream
    {
        private int _position;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position == bytes.Length || count == 0) return 0;
            buffer[offset] = bytes[_position++]; return 1;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
