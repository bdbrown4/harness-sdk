namespace Strands.Agents.Models;

/// <summary>Controls how assistant content is projected into Meta's singular content field.</summary>
public enum LlamaApiAssistantContentPolicy
{
    /// <summary>Concatenates assistant text blocks and rejects unrepresentable assistant media.</summary>
    ConcatenatedText,
    /// <summary>Matches the pinned Python adapter: validates all blocks, then retains only the first text or inline image block.</summary>
    /// <remarks>Later supported blocks are discarded. Inline assistant images exceed the vendor's published typed assistant schema; no live service compatibility is implied.</remarks>
    PythonFirstBlock
}
