namespace Strands.Agents.Bidi;

/// <summary>Selects categories written by the native text console output.</summary>
public sealed record BidiConsoleOutputOptions
{
    /// <summary>Whether to display assistant text.</summary>
    public bool ShowText { get; init; } = true;
    /// <summary>Whether to display user and assistant speech transcripts.</summary>
    public bool ShowTranscripts { get; init; } = true;
    /// <summary>Whether to display reasoning text supplied by the model.</summary>
    public bool ShowReasoning { get; init; } = true;
    /// <summary>Whether to display completed tool-call names.</summary>
    public bool ShowTools { get; init; } = true;
}
