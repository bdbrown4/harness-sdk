namespace Strands.Agents;

/// <summary>Conventional lifecycle priorities. Any integer is accepted; smaller values run first.</summary>
public static class HookOrder
{
    /// <summary>The earliest SDK policies, including durable context reduction.</summary>
    public const int SdkFirst = -100;
    /// <summary>Policies observing or transforming model output before ordinary callbacks.</summary>
    public const int InterventionOutput = -90;
    /// <summary>The default application callback priority.</summary>
    public const int Default = 0;
    /// <summary>Model routing after ordinary application policies.</summary>
    public const int ModelRouting = 50;
    /// <summary>Policies observing the finalized model input.</summary>
    public const int InterventionInput = 90;
    /// <summary>The latest SDK callbacks, including background extraction triggers.</summary>
    public const int SdkLast = 100;
}
