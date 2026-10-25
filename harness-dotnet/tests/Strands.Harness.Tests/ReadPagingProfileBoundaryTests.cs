using System.Globalization;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Files;

namespace Strands.Harness.Tests;

public sealed class ReadPagingProfileBoundaryTests
{
    private const string First = "     1\tA\n[Showing lines 1-1 of 5. Use offset/limit to read more.]";
    private const string Second = "     2\tB\n[Showing lines 2-2 of 5. Use offset/limit to read more.]";

    // These are the captured Pydantic 2.5/core 2.14.1 branches, including its
    // original UTF-8 length dispatch to num-bigint 0.4.4. They are not int(text).
    public static IEnumerable<object[]> PythonCases()
    {
        yield return Case("EighteenBytesShortParser", Quote(new string('0', 17) + "2"), Second);
        yield return Case("NineteenBytesBigParser", Quote(new string('0', 18) + "2"), Second);
        yield return Case("ShortSingleUnderscoreFallback", Quote("0_2"), Second);
        yield return Case("ShortDoubleUnderscoreRejected", Quote("0__2"), null);
        yield return Case("LongDoubleUnderscoreAccepted", Quote("0__" + new string('0', 15) + "2"), Second);
        yield return Case("ShortTrailingUnderscoresRejected", Quote("0" + new string('_', 17)), null);
        yield return Case("LongTrailingUnderscoresAccepted", Quote("0" + new string('_', 18)), First);
        yield return Case("LongLeadingUnderscoreRejected", Quote("_" + new string('0', 18)), null);
        yield return Case("DecimalEmptySuffix", Quote("2."), Second);
        yield return Case("DecimalZeros", Quote("2.000"), Second);
        yield return Case("DecimalBranchDoesNotTryShortUnderscoreFallback", Quote("0_2.0"), null);
        yield return Case("DecimalBranchRetainsOriginalLongLength", Quote("0__2." + new string('0', 14)), Second);
        yield return Case("PositiveSign", Quote("+2"), Second);
        yield return Case("NegativeSign", Quote("-2"), First);
        yield return Case("NoWhitespaceCoercion", Quote(" 2"), null);
        yield return Case("NoUnicodeDigitCoercion", Quote("Ù¢"), null);
        yield return Case("NoTrailingNul", Quote("2\0"), null);
        yield return Case("Exact4300ByteBoundary", Quote(new string('0', 4299) + "1"), First);
        yield return Case("Reject4301ByteInput", Quote(new string('0', 4300) + "1"), null);
        yield return Case("Utf8GrammarBoundary", Quote(new string('0', 4298) + "Ã©"), null);
        yield return Case("Utf8SizeBoundary", Quote(new string('0', 4299) + "Ã©"), null);
        yield return Case("IntegerLiteralI64MaxIsExact", "9223372036854775807", Past("9223372036854775807"));
        yield return Case("IntegerLiteralI64MinIsExact", "-9223372036854775808", First);
        yield return Case("FloatMaxRoundedBoundaryRejected", "9223372036854775807.0", null);
        yield return Case("FloatMinBoundaryRejected", "-9223372036854775808.0", null);
        yield return Case("FloatBelowMaxAccepted", "9223372036854774784.0", Past("9223372036854774784"));
        yield return Case("FloatAboveMinAccepted", "-9223372036854774784.0", First);
        yield return Case("FloatOutsideI64Rejected", "1e308", null);
        yield return Case("HugeIntegerLiteralDoesNotBecomeFloat", new string('7', 400), Past(new string('7', 400)));
        yield return Case("FloatUnderflowIsZero", "1e-400", First);
        yield return Case("FloatOverflowRejected", "1e400", null);
        yield return Case("FloatRoundingPrecedesIntegralCheck", "2.0000000000000001", Second);
    }

    public static IEnumerable<object[]> PythonDirectAndRegistered()
    {
        foreach (var row in PythonCases())
            foreach (var registered in new[] { false, true })
                yield return row.Concat(new object[] { registered }).ToArray();
    }

    [Theory]
    [MemberData(nameof(PythonDirectAndRegistered))]
    public async Task PythonReferenceBranchesAreAppliedBeforeSelectedIo(string scenario, string rawOffset, string? expected, bool registered)
    {
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        var sandbox = new SelectedSandbox();
        var tool = FileTools.CreateRead(sandbox, new() { PagingProfile = ReadPagingProfile.Python });
        using var document = JsonDocument.Parse("{\"path\":\"/selected/file.txt\",\"offset\":" + rawOffset + ",\"limit\":1}");
        if (expected is null)
        {
            if (registered)
            {
                await using var agent = new Agent(new() { Model = new UnusedModel(), Tools = [tool] });
                var result = await agent.InvokeToolAsync(new("read-boundary", "read", document.RootElement));
                Assert.Equal(ToolResultStatus.Error, result.Status);
            }
            else await Assert.ThrowsAnyAsync<ArgumentException>(async () => await tool.InvokeAsync(document.RootElement, Context(document.RootElement)));
            Assert.Equal(0, sandbox.TextReads);
        }
        else
        {
            Assert.Equal(expected, await Invoke(tool, document.RootElement, registered));
            Assert.Equal(1, sandbox.TextReads);
        }
        Assert.Equal(0, sandbox.BinaryReads);
    }

