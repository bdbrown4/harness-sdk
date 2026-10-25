using System.Globalization;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Files;

namespace Strands.Harness.Tests;

public sealed class ReadPagingBoundaryTests
{
    public static IEnumerable<object[]> RegisteredSliceCases()
    {
        foreach (var row in ReadPagingTests.IntegerSliceCases()) yield return row;
        var huge = new string('9', 100);
        var beyondDouble = new string('9', 400);
        const string all = "     1\tA\n     2\tB\n     3\tC\n     4\tD\n     5\tE";
        const string firstTwo = "     1\tA\n     2\tB\n[Showing lines 1-2 of 5. Use offset/limit to read more.]";
        yield return new object[] { "OffsetBeyondDecimal", "{\"path\":\"/selected/file.txt\",\"offset\":" + huge + ",\"limit\":1}", "[File has 5 lines; offset " + huge + " is past the end.]" };
        yield return new object[] { "LimitBeyondDecimal", "{\"path\":\"/selected/file.txt\",\"offset\":1,\"limit\":" + huge + "}", all };
        yield return new object[] { "NegativeOffsetBeyondDecimal", "{\"path\":\"/selected/file.txt\",\"offset\":-" + huge + ",\"limit\":2}", firstTwo };
        yield return new object[] { "NegativeLimitBeyondDecimal", "{\"path\":\"/selected/file.txt\",\"offset\":1,\"limit\":-" + huge + "}", "[File has 5 lines; offset 1 is past the end.]" };
        yield return new object[] { "OffsetBeyondDouble", "{\"path\":\"/selected/file.txt\",\"offset\":" + beyondDouble + ",\"limit\":1}", "[File has 5 lines; offset " + beyondDouble + " is past the end.]" };
        yield return new object[] { "LimitBeyondDouble", "{\"path\":\"/selected/file.txt\",\"offset\":1,\"limit\":" + beyondDouble + "}", all };
    }

    [Theory]
    [MemberData(nameof(RegisteredSliceCases))]
    public async Task RegisteredSchemaAndOriginalRichToolAcceptIntegralSlices(string scenario, string json, string expected)
    {
        var sandbox = new TextSandbox("A\nB\nC\nD\nE");
        var tool = FileTools.CreateRead(sandbox);
        var model = new UnusedModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using var document = JsonDocument.Parse(json);

        var result = await agent.InvokeToolAsync(new("registered", "read", document.RootElement));

        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(expected, result.Content);
        Assert.Equal("registered", result.ToolUseId);
        Assert.Same(tool, Assert.Single(agent.Tools));
        Assert.Equal(1, sandbox.Reads);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("{\"path\":\"/selected/file.txt\",\"limit\":0}", "None")]
    [InlineData("{\"path\":\"/selected/file.txt\",\"offset\":-7,\"limit\":0}", "-7")]
    [InlineData("{\"path\":\"/selected/file.txt\",\"offset\":0,\"limit\":0}", "0")]
    [InlineData("{\"path\":\"/selected/file.txt\",\"offset\":5,\"limit\":-1}", "5")]
    public async Task EmptyWindowsKeepTheOriginalOffsetDiagnostic(string json, string offset)
    {
        var sandbox = new TextSandbox("A\nB\nC\nD\nE");

        var result = await Invoke(FileTools.CreateRead(sandbox), json);

        Assert.Equal("[File has 5 lines; offset " + offset + " is past the end.]", result);
        Assert.Equal(1, sandbox.Reads);
    }

