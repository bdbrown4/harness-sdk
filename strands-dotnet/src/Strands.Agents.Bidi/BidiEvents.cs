using System.Text.Json;

namespace Strands.Agents.Bidi;

/// <summary>A bidirectional model output. Direct sealed records avoid extensible record ABI drift across target frameworks.</summary>
public interface IBidiOutputEvent : IAgentStreamEvent;

/// <summary>A new connection has started.</summary>
/// <param name="ConnectionId">The connection identifier.</param>
/// <param name="Model">The configured model identifier.</param>
public sealed record BidiConnectionStartEvent(string ConnectionId, string Model) : IBidiOutputEvent;
/// <summary>A response has started.</summary>
/// <param name="ResponseId">The provider response identifier.</param>
public sealed record BidiResponseStartEvent(string ResponseId) : IBidiOutputEvent;
/// <summary>A response has stopped.</summary>
/// <param name="ResponseId">The provider response identifier.</param>
public sealed record BidiResponseStopEvent(string ResponseId) : IBidiOutputEvent;
/// <summary>A visible text stream has started.</summary>
/// <param name="ContentId">The connection-local content identifier.</param>
public sealed record BidiTextStartEvent(string ContentId) : IBidiOutputEvent;
/// <summary>A visible text delta.</summary>
/// <param name="Delta">The appended text.</param>
/// <param name="ContentId">The connection-local content identifier.</param>
public sealed record BidiTextDeltaEvent(string Delta, string ContentId) : IBidiOutputEvent;
/// <summary>A visible text stream has stopped.</summary>
/// <param name="ContentId">The connection-local content identifier.</param>
public sealed record BidiTextStopEvent(string ContentId) : IBidiOutputEvent;
/// <summary>An audio stream has started.</summary>
/// <param name="ContentId">The connection-local content identifier.</param>
public sealed record BidiAudioStartEvent(string ContentId) : IBidiOutputEvent;
/// <summary>Raw audio output encoded as base64.</summary>
/// <param name="Audio">The base64 audio bytes.</param>
/// <param name="ContentId">The connection-local content identifier.</param>
/// <param name="SampleRate">The output sample rate.</param>
/// <param name="Channels">The output channel count.</param>
/// <param name="Format">The raw audio format.</param>
public sealed record BidiAudioDeltaEvent(string Audio, string ContentId, int SampleRate, int Channels, string Format) : IBidiOutputEvent;
/// <summary>An audio stream has stopped.</summary>
/// <param name="ContentId">The connection-local content identifier.</param>
public sealed record BidiAudioStopEvent(string ContentId) : IBidiOutputEvent;
/// <summary>A user or assistant transcript has started.</summary>
/// <param name="Role">The speaker.</param>
/// <param name="ContentId">The connection-local content identifier.</param>
public sealed record BidiTranscriptStartEvent(Role Role, string ContentId) : IBidiOutputEvent;
/// <summary>A user or assistant transcript delta.</summary>
/// <param name="Delta">The appended transcript.</param>
/// <param name="Role">The speaker.</param>
/// <param name="ContentId">The connection-local content identifier.</param>
public sealed record BidiTranscriptDeltaEvent(string Delta, Role Role, string ContentId) : IBidiOutputEvent;
/// <summary>A user or assistant transcript has stopped.</summary>
/// <param name="Role">The speaker.</param>
/// <param name="ContentId">The connection-local content identifier.</param>
public sealed record BidiTranscriptStopEvent(Role Role, string ContentId) : IBidiOutputEvent;
/// <summary>The provider interrupted its response because a new user turn was detected.</summary>
public sealed record BidiBargeInEvent : IBidiOutputEvent;
/// <summary>Additive usage for one response, with optional overlapping category counts.</summary>
public sealed record BidiUsageEvent : IBidiOutputEvent
{
    /// <summary>Copies optional category maps alongside authoritative usage totals.</summary>
    public BidiUsageEvent(Usage usage, IReadOnlyDictionary<string, long>? inputDetails = null, IReadOnlyDictionary<string, long>? outputDetails = null)
    {
        if (usage is null) throw new ArgumentNullException(nameof(usage));
        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CacheReadInputTokens < 0 || usage.CacheWriteInputTokens < 0 || usage.ReportedTotalTokens is < 0) throw new ArgumentOutOfRangeException(nameof(usage));
        Usage = usage; InputDetails = Copy(inputDetails); OutputDetails = Copy(outputDetails);
    }
    /// <summary>Authoritative usage totals.</summary>
    public Usage Usage { get; }
    /// <summary>Optional detached input category counts.</summary>
    public IReadOnlyDictionary<string, long>? InputDetails { get; }
    /// <summary>Optional detached output category counts.</summary>
    public IReadOnlyDictionary<string, long>? OutputDetails { get; }
    private static IReadOnlyDictionary<string, long>? Copy(IReadOnlyDictionary<string, long>? details)
    {
        if (details is null) return null;
        if (details.Any(item => string.IsNullOrEmpty(item.Key) || item.Value < 0)) throw new ArgumentException("Usage categories require names and nonnegative counts.", nameof(details));
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(details.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
    }
}
/// <summary>A complete correlated tool batch.</summary>
public sealed record BidiToolUseBlocksEvent : IBidiOutputEvent
{
    /// <summary>Copies the provider's ordered complete tool calls.</summary>
    public BidiToolUseBlocksEvent(IReadOnlyList<ToolUseBlock> toolUses)
    {
        if (toolUses is null) throw new ArgumentNullException(nameof(toolUses));
        ToolUses = Array.AsReadOnly(new Message(Role.Assistant, toolUses.Cast<ContentBlock>().ToArray()).Content.Cast<ToolUseBlock>().ToArray());
    }
    /// <summary>Detached complete calls in provider order.</summary>
    public IReadOnlyList<ToolUseBlock> ToolUses { get; }
}

/// <summary>A native server event retained for diagnostics when no normalized event applies.</summary>
public sealed record BidiNativeEvent : IBidiOutputEvent
{
    /// <summary>Copies the original native server event.</summary>
    public BidiNativeEvent(JsonElement value) => Value = value.Clone();
    /// <summary>The detached native event.</summary>
    public JsonElement Value { get; }
}