    // RFC 8785 Appendix B provides independent ECMAScript serialization vectors.
    // Its IEEE bit patterns avoid relying on a .NET decimal formatter as an oracle.
    public static IEnumerable<object[]> EcmaVectors()
    {
        yield return Vector("0000000000000000", "0");
        yield return Vector("8000000000000000", "0");
        yield return Vector("0000000000000001", "5e-324");
        yield return Vector("8000000000000001", "-5e-324");
        yield return Vector("7fefffffffffffff", "1.7976931348623157e+308");
        yield return Vector("ffefffffffffffff", "-1.7976931348623157e+308");
        yield return Vector("4340000000000000", "9007199254740992");
        yield return Vector("c340000000000000", "-9007199254740992");
        yield return Vector("4430000000000000", "295147905179352830000");
        yield return Vector("44b52d02c7e14af5", "9.999999999999997e+22");
        yield return Vector("44b52d02c7e14af6", "1e+23");
        yield return Vector("44b52d02c7e14af7", "1.0000000000000001e+23");
        yield return Vector("444b1ae4d6e2ef4e", "999999999999999700000");
        yield return Vector("444b1ae4d6e2ef4f", "999999999999999900000");
        yield return Vector("444b1ae4d6e2ef50", "1e+21");
        yield return Vector("3eb0c6f7a0b5ed8c", "9.999999999999997e-7");
        yield return Vector("3eb0c6f7a0b5ed8d", "0.000001");
        yield return Vector("41b3de4355555553", "333333333.3333332");
        yield return Vector("41b3de4355555554", "333333333.33333325");
        yield return Vector("41b3de4355555555", "333333333.3333333");
        yield return Vector("41b3de4355555556", "333333333.3333334");
        yield return Vector("41b3de4355555557", "333333333.33333343");
        yield return Vector("becbf647612f3696", "-0.0000033333333333333333");
        yield return Vector("43143ff3c1cb0959", "1424953923781206.2");
    }

    public static IEnumerable<object[]> EcmaDirectAndRegistered()
    {
        foreach (var row in EcmaVectors())
            foreach (var registered in new[] { false, true })
                yield return row.Concat(new object[] { registered }).ToArray();
    }

    [Theory]
    [MemberData(nameof(EcmaDirectAndRegistered))]
    public async Task TypeScriptLabelsMatchIndependentIeeeSerializationVectors(string ieee, string expected, bool registered)
    {
        var bits = ulong.Parse(ieee, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var value = BitConverter.Int64BitsToDouble(unchecked((long)bits));
        Assert.Equal(expected, ReadPagingNumber.Format(value));
        var sandbox = new SelectedSandbox();
        var tool = FileTools.CreateRead(sandbox, new() { PagingProfile = ReadPagingProfile.TypeScript });
        var raw = ieee == "8000000000000000" ? "-0" : expected;
        using var document = JsonDocument.Parse("{\"path\":\"/selected/file.txt\",\"offset\":" + raw + ",\"limit\":0}");
        Assert.Equal(Past(expected), await Invoke(tool, document.RootElement, registered));
        Assert.Equal(1, sandbox.TextReads);
        Assert.Equal(0, sandbox.BinaryReads);
    }

    [Theory]
    [InlineData("0.5", "1.9", First)]
    [InlineData("-1.5", "-1.5", "     1\tA\n     2\tB\n     3\tC\n     4\tD\n[Showing lines 1-4 of 5. Use offset/limit to read more.]")]
    [InlineData("2.5", "-4", "   2.5\tB\n   3.5\tC\n[Showing lines 2.5-3.5 of 5. Use offset/limit to read more.]")]
    [InlineData("2", "1e-400", "[File has 5 lines; offset 2 is past the end.]")]
    [InlineData("1.7976931348623157e308", "1.7976931348623157e308", "[File has 5 lines; offset 1.7976931348623157e+308 is past the end.]")]
    [InlineData("1.7976931348623157e308", "-1.7976931348623157e308", "[File has 5 lines; offset 1.7976931348623157e+308 is past the end.]")]
    public async Task TypeScriptEndpointAdditionPrecedesTruncationAndClampsFiniteOverflow(string offset, string limit, string expected)
    {
        var sandbox = new SelectedSandbox();
        var tool = FileTools.CreateRead(sandbox, new() { PagingProfile = ReadPagingProfile.TypeScript });
        using var document = JsonDocument.Parse("{\"path\":\"/selected/file.txt\",\"offset\":" + offset + ",\"limit\":" + limit + "}");
        Assert.Equal(expected, await Invoke(tool, document.RootElement, true));
        Assert.Equal(1, sandbox.TextReads);
    }

    [Fact]
    public void NonfiniteValuesCannotBecomeTypeScriptLabels()
    {
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => ReadPagingNumber.Format(value));
    }

