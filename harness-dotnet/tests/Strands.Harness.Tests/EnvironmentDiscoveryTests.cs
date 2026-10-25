using System.Runtime.CompilerServices;
using Strands.Agents;
using Strands.Harness.Plugins;

namespace Strands.Harness.Tests;

public sealed class EnvironmentDiscoveryTests
{
    [Fact]
    public Task CwdFailureRetainsSelectedRelativeInstructionsAndLinks() =>
        AssertRelativeDiscoveryAsync(new SelectedEnvironment(null, throwsOnCwd: true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public Task MissingCwdRetainsSelectedRelativeInstructionsAndLinks(string? workingDirectory) =>
        AssertRelativeDiscoveryAsync(new SelectedEnvironment(workingDirectory));

    [Fact]
    public async Task PlatformFailureDoesNotSuppressAbsoluteDirectoryDiscovery()
    {
        var sandbox = AbsoluteEnvironment("/selected");
        sandbox.PlatformFailure = new IOException("No platform probe.");
        var model = new RecordingModel();
        await using var agent = await CreateAgentAsync(model, new EnvironmentContext(sandbox));

        await agent.InvokeAsync("inspect");

        var input = Input(model);
        Assert.DoesNotContain("Platform:", input);
        Assert.Contains("Working directory: /selected", input);
        Assert.Contains("selected instructions", input);
        Assert.Contains("project/src/AGENTS.md", input);
        Assert.Equal(new[] { "/selected/AGENTS.md" }, sandbox.ReadPaths);
        Assert.Equal(new[] { "/selected", "/selected/project", "/selected/project/src" }, sandbox.ListPaths);
        AssertHistoryClean(agent);
    }

    [Fact]
    public async Task FailedPlatformAndCwdProbesStillPermitIndependentRelativeDiscovery()
    {
        var sandbox = new ConfigurableEnvironment
        {
            PlatformFailure = new IOException("No platform probe."),
            CwdFailure = new IOException("No cwd probe."),
        };
        var model = new RecordingModel();
        await using var agent = await CreateAgentAsync(model, new EnvironmentContext(sandbox));

        await agent.InvokeAsync("inspect");

        var input = Input(model);
        Assert.DoesNotContain("Platform:", input);
        Assert.DoesNotContain("Working directory:", input);
        Assert.Contains("selected instructions", input);
        Assert.Contains("project/src/AGENTS.md", input);
        Assert.Equal(new[] { "AGENTS.md" }, sandbox.ReadPaths);
        Assert.Equal(new[] { ".", "project", "project/src" }, sandbox.ListPaths);
        AssertHistoryClean(agent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RootReadAndListingFailuresDoNotSuppressEachOther(bool failRead)
    {
        var sandbox = new ConfigurableEnvironment();
        if (failRead) sandbox.ReadFailure = new IOException("No root instructions.");
        else sandbox.ListFailures.Add(".");
        var model = new RecordingModel();
        await using var agent = await CreateAgentAsync(model, new EnvironmentContext(sandbox));

        await agent.InvokeAsync("inspect");

        var input = Input(model);
        if (failRead)
        {
            Assert.DoesNotContain("<AGENTS.md>", input);
            Assert.Contains("project/src/AGENTS.md", input);
            Assert.Contains("README.md, project/README.md", input);
        }
        else
        {
            Assert.Contains("selected instructions", input);
            Assert.DoesNotContain("files nearby", input);
        }
        Assert.Contains("Platform: SelectedOS", input);
        AssertHistoryClean(agent);
    }

    [Fact]
    public async Task IndependentReadCancellationIsBestEffortWhenTheInvocationIsLive()
    {
        var sandbox = new ConfigurableEnvironment { ReadFailure = new OperationCanceledException(CancellationToken.None) };
        var model = new RecordingModel();
        await using var agent = await CreateAgentAsync(model, new EnvironmentContext(sandbox));

        var result = await agent.InvokeAsync("inspect");

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.DoesNotContain("<AGENTS.md>", Input(model));
        Assert.Contains("project/src/AGENTS.md", Input(model));
        AssertHistoryClean(agent);
    }

    [Fact]
    public async Task AnUnreadableChildDirectoryDoesNotSuppressLaterSiblings()
    {
        var sandbox = new ConfigurableEnvironment();
        sandbox.Tree.Clear();
        sandbox.Tree.Add(".", [new("broken", true), new("good", true)]);
        sandbox.Tree.Add("good", [new("AGENTS.md", false), new("README.md", false)]);
        sandbox.ListFailures.Add("broken");
        var model = new RecordingModel();
        await using var agent = await CreateAgentAsync(model, new EnvironmentContext(sandbox));

        await agent.InvokeAsync("inspect");

        Assert.Contains("good/AGENTS.md", Input(model));
        Assert.Contains("good/README.md", Input(model));
        Assert.Equal(new[] { ".", "broken", "good" }, sandbox.ListPaths);
        AssertHistoryClean(agent);
    }

    [Fact]
    public async Task ASelectedSandboxMayRejectRelativePathsWithoutAnyHostFallback()
    {
        var sandbox = new ConfigurableEnvironment { ReadFailure = new IOException("Absolute paths required.") };
        sandbox.ListFailures.Add(".");
        var model = new RecordingModel();
        var plugin = new EnvironmentContext(sandbox, () => new DateTime(2026, 10, 22), "relative-rejection");
        await using var agent = await CreateAgentAsync(model, plugin);

        var result = await agent.InvokeAsync("inspect");

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        var input = Input(model);
        Assert.Contains("Platform: SelectedOS", input);
        Assert.Contains("Date: 2026-10-22", input);
        Assert.DoesNotContain("Working directory:", input);
        Assert.DoesNotContain("<AGENTS.md>", input);
        Assert.DoesNotContain("files nearby", input);
        Assert.Equal(new[] { "AGENTS.md" }, sandbox.ReadPaths);
        Assert.Equal(new[] { "." }, sandbox.ListPaths);
        AssertHistoryClean(agent);
    }

    [Fact]
    public async Task ATextOnlySandboxRemainsDateOnlyWithoutFileReads()
    {
        var sandbox = new TextOnlyEnvironment();
        var model = new RecordingModel();
        var plugin = new EnvironmentContext(sandbox, () => new DateTime(2026, 10, 22), "date-only");
        await using var agent = await CreateAgentAsync(model, plugin);

        await agent.InvokeAsync("inspect");

        Assert.Contains("<environment>\nDate: 2026-10-22\n</environment>", Input(model));
        Assert.False(sandbox.ReadAttempted);
        AssertHistoryClean(agent);
    }

    [Fact]
    public async Task RelativeDiscoveryIsMemoizedPerAgentWhileTheDateUpdates()
    {
        var sandbox = new ConfigurableEnvironment { CwdFailure = new IOException("No cwd probe.") };
        var day = new DateTime(2026, 10, 22);
        var plugin = new EnvironmentContext(sandbox, () => day, "shared-relative-environment");
        var firstModel = new RecordingModel();
        await using var first = await CreateAgentAsync(firstModel, plugin);
        await first.InvokeAsync("first");
        var calls = (sandbox.PlatformCalls, sandbox.CwdCalls, sandbox.ReadPaths.Count, sandbox.ListPaths.Count);
        sandbox.RootText = "changed instructions";
        day = day.AddDays(1);

        await first.InvokeAsync("next");

        Assert.Equal(calls, (sandbox.PlatformCalls, sandbox.CwdCalls, sandbox.ReadPaths.Count, sandbox.ListPaths.Count));
        Assert.Contains("Date: 2026-10-23", Input(firstModel, 1));
        Assert.Contains("selected instructions", Input(firstModel, 1));
        Assert.DoesNotContain("changed instructions", Input(firstModel, 1));
        var secondModel = new RecordingModel();
        await using var second = await CreateAgentAsync(secondModel, plugin);
        await second.InvokeAsync("independent");
        Assert.Equal(calls.PlatformCalls + 1, sandbox.PlatformCalls);
        Assert.Equal(calls.CwdCalls + 1, sandbox.CwdCalls);
        Assert.Contains("changed instructions", Input(secondModel));
        AssertHistoryClean(first);
        AssertHistoryClean(second);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("list")]
    public async Task RequestedCancellationUsesTheHookTokenAndDoesNotCacheAPartialGather(string phase)
    {
        using var caller = new CancellationTokenSource();
        var sandbox = new ConfigurableEnvironment();
        var recorder = new ModelHookTokens();
        var firstAttempt = true;
        CancellationToken observed = default;
        void CancelFirst(string path, CancellationToken token)
        {
            if (!firstAttempt) return;
            firstAttempt = false;
            observed = token;
            caller.Cancel();
            throw new OperationCanceledException(token);
        }
        if (phase == "read") sandbox.BeforeRead = CancelFirst;
        else sandbox.BeforeList = CancelFirst;
        var model = new RecordingModel();
        await using var agent = await CreateAgentAsync(model, new EnvironmentContext(sandbox), recorder);

        var canceled = await agent.InvokeAsync("first", cancellationToken: caller.Token);

        Assert.Equal(StopReasons.Cancelled, canceled.StopReason);
        Assert.Empty(model.Requests);
        Assert.Equal(Assert.Single(recorder.Tokens), observed);
        Assert.True(observed.IsCancellationRequested);
        Assert.True(caller.Token.IsCancellationRequested);
        var retried = await agent.InvokeAsync("retry");
        Assert.Equal(StopReasons.EndTurn, retried.StopReason);
        Assert.Equal(2, sandbox.CwdCalls);
        Assert.Equal(2, sandbox.PlatformCalls);
        Assert.Equal(2, sandbox.ReadPaths.Count);
        Assert.Equal(phase == "read" ? 3 : 4, sandbox.ListPaths.Count);
        Assert.Contains("selected instructions", Input(model));
        Assert.Contains("project/src/AGENTS.md", Input(model));
        Assert.Equal(2, recorder.Tokens.Count);
        Assert.NotEqual(recorder.Tokens[0], recorder.Tokens[1]);
        AssertHistoryClean(agent);
    }

    [Theory]
    [InlineData("/selected")]
    [InlineData("C:\\selected")]
    public async Task SuccessfulCwdKeepsItsAbsolutePathAndDisplayedRelativeLinks(string directory)
    {
        var sandbox = AbsoluteEnvironment(directory);
        var model = new RecordingModel();
        await using var agent = await CreateAgentAsync(model, new EnvironmentContext(sandbox));

        await agent.InvokeAsync("inspect");

        var separator = directory[0] == '/' ? "/" : "\\";
        Assert.Equal(new[] { directory + separator + "AGENTS.md" }, sandbox.ReadPaths);
        Assert.Equal(new[] { directory, directory + separator + "project", directory + separator + "project" + separator + "src" }, sandbox.ListPaths);
        Assert.Contains("Working directory: " + directory, Input(model));
        Assert.Contains("project/src/AGENTS.md", Input(model));
        Assert.Contains("project/README.md", Input(model));
        AssertHistoryClean(agent);
    }

    [Fact]
    public async Task RelativeDiscoveryPreservesDepthSkippedDirectoriesAndTheInstructionCap()
    {
        var sandbox = new ConfigurableEnvironment { RootText = new string('a', EnvironmentContext.AgentsMarkdownCap + 1) };
        sandbox.Tree.Clear();
        sandbox.Tree.Add(".", [new("AGENTS.md", false), new("a", true), new("node_modules", true), new(".hidden", true)]);
        sandbox.Tree.Add("a", [new("AGENTS.md", false), new("README.md", false), new("b", true)]);
        sandbox.Tree.Add("a/b", [new("AGENTS.md", false), new("c", true)]);
        var model = new RecordingModel();
        await using var agent = await CreateAgentAsync(model, new EnvironmentContext(sandbox));

        await agent.InvokeAsync("inspect");

        var input = Input(model);
        const string marker = "\n… (truncated — read the full file if you need the rest)";
        Assert.Contains("<AGENTS.md>\n" + new string('a', EnvironmentContext.AgentsMarkdownCap) + marker + "\n</AGENTS.md>", input);
        Assert.DoesNotContain(new string('a', EnvironmentContext.AgentsMarkdownCap + 1), input);
        Assert.Contains("Other AGENTS.md files nearby (read as needed): a/AGENTS.md, a/b/AGENTS.md", input);
        Assert.Equal(new[] { ".", "a", "a/b" }, sandbox.ListPaths);
        Assert.DoesNotContain("node_modules", input);
        Assert.DoesNotContain(".hidden", input);
        Assert.DoesNotContain("a/b/c", input);
        AssertHistoryClean(agent);
    }

    private static Task<Agent> CreateAgentAsync(RecordingModel model, EnvironmentContext plugin, IPlugin? preceding = null) =>
        Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Plugins = preceding is null ? [plugin] : [preceding, plugin],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

    private static string Input(RecordingModel model, int index = 0) =>
        string.Join("\n", model.Requests[index].Messages.Select(message => message.Text));

    private static void AssertHistoryClean(Agent agent)
    {
        Assert.All(agent.Messages, message => Assert.DoesNotContain("<system-reminder>", message.Text));
        Assert.All(agent.Messages, message => Assert.DoesNotContain("selected instructions", message.Text));
        Assert.All(agent.Messages, message => Assert.DoesNotContain("changed instructions", message.Text));
    }

    private static ConfigurableEnvironment AbsoluteEnvironment(string directory)
    {
        var separator = directory[0] == '/' ? "/" : "\\";
        var sandbox = new ConfigurableEnvironment { WorkingDirectory = directory, ExpectedReadPath = directory + separator + "AGENTS.md" };
        sandbox.Tree.Clear();
        sandbox.Tree.Add(directory, [new("AGENTS.md", false), new("README.md", false), new("project", true)]);
        sandbox.Tree.Add(directory + separator + "project", [new("README.md", false), new("src", true)]);
        sandbox.Tree.Add(directory + separator + "project" + separator + "src", [new("AGENTS.md", false)]);
        return sandbox;
    }

    private static async Task AssertRelativeDiscoveryAsync(SelectedEnvironment sandbox)
    {
        var model = new RecordingModel();
        var plugin = new EnvironmentContext(sandbox, () => new DateTime(2026, 10, 22), "test-relative-environment");
        await using var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Plugins = [plugin],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

        var result = await agent.InvokeAsync("inspect the selected environment");

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        var request = Assert.Single(model.Requests);
        var input = string.Join("\n", request.Messages.Select(message => message.Text));
        Assert.Contains("<AGENTS.md>\nselected sandbox instructions\n</AGENTS.md>", input);
        Assert.Contains("Other AGENTS.md files nearby (read as needed): project/src/AGENTS.md", input);
        Assert.Contains("README files nearby (read as needed): README.md, project/README.md", input);
        Assert.Contains("Platform: SelectedOS", input);
        Assert.Contains("Date: 2026-10-22", input);
        Assert.DoesNotContain("Working directory:", input);
        Assert.DoesNotContain("./project", input);
        Assert.Equal(new[] { "AGENTS.md" }, sandbox.ReadPaths);
        Assert.Equal(new[] { ".", "project", "project/src" }, sandbox.ListPaths);
        Assert.Equal(1, sandbox.CwdCalls);
        Assert.All(agent.Messages, message => Assert.DoesNotContain("<system-reminder>", message.Text));
        Assert.All(agent.Messages, message => Assert.DoesNotContain("selected sandbox instructions", message.Text));
    }

    private sealed class SelectedEnvironment(string? workingDirectory, bool throwsOnCwd = false) : IEnvironmentSandbox
    {
        public List<string> ReadPaths { get; } = [];
        public List<string> ListPaths { get; } = [];
        public int CwdCalls { get; private set; }

        public ValueTask<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CwdCalls++;
            if (throwsOnCwd) throw new IOException("The selected sandbox has no working-directory probe.");
            return new(workingDirectory!);
        }

        public ValueTask<string?> GetPlatformAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new("SelectedOS");
        }

        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadPaths.Add(path);
            if (path != "AGENTS.md") throw new IOException("Unexpected selected-sandbox read path: " + path);
            return new("selected sandbox instructions");
        }

        public ValueTask<IReadOnlyList<SandboxEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListPaths.Add(path);
            IReadOnlyList<SandboxEntry> entries = path switch
            {
                "." => [new("AGENTS.md", false), new("README.md", false), new("project", true)],
                "project" => [new("README.md", false), new("src", true)],
                "project/src" => [new("AGENTS.md", false)],
                _ => throw new IOException("Unexpected selected-sandbox listing path: " + path),
            };
            return new(entries);
        }

        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Environment discovery cannot write to the selected sandbox.");
    }

