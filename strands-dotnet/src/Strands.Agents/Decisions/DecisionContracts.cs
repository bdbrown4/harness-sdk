using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Decisions;

/// <summary>A closed question; null Choices selects a boolean answer instead of a string choice.</summary>
/// <param name="Text">The nonempty question text.</param>
/// <param name="Choices">The allowed distinct, nonempty string values, or null for boolean.</param>
public sealed record DecisionQuestion(string Text, IReadOnlyList<string>? Choices = null)
{
    /// <summary>Whether the uncertainty sentinel is allowed; defaults to true.</summary>
    public bool AllowUncertain { get; init; } = true;
    /// <summary>An optional finite threshold in [0,1] for confidence-aware implementations. LLMDecisionModel validates but does not enforce this value.</summary>
    public double? MinConfidence { get; init; }
}

/// <summary>A decision model declined to commit to an answer.</summary>
/// <param name="Reason">An optional explanation.</param>
public sealed record Uncertain([property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null)
{
    /// <summary>The true marker used when serializing the source-compatible sentinel.</summary>
    [JsonPropertyName("uncertain")]
    [JsonPropertyOrder(-1)]
    public bool IsUncertain => true;
    /// <inheritdoc />
    public override string ToString() => Reason is null ? "Uncertain" : "Uncertain(" + Reason + ")";
}

/// <summary>The concrete value carried by a decision answer.</summary>
public enum DecisionAnswerKind
{
    /// <summary>A boolean answer.</summary>
    Boolean,
    /// <summary>One of the question's string choices.</summary>
    Choice,
    /// <summary>The model declined to commit.</summary>
    Uncertain
}

/// <summary>A typed boolean, choice or uncertainty value serialized as its underlying source value.</summary>
[JsonConverter(typeof(DecisionAnswerJsonConverter))]
public sealed class DecisionAnswer
{
    private DecisionAnswer(DecisionAnswerKind kind, bool? boolean = null, string? choice = null, Uncertain? uncertain = null)
    { Kind = kind; BooleanValue = boolean; ChoiceValue = choice; UncertainValue = uncertain; }
    /// <summary>The active answer representation.</summary>
    public DecisionAnswerKind Kind { get; }
    /// <summary>The boolean value, or null for another answer kind.</summary>
    public bool? BooleanValue { get; }
    /// <summary>The string choice, or null for another answer kind.</summary>
    public string? ChoiceValue { get; }
    /// <summary>The uncertainty sentinel, or null for another answer kind.</summary>
    public Uncertain? UncertainValue { get; }
    /// <summary>Creates a boolean answer.</summary>
    /// <param name="value">The selected boolean.</param>
    /// <returns>A boolean answer.</returns>
    public static DecisionAnswer FromBoolean(bool value) => new(DecisionAnswerKind.Boolean, boolean: value);
    /// <summary>Creates a string choice answer.</summary>
    /// <param name="value">The selected nonempty choice.</param>
    /// <returns>A choice answer.</returns>
    public static DecisionAnswer FromChoice(string value)
    {
        Guard.NotNull(value, nameof(value));
        if (value.Length == 0) throw new ArgumentException("A choice cannot be empty.", nameof(value));
        return new(DecisionAnswerKind.Choice, choice: value);
    }
    /// <summary>Creates an uncertainty answer.</summary>
    /// <param name="reason">An optional explanation.</param>
    /// <returns>An uncertainty answer.</returns>
    public static DecisionAnswer FromUncertain(string? reason = null) => new(DecisionAnswerKind.Uncertain, uncertain: new(reason));
}

/// <summary>Timing reported by the provider, or measured around the direct model operation when absent.</summary>
/// <param name="Latency">The nonnegative time spent in the model operation.</param>
public sealed record DecisionMetadata(TimeSpan Latency);

/// <summary>An immutable answer map, provider usage and timing for one decision call.</summary>
public sealed class DecisionResult
{
    /// <summary>Copies an answer map into an immutable result.</summary>
    /// <param name="answers">Answers keyed by the original question identifiers.</param>
    /// <param name="metadata">The model operation's nonnegative timing.</param>
    /// <param name="usage">Optional provider usage; absence is distinct from zero.</param>
    public DecisionResult(IReadOnlyDictionary<string, DecisionAnswer> answers, DecisionMetadata metadata, Usage? usage = null)
    {
        Guard.NotNull(answers, nameof(answers)); Guard.NotNull(metadata, nameof(metadata));
        if (metadata.Latency < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(metadata));
        Answers = new ReadOnlyDictionary<string, DecisionAnswer>(answers.ToDictionary(pair => pair.Key, pair => pair.Value ?? throw new ArgumentException("Answers cannot contain null.", nameof(answers)), StringComparer.Ordinal));
        Metadata = metadata; Usage = usage;
    }
    /// <summary>The copied, read-only answer map.</summary>
    public IReadOnlyDictionary<string, DecisionAnswer> Answers { get; }
    /// <summary>The provider timing or local elapsed-time fallback.</summary>
    public DecisionMetadata Metadata { get; }
    /// <summary>Optional provider token accounting.</summary>
    public Usage? Usage { get; }
}

internal sealed class DecisionAnswerJsonConverter : JsonConverter<DecisionAnswer>
{
    public override DecisionAnswer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return DecisionAnswer.FromBoolean(reader.GetBoolean());
        if (reader.TokenType == JsonTokenType.String) return DecisionAnswer.FromChoice(reader.GetString()!);
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("uncertain", out var marker) || marker.ValueKind != JsonValueKind.True)
            throw new JsonException("A decision answer must be a boolean, string or uncertainty sentinel.");
        string? reason = null;
        if (value.TryGetProperty("reason", out var explanation))
        {
            if (explanation.ValueKind != JsonValueKind.String) throw new JsonException("An uncertainty reason must be a string.");
            reason = explanation.GetString();
        }
        return DecisionAnswer.FromUncertain(reason);
    }
    public override void Write(Utf8JsonWriter writer, DecisionAnswer value, JsonSerializerOptions options)
    {
        if (value.Kind == DecisionAnswerKind.Boolean) writer.WriteBooleanValue(value.BooleanValue!.Value);
        else if (value.Kind == DecisionAnswerKind.Choice) writer.WriteStringValue(value.ChoiceValue);
        else JsonSerializer.Serialize(writer, value.UncertainValue, options);
    }
}
