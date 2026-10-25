using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Files;
using Strands.Agents.Skills;

namespace Strands.Harness.Tests;

public sealed class SkillsHarnessTests
{
    [Fact]
    public async Task ExplicitNativeSkillsExposeMetadataAndActivateInstructions()
    {
        var model = new SkillModel();
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Skills = new() { Sources = [new Skill(new() { Name = "pdf-processing", Description = "Process PDFs", Instructions = "Read every page." })] },
        });

        Assert.Equal("done", (await agent.InvokeAsync("process a PDF")).ToString());

        Assert.Equal("skills", Assert.Single(model.Requests[0].Tools!).Name);
        Assert.Contains("<name>pdf-processing</name>", model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Read every page.", model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal("Read every page.", Assert.IsType<ToolResultBlock>(model.Requests[1].Messages.Last().Content[0]).Content);
    }

    [Fact]
    public async Task PortableSkillsResolveExplicitPathsAgainstProjectRootAndPreserveHttpsUrls()
    {
        var root = Path.Combine(Path.GetTempPath(), "strands-configured-skills");
        var config = HarnessAgentConfig.Parse("""
            {"builtinTools":[],"builtinPlugins":[],"caching":false,"contextManager":false,"memory":false,"session":false,
             "skills":["./skills/pdf-processing","https://skills.example.test/SKILL.md"]}
            """);

        var options = config.ToOptions(new() { ModelResolver = _ => new UnusedModel() }, root);

        Assert.True(options.Skills.Enabled);
        Assert.Equal(Path.Combine(root, "skills", "pdf-processing"), options.Skills.Sources![0].Location);
        Assert.Equal("https://skills.example.test/SKILL.md", options.Skills.Sources[1].Location);
    }

    [Fact]
    public async Task DefaultSkillsAreDiscoveredThroughTheSelectedFilesystem()
    {
        var files = new DefaultSkillFiles();
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = new UnusedModel(), FileSystem = files },
            BuiltinTools = [],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

        Assert.Equal("skills", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal(1, files.Reads);
        Assert.All(files.Lists, path => Assert.StartsWith("./.agent/skills", path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrEmptySkillsDoNotInspectAnyFilesystem(bool emptySources)
    {
        var files = new DefaultSkillFiles();
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = new UnusedModel(), FileSystem = files },
            BuiltinTools = [],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Skills = emptySources ? new() { Sources = [] } : new() { Enabled = false },
        });

        Assert.Empty(agent.Tools);
        Assert.Empty(files.Lists);
        Assert.Equal(0, files.Reads);
    }

    [Fact]
    public async Task ApplicationSkillsPluginWinsOverConvenienceSourcesAndDefaultDiscovery()
    {
        var files = new DefaultSkillFiles();
        var plugin = new AgentSkills(new() { Skills = [new Skill(new() { Name = "application", Description = "Application skill" })] });
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = new UnusedModel(), FileSystem = files, Plugins = [plugin] },
            BuiltinTools = [],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Skills = new() { Sources = ["/must-not-be-read"] },
        });

        Assert.Equal("application", Assert.Single(await plugin.GetAvailableSkillsAsync(agent)).Name);
        Assert.Equal("skills", Assert.Single(agent.Tools).Spec.Name);
        Assert.Empty(files.Lists);
        Assert.Equal(0, files.Reads);
    }

    [Fact]
    public async Task HarnessFileSystemReadsThroughTheSelectedSandboxAndRejectsUnavailableListing()
    {
        var sandbox = new TextOnlySandbox();
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = new UnusedModel(),
            Sandbox = sandbox,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });
        var files = Assert.IsAssignableFrom<IReadOnlyFileSystem>(agent.FileSystem);
        const string path = "/selected-sandbox/SKILL.md";
        Assert.Equal("selected filesystem", await files.ReadTextAsync(path));
        Assert.Equal(path, sandbox.ReadPath);
        await Assert.ThrowsAsync<NotSupportedException>(() => files.ListFilesAsync("/selected-sandbox").AsTask());
    }

    private sealed class TextOnlySandbox : ISandbox
    {
        public string? ReadPath { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ReadPath = path; return new("selected filesystem");
        }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class DefaultSkillFiles : IReadOnlyFileSystem
    {
        public List<string> Lists { get; } = [];
        public int Reads { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("./.agent/skills/pdf-processing/SKILL.md", path); Reads++;
            return new("---\nname: pdf-processing\ndescription: Process PDFs\n---\nSelected instructions.");
        }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Lists.Add(path);
            IReadOnlyList<FileEntry> entries = path switch
            {
                "./.agent/skills" => [new("pdf-processing", true)],
                "./.agent/skills/pdf-processing" => [new("SKILL.md", false)],
                _ => throw new IOException("Unexpected filesystem inspection."),
            };
            return new(entries);
        }
    }
    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This test does not invoke a model.");
    }
    private sealed class SkillModel : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            yield return new ModelMessageStartEvent();
            if (Requests.Count == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("activate", "skills", JsonSerializer.SerializeToElement(new { skill_name = "pdf-processing" })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }
}
