using System.Text.Json;
using Strands.Agents.Context;

namespace Strands.Agents.Tests.Context;

public sealed class SharedContextSearchTests
{
    public static IEnumerable<object[]> Fixtures()
    {
        using var stream = typeof(SharedContextSearchTests).Assembly.GetManifestResourceStream("Strands.Agents.Tests.Context.Fixtures.shared-search.json")!;
        using var document = JsonDocument.Parse(stream);
        foreach (var fixture in document.RootElement.EnumerateArray()) yield return [fixture.GetProperty("name").GetString()!, fixture.Clone()];
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task MatchesIndependentTypeScriptAndPythonSearchFixture(string name, JsonElement fixture)
    {
        _ = name;
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var result = new ToolResultBlock("call", [new TextBlock(new string('p', 8_000)), new TextBlock(fixture.GetProperty("text").GetString()!)]);
        var options = new ToolResultOffloadOptions { MaxRetrievalTokens = fixture.GetProperty("maxChars").GetInt32() / 4 };
        var agent = new Agent(new() { Model = model, Messages = ToolResultOffloaderTests.History(result), Plugins = [new ToolResultOffloader(options)] });
        await agent.InvokeAsync(null);
        var firstReference = ToolResultOffloaderTests.Reference(model.Requests[0]);
        var reference = firstReference.Substring(0, firstReference.LastIndexOf('_') + 1) + "1";
        var input = fixture.GetProperty("input").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        input.Add("reference", JsonSerializer.SerializeToElement(reference));
        var actual = await ToolResultOffloaderTests.Retrieve(agent, reference, input);
        Assert.Equal(ToolResultStatus.Success, actual.Status);
        Assert.Equal(fixture.GetProperty("expected").GetString(), actual.Content);
    }
}
