using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tests;

public sealed class ReadPagingTests
{
    public static IEnumerable<object[]> IntegerSliceCases()
    {
        yield return new object[] { "NullOffset", "{\"path\":\"/selected/file.txt\",\"offset\":null,\"limit\":2}", "     1\tA\n     2\tB\n[Showing lines 1-2 of 5. Use offset/limit to read more.]" };
        yield return new object[] { "NullLimit", "{\"path\":\"/selected/file.txt\",\"offset\":2,\"limit\":null}", "     2\tB\n     3\tC\n     4\tD\n     5\tE\n[Showing lines 2-5 of 5. Use offset/limit to read more.]" };
        yield return new object[] { "ZeroOffset", "{\"path\":\"/selected/file.txt\",\"offset\":0,\"limit\":2}", "     1\tA\n     2\tB\n[Showing lines 1-2 of 5. Use offset/limit to read more.]" };
        yield return new object[] { "NegativeOffset", "{\"path\":\"/selected/file.txt\",\"offset\":-7,\"limit\":2}", "     1\tA\n     2\tB\n[Showing lines 1-2 of 5. Use offset/limit to read more.]" };
        yield return new object[] { "ZeroLimit", "{\"path\":\"/selected/file.txt\",\"offset\":1,\"limit\":0}", "[File has 5 lines; offset 1 is past the end.]" };
        yield return new object[] { "NegativeEndRelativeToFile", "{\"path\":\"/selected/file.txt\",\"offset\":1,\"limit\":-1}", "     1\tA\n     2\tB\n     3\tC\n     4\tD\n[Showing lines 1-4 of 5. Use offset/limit to read more.]" };
        yield return new object[] { "NegativeEndRelativeAfterStart", "{\"path\":\"/selected/file.txt\",\"offset\":2,\"limit\":-3}", "     2\tB\n     3\tC\n[Showing lines 2-3 of 5. Use offset/limit to read more.]" };
        yield return new object[] { "EndBeforeStart", "{\"path\":\"/selected/file.txt\",\"offset\":3,\"limit\":-1}", "[File has 5 lines; offset 3 is past the end.]" };
        yield return new object[] { "LimitAboveTenThousand", "{\"path\":\"/selected/file.txt\",\"offset\":1,\"limit\":10001}", "     1\tA\n     2\tB\n     3\tC\n     4\tD\n     5\tE" };
        yield return new object[] { "HugePositiveOffset", "{\"path\":\"/selected/file.txt\",\"offset\":922337203685477580812345,\"limit\":1}", "[File has 5 lines; offset 922337203685477580812345 is past the end.]" };
        yield return new object[] { "HugePositiveLimit", "{\"path\":\"/selected/file.txt\",\"offset\":1,\"limit\":922337203685477580812345}", "     1\tA\n     2\tB\n     3\tC\n     4\tD\n     5\tE" };
        yield return new object[] { "HugeNegativeOffset", "{\"path\":\"/selected/file.txt\",\"offset\":-922337203685477580812345,\"limit\":2}", "     1\tA\n     2\tB\n[Showing lines 1-2 of 5. Use offset/limit to read more.]" };
        yield return new object[] { "HugeNegativeLimit", "{\"path\":\"/selected/file.txt\",\"offset\":1,\"limit\":-922337203685477580812345}", "[File has 5 lines; offset 1 is past the end.]" };
        yield return new object[] { "NullOffsetEmptyWindow", "{\"path\":\"/selected/file.txt\",\"offset\":null,\"limit\":0}", "[File has 5 lines; offset None is past the end.]" };
    }

    [Theory]
    [MemberData(nameof(IntegerSliceCases))]
    public async Task IntegralPagingMatchesThePinnedSource(string scenario, string json, string expected)
    {
        var sandbox = new SelectedTextSandbox();
        var tool = FileTools.CreateRead(sandbox);
        using var document = JsonDocument.Parse(json);
        var input = document.RootElement;
        var context = new ToolContext(null!, new("slice", tool.Spec.Name, input), new Dictionary<string, object?>());

        var result = await tool.InvokeAsync(input, context);

        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(expected, result);
        Assert.Equal(1, sandbox.Reads);
        Assert.Equal("/selected/file.txt", sandbox.LastPath);
        Assert.Equal(CancellationToken.None, sandbox.LastToken);
    }

    private sealed class SelectedTextSandbox : ISandbox
    {
        public int Reads { get; private set; }
        public string? LastPath { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("/selected/file.txt", path);
            Reads++;
            LastPath = path;
            LastToken = cancellationToken;
            return new("A\nB\nC\nD\nE");
        }

        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The selected read-only fixture cannot write.");
    }
}
