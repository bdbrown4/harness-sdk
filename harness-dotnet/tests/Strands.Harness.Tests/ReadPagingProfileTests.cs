using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Files;

namespace Strands.Harness.Tests;

public sealed class ReadPagingProfileTests
{
    public static IEnumerable<object[]> ProfileCases()
    {
        const string firstTwo = "     1\tA\n     2\tB\n[Showing lines 1-2 of 5. Use offset/limit to read more.]";
        const string secondTwo = "     2\tB\n     3\tC\n[Showing lines 2-3 of 5. Use offset/limit to read more.]";
        yield return new object[] { "TsFractionalOffset", ReadPagingProfile.TypeScript, "{\"path\":\"/selected/file.txt\",\"offset\":2.5,\"limit\":2}", "   2.5\tB\n   3.5\tC\n[Showing lines 2.5-3.5 of 5. Use offset/limit to read more.]" };
        yield return new object[] { "TsFractionalLimit", ReadPagingProfile.TypeScript, "{\"path\":\"/selected/file.txt\",\"offset\":2,\"limit\":1.5}", "     2\tB\n[Showing lines 2-2 of 5. Use offset/limit to read more.]" };
        yield return new object[] { "TsAddBeforeTruncatingEndpoints", ReadPagingProfile.TypeScript, "{\"path\":\"/selected/file.txt\",\"offset\":2.5,\"limit\":-2.25}", "[File has 5 lines; offset 2.5 is past the end.]" };
        yield return new object[] { "TsMissingOffsetDiagnostic", ReadPagingProfile.TypeScript, "{\"path\":\"/selected/file.txt\",\"limit\":0}", "[File has 5 lines; offset undefined is past the end.]" };
        yield return new object[] { "TsLargeExponentRendering", ReadPagingProfile.TypeScript, "{\"path\":\"/selected/file.txt\",\"offset\":1e20,\"limit\":0}", "[File has 5 lines; offset 100000000000000000000 is past the end.]" };
        yield return new object[] { "TsSmallExponentRendering", ReadPagingProfile.TypeScript, "{\"path\":\"/selected/file.txt\",\"offset\":1e-7,\"limit\":0}", "[File has 5 lines; offset 1e-7 is past the end.]" };
        yield return new object[] { "TsBinary64IntegerRounding", ReadPagingProfile.TypeScript, "{\"path\":\"/selected/file.txt\",\"offset\":9007199254740993,\"limit\":0}", "[File has 5 lines; offset 9007199254740992 is past the end.]" };
        yield return new object[] { "PyWholeDecimalNumber", ReadPagingProfile.Python, "{\"path\":\"/selected/file.txt\",\"offset\":2.0,\"limit\":2}", secondTwo };
        yield return new object[] { "PyScientificNumber", ReadPagingProfile.Python, "{\"path\":\"/selected/file.txt\",\"offset\":1e2,\"limit\":1}", "[File has 5 lines; offset 100 is past the end.]" };
        yield return new object[] { "PyIntegerString", ReadPagingProfile.Python, "{\"path\":\"/selected/file.txt\",\"offset\":\"2\",\"limit\":2}", secondTwo };
        yield return new object[] { "PyZeroDecimalString", ReadPagingProfile.Python, "{\"path\":\"/selected/file.txt\",\"offset\":\"2.0\",\"limit\":2}", secondTwo };
        yield return new object[] { "PyBooleanOffset", ReadPagingProfile.Python, "{\"path\":\"/selected/file.txt\",\"offset\":true,\"limit\":2}", firstTwo };
        yield return new object[] { "PyBooleanZeroLimit", ReadPagingProfile.Python, "{\"path\":\"/selected/file.txt\",\"offset\":2,\"limit\":false}", "[File has 5 lines; offset 2 is past the end.]" };
    }

    public static IEnumerable<object[]> DirectAndRegisteredCases()
    {
        foreach (var row in ProfileCases())
            foreach (var registered in new[] { false, true })
                yield return row.Concat(new object[] { registered }).ToArray();
    }

    [Theory]
    [MemberData(nameof(DirectAndRegisteredCases))]
    public async Task SourceProfilesReturnTheirExactNumberingAndSlice(string scenario, ReadPagingProfile profile, string json, string expected, bool registered)
    {
        var sandbox = new SelectedSandbox();
        var tool = FileTools.CreateRead(sandbox, new() { PagingProfile = profile });
        using var document = JsonDocument.Parse(json);
        var input = document.RootElement;
        string actual;
        if (registered)
        {
            var model = new UnusedModel();
            await using var agent = new Agent(new() { Model = model, Tools = [tool] });
            var result = await agent.InvokeToolAsync(new("read-profile", "read", input));
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Equal("read-profile", result.ToolUseId);
            Assert.Same(tool, Assert.Single(agent.Tools));
            Assert.Equal(0, model.Calls);
            actual = result.Content;
        }
        else actual = await tool.InvokeAsync(input, Context(input));
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(expected, actual);
        Assert.Equal(1, sandbox.TextReads);
        Assert.Equal(0, sandbox.BinaryReads);
        Assert.Equal("/selected/file.txt", sandbox.LastPath);
    }

