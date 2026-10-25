using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Files;

namespace Strands.Agents.Skills;

/// <summary>A native Skill instance, filesystem path, or HTTPS SKILL.md URL.</summary>
public sealed class SkillSource
{
    /// <summary>Creates a path or HTTPS URL source.</summary>
    public SkillSource(string location)
    {
        Guard.NotNullOrWhiteSpace(location, nameof(location)); Location = location;
    }
    /// <summary>Creates a source from an immutable native skill.</summary>
    public SkillSource(Skill skill)
    {
        Guard.NotNull(skill, nameof(skill)); Skill = skill;
    }
    /// <summary>The path or URL, or null for a native skill.</summary>
    public string? Location { get; }
    /// <summary>The native skill, or null for a path or URL.</summary>
    public Skill? Skill { get; }
    /// <summary>Uses a string path or URL as a source.</summary>
    public static implicit operator SkillSource(string location) => new(location);
    /// <summary>Uses an immutable native skill as a source.</summary>
    public static implicit operator SkillSource(Skill skill) => new(skill);
}

/// <summary>Sources and progressive-disclosure policy for the skills plugin.</summary>
public sealed record AgentSkillsOptions
{
    /// <summary>Native skills, filesystem paths, or HTTPS URLs, processed in source order.</summary>
    public required IReadOnlyList<SkillSource> Skills { get; init; }
    /// <summary>Maximum resource filenames shown during activation; defaults to 20.</summary>
    public int MaxResourceFiles { get; init; } = 20;
    /// <summary>Whether cosmetic skill validation issues reject a loaded skill.</summary>
    public bool Strict { get; init; }
    /// <summary>The JSON application-state key containing activation state.</summary>
    public string StateKey { get; init; } = "agent_skills";
    /// <summary>An explicit filesystem, otherwise the agent filesystem or standalone host filesystem.</summary>
    public IReadOnlyFileSystem? FileSystem { get; init; }
    /// <summary>An optional caller-owned HTTP client for HTTPS sources.</summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>Progressively discloses skills through metadata context and an activation tool.</summary>
public sealed class AgentSkills : IPlugin
{
    private readonly AgentSkillsOptions _options;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly object _gate = new();
    private Catalog _catalog;
    /// <summary>Copies the configured sources and validates plugin policy.</summary>
    public AgentSkills(AgentSkillsOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(options.Skills, nameof(options.Skills));
        Guard.NotNullOrWhiteSpace(options.StateKey, nameof(options.StateKey));
        if (options.MaxResourceFiles < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxResourceFiles must be positive.");
        if (options.Skills.Any(source => source is null)) throw new ArgumentException("Skill sources cannot contain null.", nameof(options));
        _options = options with { Skills = Array.AsReadOnly(options.Skills.ToArray()) };
        _catalog = new(_options.Skills);
        _tools = Array.AsReadOnly(new ITool[] { new FunctionTool(new("skills", "Activate a skill to load its full instructions. Use this tool to load the complete instructions for a skill listed in the available_skills section of your system prompt.", JsonSerializer.SerializeToElement(new { type = "object", properties = new { skill_name = new { type = "string", minLength = 1, description = "Name of the skill to activate" } }, required = new[] { "skill_name" } })), ActivateAsync) });
    }
    /// <inheritdoc />
    public string Name => "strands:agent-skills";
    /// <inheritdoc />
    public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        var available = await EnsureAgentSkillsAsync(agent, cancellationToken).ConfigureAwait(false);
        if (available.Count == 0) SdkLogging.Warning("no skills were loaded, the agent will have no skills available");
        agent.Hooks.Register<BeforeModelCallEvent>(async (value, token) =>
        {
            if (!ReferenceEquals(value.Agent, agent)) return;
            var skills = await EnsureAgentSkillsAsync(agent, token).ConfigureAwait(false);
            var xml = SkillsXml(skills);
            if (value.Request.SystemPromptContent is { } content)
                value.Request = value.Request with { SystemPromptContent = content.Concat(new ContentBlock[] { new TextBlock(xml) }).ToArray() };
            else
                value.Request = value.Request with { SystemPrompt = string.IsNullOrEmpty(value.Request.SystemPrompt) ? xml : value.Request.SystemPrompt + "\n\n" + xml };
        });
    }
    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools() => _tools;
    /// <summary>Returns available skills; filesystem sources are specific to the supplied agent.</summary>
    public async Task<IReadOnlyList<Skill>> GetAvailableSkillsAsync(Agent? agent = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Catalog catalog;
        lock (_gate) catalog = _catalog;
        if (agent is not null)
        {
            Lazy<Task<IReadOnlyList<Skill>>>? entry;
            lock (_gate) catalog.Agents.TryGetValue(agent, out entry);
            if (entry is not null) return await AwaitAsync(entry.Value, cancellationToken).ConfigureAwait(false);
        }
        return await LoadBaseSkillsAsync(catalog, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the skill catalog and invalidates agent filesystem caches without changing persisted activation state.</summary>
    /// <param name="skills">Native skills, paths, or HTTPS sources for subsequent queries and invocations.</param>
    public void SetAvailableSkills(IReadOnlyList<SkillSource> skills)
    {
        Guard.NotNull(skills, nameof(skills));
        if (skills.Any(source => source is null)) throw new ArgumentException("Skill sources cannot contain null.", nameof(skills));
        var catalog = new Catalog(Array.AsReadOnly(skills.ToArray()));
        lock (_gate) _catalog = catalog;
    }

    private async Task<IReadOnlyList<Skill>> EnsureAgentSkillsAsync(Agent agent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Catalog catalog;
        Lazy<Task<IReadOnlyList<Skill>>> entry;
        lock (_gate)
        {
            catalog = _catalog;
            entry = catalog.Agents.GetValue(agent, key => new(() => LoadSkillsAsync(catalog, key, cancellationToken), true));
        }
        try
        {
            var skills = await entry.Value.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return skills;
        }
        catch
        {
            lock (_gate)
                if ((entry.Value.IsFaulted || entry.Value.IsCanceled) && catalog.Agents.TryGetValue(agent, out var current) && ReferenceEquals(current, entry)) catalog.Agents.Remove(agent);
            throw;
        }
    }
    /// <summary>Returns activated skill names in activation order, most recent last.</summary>
    public IReadOnlyList<string> GetActivatedSkills(Agent agent)
    {
        Guard.NotNull(agent, nameof(agent));
        var data = agent.State.Get(_options.StateKey);
        if (data is not { ValueKind: JsonValueKind.Object } || !data.Value.TryGetProperty("activatedSkills", out var activated)) return [];
        if (activated.ValueKind != JsonValueKind.Array || activated.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
            throw new InvalidOperationException("Skill activation state must contain an array of names.");
        return Array.AsReadOnly(activated.EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    private async Task<IReadOnlyList<Skill>> LoadBaseSkillsAsync(Catalog catalog, CancellationToken cancellationToken)
    {
        await catalog.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (catalog.BaseSkills is not null) return catalog.BaseSkills;
            var skills = new List<Skill>();
            foreach (var source in catalog.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Skill is { } supplied) Add(skills, supplied);
                else if (source.Location!.StartsWith("https://", StringComparison.Ordinal))
                {
                    try { Add(skills, await Skill.FromUrlAsync(source.Location, _options.HttpClient, _options.Strict, cancellationToken).ConfigureAwait(false)); }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        SdkLogging.Warning("url=<{Url}>, error_type=<{ErrorType}> | failed to load skill from URL", source.Location, exception.GetType().Name);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            catalog.BaseSkills = Array.AsReadOnly(skills.ToArray());
            return catalog.BaseSkills;
        }
        finally { catalog.Gate.Release(); }
    }

    private async Task<IReadOnlyList<Skill>> LoadSkillsAsync(Catalog catalog, Agent agent, CancellationToken cancellationToken)
    {
        var skills = new List<Skill>(await LoadBaseSkillsAsync(catalog, cancellationToken).ConfigureAwait(false));
        foreach (var source in catalog.Sources.Where(source => source.Location is not null && !source.Location.StartsWith("https://", StringComparison.Ordinal)))
            foreach (var skill in await SkillFileSources.DiscoverAsync(source.Location!, FileSystem(agent), _options.Strict, cancellationToken).ConfigureAwait(false)) Add(skills, skill);
        return Array.AsReadOnly(skills.ToArray());
    }

    private static void Add(List<Skill> skills, Skill skill)
    {
        var previous = skills.FindIndex(candidate => string.Equals(candidate.Name, skill.Name, StringComparison.Ordinal));
        if (previous < 0) skills.Add(skill);
        else { SdkLogging.Warning("name=<{Name}> | duplicate skill name, overwriting previous skill", skill.Name); skills[previous] = skill; }
    }

    private static async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) return await task.ConfigureAwait(false);
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => canceled.TrySetResult(true));
        if (!ReferenceEquals(await Task.WhenAny(task, canceled.Task).ConfigureAwait(false), task)) cancellationToken.ThrowIfCancellationRequested();
        return await task.ConfigureAwait(false);
    }

    private async ValueTask<string> ActivateAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken)
    {
        var name = input.GetProperty("skill_name").GetString()!;
        var skills = await EnsureAgentSkillsAsync(context.Agent, cancellationToken).ConfigureAwait(false);
        var found = skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.Ordinal));
        if (found is null) return "Skill '" + name + "' not found. Available skills: " + string.Join(", ", skills.Select(skill => skill.Name));
        var data = context.Agent.State.Get(_options.StateKey);
        if (data is not null && data.Value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Skill application state must be an object.");
        var state = data?.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal) ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        state["activatedSkills"] = JsonSerializer.SerializeToElement(GetActivatedSkills(context.Agent).Where(value => !string.Equals(value, name, StringComparison.Ordinal)).Concat(new[] { name }).ToArray());
        context.Agent.State.Set(_options.StateKey, state);
        if (found.Instructions.Length == 0) return "Skill '" + name + "' activated (no instructions available).";
        var metadata = new List<string>();
        if (found.AllowedTools is { Count: > 0 }) metadata.Add("Allowed tools: " + string.Join(", ", found.AllowedTools));
        if (found.Compatibility is not null) metadata.Add("Compatibility: " + found.Compatibility);
        if (found.Path is not null) metadata.Add("Location: " + SkillFileSources.Join(found.Path, "SKILL.md"));
        var result = found.Instructions + (metadata.Count == 0 ? string.Empty : "\n\n---\n" + string.Join("\n", metadata));
        if (found.Path is not null)
        {
            var resources = await SkillFileSources.ResourcesAsync(found.Path, FileSystem(context.Agent), _options.MaxResourceFiles, cancellationToken).ConfigureAwait(false);
            if (resources.Count > 0) result += "\n\nAvailable resources:\n" + string.Join("\n", resources.Select(resource => "  " + resource));
        }
        return result;
    }

    private static string SkillsXml(IReadOnlyList<Skill> skills)
    {
        if (skills.Count == 0) return "<available_skills>\nNo skills are currently available.\n</available_skills>";
        var lines = new List<string> { "<available_skills>" };
        foreach (var skill in skills)
        {
            lines.Add("<skill>");
            lines.Add("<name>" + EscapeXml(skill.Name) + "</name>");
            lines.Add("<description>" + EscapeXml(skill.Description) + "</description>");
            if (skill.Path is not null) lines.Add("<location>" + EscapeXml(skill.Path + "/SKILL.md") + "</location>");
            lines.Add("</skill>");
        }
        lines.Add("</available_skills>"); return string.Join("\n", lines);
    }
    private static string EscapeXml(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    private IReadOnlyFileSystem FileSystem(Agent agent) => _options.FileSystem ?? agent.FileSystem ?? new LocalFileSystem();

    private sealed class Catalog(IReadOnlyList<SkillSource> sources)
    {
        internal IReadOnlyList<SkillSource> Sources { get; } = sources;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal IReadOnlyList<Skill>? BaseSkills { get; set; }
        internal ConditionalWeakTable<Agent, Lazy<Task<IReadOnlyList<Skill>>>> Agents { get; } = new();
    }
}
