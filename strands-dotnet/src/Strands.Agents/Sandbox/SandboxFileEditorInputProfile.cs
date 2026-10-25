namespace Strands.Agents.Sandbox;

/// <summary>Numeric and optional-input behavior for an explicitly configured sandbox editor.</summary>
public enum SandboxFileEditorInputProfile
{
    /// <summary>Exact native mathematical integers and the established nullable range behavior.</summary>
    Native = 0,
    /// <summary>Finite binary64 positions and optional-but-not-null TypeScript inputs.</summary>
    TypeScript = 1,
    /// <summary>Registered Python tool-input behavior under the documented Pydantic reference.</summary>
    Python = 2,
}
