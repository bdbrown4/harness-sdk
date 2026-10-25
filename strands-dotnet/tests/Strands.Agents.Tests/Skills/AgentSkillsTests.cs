using Strands.Agents.Files;
using Strands.Agents.Skills;

namespace Strands.Agents.Tests.Skills;

public sealed class AgentSkillsTests
{
    [Fact]
    public async Task NativeSkillsExposeOnlyEscapedMetadataThenRevealInstructionsAndPersistActivation()
    {
        var skill = new Skill(new() { Name = "pdf-processing", Description = "Extract <tables> & text", Instructions = "Read every page before extracting a table." });
        var plugin = new AgentSkills(new() { Skills = [skill] });
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("activate", "skills", "{\"skill_name\":\"pdf-processing\"}")), AgentTests.Text("done"));
        await using var agent = new Agent(new() { Model = model, Plugins = [plugin], SystemPrompt = "base instructions" });

        Assert.Equal("done", (await agent.InvokeAsync("process a PDF")).ToString());

        Assert.Equal("skills", Assert.Single(model.Requests[0].Tools!).Name);
        Assert.StartsWith("base instructions", model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("<description>Extract &lt;tables&gt; &amp; text</description>", model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(skill.Instructions, model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(skill.Instructions, Assert.IsType<ToolResultBlock>(Assert.Single(model.Requests[1].Messages.Last().Content)).Content);
        Assert.Equal(new[] { "pdf-processing" }, plugin.GetActivatedSkills(agent));
        Assert.Same(skill, Assert.Single(await plugin.GetAvailableSkillsAsync(agent)));
    }

    [Fact]
    public async Task FilesystemSourcesUseTheAgentBackendAndListBoundedResourcesOnActivation()
    {
        var files = new SkillFiles();
        var plugin = new AgentSkills(new() { Skills = ["/sandbox-skills"], Strict = true, MaxResourceFiles = 1 });
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("activate", "skills", "{\"skill_name\":\"pdf-processing\"}")), AgentTests.Text("done"));
        await using var agent = new Agent(new() { Model = model, Plugins = [plugin], FileSystem = files });

        Assert.Equal("done", (await agent.InvokeAsync("process a PDF")).ToString());

        var result = Assert.IsType<ToolResultBlock>(Assert.Single(model.Requests[1].Messages.Last().Content));
        Assert.Contains("Read the PDF.", result.Content, StringComparison.Ordinal);
        Assert.Contains("Location: /sandbox-skills/pdf-processing/SKILL.md", result.Content, StringComparison.Ordinal);
        Assert.Contains("references/a.md", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("references/b.md", result.Content, StringComparison.Ordinal);
        Assert.Contains("... (truncated at 1 files)", result.Content, StringComparison.Ordinal);
        Assert.Equal("/sandbox-skills/pdf-processing/SKILL.md", Assert.Single(files.Reads));
        Assert.Equal("/sandbox-skills/pdf-processing", Assert.Single(await plugin.GetAvailableSkillsAsync(agent)).Path);
    }

    [Fact]
    public async Task StandaloneSkillsDiscoverARealHostDirectoryAndReadItsUtf8Instructions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands skills " + Guid.NewGuid().ToString("N"));
        var skillDirectory = Path.Combine(directory, "pdf-processing");
        Directory.CreateDirectory(skillDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: pdf-processing\ndescription: Process PDFs\n---\nRead Café ☕.");
            var plugin = new AgentSkills(new() { Skills = [directory], Strict = true });
            var model = new AgentTests.ScriptedModel(AgentTests.Call(("activate", "skills", "{\"skill_name\":\"pdf-processing\"}")), AgentTests.Text("done"));
            await using var agent = new Agent(new() { Model = model, Plugins = [plugin] });

            Assert.Equal("done", (await agent.InvokeAsync("process a PDF")).ToString());

            Assert.Contains("Read Café ☕.", Assert.IsType<ToolResultBlock>(Assert.Single(model.Requests[1].Messages.Last().Content)).Content, StringComparison.Ordinal);
            Assert.Equal(skillDirectory, Assert.Single(await plugin.GetAvailableSkillsAsync(agent)).Path!.Replace('/', Path.DirectorySeparatorChar));
            Assert.Empty(await plugin.GetAvailableSkillsAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task HttpsSourcesAreFetchedOnceAndDisposeResponsesWithoutOwningTheSuppliedClient()
    {
        using var handler = new SkillHttpHandler();
        using var http = new HttpClient(handler);
        var plugin = new AgentSkills(new() { Skills = ["https://skills.example.test/pdf/SKILL.md"], HttpClient = http });
        var available = await plugin.GetAvailableSkillsAsync();
        Assert.Equal("pdf-processing", Assert.Single(available).Name);
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("activate", "skills", "{\"skill_name\":\"pdf-processing\"}")), AgentTests.Text("done"));
        await using var agent = new Agent(new() { Model = model, Plugins = [plugin] });

        Assert.Equal("done", (await agent.InvokeAsync("process a PDF")).ToString());

        Assert.Equal("Read Café ☕.", Assert.IsType<ToolResultBlock>(Assert.Single(model.Requests[1].Messages.Last().Content)).Content);
        Assert.Equal(1, handler.Requests);
        Assert.True(Assert.Single(handler.Responses).Disposed);
        await agent.DisposeAsync();
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task ReplacingTheCatalogUpdatesInitializedAgentsWithoutErasingActivationState()
    {
        var original = new Skill(new() { Name = "pdf-processing", Description = "Process PDFs", Instructions = "Original instructions." });
        var replacement = new Skill(new() { Name = "review", Description = "Review a document", Instructions = "Replacement instructions." });
        var plugin = new AgentSkills(new() { Skills = [original] });
        var model = new AgentTests.ScriptedModel(
            AgentTests.Call(("original", "skills", "{\"skill_name\":\"pdf-processing\"}")), AgentTests.Text("first"),
            AgentTests.Call(("replacement", "skills", "{\"skill_name\":\"review\"}")), AgentTests.Text("second"));
        await using var agent = new Agent(new() { Model = model, Plugins = [plugin], SystemPrompt = "base" });
        Assert.Equal("first", (await agent.InvokeAsync("process")).ToString());

        plugin.SetAvailableSkills([replacement]);

        Assert.Same(replacement, Assert.Single(await plugin.GetAvailableSkillsAsync(agent)));
        Assert.Equal(new[] { "pdf-processing" }, plugin.GetActivatedSkills(agent));
        Assert.Equal("second", (await agent.InvokeAsync("review")).ToString());
        Assert.Contains("<name>review</name>", model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<name>pdf-processing</name>", model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal("Replacement instructions.", Assert.IsType<ToolResultBlock>(Assert.Single(model.Requests[3].Messages.Last().Content)).Content);
        Assert.Equal(new[] { "pdf-processing", "review" }, plugin.GetActivatedSkills(agent));
        Assert.Equal("base", agent.SystemPrompt);
    }

    [Fact]
    public async Task AnOlderInFlightDownloadCannotOverwriteAReplacementCatalog()
    {
        using var handler = new DeferredSkillHandler();
        using var http = new HttpClient(handler);
        var plugin = new AgentSkills(new() { Skills = ["https://skills.example.test/SKILL.md"], HttpClient = http });
        var loading = plugin.GetAvailableSkillsAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = new Skill(new() { Name = "replacement", Description = "The new catalog" });

        plugin.SetAvailableSkills([replacement]);

        Assert.Same(replacement, Assert.Single(await plugin.GetAvailableSkillsAsync()));
        handler.Release.TrySetResult(new(System.Net.HttpStatusCode.OK) { Content = new SkillContent() });
        Assert.Equal("pdf-processing", Assert.Single(await loading.WaitAsync(TimeSpan.FromSeconds(10))).Name);
        Assert.Same(replacement, Assert.Single(await plugin.GetAvailableSkillsAsync()));
    }

    [Fact]
    public async Task CancelingAQueryDoesNotEvictAnotherCallersActiveInitialization()
    {
        using var handler = new DeferredSkillHandler();
        using var http = new HttpClient(handler);
        var plugin = new AgentSkills(new() { Skills = ["https://skills.example.test/SKILL.md"], HttpClient = http });
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(), Plugins = [plugin] });
        var initializing = agent.InitializeAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource();
        var query = plugin.GetAvailableSkillsAsync(agent, canceled.Token);

        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(TimeSpan.FromSeconds(10)));
        handler.Release.TrySetResult(new(System.Net.HttpStatusCode.OK) { Content = new SkillContent() });
        await initializing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("pdf-processing", Assert.Single(await plugin.GetAvailableSkillsAsync(agent)).Name);
        Assert.Equal(1, handler.Requests);
    }

    private sealed class SkillFiles : IReadOnlyFileSystem
    {
        public List<string> Reads { get; } = [];
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads.Add(path);
            return path == "/sandbox-skills/pdf-processing/SKILL.md"
                ? new("---\nname: pdf-processing\ndescription: Process PDFs\n---\nRead the PDF.")
                : throw new FileNotFoundException("Missing test skill file.", path);
        }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FileEntry> entries = path switch
            {
                "/sandbox-skills" => [new("pdf-processing", true)],
                "/sandbox-skills/pdf-processing" => [new("SKILL.md", false), new("references", true)],
                "/sandbox-skills/pdf-processing/references" => [new("b.md", false), new("a.md", false)],
                _ => throw new DirectoryNotFoundException("Missing test skill directory."),
            };
            return new(entries);
        }
    }

    private sealed class SkillHttpHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Disposed { get; private set; }
        public List<SkillContent> Responses { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests++;
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("strands-agents-sdk", request.Headers.UserAgent.ToString());
            var content = new SkillContent(); Responses.Add(content);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class DeferredSkillHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; Entered.TrySetResult(true);
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }
    private sealed class SkillContent() : StringContent("---\nname: pdf-processing\ndescription: Process PDFs\n---\nRead Café ☕.", System.Text.Encoding.UTF8)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
