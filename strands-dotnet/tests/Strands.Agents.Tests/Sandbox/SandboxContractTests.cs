using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxContractTests
{
    [Theory]
    [InlineData("ProxyCommand=touch forbidden")]
    [InlineData("LocalCommand=touch forbidden")]
    [InlineData("PermitLocalCommand yes")]
    [InlineData("Include=other.conf")]
    public void SshRejectsOptionsThatCanExecuteLocalCodeOrLoadConfiguration(string option)
    {
        var error = Assert.Throws<ArgumentException>(() => new SshSandbox(new() { Host = "localhost", WorkingDirectory = "/task", SshOptions = new[] { option } }));
        Assert.Contains("not allowed", error.Message);
    }

    [Fact]
    public void SshPolicySnapshotsOptionsAndSupportsAnExplicitUnsafeOptIn()
    {
        var values = new[] { "cOnNeCtTiMeOuT=5" };
        var sandbox = new SshSandbox(new() { Host = "localhost", WorkingDirectory = "/task", SshOptions = values });
        values[0] = "ProxyCommand=forbidden";
        Assert.Equal("cOnNeCtTiMeOuT=5", Assert.Single(sandbox.Options.SshOptions));
        _ = new SshSandbox(new() { Host = "localhost", WorkingDirectory = "/task", AllowUnsafeSshOptions = true, SshOptions = new[] { "UserKnownHostsFile=/owned/known_hosts" } });
    }

    [Fact]
    public async Task SshUsesLiteralClientArgvAndQuotedTargetEnvironment()
    {
        var sandbox = new SshSandbox(new()
        {
            Host = "-oProxyCommand=not-local",
            WorkingDirectory = "/task's root",
            IdentityFile = "key file 'space'",
            Port = 2222,
            SshOptions = new[] { "ConnectTimeout=5" },
            Process = Fixture(),
        });
        var result = await sandbox.ExecuteAsync("printf '%s' \"$VALUE\"", new() { Environment = new Dictionary<string, string> { ["VALUE"] = "a' b;$HOME\nnext" } });
        using var parsed = JsonDocument.Parse(result.Stdout);
        var arguments = parsed.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(new[] { "-o", "StrictHostKeyChecking=accept-new", "-o", "BatchMode=yes", "-p", "2222", "-i", "key file 'space'", "-o", "ConnectTimeout=5", "--", "-oProxyCommand=not-local", "cd '/task'\\''s root' && export VALUE='a'\\'' b;$HOME\nnext' && printf '%s' \"$VALUE\"" }, arguments);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RealClientProcessAcceptsLargeDetachedStdinAndReportsNonzeroExit()
    {
        var data = Enumerable.Range(0, 1_500_000).Select(value => (byte)(value * 31)).ToArray();
        var expected = (byte[])data.Clone();
        var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = Fixture("stdin", "17") });
        var pending = sandbox.ExecuteAsync("opaque command", new() { StandardInput = data }).AsTask();
        Array.Clear(data, 0, data.Length);
        var result = await pending;
        Assert.Equal(expected, Convert.FromBase64String(result.Stdout));
        Assert.Equal(17, result.ExitCode);
    }

    [Fact]
    public async Task SuccessfulExecutionWithADeadlineIsNotClassifiedAsATimeout()
    {
        var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = Fixture() });
        var result = await sandbox.ExecuteAsync("true", new() { Timeout = TimeSpan.FromSeconds(10) });
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void TargetEnvironmentNamesRequireAsciiPosixIdentifiers()
    {
        var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = Fixture() });
        Assert.Throws<ArgumentException>(() => sandbox.ExecuteStreamingAsync("true", new() { Environment = new Dictionary<string, string> { ["VALUE١"] = "x" } }));
    }

    [Fact]
    public async Task DockerUsesLiteralArgvAndAnExplicitContainerSeparator()
    {
        var sandbox = new DockerSandbox(new() { Container = "--privileged", User = "user name", WorkingDirectory = "/work 'space'", Process = Fixture() });
        var result = await sandbox.ExecuteAsync("printf '%s' \"$VALUE\"", new() { Environment = new Dictionary<string, string> { ["VALUE"] = "a;$(not-local)\nnext" } });
        using var parsed = JsonDocument.Parse(result.Stdout);
        Assert.Equal(new[] { "exec", "--user", "user name", "-w", "/work 'space'", "-e", "VALUE=a;$(not-local)\nnext", "--", "--privileged", "sh", "-c", "printf '%s' \"$VALUE\"" }, parsed.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray());
    }

    [Fact]
    public async Task RemoteBinaryReadsAreByteExactAndTextDecodingIsStrict()
    {
        var bytes = new byte[] { 239, 187, 191, 0, 255, 13, 10 };
        var process = Fixture("literal") with { Environment = new Dictionary<string, string> { ["STRANDS_SANDBOX_FIXTURE_MODE"] = "literal", ["STRANDS_SANDBOX_FIXTURE_OUTPUT"] = Convert.ToBase64String(bytes) } };
        var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = process });
        Assert.Equal(bytes, await sandbox.ReadBytesAsync("file 'name'"));
        await Assert.ThrowsAsync<System.Text.DecoderFallbackException>(async () => await sandbox.ReadTextAsync("file 'name'"));
    }

    [Fact]
    public async Task CancellationAfterIncrementalReadinessReapsTheOwnedClient()
    {
        using var cancellation = new CancellationTokenSource();
        var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = Fixture("hang") });
        await using var enumerator = sandbox.ExecuteStreamingAsync("opaque", cancellationToken: cancellation.Token).GetAsyncEnumerator();
        Assert.True(await Bounded(enumerator.MoveNextAsync().AsTask()));
        var chunk = Assert.IsType<StreamChunk>(enumerator.Current);
        var pid = int.Parse(chunk.Data.Substring("ready:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        cancellation.Cancel();
        SandboxAbortException? error = null;
        try { while (await enumerator.MoveNextAsync()) { } }
        catch (SandboxAbortException exception) { error = exception; }
        Assert.NotNull(error);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Contains("ready:", error.PartialResult.Stdout);
        AssertExited(pid);
    }

    [Fact]
    public async Task DisposingAnIncrementalReaderReapsTheOwnedClient()
    {
        var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = Fixture("hang") });
        var enumerator = sandbox.ExecuteStreamingAsync("opaque").GetAsyncEnumerator();
        try
        {
            Assert.True(await Bounded(enumerator.MoveNextAsync().AsTask()));
            var pid = int.Parse(Assert.IsType<StreamChunk>(enumerator.Current).Data.Substring("ready:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            await Bounded(DisposeAsync());
            AssertExited(pid);
        }
        finally { await enumerator.DisposeAsync(); }
        async Task DisposeAsync()
        {
            try { await enumerator.DisposeAsync(); }
            catch (Exception error)
            {
                var diagnostics = error.Data["Strands.CleanupErrors"] is IEnumerable<Exception> errors ? string.Join(" | ", errors.Select(item => item.ToString())) : "none";
                throw new InvalidOperationException("Unexpected reader-disposal failure; cleanup=" + diagnostics, error);
            }
        }
    }

    [Fact]
    public async Task TimeoutIsBoundedAndDoesNotRequireStartupOutput()
    {
        var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = Fixture("hang") });
        var error = await Assert.ThrowsAsync<SandboxTimeoutException>(async () => await Bounded(sandbox.ExecuteAsync("opaque", new() { Timeout = TimeSpan.FromSeconds(2) }).AsTask()));
        Assert.Equal(TimeSpan.FromSeconds(2), error.Timeout);
        Assert.Equal(124, error.PartialResult.ExitCode);
    }

    [Fact]
    public async Task CombinedUtf8CaptureCannotExceedTheApplicationPolicy()
    {
        var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = Fixture("unicode") with { MaximumOutputBytes = 101 } });
        var result = await sandbox.ExecuteAsync("opaque", new() { MaximumOutputBytes = 9999 });
        Assert.True(result.OutputTruncated);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Stdout) + Encoding.UTF8.GetByteCount(result.Stderr), 1, 101);
        Assert.DoesNotContain("\uFFFD", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCommandUtf8FailsInsteadOfChangingBytes()
    {
        var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = Fixture("invalidUtf8") });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await sandbox.ExecuteAsync("opaque"));
    }

    [Fact]
    public async Task RootExitWithAnInheritedOpenPipeHasBoundedCleanup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-sandbox-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "pid.txt");
        var policy = Fixture("orphan") with { CleanupTimeout = TimeSpan.FromSeconds(1), Environment = new Dictionary<string, string> { ["STRANDS_SANDBOX_FIXTURE_MODE"] = "orphan", ["STRANDS_SANDBOX_FIXTURE_PID_FILE"] = marker } };
        IOException? failure = null;
        try
        {
            var sandbox = new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = policy });
            failure = await Assert.ThrowsAnyAsync<IOException>(async () => await Bounded(sandbox.ExecuteAsync("opaque").AsTask()));
        }
        finally
        {
            if (File.Exists(marker))
            {
                var pid = int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture);
                try { using var child = Process.GetProcessById(pid); if (!child.HasExited) child.Kill(); }
                catch (ArgumentException) { }
            }
            Directory.Delete(directory, true);
        }
        if (failure?.Data["Strands.CleanupTask"] is Task<IReadOnlyList<Exception>> cleanup) _ = await Bounded(cleanup);
    }

    [Fact]
    public async Task AcceptedOutputRemainsOrderedBeforeTheTerminalFailure()
    {
        using var queue = new SandboxProcess.EventQueue();
        var first = new StreamChunk("first", StreamType.Stdout);
        var second = new StreamChunk("second", StreamType.Stderr);
        var failure = new TimeoutException("expired after accepted output");
        queue.Write(first);
        Assert.Same(first, (await queue.ReadAsync()).Item);
        queue.Write(second);
        queue.Complete(failure);
        Assert.Same(second, (await queue.ReadAsync()).Item);
        Assert.Same(failure, (await queue.ReadAsync()).Error);
    }

    [Fact]
    public async Task DirectoryFramesPreserveNamesAndRejectIncompleteMetadata()
    {
        var sandbox = new ResponseSandbox(new(0, "line\nname\0" + "1\0" + "1\0.hidden\0" + "0\0" + "0\0", ""));
        var entries = await sandbox.ListFilesAsync("-directory 'name'");
        Assert.Equal(new[] { ".hidden", "line\nname" }, entries.Select(entry => entry.Name));
        Assert.True(entries[1].IsDirectory && entries[1].IsSymbolicLink);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new ResponseSandbox(new(0, "partial\0", "")).ListFilesAsync("."));
    }

    [Fact]
    public async Task FileTransfersFailClosedOnMissingPathsAndTruncatedCapture()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await new ResponseSandbox(new(77, "", "")).ReadBytesAsync("missing"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await new ResponseSandbox(new(77, "", "")).ListFilesAsync("missing"));
        await Assert.ThrowsAsync<IOException>(async () => await new ResponseSandbox(new(0, "YQ==", "") { OutputTruncated = true }).ReadBytesAsync("truncated"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new ResponseSandbox(new(0, "%%%", "")).ReadBytesAsync("malformed"));
    }

    [Fact]
    public async Task RemoteWritesOwnLargeStdinAndCheckLengthBeforeAtomicReplacement()
    {
        var bytes = Enumerable.Range(0, 1_500_000).Select(index => (byte)index).ToArray();
        var expected = (byte[])bytes.Clone();
        var sandbox = new ResponseSandbox(new(0, "", ""));
        var pending = sandbox.WriteBytesAsync("-file 'quote'", bytes).AsTask();
        Array.Clear(bytes, 0, bytes.Length);
        await pending;
        Assert.Equal(expected, Convert.FromBase64String(Encoding.ASCII.GetString(Assert.IsType<byte[]>(sandbox.Input))));
        Assert.Contains("wc -c", sandbox.Command);
        Assert.Contains("&& mv -f", sandbox.Command);
        Assert.Contains("test ! -L", sandbox.Command);
        Assert.DoesNotContain(Convert.ToBase64String(expected), sandbox.Command);
    }

    [Theory]
    [InlineData("C:relative-client.exe")]
    [InlineData("\\root-relative-client.exe")]
    public void WindowsExecutableOverridesMustBeFullyQualified(string executable)
    {
        if (Path.DirectorySeparatorChar != '\\') return;
        Assert.Throws<ArgumentException>(() => new SshSandbox(new() { Host = "fixture", WorkingDirectory = "/", Process = new() { ExecutablePath = executable } }));
    }

    private sealed class ResponseSandbox(ExecutionResult response) : PosixShellSandbox
    {
        internal string? Command { get; private set; }
        internal byte[]? Input { get; private set; }
        public override async IAsyncEnumerable<SandboxStreamEvent> ExecuteStreamingAsync(string command, ExecuteOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Command = command; Input = options?.StandardInput;
            await Task.CompletedTask;
            yield return response;
        }
    }

    private static async Task<T> Bounded<T>(Task<T> operation)
    {
        if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(15))) != operation) throw new TimeoutException("The sandbox contract did not finish within its test deadline.");
        return await operation;
    }
    private static async Task Bounded(Task operation)
    {
        if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(15))) != operation) throw new TimeoutException("The sandbox cleanup did not finish within its test deadline.");
        await operation;
    }
    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, "The owned native client is still running."); }
        catch (ArgumentException) { }
    }

    private static SandboxProcessOptions Fixture(string mode = "argv", string exit = "0") => new()
    {
        ExecutablePath = Path.Combine(AppContext.BaseDirectory, "SandboxProcessFixture", "Strands.Sandbox.ProcessFixture" + (Path.DirectorySeparatorChar == '\\' ? ".exe" : "")),
        Environment = new Dictionary<string, string> { ["STRANDS_SANDBOX_FIXTURE_MODE"] = mode, ["STRANDS_SANDBOX_FIXTURE_EXIT"] = exit },
    };
}
