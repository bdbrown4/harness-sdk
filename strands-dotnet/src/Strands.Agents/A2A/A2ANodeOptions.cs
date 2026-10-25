namespace Strands.Agents.A2A;

/// <summary>Remote graph-node projection and durable stable-pause limits. The client and remote execution remain borrowed.</summary>
public sealed record A2ANodeOptions
{
    /// <summary>The remote content/streaming settings. Graph interruption follows the actual protocol state regardless of the standalone stop-reason projection.</summary>
    public A2AAgentOptions AgentOptions { get; init; } = new();
    /// <summary>The maximum serialized pending checkpoint size, defaulting to 256 KiB.</summary>
    public int MaxCheckpointBytes { get; init; } = 256 * 1024;
    /// <summary>The maximum unanswered remote questions in one stable pause, defaulting to 64.</summary>
    public int MaxQuestions { get; init; } = 64;
}