    [Fact]
    public async Task CallerCancellationPrecedesExplicitProfileValidationAndAnySelectedIo()
    {
        foreach (var profile in new[] { ReadPagingProfile.NativeIntegral, ReadPagingProfile.Python, ReadPagingProfile.TypeScript })
        {
            var sandbox = new SelectedSandbox();
            var tool = FileTools.CreateRead(sandbox, new() { PagingProfile = profile });
            using var document = JsonDocument.Parse("{\"path\":\"/selected/image.PNG\",\"offset\":{}}");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tool.InvokeAsync(document.RootElement, Context(document.RootElement), cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(0, sandbox.TextReads);
            Assert.Equal(0, sandbox.BinaryReads);
        }
    }

    [Fact]
    public async Task DefaultMediaKeepsItsExistingPagingIndependenceAndBound()
    {
        var sandbox = new SelectedSandbox();
        var tool = FileTools.CreateRead(sandbox, new() { MaximumMediaBytes = 2, Media = true });
        using var document = JsonDocument.Parse("{\"path\":\"/selected/image.PNG\",\"offset\":{}}");
        var result = await Assert.IsAssignableFrom<IRichTool>(tool).InvokeResultAsync(document.RootElement, Context(document.RootElement));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Single(result.ContentBlocks.OfType<ImageBlock>());
        Assert.Equal(1, sandbox.BinaryReads);
        Assert.Equal(0, sandbox.TextReads);
    }

    [Fact]
    public async Task AssembledHarnessUsesTheExplicitPagingProfileAndBorrowedSandbox()
    {
        var sandbox = new SelectedSandbox();
        var model = new UnusedModel();
        await using var agent = await Harness.CreateAsync(new()
        {
            Model = model,
            Sandbox = sandbox,
            BuiltinTools = ["read"],
            BuiltinPlugins = [],
            BackgroundTasks = null,
            Skills = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Read = new() { PagingProfile = ReadPagingProfile.TypeScript }
        });
        Assert.Same(model, agent.Model);
        Assert.Equal("read", Assert.Single(agent.Tools).Spec.Name);
        using var document = JsonDocument.Parse("{\"path\":\"/selected/file.txt\",\"offset\":2.5,\"limit\":2}");
        var result = await agent.InvokeToolAsync(new("harness-profile", "read", document.RootElement));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("   2.5\tB\n   3.5\tC\n[Showing lines 2.5-3.5 of 5. Use offset/limit to read more.]", result.Content);
        Assert.Equal(1, sandbox.TextReads);
        Assert.Equal(0, sandbox.BinaryReads);
    }

    private static object[] Case(string scenario, string raw, string? expected) => [scenario, raw, expected!];
    private static object[] Vector(string ieee, string expected) => [ieee, expected];
    private static string Quote(string text) => JsonSerializer.Serialize(text);
    private static string Past(string offset) => "[File has 5 lines; offset " + offset + " is past the end.]";
    private static ToolContext Context(JsonElement input) => new(null!, new("read-boundary", "read", input), new Dictionary<string, object?>());

    private static async Task<string> Invoke(ITool tool, JsonElement input, bool registered)
    {
        if (!registered) return await tool.InvokeAsync(input, Context(input));
        await using var agent = new Agent(new() { Model = new UnusedModel(), Tools = [tool] });
        var result = await agent.InvokeToolAsync(new("read-boundary", "read", input));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Same(tool, Assert.Single(agent.Tools));
        return result.Content;
    }

    private sealed class SelectedSandbox : ISandbox, IReadOnlyFileSystem, IBoundedFileReader
    {
        public int TextReads { get; private set; }
        public int BinaryReads { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("/selected/file.txt", path);
            TextReads++;
            return new("A\nB\nC\nD\nE");
        }
        public ValueTask<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("/selected/image.PNG", path);
            Assert.Equal(2, maximumBytes);
            BinaryReads++;
            return new(new byte[] { 1, 2 });
        }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No directory reads are permitted.");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No writes are permitted.");
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Read must not invoke a model.");
    }
}
