using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tests;

public sealed class HarnessAssemblyLoaderTests
{
    private const string Minimal = "\"builtinTools\":[],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false";
    private const string AssemblyName = "Strands.Harness.ModuleFixture.dll";
    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "ModuleFixture");
    private static HarnessAssemblyLoader Loader() => new(FixtureDirectory);

    [Fact]
    public async Task ManagedDefaultAndNamedExportsFlattenInDeclarationOrderAndInvokeRealTools()
    {
        var options = Config("\"tools\":[" + Reference() + "," + Reference("echo") + "]").ToOptions(Loader().Bind());
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        Assert.Equal(new[] { "first", "second", "assembly_echo" }, agent.Tools.Select(tool => tool.Spec.Name));
        var result = await agent.InvokeToolAsync(new("call", "assembly_echo", JsonSerializer.SerializeToElement(new { value = "hello" })));
        Assert.Equal("native:hello", result.Content);
    }

    [Fact]
    public async Task StaticClassExportsUseExistingToolFactoryAndDoNotInvokeToolBodiesDuringBinding()
    {
        var options = Config("\"tools\":[" + Reference("toolClass") + "," + Reference("Strands.Harness.ModuleFixture.Exports::Echo") + "]").ToOptions(Loader().Bind());
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        Assert.Equal(new[] { "class_tool", "assembly_echo" }, agent.Tools.Select(tool => tool.Spec.Name));
    }

    [Fact]
    public async Task PluginSettingsAndSubagentExportsKeepNativeTypesAndInitialization()
    {
        var options = Config("\"plugins\":[" + Reference("plugin") + "],\"subagents\":[" + Reference("agent") + "],\"agentConfigModules\":{\"limits\":" + Reference("settings") + "}").ToOptions(Loader().Bind());
        Assert.Equal(7, options.AgentOptions!.MaxTurns);
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        Assert.True(agent.State.Get<bool>("assemblyPlugin"));
        var result = await agent.InvokeToolAsync(new("delegate", "assembly_specialist", JsonSerializer.SerializeToElement(new { task = "hello" })));
        Assert.Contains("loaded agent", result.Content);
    }

    [Fact]
    public void ExplicitSpecificAndFallbackBindingsKeepPrecedence()
    {
        var calls = 0;
        var bindings = Loader().Bind(new() { ModuleResolver = _ => { calls++; return "explicit"; } });
        Assert.Equal("explicit", bindings.ModuleResolver!(null!));
        Assert.Equal(1, calls);
        var tool = ToolFactory.Create((Func<string>)(() => "explicit"), new() { Name = "explicit", Description = "Explicit tool" });
        var options = Config("\"tools\":[" + Reference("missing") + "]").ToOptions(Loader().Bind(new() { ToolResolver = _ => tool }));
        Assert.Same(tool, Assert.Single(options.Tools));
    }

    [Theory]
    [InlineData("missing", "does not export")]
    [InlineData("null", "returned null")]
    [InlineData("wrong", "ordered list")]
    [InlineData("invalidTools", "nonnull")]
    [InlineData("Strands.Harness.ModuleFixture.Exports::Overloaded", "ambiguous")]
    public void InvalidExportsFailBeforeNativeModelAllocation(string export, string diagnosis)
    {
        var calls = 0;
        var config = HarnessAgentConfig.Parse("{" + Minimal + ",\"tools\":[" + Reference(export) + "]}");
        var failure = Assert.ThrowsAny<Exception>(() => config.ToOptions(Loader().Bind(new() { ModelFactory = _ => { calls++; throw new InvalidOperationException("Must not allocate"); } })));
        Assert.Contains(diagnosis, failure.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ThrowingExportPreservesTheApplicationException()
    {
        var failure = Assert.Throws<InvalidDataException>(() => Config("\"tools\":[" + Reference("throwing") + "]").ToOptions(Loader().Bind()));
        Assert.Equal("fixture export failed", failure.Message);
    }

    [Theory]
    [InlineData("python")]
    [InlineData("typescript")]
    public void ForeignLanguageModulesAreRejectedBeforeExecution(string language)
    {
        var failure = Assert.Throws<NotSupportedException>(() => Config("\"tools\":[" + Reference("echo", language: language) + "]").ToOptions(Loader().Bind()));
        Assert.Contains("Cannot load " + language, failure.Message);
    }

    [Fact]
    public void NativeLanguageMetadataRoundTripsAndLoadsWithoutForeignInterpreters()
    {
        var config = Config("\"tools\":[" + Reference("echo", language: "csharp") + "]");
        Assert.Equal("csharp", config.ToJsonElement().GetProperty("tools")[0].GetProperty("language").GetString());
        Assert.Equal("assembly_echo", Assert.Single(config.ToOptions(Loader().Bind()).Tools).Spec.Name);
    }

    [Theory]
    [InlineData("../Strands.Harness.Tests.dll", "beneath")]
    [InlineData("tools.py", "managed .dll")]
    public void ModulePathsCannotEscapeTheExplicitTrustedRoot(string module, string diagnosis)
    {
        var failure = Assert.ThrowsAny<Exception>(() => Config("\"tools\":[" + Reference(module: module) + "]").ToOptions(Loader().Bind()));
        Assert.Contains(diagnosis, failure.Message);
    }

    [Fact]
    public void DuplicateNamesAreDetectedBeforeAnyExportGetterRuns()
    {
        DuplicateExports.GetterCalls = 0;
        var module = Path.GetFileName(typeof(HarnessAssemblyLoaderTests).Assembly.Location);
        var loader = new HarnessAssemblyLoader(AppContext.BaseDirectory);
        var failure = Assert.Throws<ArgumentException>(() => Config("\"tools\":[" + Reference("duplicate", module) + "]").ToOptions(loader.Bind()));
        Assert.Contains("duplicate export", failure.Message);
        Assert.Equal(0, DuplicateExports.GetterCalls);
    }

    [Fact]
    public async Task ExportedObjectsRemainBorrowedAcrossHarnessAndChildDisposal()
    {
        var options = Config("\"tools\":[" + Reference("borrowed") + "]").ToOptions(Loader().Bind());
        var tool = Assert.Single(options.Tools);
        var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        await using (var child = agent.CreateChild())
        {
            await child.InitializeAsync();
            Assert.Same(tool, Assert.Single(child.Tools));
        }
        await agent.DisposeAsync();
        var input = JsonSerializer.SerializeToElement(new { });
        Assert.Equal("alive", await tool.InvokeAsync(input, new(agent, new("call", tool.Spec.Name, input), new Dictionary<string, object?>())));
    }

    [Fact]
    public async Task PrecompiledStandardExtensionCanInitializeAllPublishedDependencyOptions()
    {
        var options = Config("\"tools\":[" + Reference("initSetters") + "]").ToOptions(Loader().Bind());
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        var result = await agent.InvokeToolAsync(new("call", "init_setters", JsonSerializer.SerializeToElement(new { })));
        Assert.Equal("native:fixture:3", result.Content);
    }

    [Fact]
    public async Task CollidingExportListsAreRejectedBeforePluginInitialization()
    {
        var options = Config("\"tools\":[" + Reference() + "," + Reference() + "],\"plugins\":[" + Reference("plugin") + "]").ToOptions(Loader().Bind());
        await Assert.ThrowsAsync<ArgumentException>(async () => await Harness.CreateAsync(options with { BackgroundTasks = null }));
        var plugin = Assert.Single(options.Plugins);
        Assert.Equal(0, plugin.GetType().GetProperty("InitializeCalls")!.GetValue(plugin));
    }

    [Fact]
    public async Task CallerCancellationBeforeConstructionKeepsBorrowedExportsUsable()
    {
        var options = Config("\"tools\":[" + Reference("borrowed") + "]").ToOptions(Loader().Bind());
        var tool = Assert.Single(options.Tools);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Harness.CreateAsync(options with { BackgroundTasks = null }, cancellation.Token));
        var input = JsonSerializer.SerializeToElement(new { });
        Assert.Equal("alive", await tool.InvokeAsync(input, new(null!, new("call", tool.Spec.Name, input), new Dictionary<string, object?>())));
    }

    [Theory]
    [InlineData("asyncAgent")]
    [InlineData("asyncAgentDelegate")]
    public void UnsupportedAsyncSubagentFactoriesAreRejectedBeforeTheirBodiesRun(string export)
    {
        var failure = Assert.Throws<ArgumentException>(() => Config("\"subagents\":[" + Reference(export) + "]").ToOptions(Loader().Bind()));
        Assert.Contains("synchronous", failure.Message);
    }

    [Fact]
    public void PreviouslyLoadedSameIdentityCannotSubstituteAnAssemblyFromAnotherRoot()
    {
        var original = Config("\"tools\":[" + Reference("origin") + "]").ToOptions(Loader().Bind());
        Assert.Equal("origin", Assert.Single(original.Tools).Spec.Name);
        var loader = new HarnessAssemblyLoader(Path.Combine(AppContext.BaseDirectory, "ModuleConflictFixture"));
        var failure = Assert.Throws<FileLoadException>(() => Config("\"tools\":[" + Reference("origin") + "]").ToOptions(loader.Bind()));
        Assert.Contains("already loaded", failure.Message);
    }

    private static HarnessAgentConfig Config(string fields) => HarnessAgentConfig.Parse("{" + Minimal + ",\"modelModule\":" + Reference("model") + "," + fields + "}");
    private static string Reference(string? export = null, string? module = null, string? language = null) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["module"] = module ?? AssemblyName, ["export"] = export ?? "default" }.Concat(language is null ? Array.Empty<KeyValuePair<string, object?>>() : new[] { new KeyValuePair<string, object?>("language", language) }).ToDictionary(pair => pair.Key, pair => pair.Value));
}

public static class DuplicateExports
{
    public static int GetterCalls;
    [HarnessExport("duplicate")]
    public static object First { get { GetterCalls++; return new object(); } }
    [HarnessExport("duplicate")]
    public static object Second { get { GetterCalls++; return new object(); } }
}
