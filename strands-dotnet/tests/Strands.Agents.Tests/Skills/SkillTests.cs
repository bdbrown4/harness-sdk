using Strands.Agents.Skills;

namespace Strands.Agents.Tests.Skills;

public sealed class SkillTests
{
    [Fact]
    public void ParsesYamlFrontmatterIncludingFoldedTextListsAndNestedTypedMetadata()
    {
        var skill = Skill.FromContent("""
            ---
            name: pdf-processing
            description: >-
              Process PDF files
              and extract tables.
            allowed-tools:
              - read
              - write
            metadata:
              nested:
                enabled: true
                attempts: 3
            license: MIT
            compatibility: Requires a PDF reader
            ---
            # Instructions
            Read the source document.
            """, strict: true);

        Assert.Equal("pdf-processing", skill.Name);
        Assert.Equal("Process PDF files and extract tables.", skill.Description);
        Assert.Equal(new[] { "read", "write" }, skill.AllowedTools);
        Assert.True(skill.Metadata.GetProperty("nested").GetProperty("enabled").GetBoolean());
        Assert.Equal(3, skill.Metadata.GetProperty("nested").GetProperty("attempts").GetInt32());
        Assert.Equal("MIT", skill.License);
        Assert.Equal("Requires a PDF reader", skill.Compatibility);
        Assert.Equal("# Instructions\nRead the source document.", skill.Instructions);
    }

    [Fact]
    public void PreservesQuotedScalarsAndResolvesNonCyclicYamlAliases()
    {
        var skill = Skill.FromContent("""
            ---
            name: pdf-processing
            description: "Process: PDF files"
            metadata:
              defaults: &defaults
                enabled: true
                code: "001"
              copied: *defaults
              quoted: "true"
              number: 1.5
            ---
            Instructions
            """, strict: true);

        Assert.Equal("Process: PDF files", skill.Description);
        Assert.Equal("001", skill.Metadata.GetProperty("copied").GetProperty("code").GetString());
        Assert.True(skill.Metadata.GetProperty("copied").GetProperty("enabled").GetBoolean());
        Assert.Equal("true", skill.Metadata.GetProperty("quoted").GetString());
        Assert.Equal(1.5, skill.Metadata.GetProperty("number").GetDouble());
    }

    [Theory]
    [InlineData("name: pdf-processing\nname: different\ndescription: Process PDFs")]
    [InlineData("name: pdf-processing\ndescription: Process PDFs\nmetadata: &cycle\n  recursive: *cycle")]
    [InlineData("name: true\ndescription: Process PDFs")]
    [InlineData("name: pdf-processing\ndescription: 42")]
    public void RejectsDuplicateFieldsCyclicMetadataAndNonStringRequiredFields(string yaml)
        => Assert.Throws<ArgumentException>(() => Skill.FromContent("---\n" + yaml + "\n---\nInstructions"));

    [Fact]
    public void ColonFallbackPreservesUnquotedDescriptionText()
    {
        var skill = Skill.FromContent("---\nname: pdf-processing\ndescription: Process: PDF files\n---\nInstructions", strict: true);
        Assert.Equal("Process: PDF files", skill.Description);
    }

    [Fact]
    public void NativeDefinitionsRejectInvalidNullsAndNonObjectMetadata()
    {
        Assert.Throws<ArgumentNullException>(() => new Skill(new() { Name = null!, Description = "Description" }));
        Assert.Throws<ArgumentNullException>(() => new Skill(new() { Name = "native", Description = null! }));
        Assert.Throws<ArgumentNullException>(() => new Skill(new() { Name = "native", Description = "Description", Instructions = null! }));
        Assert.Throws<ArgumentException>(() => new Skill(new() { Name = "native", Description = "Description", Metadata = System.Text.Json.JsonSerializer.SerializeToElement(42) }));
    }
}
