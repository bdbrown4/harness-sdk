namespace Strands.Agents.Bidi;

/// <summary>A complete visible text block, emitted by the agent after its stop event.</summary>
/// <param name="Text">The accumulated text.</param>
/// <param name="ContentId">The correlated content identifier.</param>
public sealed record BidiTextBlockEvent(string Text, string ContentId) : IBidiOutputEvent;
/// <summary>A complete transcript, emitted by the agent after its stop event.</summary>
/// <param name="Transcript">The accumulated transcript.</param>
/// <param name="Role">The speaker.</param>
/// <param name="ContentId">The correlated content identifier.</param>
public sealed record BidiTranscriptBlockEvent(string Transcript, Role Role, string ContentId) : IBidiOutputEvent;
/// <summary>The start of a provider's visible reasoning stream.</summary>
/// <param name="ContentId">The correlated content identifier.</param>
public sealed record BidiReasoningStartEvent(string ContentId) : IBidiOutputEvent;
/// <summary>A visible reasoning delta.</summary>
/// <param name="Delta">The appended reasoning text.</param>
/// <param name="ContentId">The correlated content identifier.</param>
public sealed record BidiReasoningDeltaEvent(string Delta, string ContentId) : IBidiOutputEvent;
/// <summary>The end of a visible reasoning stream.</summary>
/// <param name="ContentId">The correlated content identifier.</param>
public sealed record BidiReasoningStopEvent(string ContentId) : IBidiOutputEvent;
/// <summary>A complete visible reasoning block, emitted by the agent after its stop event.</summary>
/// <param name="Text">The accumulated reasoning text.</param>
/// <param name="ContentId">The correlated content identifier.</param>
public sealed record BidiReasoningBlockEvent(string Text, string ContentId) : IBidiOutputEvent;
