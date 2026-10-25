namespace Strands.Agents;

/// <summary>Marks a public instance method for explicit typed plugin-hook discovery.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class HookAttribute : Attribute
{
    /// <summary>Callback priority; lower values run first.</summary>
    public int Order { get; set; }
}