    [Theory]
    [InlineData(ReadPagingProfile.Python, "\"1e2\"", false)]
    [InlineData(ReadPagingProfile.Python, "\"1e2\"", true)]
    [InlineData(ReadPagingProfile.Python, "1.5", false)]
    [InlineData(ReadPagingProfile.Python, "1.5", true)]
    [InlineData(ReadPagingProfile.TypeScript, "true", false)]
    [InlineData(ReadPagingProfile.TypeScript, "true", true)]
    [InlineData(ReadPagingProfile.TypeScript, "\"2\"", false)]
    [InlineData(ReadPagingProfile.TypeScript, "\"2\"", true)]
    public async Task InvalidSourceInputsAreRejectedBeforeReading(ReadPagingProfile profile, string rawOffset, bool registered)
    {
        var sandbox = new SelectedSandbox();
        var tool = FileTools.CreateRead(sandbox, new() { PagingProfile = profile });
        using var document = JsonDocument.Parse("{\"path\":\"/selected/file.txt\",\"offset\":" + rawOffset + "}");
        if (registered)
        {
            var model = new UnusedModel();
            await using var agent = new Agent(new() { Model = model, Tools = [tool] });
            var result = await agent.InvokeToolAsync(new("read-profile", "read", document.RootElement));
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Equal(0, model.Calls);
        }
        else await Assert.ThrowsAnyAsync<ArgumentException>(async () => await tool.InvokeAsync(document.RootElement, Context(document.RootElement)));
        Assert.Equal(0, sandbox.TextReads);
        Assert.Equal(0, sandbox.BinaryReads);
    }

    [Theory]
    [InlineData(ReadPagingProfile.TypeScript, "null", false)]
    [InlineData(ReadPagingProfile.TypeScript, "null", true)]
    [InlineData(ReadPagingProfile.Python, "1.5", false)]
    [InlineData(ReadPagingProfile.Python, "1.5", true)]
    public async Task ExplicitProfilesValidateBeforeSelectedMediaReads(ReadPagingProfile profile, string rawOffset, bool registered)
    {
        var sandbox = new SelectedSandbox();
        var tool = FileTools.CreateRead(sandbox, new() { PagingProfile = profile, MaximumMediaBytes = 2 });
        using var document = JsonDocument.Parse("{\"path\":\"/selected/image.PNG\",\"offset\":" + rawOffset + "}");
        if (registered)
        {
            var model = new UnusedModel();
            await using var agent = new Agent(new() { Model = model, Tools = [tool] });
            var result = await agent.InvokeToolAsync(new("read-profile", "read", document.RootElement));
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Equal(0, model.Calls);
        }
        else await Assert.ThrowsAnyAsync<ArgumentException>(async () => await tool.InvokeAsync(document.RootElement, Context(document.RootElement)));
        Assert.Equal(0, sandbox.TextReads);
        Assert.Equal(0, sandbox.BinaryReads);
    }

    [Fact]
    public void PublishedProfileKindsAreExplicitAndDefaultRemainsIntegral()
    {
        var sandbox = new SelectedSandbox();
        Assert.Equal(ReadPagingProfile.NativeIntegral, new ReadConfig().PagingProfile);
        foreach (var profile in new[] { ReadPagingProfile.NativeIntegral, ReadPagingProfile.Python, ReadPagingProfile.TypeScript })
        {
            var tool = FileTools.CreateRead(sandbox, new() { PagingProfile = profile });
            var field = tool.Spec.InputSchema.GetProperty("properties").GetProperty("offset").GetProperty("type");
            if (profile == ReadPagingProfile.TypeScript) Assert.Equal("number", field.GetString());
            else Assert.Equal(profile == ReadPagingProfile.Python ? new[] { "number", "string", "boolean", "null" } : new[] { "integer", "null" }, field.EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal("path", Assert.Single(tool.Spec.InputSchema.GetProperty("required").EnumerateArray()).GetString());
            Assert.False(tool.Spec.InputSchema.GetProperty("additionalProperties").GetBoolean());
        }
        Assert.Equal(0, sandbox.TextReads);
        Assert.Equal(0, sandbox.BinaryReads);
    }

    [Fact]
    public void InvalidProfileCannotPublishAToolOrReadTheSandbox()
    {
        var sandbox = new SelectedSandbox();
        Assert.Throws<ArgumentOutOfRangeException>(() => FileTools.CreateRead(sandbox, new() { PagingProfile = (ReadPagingProfile)99 }));
        Assert.Equal(0, sandbox.TextReads);
        Assert.Equal(0, sandbox.BinaryReads);
    }

    private static ToolContext Context(JsonElement input) => new(null!, new("read-profile", "read", input), new Dictionary<string, object?>());

    private sealed class SelectedSandbox : ISandbox, IReadOnlyFileSystem, IBoundedFileReader
    {
        public int TextReads { get; private set; }
        public int BinaryReads { get; private set; }
        public string? LastPath { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("/selected/file.txt", path);
            TextReads++;
            LastPath = path;
            return new("A\nB\nC\nD\nE");
        }
        public ValueTask<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("/selected/image.PNG", path);
            Assert.Equal(2, maximumBytes);
            BinaryReads++;
            LastPath = path;
            return new(new byte[] { 1, 2 });
        }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No directory reads are permitted.");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No writes are permitted.");
    }

    private sealed class UnusedModel : IModel
    {
        public int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The selected direct tool must not invoke a model.");
        }
    }
}
