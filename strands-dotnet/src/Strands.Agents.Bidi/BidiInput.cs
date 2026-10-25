namespace Strands.Agents.Bidi;

/// <summary>A complete message or streaming audio delta sent to a bidirectional model.</summary>
public interface IBidiInput;

/// <summary>An owned ordered message containing user text/images or tool results.</summary>
public sealed record BidiMessage : IBidiInput
{
    /// <summary>Copies content and rejects empty messages and mixed user/tool-result input.</summary>
    public BidiMessage(IReadOnlyList<ContentBlock> content)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));
        if (content.Count == 0) throw new ArgumentException("A bidirectional message requires content.", nameof(content));
        var detached = new Message(Role.User, content).Content;
        if (detached.Any(block => block is not (TextBlock or ImageBlock or ToolResultBlock))) throw new ArgumentException("Bidirectional messages support text, image or tool-result blocks.", nameof(content));
        if (detached.Any(block => block is ToolResultBlock) && detached.Any(block => block is not ToolResultBlock)) throw new ArgumentException("Tool results cannot be mixed with user text or images.", nameof(content));
        Content = detached;
    }
    /// <summary>Detached ordered content.</summary>
    public IReadOnlyList<ContentBlock> Content { get; }
}

/// <summary>An owned raw PCM audio delta. Sending it does not explicitly end the user's turn.</summary>
public sealed record BidiAudioDelta : IBidiInput
{
    private readonly byte[] _data;
    /// <summary>Copies raw mono PCM16 audio bytes.</summary>
    public BidiAudioDelta(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        _data = data.ToArray();
    }
    /// <summary>Returns an owned copy of the raw audio bytes.</summary>
    public byte[] GetBytes() => _data.ToArray();
    /// <summary>The raw byte count.</summary>
    public int Length => _data.Length;
}
