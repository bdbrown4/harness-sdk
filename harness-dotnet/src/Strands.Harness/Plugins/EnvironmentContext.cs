using System.Globalization;
using Strands.Agents;

namespace Strands.Harness.Plugins;

/// <summary>The unit used to cap injected project instructions.</summary>
public enum EnvironmentCharacterCounting
{
    /// <summary>Count UTF16 code units, matching TypeScript string slicing and the native default.</summary>
    Utf16CodeUnits,
    /// <summary>Count Unicode code points, matching Python string slicing; combining marks count separately.</summary>
    UnicodeCodePoints
}

/// <summary>Configuration for environment context injection.</summary>
public sealed record EnvironmentContextConfig
{
    /// <summary>The plugin name used for duplicate detection.</summary>
    public string Name { get; init; } = "strands:environment";
    /// <summary>The unit for the16000-character AGENTS.md cap. Defaults to UTF16 code units.</summary>
    public EnvironmentCharacterCounting CharacterCounting { get; init; }
}

/// <summary>Injects sandbox environment details and project instructions on fresh user turns, using per-agent memoized discovery.</summary>
public sealed class EnvironmentContext : IPlugin
{
    /// <summary>The maximum number of AGENTS.md units, counted by the configured policy, injected before a truncation marker.</summary>
    public const int AgentsMarkdownCap = 16000;
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal)
    {
        "node_modules", "dist", "build", "target", "__pycache__", "venv", "site-packages", "vendor",
    };
    private readonly ISandbox _sandbox;
    private readonly Func<DateTime> _today;
    private readonly EnvironmentCharacterCounting _characterCounting;

    /// <summary>Creates environment injection through the supplied sandbox, or a local filesystem when omitted.</summary>
    public EnvironmentContext(ISandbox? sandbox = null, EnvironmentContextConfig? config = null)
        : this(sandbox ?? new LocalFileSandbox(), () => DateTime.Today, config ?? new()) { }

    internal EnvironmentContext(ISandbox sandbox, Func<DateTime> today, string name)
        : this(sandbox, today, new EnvironmentContextConfig { Name = name }) { }

    private EnvironmentContext(ISandbox sandbox, Func<DateTime> today, EnvironmentContextConfig config)
    {
        Guard.NotNull(sandbox, nameof(sandbox));
        Guard.NotNull(today, nameof(today));
        var name = config.Name;
        Guard.NotBlank(name, nameof(name));
        if (!Enum.IsDefined(typeof(EnvironmentCharacterCounting), config.CharacterCounting))
            throw new ArgumentOutOfRangeException(nameof(config), "Unknown environment character-counting policy.");
        _sandbox = sandbox;
        _today = today;
        _characterCounting = config.CharacterCounting;
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();

    /// <inheritdoc />
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        Gathered? gathered = null;
        agent.Hooks.Register<BeforeModelCallEvent>(async (value, token) =>
        {
            if (!ReferenceEquals(value.Agent, agent) || !ContextInjection.IsUserTurn(value.Request.Messages)) return;
            try
            {
                gathered ??= await GatherAsync(token).ConfigureAwait(false);
                value.Request = ContextInjection.Fold(value.Request, Render(gathered, _today()));
            }
            catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested))
            {
                HarnessLogging.Warning("plugin=<{Plugin}> | environment context could not be rendered; skipping injection", Name);
            }
        });
        return default;
    }

    private async Task<Gathered> GatherAsync(CancellationToken cancellationToken)
    {
        if (_sandbox is not IEnvironmentSandbox environment) return new(null, null, null, [], []);
        var platform = await BestEffort(() => environment.GetPlatformAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        var workingDirectory = await BestEffort(() => environment.GetWorkingDirectoryAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        // Relative file support remains best effort within the selected sandbox.
        var root = workingDirectory ?? ".";
        var instructions = await BestEffort(() => _sandbox.ReadTextAsync(Join(root, "AGENTS.md"), cancellationToken), cancellationToken).ConfigureAwait(false);
        instructions = TruncateInstructions(instructions);
        var agents = new List<string>();
        var readmes = new List<string>();
        await DiscoverAsync(environment, root, string.Empty, 0, agents, readmes, cancellationToken).ConfigureAwait(false);
        return new(platform, workingDirectory, instructions, agents, readmes);
    }

    private string? TruncateInstructions(string? text)
    {
        if (text is null || text.Length <= AgentsMarkdownCap) return text;
        var end = AgentsMarkdownCap;
        if (_characterCounting == EnvironmentCharacterCounting.UnicodeCodePoints)
        {
            end = 0;
            for (var count = 0; count < AgentsMarkdownCap && end < text.Length; count++, end++)
                if (char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1])) end++;
        }
        return end == text.Length ? text : text.Substring(0, end) + "\n… (truncated — read the full file if you need the rest)";
    }

    private static async Task DiscoverAsync(IEnvironmentSandbox sandbox, string root, string relative, int depth,
        List<string> agents, List<string> readmes, CancellationToken cancellationToken)
    {
        var entries = await BestEffort(() => sandbox.ListFilesAsync(relative.Length == 0 ? root : Join(root, relative), cancellationToken), cancellationToken).ConfigureAwait(false);
        if (entries is null) return;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                if (depth < 2 && !SkippedDirectories.Contains(entry.Name) && !entry.Name.StartsWith(".", StringComparison.Ordinal))
                    await DiscoverAsync(sandbox, root, path, depth + 1, agents, readmes, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (entry.Name == "AGENTS.md" && relative.Length > 0) agents.Add(path);
            if (entry.Name == "README.md") readmes.Add(path);
        }
    }

    private static async Task<T?> BestEffort<T>(Func<ValueTask<T>> read, CancellationToken cancellationToken) where T : class?
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested)) { return default; }
    }

    private static string Render(Gathered gathered, DateTime today)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(gathered.Platform)) lines.Add("Platform: " + gathered.Platform);
        lines.Add("Date: " + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(gathered.WorkingDirectory)) lines.Add("Working directory: " + gathered.WorkingDirectory);
        var sections = new List<string> { "<environment>\n" + string.Join("\n", lines) + "\n</environment>" };
        if (!string.IsNullOrEmpty(gathered.Instructions)) sections.Add("<AGENTS.md>\n" + gathered.Instructions + "\n</AGENTS.md>");
        if (gathered.OtherAgents.Count > 0) sections.Add("Other AGENTS.md files nearby (read as needed): " + string.Join(", ", gathered.OtherAgents));
        if (gathered.Readmes.Count > 0) sections.Add("README files nearby (read as needed): " + string.Join(", ", gathered.Readmes));
        return "<system-reminder>\n" + string.Join("\n\n", sections) + "\n</system-reminder>";
    }

    private static string Join(string root, string relative)
    {
        if (root == ".") return relative;
        var separator = root.IndexOf('\\') >= 0 && !root.StartsWith("/", StringComparison.Ordinal) ? "\\" : "/";
        return root.TrimEnd('/', '\\') + separator + relative.Replace("/", separator);
    }

    private sealed record Gathered(string? Platform, string? WorkingDirectory, string? Instructions, IReadOnlyList<string> OtherAgents, IReadOnlyList<string> Readmes);
}
