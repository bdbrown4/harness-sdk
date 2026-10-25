namespace Strands.Agents.MultiAgent;

/// <summary>When the borrowed native session store restores orchestration state relative to user plugins and callbacks.</summary>
public enum MultiAgentSessionRestoreProfile
{
    /// <summary>Restore before user plugin initialization, preserving the native default.</summary>
    Native = 0,
    /// <summary>Wire restoration after plugin initialization and before Initialized, then restore on the first BeforeInvocation.</summary>
    TypeScriptSnapshot,
    /// <summary>Restore during Initialized at Python's ordinary-priority manager position.</summary>
    PythonRepository,
    /// <summary>Wire restoration during Initialized at Python's manager position, then restore on the first BeforeInvocation.</summary>
    PythonSnapshot
}

/// <summary>When a swarm captures missing reset baselines relative to user plugin initialization.</summary>
public enum SwarmBaselineCapturePhase
{
    /// <summary>Capture each baseline before user plugins, preserving the native default.</summary>
    BeforePlugins = 0,
    /// <summary>Capture missing baselines after user plugins and before Initialized, matching Python Swarm construction.</summary>
    AfterPlugins
}
