namespace Strands.Agents.Files;

/// <summary>Selects text conversion for an explicit filesystem convenience call.</summary>
public enum FileTextProfile
{
    /// <summary>Uses native selected-backend reads and strict UTF-8 writes.</summary>
    Native = 0,
    /// <summary>Requests the pinned TypeScript UTF-8 conversion profile.</summary>
    TypeScript = 1,
    /// <summary>Requests the pinned Python default UTF-8 conversion profile.</summary>
    Python = 2,
}
