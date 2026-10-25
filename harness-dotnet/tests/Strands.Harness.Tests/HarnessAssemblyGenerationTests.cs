using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tests;

public sealed class HarnessAssemblyGenerationTests
{
    private const string Minimal = "\"builtinTools\":[],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false";
    private const string Module = "Strands.Harness.ModuleFixture.dll";

    [Fact]
    public async Task DeclaredFileChangesReinitializeSameBytesWhileOldOptionsRemainBorrowed()
    {
        await InWorkspace(async workspace =>
        {
            File.WriteAllText(workspace.Side, "first");
            var config = Config(["version.txt"]);
            var loader = new HarnessAssemblyLoader(workspace.Root);
            var before = loader.ResolveOptions(config);
            var unchanged = loader.ResolveOptions(config);
            Assert.Same(Assert.Single(before.Tools), Assert.Single(unchanged.Tools));
            var bytes = File.ReadAllBytes(Path.Combine(workspace.Root, Module));
            File.WriteAllText(workspace.Side, "second");
            var after = loader.ResolveOptions(config);
            Assert.NotSame(Assert.Single(before.Tools), Assert.Single(after.Tools));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(workspace.Root, Module)));
            await using var first = await Harness.CreateAsync(before with { BackgroundTasks = null });
            await using var second = await Harness.CreateAsync(after with { BackgroundTasks = null });
            var input = JsonSerializer.SerializeToElement(new { });
            var oldResult = await first.InvokeToolAsync(new("old", "generation", input));
            var newResult = await second.InvokeToolAsync(new("new", "generation", input));
            Assert.NotEqual(oldResult.Content, newResult.Content);
            await second.DisposeAsync();
            Assert.Equal(oldResult.Content, (await first.InvokeToolAsync(new("still-old", "generation", input))).Content);
        });
    }

    [Fact]
    public void AbsentFilesRetainOrdinaryLoaderIdentityAndIgnoreUnlistedChanges()
    {
        var module = Path.Combine(AppContext.BaseDirectory, "ModuleFixture", Module);
        var moduleDirectory = Path.GetDirectoryName(module)!;
        var temporary = Strands.Tests.TestPaths.GetPhysicalTempPath();
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var sideParent = string.Equals(Path.GetPathRoot(module), Path.GetPathRoot(temporary), comparison) ? temporary : moduleDirectory;
        var side = Path.Combine(sideParent, "managed-generation-unlisted-" + Guid.NewGuid().ToString("N") + ".txt");
        var directory = moduleDirectory;
        while (!side.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("The fixture module and its unlisted file must share a filesystem root.");
        string Reference(string export) => JsonSerializer.Serialize(new { module, export, files = Array.Empty<string>() });
        try
        {
            var loader = new HarnessAssemblyLoader(directory);
            var config = HarnessAgentConfig.Parse("{" + Minimal + ",\"tools\":[" + Reference("generation") + "],\"modelModule\":" + Reference("model") + "}");
            var first = loader.ResolveOptions(config);
            File.WriteAllText(side, "unlisted");
            var second = loader.ResolveOptions(config);
            var ordinary = config.ToOptions(loader.Bind());
            Assert.Same(Assert.Single(first.Tools), Assert.Single(second.Tools));
            Assert.Same(Assert.Single(first.Tools), Assert.Single(ordinary.Tools));
            Assert.NotEqual("", Assert.Single(first.Tools).GetType().Assembly.Location);
        }
        finally { if (File.Exists(side)) File.Delete(side); }
    }

    [Fact]
    public Task SameDeclaredBytesReuseIdentityEvenWhenTheFileIsRewritten()
        => InWorkspace(workspace =>
        {
            var loader = new HarnessAssemblyLoader(workspace.Root);
            File.WriteAllText(workspace.Side, "unchanged");
            var first = loader.ResolveOptions(Config(["version.txt"]));
            File.WriteAllText(workspace.Side, "unchanged");
            var second = loader.ResolveOptions(Config(["./version.txt", "version.txt"]));
            Assert.Same(Assert.Single(first.Tools), Assert.Single(second.Tools));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ADeclaredDirectoryHashesItsSortedContentsAndSkipsDependencyCaches()
        => InWorkspace(workspace =>
        {
            var directory = Path.Combine(workspace.Root, "sources");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "b.txt"), "b");
            File.WriteAllText(Path.Combine(directory, "a.txt"), "a");
            var loader = new HarnessAssemblyLoader(workspace.Root);
            var config = Config(["sources"]);
            var first = loader.ResolveOptions(config);
            foreach (var ignored in new[] { ".git", ".venv", "node_modules" })
            {
                var cache = Path.Combine(directory, ignored);
                Directory.CreateDirectory(cache);
                File.WriteAllText(Path.Combine(cache, "ignored.txt"), "changed");
            }
            Assert.Same(Assert.Single(first.Tools), Assert.Single(loader.ResolveOptions(config).Tools));
            File.WriteAllText(Path.Combine(directory, "a.txt"), "changed");
            Assert.NotSame(Assert.Single(first.Tools), Assert.Single(loader.ResolveOptions(config).Tools));
            return Task.CompletedTask;
        });

    [Fact]
    public Task CreatingAMissingDeclaredFileInvalidatesTheGeneration()
        => InWorkspace(workspace =>
        {
            var loader = new HarnessAssemblyLoader(workspace.Root);
            var config = Config(["future/nested.txt"]);
            var first = loader.ResolveOptions(config);
            Assert.Same(Assert.Single(first.Tools), Assert.Single(loader.ResolveOptions(config).Tools));
            Directory.CreateDirectory(Path.Combine(workspace.Root, "future"));
            File.WriteAllText(Path.Combine(workspace.Root, "future", "nested.txt"), "created");
            Assert.NotSame(Assert.Single(first.Tools), Assert.Single(loader.ResolveOptions(config).Tools));
            return Task.CompletedTask;
        });

    [Fact]
    public Task TheTrustedDirectoryMayBeDeclaredWithATrailingSeparatorOnTheLoader()
        => InWorkspace(workspace =>
        {
            var loader = new HarnessAssemblyLoader(workspace.Root + Path.DirectorySeparatorChar);
            var config = Config(["."]);
            var first = loader.ResolveOptions(config);
            Assert.Same(Assert.Single(first.Tools), Assert.Single(loader.ResolveOptions(config).Tools));
            File.WriteAllText(workspace.Side, "changed root content");
            Assert.NotSame(Assert.Single(first.Tools), Assert.Single(loader.ResolveOptions(config).Tools));
            return Task.CompletedTask;
        });

    [Fact]
    public Task OneConfigurationGenerationCoversEverySelectedRoot()
        => InWorkspace(workspace =>
        {
            File.Copy(Path.Combine(workspace.Root, Module), Path.Combine(workspace.Root, "second.dll"));
            var config = HarnessAgentConfig.Parse("{" + Minimal + ",\"tools\":[" + Reference("generation", ["version.txt"]) + "," +
                JsonSerializer.Serialize(new { module = "second.dll", export = "initSetters" }) + "],\"modelModule\":" + Reference("model", []) + "}");
            var loader = new HarnessAssemblyLoader(workspace.Root);
            var first = loader.ResolveOptions(config);
            File.WriteAllText(workspace.Side, "changed");
            var second = loader.ResolveOptions(config);
            Assert.Equal(new[] { "generation", "init_setters" }, second.Tools.Select(tool => tool.Spec.Name));
            for (var index = 0; index < 2; index++) Assert.NotSame(first.Tools[index], second.Tools[index]);
            Assert.NotSame(first.AgentOptions!.Model, second.AgentOptions!.Model);
            return Task.CompletedTask;
        });

    [Fact]
    public Task AllRootBytesAreCapturedBeforeTheFirstApplicationResolverRuns()
        => InWorkspace(async workspace =>
        {
            var config = HarnessAgentConfig.Parse("{" + Minimal + ",\"tools\":[" + Reference("generation", ["version.txt"]) +
                "],\"modelModule\":" + Reference("model", []) + ",\"agentConfigModules\":{\"prepare\":{\"module\":\"application\"}}}");
            var calls = 0;
            var options = new HarnessAssemblyLoader(workspace.Root).ResolveOptions(config, new()
            {
                AgentConfigModuleResolver = (_, _) =>
                {
                    calls++;
                    File.WriteAllText(Path.Combine(workspace.Root, Module), "invalid image after capture");
                    return core => core;
                },
            });
            Assert.Equal(1, calls);
            await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
            Assert.Equal(32, (await agent.InvokeToolAsync(new("captured", "generation", JsonSerializer.SerializeToElement(new { })))).Content.Length);
        });

    [Fact]
    public Task InvalidLaterImagesFailBeforeModelAllocationAndLeaveOldOptionsUsable()
        => InWorkspace(async workspace =>
        {
            var loader = new HarnessAssemblyLoader(workspace.Root);
            var config = Config(["version.txt"]);
            var first = loader.ResolveOptions(config);
            File.WriteAllText(Path.Combine(workspace.Root, Module), "bad image");
            var allocations = 0;
            var invalid = HarnessAgentConfig.Parse("{" + Minimal + ",\"tools\":[" + Reference("generation", ["version.txt"]) + "]}");
            Assert.Throws<BadImageFormatException>(() => loader.ResolveOptions(invalid, new() { ModelFactory = _ => { allocations++; throw new InvalidOperationException("must not allocate"); } }));
            Assert.Equal(0, allocations);
            await using var agent = await Harness.CreateAsync(first with { BackgroundTasks = null });
            Assert.Equal(32, (await agent.InvokeToolAsync(new("old", "generation", JsonSerializer.SerializeToElement(new { })))).Content.Length);
        });

    [Theory]
    [InlineData("../escape")]
    [InlineData("../outside/nested.txt")]
    public Task InvalidDeclaredPathsFailBeforeApplicationCallbacks(string file)
        => InWorkspace(workspace =>
        {
            var calls = 0;
            var config = HarnessAgentConfig.Parse("{" + Minimal + ",\"tools\":[" + Reference("generation", [file]) +
                "],\"agentConfigModules\":{\"prepare\":{\"module\":\"application\"}}}");
            Assert.Throws<ArgumentException>(() => new HarnessAssemblyLoader(workspace.Root).ResolveOptions(config, new()
            {
                AgentConfigModuleResolver = (_, _) => { calls++; return core => core; },
            }));
            Assert.Equal(0, calls);
            return Task.CompletedTask;
        });

    [Fact]
    public void ExplicitFallbackAndSpecificResolversBypassUnselectedNativePaths()
    {
        var tool = ToolFactory.Create((Func<string>)(() => "application"), new() { Name = "application", Description = "Borrowed application tool" });
        var config = HarnessAgentConfig.Parse("{" + Minimal + ",\"tools\":[{\"module\":\"../unselected.dll\",\"files\":[\"../unselected\"]}]}");
        var bindings = new HarnessConfigBindings { AgentOptions = new() { Model = new AnswerModel() } };
        var loader = new HarnessAssemblyLoader(Path.Combine(Path.GetTempPath(), "not-created-" + Guid.NewGuid().ToString("N")));
        Assert.Same(tool, Assert.Single(loader.ResolveOptions(config, bindings with { ModuleResolver = _ => tool }).Tools));
        Assert.Same(tool, Assert.Single(loader.ResolveOptions(config, bindings with { ToolResolver = _ => tool }).Tools));
    }

    [Fact]
    public Task ConcurrentResolutionPublishesOneStaticGeneration()
        => InWorkspace(async workspace =>
        {
            var loader = new HarnessAssemblyLoader(workspace.Root);
            var config = Config(["version.txt"]);
            var options = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => loader.ResolveOptions(config))));
            var first = Assert.Single(options[0].Tools);
            foreach (var option in options) Assert.Same(first, Assert.Single(option.Tools));
        });

    [Fact]
    public Task ExportGettersCanResolveThePublishedGenerationOnAnotherThread()
        => InWorkspace(workspace =>
        {
            var loader = new HarnessAssemblyLoader(workspace.Root);
            var config = Config(["version.txt"]);
            var first = loader.ResolveOptions(config);
            var exportType = first.AgentOptions!.Model.GetType().Assembly.GetType("Strands.Harness.ModuleFixture.Exports", true)!;
            var callback = exportType.GetProperty("OnRead")!;
            var calls = 0;
            callback.SetValue(null, (Action)(() =>
            {
                callback.SetValue(null, null);
                var nested = Task.Run(() => loader.ResolveOptions(config));
                Assert.True(nested.Wait(TimeSpan.FromSeconds(10)), "Getter retained the loader cache lock.");
                Assert.Same(Assert.Single(first.Tools), Assert.Single(nested.GetAwaiter().GetResult().Tools));
                calls++;
            }));
            Assert.Same(Assert.Single(first.Tools), Assert.Single(loader.ResolveOptions(Config(["version.txt"], "reentrant")).Tools));
            Assert.Equal(1, calls);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ThrowingGettersKeepTheirPrimaryExceptionAndDoNotPoisonTheCache()
        => InWorkspace(workspace =>
        {
            var loader = new HarnessAssemblyLoader(workspace.Root);
            var config = Config(["version.txt"]);
            var first = loader.ResolveOptions(config);
            var exportType = first.AgentOptions!.Model.GetType().Assembly.GetType("Strands.Harness.ModuleFixture.Exports", true)!;
            var callback = exportType.GetProperty("OnRead")!;
            var primary = new InvalidDataException("application getter failed");
            callback.SetValue(null, (Action)(() => throw primary));
            Assert.Same(primary, Assert.Throws<InvalidDataException>(() => loader.ResolveOptions(Config(["version.txt"], "reentrant"))));
            callback.SetValue(null, null);
            Assert.Same(Assert.Single(first.Tools), Assert.Single(loader.ResolveOptions(Config(["version.txt"], "reentrant")).Tools));
            return Task.CompletedTask;
        });

    [Fact]
    public Task GenerationExportsKeepRichStreamingAndChildIdentity()
        => InWorkspace(async workspace =>
        {
            var options = new HarnessAssemblyLoader(workspace.Root).ResolveOptions(Config(["version.txt"], "generationStream"));
            var original = Assert.Single(options.Tools);
            Assert.IsAssignableFrom<IRichTool>(original);
            Assert.IsAssignableFrom<IStreamingTool>(original);
            await using var parent = await Harness.CreateAsync(options with { BackgroundTasks = null });
            await using var child = parent.CreateChild();
            await child.InitializeAsync();
            Assert.Same(original, Assert.Single(parent.Tools));
            Assert.Same(original, Assert.Single(child.Tools));
            var result = await child.InvokeToolAsync(new("rich", "generation_stream", JsonSerializer.SerializeToElement(new { })));
            Assert.Equal(32, result.Content.Length);
            Assert.Equal(result.Content, Assert.IsType<JsonBlock>(result.ContentBlocks[1]).Json.GetProperty("generation").GetString());
        });

    [Fact]
    public Task CancellationBeforeConstructionRetainsTheBorrowedGeneration()
        => InWorkspace(async workspace =>
        {
            var options = new HarnessAssemblyLoader(workspace.Root).ResolveOptions(Config(["version.txt"], "borrowed"));
            var original = Assert.Single(options.Tools);
            using var caller = new CancellationTokenSource();
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Harness.CreateAsync(options with { BackgroundTasks = null }, caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
            var input = JsonSerializer.SerializeToElement(new { });
            Assert.Equal("alive", await original.InvokeAsync(input, new(null!, new("borrowed", original.Spec.Name, input), new Dictionary<string, object?>())));
        });

    private static HarnessAgentConfig Config(string[] files, string export = "generation")
        => HarnessAgentConfig.Parse("{" + Minimal + ",\"tools\":[" + Reference(export, files) + "],\"modelModule\":" + Reference("model", []) + "}");

    private static string Reference(string export, string[] files)
        => JsonSerializer.Serialize(new { module = Module, export, files });

    private static async Task InWorkspace(Func<Workspace, Task> body)
    {
        var workspace = new Workspace();
        Exception? primary = null;
        try { await body(workspace); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { workspace.Dispose(); }
            catch (Exception cleanup) when (primary is not null)
            { primary.Data["Strands.CleanupErrors"] = new List<Exception> { cleanup }; }
        }
    }

    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "managed-generation-" + Guid.NewGuid().ToString("N"));
        internal string Side => Path.Combine(Root, "version.txt");
        internal Workspace()
        {
            Directory.CreateDirectory(Root);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "ModuleFixture", Module), Path.Combine(Root, Module));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class AnswerModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
