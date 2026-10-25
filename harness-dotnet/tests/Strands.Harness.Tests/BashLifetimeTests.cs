using System.Text;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed partial class BashTests
{
    [Fact]
    public Task ShuttingDownOneAgentLeavesAnotherBorrowedAssociationUsable() => WithFixture(async fixture =>
    {
        var second = fixture.AddAgent();
        await fixture.Run("STRANDS_FIRST_AGENT=first");
        await fixture.Run("STRANDS_SECOND_AGENT=second", second);
        await fixture.Agent.ShutdownAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await fixture.Start(":"));
        Assert.Equal("second", await fixture.Run("builtin printf '%s' \"$STRANDS_SECOND_AGENT\"", second));
        Assert.Equal("absent", await fixture.Run("builtin printf '%s' \"${STRANDS_FIRST_AGENT-absent}\"", second));
    });

    [Fact]
    public Task AgentShutdownPhysicallyJoinsAnActiveAssociation() => WithFixture(async fixture =>
    {
        var gate = fixture.Gate();
        var active = fixture.Start(gate.Command);
        await BashFixture.Ready(gate.Ready);
        using var root = BindOwnedBashRoot(fixture);
        var processes = fixture.ObserveNewInterpreters();
        Assert.Contains(processes, process => process.Id == root.Id);
        await Settled(fixture.Agent.ShutdownAsync());
        AssertOwnedAssociationExited(root, processes, "agent-shutdown");
        Assert.IsType<BashSessionException>(await Failed(active));
    });

    [Fact]
    public Task ToolDisposalJoinsBothActiveAgentSessions() => WithFixture(async fixture =>
    {
        var second = fixture.AddAgent();
        var firstGate = fixture.Gate("first");
        var secondGate = fixture.Gate("second");
        var first = fixture.Start(firstGate.Command);
        var other = fixture.Start(secondGate.Command, second);
        await BashFixture.Ready(firstGate.Ready);
        await BashFixture.Ready(secondGate.Ready);
        using var firstRoot = BindOwnedBashRoot(fixture);
        using var secondRoot = BindOwnedBashRoot(fixture, second);
        Assert.NotEqual(firstRoot.Id, secondRoot.Id);
        var processes = fixture.ObserveNewInterpreters();
        Assert.Contains(processes, process => process.Id == firstRoot.Id);
        Assert.Contains(processes, process => process.Id == secondRoot.Id);
        await Settled(fixture.Tool.DisposeAsync().AsTask());
        Exception? physicalFailure = null;
        try { BashFixture.AssertExited([firstRoot, secondRoot]); }
        catch (Exception error) { physicalFailure = error; throw; }
        finally
        {
            try
            {
                _bashOutput.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    Phase = "two-agent-dispose-owned-roots-and-same-image-observations",
                    OwnedRoots = new[] { ProcessObservation(firstRoot), ProcessObservation(secondRoot) },
                    SameImageObserved = processes.Select(ProcessObservation).ToArray()
                }));
            }
            catch (Exception diagnostic) when (physicalFailure is not null) { physicalFailure.Data["BashTest.Observation"] = diagnostic; }
        }
        Assert.IsType<BashSessionException>(await Failed(first));
        Assert.IsType<BashSessionException>(await Failed(other));
    });

    [Fact]
    public Task ConcurrentToolDisposalUsesOneCompletionAndPermanentlyClosesAdmission() => WithFixture(async fixture =>
    {
        await fixture.Run(":");
        var first = fixture.Tool.DisposeAsync().AsTask();
        var second = fixture.Tool.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await Settled(first);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await fixture.Start(":"));
        Assert.Same(first, fixture.Tool.DisposeAsync().AsTask());
    });

    [Fact]
    public Task AnAgentAlreadyShutDownCannotEstablishANewInterpreter() => WithFixture(async fixture =>
    {
        await fixture.Agent.ShutdownAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await fixture.Start("builtin printf should-not-run"));
    });

    [Fact]
    public async Task ExistingMalformedExecutableFailsRepeatedlyWithoutExecutingItsText()
    {
        var root = Path.Combine(Path.GetTempPath(), "strands-bash-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "must-not-exist");
        var executable = Path.Combine(root, "malformed.exe");
        File.WriteAllText(executable, "builtin printf unsafe > " + ShellPath(marker), new UTF8Encoding(false));
        try
        {
            await using var tool = Bash.Create(new() { ExecutablePath = executable, WorkingDirectory = root });
            await using var agent = NewAgent();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var task = Invoke(tool, agent, new { mode = "execute", command = ":" });
                var error = await Failed(task);
                var session = Assert.IsType<BashSessionException>(error);
                Assert.NotNull(session.InnerException);
                Assert.False(File.Exists(marker));
            }
            await Settled(tool.DisposeAsync().AsTask());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public Task MissingWorkingDirectoryDoesNotLeaveAStartedGeneration() => WithFixture(async fixture =>
    {
        var error = await Failed(fixture.Start(":"));
        Assert.NotNull(Assert.IsType<BashSessionException>(error).InnerException);
        await Settled(fixture.Tool.DisposeAsync().AsTask());
    }, root => new() { ExecutablePath = Environment.GetEnvironmentVariable("STRANDS_BASH_TEST_EXECUTABLE"), WorkingDirectory = Path.Combine(root, "missing-directory") });

    [Fact]
    public Task ExplicitInterpreterExitFailsTheCommandAndAllowsAFreshSession() => WithFixture(async fixture =>
    {
        await fixture.Run("STRANDS_EXIT_VALUE=old");
        using var root = BindOwnedBashRoot(fixture);
        var processes = fixture.ObserveNewInterpreters();
        Assert.Contains(processes, process => process.Id == root.Id);
        var exitError = Assert.IsType<BashSessionException>(await Failed(fixture.Start("exit 7")));
        if (exitError.Data["Strands.CleanupErrors"] is IEnumerable<Exception> cleanupErrors)
        {
            foreach (var cleanupException in cleanupErrors) _bashOutput.WriteLine(cleanupException.ToString());
        }
        Assert.Null(exitError.Data["Strands.CleanupErrors"]);
        BashFixture.AssertExited([root]);
        RecordExitMembership(root, processes);
        Assert.Equal("fresh", await fixture.Run("builtin printf '%s' \"${STRANDS_EXIT_VALUE-fresh}\""));
    });

    [Fact]
    public async Task RestartIsLazyAndNeverRequiresAnInterpreterUntilExecute()
    {
        await using var tool = Bash.Create(new() { ExecutablePath = Path.Combine(Path.GetTempPath(), "absent-bash-" + Guid.NewGuid().ToString("N")) });
        await using var agent = NewAgent();
        Assert.Equal("Bash session restarted", (await Invoke(tool, agent, new { mode = "restart" })).Content);
        Assert.Equal("Bash session restarted", (await Invoke(tool, agent, new { mode = "restart" })).Content);
        Assert.IsType<BashSessionException>(await Failed(Invoke(tool, agent, new { mode = "execute", command = ":" })));
    }
}
