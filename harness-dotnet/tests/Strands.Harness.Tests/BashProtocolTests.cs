using System.Text;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed partial class BashTests
{
    [Fact]
    public Task WhitespaceOnlyCommandsAreValidAndProduceEmptyStreams() => WithFixture(async fixture =>
    {
        var result = ResultData(await fixture.Start(" \t\n "));
        Assert.Equal("", result.GetProperty("output").GetString());
        Assert.Equal("", result.GetProperty("error").GetString());
    });

    [Fact]
    public Task NonzeroCommandStatusDoesNotInventAnExitCodeResult() => WithFixture(async fixture =>
    {
        var result = ResultData(await fixture.Start("builtin printf useful; builtin printf warning >&2; false"));
        Assert.Equal("useful", result.GetProperty("output").GetString());
        Assert.Equal("warning", result.GetProperty("error").GetString());
        Assert.Equal(new[] { "output", "error" }, result.EnumerateObject().Select(property => property.Name));
        Assert.Equal("next", await fixture.Run("builtin printf next"));
    });

    [Fact]
    public Task BothStreamFencesCompleteBeforeTheNextCommand() => WithFixture(async fixture =>
    {
        var result = ResultData(await fixture.Start("builtin printf stdout; sleep 0.05; builtin printf stderr >&2"));
        Assert.Equal("stdout", result.GetProperty("output").GetString());
        Assert.Equal("stderr", result.GetProperty("error").GetString());
        var empty = ResultData(await fixture.Start(":"));
        Assert.Equal("", empty.GetProperty("output").GetString());
        Assert.Equal("", empty.GetProperty("error").GetString());
    });

    [Fact]
    public Task CommandQuotingPreservesQuotesOperatorsNewlinesAndUnicode() => WithFixture(async fixture =>
    {
        const string expected = "snow 雪 'quoted' \"double\" $HOME & | ; % ^\nsecond line 🚀";
        Assert.Equal(expected, await fixture.Run("builtin printf '%s' " + ShellQuote(expected)));
        Assert.Equal("unchanged", await fixture.Run("builtin printf '%s' \"${STRANDS_NOT_EXECUTED-unchanged}\""));
    });

    [Theory]
    [InlineData("euro", "€")]
    [InlineData("cjk", "雪")]
    [InlineData("astral", "🚀")]
    public Task Utf8CodePointsSurviveOneByteWritesOnBothStreams(string caseName, string value) => WithFixture(async fixture =>
    {
        Assert.False(string.IsNullOrWhiteSpace(caseName));
        // Separate writes exercise incremental collection. OS read boundaries remain nondeterministic.
        var fragments = Encoding.UTF8.GetBytes(value).Select(b => "builtin printf '\\" + Convert.ToString(b, 8).PadLeft(3, '0') + "'; sleep 0.02");
        var emit = string.Join("; ", fragments);
        var result = ResultData(await fixture.Start(emit + "; { " + emit + "; } >&2"));
        Assert.Equal(value, result.GetProperty("output").GetString());
        Assert.Equal(value, result.GetProperty("error").GetString());
    });

    [Theory]
    [InlineData("ecmascript-space", "\t\r\n \u00a0\u1680\u2000\u2028\u2029\u202f\u205f\u3000\ufeff", "payload")]
    [InlineData("nel-is-not-js-space", "\u0085", "\u0085payload\u0085")]
    public Task OutputTrimmingMatchesThePinnedJavaScriptWhitespaceSet(string caseName, string surround, string expected) => WithFixture(async fixture =>
    {
        Assert.False(string.IsNullOrWhiteSpace(caseName));
        var value = surround + "payload" + surround;
        var result = ResultData(await fixture.Start("builtin printf '%s' " + ShellQuote(value) + "; builtin printf '%s' " + ShellQuote(value) + " >&2"));
        Assert.Equal(expected, result.GetProperty("output").GetString());
        Assert.Equal(expected, result.GetProperty("error").GetString());
    });

    [Fact]
    public Task UnrelatedFenceShapedOutputIsOrdinaryData() => WithFixture(async fixture =>
    {
        const string fake = "\u001e00000000000000000000000000000000:out\u001f";
        Assert.Equal(fake + "tail", await fixture.Run("builtin printf '%s' " + ShellQuote(fake) + "; sleep 0.05; builtin printf tail"));
    });

    [Fact]
    public Task CombinedOutputLimitRetainsBoundedPartialAndAllowsAFreshGeneration() => WithFixture(async fixture =>
    {
        var error = await Assert.ThrowsAsync<BashSessionException>(async () => await fixture.Start("builtin printf '%080d' 0; builtin printf '%080d' 0 >&2"));
        Assert.Contains("MaximumOutputBytes", error.Message);
        var output = Assert.IsType<string>(error.Data["Strands.Bash.StandardOutput"]);
        var standardError = Assert.IsType<string>(error.Data["Strands.Bash.StandardError"]);
        Assert.InRange(Encoding.UTF8.GetByteCount(output) + Encoding.UTF8.GetByteCount(standardError), 1, 128);
        Assert.Equal("after", await fixture.Run("builtin printf after"));
    }, root => new() { ExecutablePath = Environment.GetEnvironmentVariable("STRANDS_BASH_TEST_EXECUTABLE"), WorkingDirectory = root, MaximumOutputBytes = 128 });

    [Fact]
    public Task ExecutionDeadlinePreservesPartialOutputAndReplacesTheStoppedGeneration() => WithFixture(async fixture =>
    {
        // Establish both completion fences before measuring a command's additive partial-output contract.
        Assert.Equal("", await fixture.Run(":"));
        var error = await Assert.ThrowsAsync<BashTimeoutException>(async () => await fixture.Start("STRANDS_OLD_GENERATION=present; builtin printf partial; builtin printf diagnostic >&2; sleep 10", timeout: 1.5));
        Assert.Equal("partial", error.Data["Strands.Bash.StandardOutput"]);
        Assert.Equal("diagnostic", error.Data["Strands.Bash.StandardError"]);
        Assert.Equal("fresh", await fixture.Run("builtin printf '%s' \"${STRANDS_OLD_GENERATION-fresh}\""));
    });
}
