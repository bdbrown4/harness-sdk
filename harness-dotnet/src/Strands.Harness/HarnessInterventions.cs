using System.Text.Json;
using Strands.Agents.Files;
using Strands.Agents.Interventions;

namespace Strands.Harness;

/// <summary>A preset or policy string, or a caller-owned native intervention handler.</summary>
public sealed class HarnessInterventionValue
{
    /// <summary>Creates a preset, natural-language policy, or .cedar file reference.</summary>
    public HarnessInterventionValue(string value) { Guard.NotNull(value, nameof(value)); Value = value; }
    /// <summary>Preserves a caller-owned handler, including configured steering and Cedar handlers.</summary>
    public HarnessInterventionValue(InterventionHandler value) { Guard.NotNull(value, nameof(value)); Value = value; }
    internal object Value { get; }
    /// <summary>Converts a preset, prose policy, or .cedar path into a typed setting.</summary>
    public static implicit operator HarnessInterventionValue(string value) => new(value);
    /// <summary>Converts a caller-owned native handler into a typed setting.</summary>
    public static implicit operator HarnessInterventionValue(InterventionHandler value) => new(value);
}

/// <summary>Approval callbacks and optional Cedar integration used when resolving harness intervention settings.</summary>
public sealed record HarnessInterventionOptions
{
    /// <summary>A caller-owned approval callback. Null uses interrupt/resume; use HumanApprovalPrompts.Stdio explicitly for console input.</summary>
    public Func<string, CancellationToken, ValueTask<JsonElement?>>? Ask { get; init; }
    /// <summary>The standalone resolver's file backend; null uses the local filesystem. Harness.CreateAsync always binds its selected filesystem.</summary>
    public IReadOnlyFileSystem? FileSystem { get; init; }
    /// <summary>Constructs a caller-owned Cedar handler from policy text read through the selected filesystem. The optional genuine Cedar package supplies the evaluator; no evaluator is substituted.</summary>
    public Func<string, InterventionHandler>? CedarPolicyFactory { get; init; }
}

/// <summary>Resolves the shared off/ask/smart/prose/.cedar grammar into real SDK intervention handlers.</summary>
public static class HarnessInterventions
{
    /// <summary>Resolves ordered settings and rejects duplicate handler names. Raw handlers remain caller-owned; .cedar files use the selected filesystem.</summary>
    public static async Task<IReadOnlyList<InterventionHandler>> ResolveAsync(IReadOnlyList<HarnessInterventionValue>? values = null, HarnessInterventionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        var handlers = new List<InterventionHandler>();
        foreach (var setting in values?.ToArray() ?? Array.Empty<HarnessInterventionValue>())
        {
            Guard.NotNull(setting, nameof(values));
            cancellationToken.ThrowIfCancellationRequested();
            if (setting.Value is InterventionHandler supplied) { handlers.Add(supplied); continue; }
            var policy = (string)setting.Value;
            var text = policy.Trim();
            if (text == "off") continue;
            if (text.EndsWith(".cedar", StringComparison.Ordinal))
            {
                var factory = options.CedarPolicyFactory ?? throw new NotSupportedException("A .cedar intervention requires the optional genuine Strands.Agents.Cedar evaluator. Bind CedarPolicyFactory to construct its handler from the policy text; no evaluator or host filesystem is substituted.");
                var content = await (options.FileSystem ?? new LocalFileSystem()).ReadTextAsync(text, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                handlers.Add(factory(content) ?? throw new InvalidOperationException("CedarPolicyFactory returned null."));
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            var classifier = text == "ask" ? null : LlmRiskClassifier.Create(text == "smart" ? null : new() { SystemPrompt = policy });
            handlers.Add(new HumanInTheLoop(new() { Ask = options.Ask, Classifier = classifier }));
        }
        ValidateNames(handlers);
        return handlers.AsReadOnly();
    }

    internal static void ValidateNames(IReadOnlyList<InterventionHandler> handlers)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            Guard.NotNull(handler, nameof(handlers));
            Guard.NotBlank(handler.Name, nameof(handlers));
            if (!names.Add(handler.Name)) throw new ArgumentException("Two interventions share handler name '" + handler.Name + "'. Layer different handler kinds rather than two with the same name.", nameof(handlers));
        }
    }
}