    private sealed class RecordingModel : IModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private sealed class ConfigurableEnvironment : IEnvironmentSandbox
    {
        public string? WorkingDirectory { get; init; }
        public string ExpectedReadPath { get; init; } = "AGENTS.md";
        public string RootText { get; set; } = "selected instructions";
        public Exception? PlatformFailure { get; set; }
        public Exception? CwdFailure { get; init; }
        public Exception? ReadFailure { get; set; }
        public Action<string, CancellationToken>? BeforeRead { get; set; }
        public Action<string, CancellationToken>? BeforeList { get; set; }
        public int PlatformCalls { get; private set; }
        public int CwdCalls { get; private set; }
        public List<string> ReadPaths { get; } = [];
        public List<string> ListPaths { get; } = [];
        public HashSet<string> ListFailures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<SandboxEntry>> Tree { get; } = new(StringComparer.Ordinal)
        {
            ["."] = [new("AGENTS.md", false), new("README.md", false), new("project", true)],
            ["project"] = [new("README.md", false), new("src", true)],
            ["project/src"] = [new("AGENTS.md", false)],
        };

        public ValueTask<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CwdCalls++;
            if (CwdFailure is not null) throw CwdFailure;
            return new(WorkingDirectory!);
        }

        public ValueTask<string?> GetPlatformAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlatformCalls++;
            if (PlatformFailure is not null) throw PlatformFailure;
            return new("SelectedOS");
        }

        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadPaths.Add(path);
            BeforeRead?.Invoke(path, cancellationToken);
            if (ReadFailure is not null) throw ReadFailure;
            if (path != ExpectedReadPath) throw new IOException("Unexpected selected-sandbox read: " + path);
            return new(RootText);
        }

        public ValueTask<IReadOnlyList<SandboxEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListPaths.Add(path);
            BeforeList?.Invoke(path, cancellationToken);
            if (ListFailures.Contains(path)) throw new IOException("Selected listing unavailable: " + path);
            if (!Tree.TryGetValue(path, out var entries)) throw new IOException("Unexpected selected-sandbox listing: " + path);
            return new(entries);
        }

        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Environment discovery cannot write.");
    }

    private sealed class TextOnlyEnvironment : ISandbox
    {
        public bool ReadAttempted { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            throw new IOException("No environment capability.");
        }

        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Environment discovery cannot write.");
    }

    private sealed class ModelHookTokens : IPlugin
    {
        public string Name => "model-hook-token-recorder";
        public List<CancellationToken> Tokens { get; } = [];
        public IReadOnlyList<ITool> GetTools() => [];
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            agent.Hooks.Register<BeforeModelCallEvent>((value, token) =>
            {
                if (ReferenceEquals(value.Agent, agent)) Tokens.Add(token);
                return default;
            });
            return default;
        }
    }
}
