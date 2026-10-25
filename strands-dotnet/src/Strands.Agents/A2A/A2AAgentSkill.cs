using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>A detached A2A skill declaration retaining its original protocol members and extensions.</summary>
public sealed class A2AAgentSkill
{
    /// <summary>Validates and copies a protocol AgentSkill object.</summary>
    public A2AAgentSkill(JsonElement skill)
    {
        A2AJson.RequireObject(skill); A2AJson.ValidateUniqueProperties(skill);
        _ = A2AJson.RequiredString(skill, "id"); _ = A2AJson.RequiredString(skill, "name");
        A2ACardJson.String(skill, "description");
        if (!skill.TryGetProperty("tags", out var tags)) throw new A2AProtocolException("An A2A skill requires tags.");
        A2ACardJson.Strings(tags);
        foreach (var property in new[] { "examples", "inputModes", "outputModes" })
            if (skill.TryGetProperty(property, out var values)) A2ACardJson.Strings(values);
        if (skill.TryGetProperty("security", out var security)) A2ACardJson.Security(security);
        Json = skill.Clone();
    }
    /// <summary>The detached skill JSON with protocol wire casing.</summary>
    public JsonElement Json { get; }
    /// <summary>Creates Python-compatible skill metadata from one tool specification: name as identifier and name, original description, and empty tags.</summary>
    public static A2AAgentSkill FromTool(ToolSpec tool)
    {
        Guard.NotNull(tool, nameof(tool));
        return new(JsonSerializer.SerializeToElement(new { id = tool.Name, name = tool.Name, description = tool.Description, tags = Array.Empty<string>() }));
    }
}
