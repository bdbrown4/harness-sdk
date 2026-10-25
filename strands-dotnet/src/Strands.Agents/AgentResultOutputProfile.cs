namespace Strands.Agents;

/// <summary>Selects source-derived safe-field and text-presentation rules, not a foreign message wire format.</summary>
public enum AgentResultOutputProfile
{
    /// <summary>Retain structured JSON and use TypeScript-style text presentation.</summary>
    TypeScript,
    /// <summary>Omit structured JSON from the safe projection and use Python-style text presentation.</summary>
    Python
}
