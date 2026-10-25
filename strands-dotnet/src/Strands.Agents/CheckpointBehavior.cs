namespace Strands.Agents;

/// <summary>Opt-in model/tool cycle boundaries for explicit invocation resumption.</summary>
public enum CheckpointBehavior
{
    /// <summary>Run the ordinary agent loop without cycle pauses.</summary>
    Disabled,
    /// <summary>Pause before retaining a model tool request; resumption regenerates it, following TypeScript.</summary>
    TypeScriptRegenerate,
    /// <summary>Retain a model tool request at the pause; resumption executes those calls, following Python.</summary>
    PythonReplay
}
