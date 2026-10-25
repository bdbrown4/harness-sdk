using System.Text.Json;
using Strands.Harness.Mcp;

namespace Strands.Harness.Tests;

public sealed class McpServerOptionsJsonTests
{
    [Fact]
    public void DefaultAndExplicitNullFieldsPreserveTheExistingOptionsJsonShape()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Command"] = "null",
            ["Arguments"] = "[]",
            ["Environment"] = "{}",
            ["WorkingDirectory"] = "null",
            ["Url"] = "null",
            ["Headers"] = "{}",
            ["Transport"] = "null",
            ["Prefix"] = "null",
            ["ToolFilters"] = "null",
            ["Disabled"] = "false",
            ["ContinueOnError"] = "null",
            ["TasksConfig"] = "null",
            ["Auth"] = "null",
            ["AuthOptions"] = "null"
        };
        foreach (var options in new[]
        {
            new McpServerOptions(),
            new McpServerOptions { ApplicationName = null, ApplicationVersion = null, StartupTimeout = null }
        })
        {
            var json = JsonSerializer.Serialize(options);
            using var document = JsonDocument.Parse(json);
            var actual = document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name, property => property.Value.GetRawText(), StringComparer.Ordinal);
            Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                actual.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            var roundTrip = JsonSerializer.Deserialize<McpServerOptions>(json)!;
            Assert.Null(roundTrip.ApplicationName);
            Assert.Null(roundTrip.ApplicationVersion);
            Assert.Null(roundTrip.StartupTimeout);
        }
    }

    [Fact]
    public void ExplicitIdentityAndTimeoutAreIncludedAndRoundTripWithExistingSettings()
    {
        var options = new McpServerOptions
        {
            Url = "https://m05.example.test/mcp",
            Prefix = "owner_",
            Headers = new Dictionary<string, string> { ["X-Owner"] = "kept" },
            ApplicationName = "json-owner",
            ApplicationVersion = "4.5.6",
            StartupTimeout = TimeSpan.FromMilliseconds(4321)
        };

        var json = JsonSerializer.Serialize(options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("json-owner", document.RootElement.GetProperty("ApplicationName").GetString());
        Assert.Equal("4.5.6", document.RootElement.GetProperty("ApplicationVersion").GetString());
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("StartupTimeout").ValueKind);
        var roundTrip = JsonSerializer.Deserialize<McpServerOptions>(json)!;
        Assert.Equal(options.ApplicationName, roundTrip.ApplicationName);
        Assert.Equal(options.ApplicationVersion, roundTrip.ApplicationVersion);
        Assert.Equal(options.StartupTimeout, roundTrip.StartupTimeout);
        Assert.Equal(options.Url, roundTrip.Url);
        Assert.Equal(options.Prefix, roundTrip.Prefix);
        Assert.Equal("kept", roundTrip.Headers["X-Owner"]);
    }

    [Fact]
    public void RecordCopyCanClearTheNewFieldsWithoutChangingTheOriginalOrExistingSettings()
    {
        var original = new McpServerOptions
        {
            Url = "https://m05.example.test/mcp",
            Prefix = "kept_",
            ApplicationName = "original-owner",
            ApplicationVersion = "1.2.3",
            StartupTimeout = TimeSpan.FromSeconds(7)
        };
        var cleared = original with { ApplicationName = null, ApplicationVersion = null, StartupTimeout = null };

        var json = JsonSerializer.Serialize(cleared);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("ApplicationName", out _));
        Assert.False(document.RootElement.TryGetProperty("ApplicationVersion", out _));
        Assert.False(document.RootElement.TryGetProperty("StartupTimeout", out _));
        var roundTrip = JsonSerializer.Deserialize<McpServerOptions>(json)!;
        Assert.Null(roundTrip.ApplicationName);
        Assert.Null(roundTrip.ApplicationVersion);
        Assert.Null(roundTrip.StartupTimeout);
        Assert.Equal(original.Url, roundTrip.Url);
        Assert.Equal(original.Prefix, roundTrip.Prefix);
        Assert.Equal("original-owner", original.ApplicationName);
        Assert.Equal("1.2.3", original.ApplicationVersion);
        Assert.Equal(TimeSpan.FromSeconds(7), original.StartupTimeout);
    }
}
