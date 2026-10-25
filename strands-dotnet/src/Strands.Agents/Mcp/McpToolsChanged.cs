namespace Strands.Agents.Mcp;

/// <summary>An immutable accepted discovery snapshot for one notification subscription.</summary>
public sealed class McpToolsChanged
{
    internal McpToolsChanged(long version, IEnumerable<string> previousNames, IEnumerable<McpTool> tools)
    {
        Version = version;
        PreviousNames = Array.AsReadOnly(previousNames.ToArray());
        Tools = Array.AsReadOnly(tools.ToArray());
    }

    /// <summary>The increasing accepted snapshot version within this subscription.</summary>
    public long Version { get; }
    /// <summary>The adapted names from the previous accepted snapshot.</summary>
    public IReadOnlyList<string> PreviousNames { get; }
    /// <summary>The complete adapted tools in server order for this accepted snapshot.</summary>
    public IReadOnlyList<McpTool> Tools { get; }
}
