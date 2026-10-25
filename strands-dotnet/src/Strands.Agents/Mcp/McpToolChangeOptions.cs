namespace Strands.Agents.Mcp;

/// <summary>Selects source-compatible notification scheduling and refresh-failure behavior.</summary>
public enum McpToolChangeProfile
{
    /// <summary>Delay each serialized round and preserve a queued round after failure.</summary>
    Python = 0,
    /// <summary>Debounce notification bursts and discard already queued work after a failed round.</summary>
    TypeScript = 1,
}

/// <summary>Configures one tool-change subscription without changing ordinary cached discovery.</summary>
public sealed record McpToolChangeOptions
{
    /// <summary>The source profile. Defaults to Python.</summary>
    public McpToolChangeProfile Profile { get; init; } = McpToolChangeProfile.Python;
    /// <summary>The notification delay. Defaults to 300 milliseconds.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(300);
    /// <summary>The maximum modern subscription acknowledgement wait. Defaults to 30 seconds.</summary>
    public TimeSpan AcknowledgementTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
