using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.Middleware;

public sealed class MiddlewareStageFactoryArgumentTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("snapshotContext")]
    [InlineData("snapshotResult")]
    public void FactoryRejectsEachNullArgumentBeforeSnapshotDelegatesRun(string argument)
    {
        var contextSnapshots = 0;
        var resultSnapshots = 0;
        Func<string, string> snapshotContext = value => { contextSnapshots++; return value; };
        Func<string, string> snapshotResult = value => { resultSnapshots++; return value; };

        var error = Assert.Throws<ArgumentNullException>(() => MiddlewareStages.Create<string, string>(
            argument == "name" ? null! : "valid-stage",
            argument == "snapshotContext" ? null! : snapshotContext,
            argument == "snapshotResult" ? null! : snapshotResult));

        Assert.Equal(argument, error.ParamName);
        Assert.Equal(0, contextSnapshots);
        Assert.Equal(0, resultSnapshots);
    }
}
