using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Context;

internal static class ContextReference
{
    internal static string Create(Message message, int messageIndex, int blockIndex, int innerIndex)
    {
        using var hash = SHA256.Create();
        var fingerprint = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(message.TrackingId))).Replace("-", "").ToLowerInvariant();
        return $"ctx_{messageIndex.ToString(CultureInfo.InvariantCulture)}_{fingerprint}_{blockIndex.ToString(CultureInfo.InvariantCulture)}_{innerIndex.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static ContentBlock? Resolve(IReadOnlyList<Message> history, string reference, CancellationToken cancellationToken)
    {
        var parts = reference.Split('_');
        if (parts.Length != 5 || parts[0] != "ctx" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var messageIndex) || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var blockIndex) || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var innerIndex)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (messageIndex >= history.Count) return null;
        var message = history[messageIndex];
        if (blockIndex >= message.Content.Count || message.Role != Role.User || message.Content[blockIndex] is not ToolResultBlock result || IsRetrievalResult(history, messageIndex, result.ToolUseId)) return null;
        var blocks = result.ContentBlocks.Count > 0 ? result.ContentBlocks : [new TextBlock(result.Content)];
        if (innerIndex >= blocks.Count || !string.Equals(Create(message, messageIndex, blockIndex, innerIndex), reference, StringComparison.Ordinal)) return null;
        return Message.CloneBlock(blocks[innerIndex]);
    }

    internal static bool IsRetrievalResult(IReadOnlyList<Message> history, int messageIndex, string toolUseId) => messageIndex > 0 && history[messageIndex - 1].Role == Role.Assistant && history[messageIndex - 1].Content.OfType<ToolUseBlock>().Any(call => call.ToolUseId == toolUseId && call.Name == ContextRetrievalTool.ToolName);

    internal static int FindMessage(IReadOnlyList<Message> history, Message message)
    {
        for (var i = 0; i < history.Count; i++) if (ReferenceEquals(history[i], message)) return i;
        var serialized = JsonSerializer.Serialize(message);
        for (var i = 0; i < history.Count; i++) if (history[i].TrackingId == message.TrackingId && JsonSerializer.Serialize(history[i]) == serialized) return i;
        return -1;
    }
}