    [Theory]
    [InlineData("\"offset\":\"2\"")]
    [InlineData("\"limit\":true")]
    [InlineData("\"offset\":[]")]
    [InlineData("\"limit\":{}")]
    [InlineData("\"offset\":1.5")]
    [InlineData("\"limit\":1.5")]
    public async Task NonIntegralOrNonnumericInputsNeverReachTheSelectedTextReader(string property)
    {
        var sandbox = new TextSandbox("text");
        var tool = FileTools.CreateRead(sandbox);
        var json = "{\"path\":\"/selected/file.txt\"," + property + "}";
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, json));
        await using var agent = new Agent(new() { Model = new UnusedModel(), Tools = [tool] });
        using var document = JsonDocument.Parse(json);

        var result = await agent.InvokeToolAsync(new("invalid", "read", document.RootElement));

        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal(0, sandbox.Reads);
    }

    [Fact]
    public async Task OmittedPagingRetainsTheTwoThousandLineDefault()
    {
        var content = string.Join("\n", Enumerable.Range(1, 2050).Select(value => "line" + value.ToString(CultureInfo.InvariantCulture)));
        var expected = string.Join("\n", Enumerable.Range(1, 2000).Select(value => value.ToString(CultureInfo.InvariantCulture).PadLeft(6) + "\tline" + value.ToString(CultureInfo.InvariantCulture)));

        var result = await Invoke(FileTools.CreateRead(new TextSandbox(content)), "{\"path\":\"/selected/file.txt\"}");

        Assert.Equal(expected + "\n[Showing lines 1-2000 of 2050. Use offset/limit to read more.]", result);
    }

    [Fact]
    public async Task APageCanContainMoreThanTenThousandAvailableLines()
    {
        var content = string.Join("\n", Enumerable.Range(1, 12050).Select(value => "line" + value.ToString(CultureInfo.InvariantCulture)));
        var expected = string.Join("\n", Enumerable.Range(2000, 10001).Select(value => value.ToString(CultureInfo.InvariantCulture).PadLeft(6) + "\tline" + value.ToString(CultureInfo.InvariantCulture)));

        var result = await Invoke(FileTools.CreateRead(new TextSandbox(content)), "{\"path\":\"/selected/file.txt\",\"offset\":2000,\"limit\":10001}");

        Assert.Equal(expected + "\n[Showing lines 2000-12000 of 12050. Use offset/limit to read more.]", result);
    }

    [Theory]
    [InlineData("", "{\"path\":\"/selected/file.txt\"}", "     1\t")]
    [InlineData("A\n", "{\"path\":\"/selected/file.txt\"}", "     1\tA\n     2\t")]
    [InlineData("\U0001F600e\u0301\r\nZ", "{\"path\":\"/selected/file.txt\",\"limit\":1}", "     1\t\U0001F600e\u0301\r\n[Showing lines 1-1 of 2. Use offset/limit to read more.]")]
    [InlineData("\U0001F600e\u0301\r\nZ", "{\"path\":\"/selected/file.txt\",\"offset\":2,\"limit\":1}", "     2\tZ\n[Showing lines 2-2 of 2. Use offset/limit to read more.]")]
    public async Task IntegerLinePagingPreservesCompleteUnicodeAndSplitSemantics(string content, string json, string expected)
    {
        Assert.Equal(expected, await Invoke(FileTools.CreateRead(new TextSandbox(content)), json));
    }

    [Fact]
    public async Task SelectedReaderFailureRetainsItsOriginalIdentity()
    {
        var failure = new IOException("selected-only failure");
        var sandbox = new TextSandbox("unused", _ => throw failure);

        var error = await Assert.ThrowsAsync<IOException>(() => Invoke(FileTools.CreateRead(sandbox), "{\"path\":\"/selected/file.txt\",\"offset\":null,\"limit\":-1}"));

        Assert.Same(failure, error);
        Assert.Equal(1, sandbox.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationRemainsSelectedAndExact(bool beforeRead)
    {
        using var source = new CancellationTokenSource();
        var sandbox = new TextSandbox("unused", token =>
        {
            Assert.Equal(source.Token, token);
            source.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The requested cancellation must propagate.");
        });
        if (beforeRead) source.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(FileTools.CreateRead(sandbox), "{\"path\":\"/selected/file.txt\",\"offset\":null,\"limit\":-1}", source.Token));

        Assert.Equal(source.Token, error.CancellationToken);
        Assert.Equal(beforeRead ? 0 : 1, sandbox.Reads);
    }

    [Theory]
    [InlineData("/selected/image.PNG", true)]
    [InlineData("/selected/image.PNG", false)]
    [InlineData("/selected/report.PDF", true)]
    [InlineData("/selected/report.PDF", false)]
    public async Task MediaKeepsSelectedBoundedBytesAndIgnoresTextPaging(string path, bool media)
    {
        var sandbox = new BoundedBinarySandbox();
        var tool = FileTools.CreateRead(sandbox, new() { Media = media, MaximumMediaBytes = 2 });
        await using var agent = new Agent(new() { Model = new UnusedModel(), Tools = [tool] });
        var input = JsonSerializer.SerializeToElement(new { path, offset = (int?)null, limit = -7 });

        var result = await agent.InvokeToolAsync(new("media", "read", input));

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("media", result.ToolUseId);
        Assert.Equal(1, sandbox.Reads);
        if (media)
        {
            var block = Assert.Single(result.ContentBlocks);
            var data = block is ImageBlock image ? image.Data : Assert.IsType<DocumentBlock>(block).Data;
            Assert.True(new byte[] { 1, 2 }.SequenceEqual(Convert.FromBase64String(data)));
        }
        else
        {
            var image = path.EndsWith(".PNG", StringComparison.Ordinal);
            Assert.Equal("[" + (image ? "Image" : "Document") + " file: " + path + " (" + (image ? "png" : "pdf") + ", 2 bytes). This model cannot view " + (image ? "image" : "document") + " files, so its contents are not shown. Use shell tools if the file has a text-extractable form.]", result.Content);
        }
    }

    [Fact]
    public void NullableSchemaKeepsTheSameRequiredPathAndFactorySurface()
    {
        var sandbox = new TextSandbox("text");
        foreach (var tool in new[] { FileTools.CreateRead(sandbox), FileTools.CreateRead(sandbox, new()) })
        {
            var schema = tool.Spec.InputSchema;
            Assert.Equal("read", tool.Spec.Name);
            Assert.Equal("path", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            foreach (var name in new[] { "offset", "limit" })
                Assert.Equal(new[] { "integer", "null" }, schema.GetProperty("properties").GetProperty(name).GetProperty("type").EnumerateArray().Select(value => value.GetString()).ToArray());
        }
        Assert.Equal(0, sandbox.Reads);
    }

    private static async Task<string> Invoke(ITool tool, string json, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(json);
        var input = document.RootElement;
        return await tool.InvokeAsync(input, new(null!, new("direct", "read", input), new Dictionary<string, object?>()), cancellationToken);
    }

    private sealed class TextSandbox(string content, Func<CancellationToken, ValueTask<string>>? read = null) : ISandbox
    {
        public int Reads { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            Assert.Equal("/selected/file.txt", path);
            Reads++;
            return read is null ? new(content) : read(cancellationToken);
        }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No selected writes are permitted.");
    }

    private sealed class BoundedBinarySandbox : ISandbox, IReadOnlyFileSystem, IBoundedFileReader
    {
        public int Reads { get; private set; }
        public ValueTask<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken = default)
        {
            Assert.True(path is "/selected/image.PNG" or "/selected/report.PDF");
            Assert.Equal(2, maximumBytes);
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return new(new byte[] { 1, 2 });
        }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Media must not use text reads.");
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No directory access is permitted.");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No writes are permitted.");
    }

    private sealed class UnusedModel : IModel
    {
        public int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Direct read tools must not call a model.");
        }
    }
}
