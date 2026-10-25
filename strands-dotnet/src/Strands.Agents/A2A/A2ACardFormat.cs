namespace Strands.Agents.A2A;

/// <summary>Discovery-card defaults for the native API or one pinned language adapter. Execution, ownership and authorization policies remain explicit native settings.</summary>
public enum A2ACardFormat
{
    /// <summary>A2A 0.3 with the configured input and output MIME modes.</summary>
    Native,
    /// <summary>The pinned TypeScript adapter's A2A 0.2 and text/plain discovery defaults.</summary>
    TypeScript,
    /// <summary>The pinned Python adapter's A2A 0.3 and legacy text discovery defaults.</summary>
    Python
}
