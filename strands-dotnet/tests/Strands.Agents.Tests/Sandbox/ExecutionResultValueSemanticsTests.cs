using Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class ExecutionResultValueSemanticsTests
{
    [Fact]
    public void IndependentlyConstructedLegacyResultsKeepTheirValueSemantics()
    {
        var left = new ExecutionResult(7, "stdout", "stderr");
        var same = new ExecutionResult(7, "stdout", "stderr");
        var copy = left with { };
        var different = new[]
        {
            left with { ExitCode = 8 },
            left with { Stdout = "other stdout" },
            left with { Stderr = "other stderr" },
            left with { OutputTruncated = true },
        };

        Assert.True(left.Equals(copy));
        Assert.True(left == copy);
        Assert.All(different, value =>
        {
            Assert.False(left.Equals(value));
            Assert.True(left != value);
        });

        var observed = new[]
        {
            left.Equals(same),
            left == same,
            left.GetHashCode() == same.GetHashCode(),
            new HashSet<ExecutionResult> { left, same }.Count == 1,
        };
        Assert.Equal(new[] { true, true, true, true }, observed);
    }

    [Fact]
    public void ExplicitEmptyArtifactsAreEquivalentToLegacyDefaultMembership()
    {
        var original = new ExecutionResult(0, "", "");
        var array = new ExecutionResult(0, "", "") { OutputFiles = Array.Empty<OutputFile>() };
        var list = new ExecutionResult(0, "", "") { OutputFiles = new List<OutputFile>() };
        Assert.Empty(original.OutputFiles);
        Assert.Empty(array.OutputFiles);
        Assert.Empty(list.OutputFiles);

        var observed = new[]
        {
            original.Equals(array),
            original == array,
            original.GetHashCode() == array.GetHashCode(),
            new HashSet<ExecutionResult> { original, array }.Count == 1,
            original.Equals(list),
            original == list,
            original.GetHashCode() == list.GetHashCode(),
            new HashSet<ExecutionResult> { original, list }.Count == 1,
        };
        Assert.Equal(new[] { true, true, true, true, true, true, true, true }, observed);
    }
}
