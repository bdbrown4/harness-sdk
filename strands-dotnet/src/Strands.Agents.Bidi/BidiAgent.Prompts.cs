namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent
{
    private IReadOnlyList<ContentBlock>? _systemPromptContent;
    /// <summary>Detached structured system content. Providers receive its newline-joined text projection.</summary>
    public IReadOnlyList<ContentBlock>? SystemPromptContent
    {
        get
        {
            lock (_gate)
                return _hasPromptOverride ? SessionSnapshot.CopySystemPromptContent(_systemPromptContent) : FromText(_options.SystemPrompt ?? _options.ToolAgent.SystemPrompt);
        }
    }
    /// <summary>Replaces system content with a plain string; null explicitly clears it.</summary>
    public void SetSystemPrompt(string? value)
    {
        lock (_gate) { ThrowIfDisposed(); ApplyPrompt(FromText(value)); }
    }
    /// <summary>Replaces system content with detached text/cache-point blocks; null explicitly clears it.</summary>
    public void SetSystemPromptContent(IReadOnlyList<ContentBlock>? value)
    {
        var copy = ValidatePrompt(value, nameof(value));
        lock (_gate) { ThrowIfDisposed(); ApplyPrompt(copy); }
    }
    private void ApplyPrompt(IReadOnlyList<ContentBlock>? content)
    {
        _hasPromptOverride = true;
        _systemPromptContent = content;
        _systemPrompt = SessionSnapshot.GetSystemPromptText(content);
    }
    private static IReadOnlyList<ContentBlock>? FromText(string? value) => value is null ? null : Array.AsReadOnly<ContentBlock>([new TextBlock(value)]);
    private static IReadOnlyList<ContentBlock>? ValidatePrompt(IReadOnlyList<ContentBlock>? value, string parameter)
    {
        try { return SessionSnapshot.CopySystemPromptContent(value); }
        catch (InvalidDataException error) { throw new ArgumentException(error.Message, parameter, error); }
    }
}
