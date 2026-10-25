using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;
using Xunit.Abstractions;
using McpClient = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpStdioLifetimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsBatchQuoteArgumentsFailBeforeApplicationCodeRuns(bool extensionless)
    {
        if (Path.DirectorySeparatorChar != '\\')
        {
            output.WriteLine("Windows batch quote validation does not restrict Unix executable arguments.");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "strands stdio reject batch " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var marker = Path.Combine(directory, "application-started");
            var script = Path.Combine(directory, "stdio-launch.cmd");
            File.WriteAllText(script, "@echo application-started>\"" + marker + "\"\r\n");
            var options = new StdioClientTransportOptions
            {
                Command = extensionless ? "stdio-launch" : script,
                Arguments = new[] { "a\"&echo STRANDS_STDIO_SECOND_COMMAND&\"b" },
                WorkingDirectory = directory
            };
            var error = await Assert.ThrowsAsync<ArgumentException>(() => McpClient.CreateStdioAsync(options));
            Assert.Contains("explicit .exe or .com", error.Message);
            Assert.False(File.Exists(marker));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("percent%STRANDS_STDIO_LITERAL%")]
    [InlineData("quote\"&word")]
    [InlineData("a\"&echo STRANDS_STDIO_SECOND_COMMAND&\"b")]
    [InlineData("parentheses(value)!caret^word")]
    public async Task NativeExecutableTreatsPercentAndEmbeddedQuotesAsArgumentData(string argument)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands stdio literal " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "sentinel"), "literal arguments");
            var options = Options(directory, argument);
            options.EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            options.EnvironmentVariables["STRANDS_STDIO_LITERAL"] = "must not expand";
            var client = await Deadline(McpClient.CreateStdioAsync(options));
            try
            {
                var result = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { inspectRuntime = true, environment = "UNSET_STDIO_TEST", inherited = "UNSET_STDIO_TEST", sentinel = "sentinel" }));
                using var document = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
                Assert.Equal(argument, Assert.Single(document.RootElement.GetProperty("arguments").EnumerateArray()).GetString());
                Assert.Empty(document.RootElement.GetProperty("coordinationEnvironment").EnumerateArray());
            }
            finally { await Deadline(client.DisposeAsync().AsTask()); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PrecancelledConnectionDoesNotExecuteTheConfiguredCommand()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands stdio precancel " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "not-started.pid");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => McpClient.CreateStdioAsync(Options(directory, "--block-initialize", "--initialize-marker=" + marker), cancellationToken: cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.False(File.Exists(marker));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ThrowingBorrowedStderrObserversCannotReplaceTheProtocolOutcome()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands stdio diagnostic " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var options = Options(directory, "--emit-stderr");
            options.StandardErrorLines = _ => { observed.TrySetResult(true); throw new InvalidOperationException("borrowed diagnostic failed"); };
            var client = await Deadline(McpClient.CreateStdioAsync(options));
            try
            {
                await Deadline(observed.Task);
                var result = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "still connected" }));
                Assert.Equal("still connected", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            }
            finally { await Deadline(client.DisposeAsync().AsTask()); }
            var details = Assert.IsType<StdioClientCompletionDetails>(await client.Protocol.Completion);
            Assert.Contains("owned stdio diagnostic", details.StandardErrorTail!);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedInitializationKeepsCancellationPrimaryAndExposesDeferredDiagnosticCleanup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands stdio cancel diagnostic " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "initializing.pid");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Process? process = null;
        Task<McpClient>? connection = null;
        Task<IReadOnlyList<Exception>>? deferred = null;
        try
        {
            var options = Options(directory, "--block-initialize", "--initialize-marker=" + marker, "--emit-stderr");
            options.StandardErrorLines = _ => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); };
            connection = McpClient.CreateStdioAsync(options, cancellationToken: cancellation.Token);
            await Deadline(entered.Task);
            await WaitForMarkerAsync(marker);
            process = Process.GetProcessById(int.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture));

            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Deadline(connection));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(process.HasExited);
            var cleanup = Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"]);
            var pending = Assert.Single(cleanup, item => item.Data["Strands.CleanupTask"] is Task<IReadOnlyList<Exception>>);
            deferred = Assert.IsAssignableFrom<Task<IReadOnlyList<Exception>>>(pending.Data["Strands.CleanupTask"]);
            Assert.False(deferred.IsCompleted);
        }
        finally
        {
            cancellation.Cancel(); release.TrySetResult(true);
            if (deferred is not null) Assert.Empty(await Deadline(deferred));
            if (connection is not null) { try { await Deadline(connection); } catch (OperationCanceledException) { } }
            process?.Dispose(); Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringInitializationReapsTheStartedServer(bool explicitSettings)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands stdio initialization " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "initializing.pid");
        using var cancellation = new CancellationTokenSource();
        Process? process = null;
        Task<McpClient>? connection = null;
        try
        {
            var options = Options(directory, "--block-initialize", "--initialize-marker=" + marker);
            connection = explicitSettings
                ? McpClient.CreateStdioAsync(new McpClientSettings { TasksConfig = new() }, options, cancellationToken: cancellation.Token)
                : McpClient.CreateStdioAsync(options, cancellationToken: cancellation.Token);
            await WaitForMarkerAsync(marker);
            process = Process.GetProcessById(int.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture));
            Assert.False(process.HasExited);

            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Deadline(connection));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(process.HasExited, "A cancelled connection cannot return before its released server has exited.");
        }
        finally
        {
            cancellation.Cancel();
            if (connection is not null) { try { await Deadline(connection); } catch (OperationCanceledException) { } }
            process?.Dispose(); Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WindowsCmdAndPathextLaunchersRetainSeparatedArguments()
    {
        if (Path.DirectorySeparatorChar != '\\')
        {
            output.WriteLine("CMD/PATHEXT applies to the Windows command environment only.");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "strands stdio cmd " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "sentinel"), "wrapper directory");
            var baseline = Options(directory);
            // The batch file accepts application arguments through %*, independently of the release gate.
            var launch = "@echo off\r\n\"" + baseline.Command + "\" " + string.Join(" ", baseline.Arguments!.Select(item => "\"" + item + "\"")) + " %*\r\n";
            File.WriteAllText(Path.Combine(directory, "stdio-launch.cmd"), launch);
            var arguments = new[] { "argument with spaces", "", "a&b", "trailing\\", "percent%STRANDS_STDIO_LITERAL%" };
            var options = new StdioClientTransportOptions
            {
                Command = "stdio-launch",
                Arguments = arguments,
                WorkingDirectory = directory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
                ShutdownTimeout = TimeSpan.FromSeconds(1)
            };
            options.EnvironmentVariables["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            options.EnvironmentVariables["STRANDS_STDIO_LITERAL"] = "must not expand";
            var client = await Deadline(McpClient.CreateStdioAsync(options));
            try
            {
                var result = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { inspectRuntime = true, environment = "UNSET_STDIO_TEST", inherited = "UNSET_STDIO_TEST", sentinel = "sentinel" }));
                using var document = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
                Assert.Equal(arguments, document.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()));
                Assert.Empty(document.RootElement.GetProperty("coordinationEnvironment").EnumerateArray());
            }
            finally { await Deadline(client.DisposeAsync().AsTask()); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NativeRuntimeRetainsUtf8SeparatedArgumentsSelectedEnvironmentAndNoReleaseVariable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands stdio runtime " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var name = "STRANDS_STDIO_" + Guid.NewGuid().ToString("N");
        var inherited = "STRANDS_STDIO_PRIVATE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(inherited, "host only");
        Process? process = null;
        try
        {
            File.WriteAllText(Path.Combine(directory, "sentinel"), "Café 雪");
            var arguments = new[] { "argument with spaces", "", "Café 雪", "a&b", "a|b", "a^b", "trailing\\" };
            var options = Options(directory, arguments);
            options.InheritEnvironmentVariables = false;
            options.EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            options.EnvironmentVariables[name] = "explicit 雪";
            var client = await Deadline(McpClient.CreateStdioAsync(options));
            try
            {
                process = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo.Version, CultureInfo.InvariantCulture));
                var result = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { inspectRuntime = true, environment = name, inherited, sentinel = "sentinel" }));
                using var document = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
                var runtime = document.RootElement;
                Assert.Equal(arguments, runtime.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()));
                Assert.Equal("explicit 雪", runtime.GetProperty("environment").GetString());
                Assert.Equal(JsonValueKind.Null, runtime.GetProperty("inherited").ValueKind);
                Assert.Equal("Café 雪", runtime.GetProperty("sentinel").GetString());
                Assert.Empty(runtime.GetProperty("coordinationEnvironment").EnumerateArray());
            }
            finally { await Deadline(client.DisposeAsync().AsTask()); }
            Assert.True(process!.HasExited);
            var details = Assert.IsType<StdioClientCompletionDetails>(await client.Protocol.Completion);
            Assert.NotNull(details.ProcessId);
            Assert.NotNull(details.ExitCode);
        }
        finally { process?.Dispose(); Environment.SetEnvironmentVariable(inherited, null); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ABlockedBorrowedStderrObserverCannotBecomeSuccessfulCompletedCleanup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands stdio stderr " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<Exception>>? deferred = null;
        McpClient? client = null;
        Process? process = null;
        try
        {
            var options = Options(directory, "--emit-stderr");
            options.StandardErrorLines = _ => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); };
            client = await Deadline(McpClient.CreateStdioAsync(options));
            process = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo.Version, CultureInfo.InvariantCulture));
            await Deadline(entered.Task);

            var error = await Assert.ThrowsAnyAsync<IOException>(() => Deadline(client.DisposeAsync().AsTask()));

            Assert.True(process.HasExited, "The actual process must be reaped even when application diagnostic code ignores shutdown.");
            deferred = Assert.IsAssignableFrom<Task<IReadOnlyList<Exception>>>(error.Data["Strands.CleanupTask"]);
            Assert.False(deferred.IsCompleted);
            if (error.Data["Strands.CleanupErrors"] is IEnumerable<Exception> diagnostics)
                Assert.DoesNotContain(diagnostics, secondary => ReferenceEquals(error, secondary));
        }
        finally
        {
            release.TrySetResult(true);
            if (deferred is not null) Assert.Empty(await Deadline(deferred));
            if (client is not null) { try { await client.DisposeAsync(); } catch (IOException) { } }
            process?.Dispose(); Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DisposalHonorsThePlatformsOwnedDescendantContractAfterTheLauncherExits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands owned stdio " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "child.pid");
        McpClient? client = null;
        Process? server = null;
        Process? child = null;
        Task<IReadOnlyList<Exception>>? deferred = null;
        try
        {
            client = await Deadline(McpClient.CreateStdioAsync(Options(directory, "--spawn-descendant=" + marker, "--exit-after-call")));
            server = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo.Version, CultureInfo.InvariantCulture));
            await WaitForMarkerAsync(marker);
            child = Process.GetProcessById(int.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture));
            Assert.False(child.HasExited);
            var result = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "published before exit" }));
            Assert.Single(result.Content);
            Assert.True(await Task.Run(() => server.WaitForExit(10000)), "The server must actually exit before disposal starts.");

            if (Path.DirectorySeparatorChar == '\\')
            {
                await Deadline(client.DisposeAsync().AsTask());
                Assert.True(child.HasExited, "Successful owned Windows stdio disposal must not leave its descendant holding the working directory and pipes.");
            }
            else
            {
                output.WriteLine("Unix orphan limitation: a descendant that outlives its launcher is outside the platform process-tree API's lookup.");
                var error = await Assert.ThrowsAnyAsync<IOException>(() => Deadline(client.DisposeAsync().AsTask()));
                deferred = Assert.IsAssignableFrom<Task<IReadOnlyList<Exception>>>(error.Data["Strands.CleanupTask"]);
                Assert.False(deferred.IsCompleted);
                Assert.False(child.HasExited);
            }
        }
        finally
        {
            File.WriteAllText(marker + ".release", "release fixture");
            if (child is not null)
                try { Assert.True(await Task.Run(() => child.WaitForExit(10000)), "The task-owned fixture must exit after its release marker."); }
                finally { child.Dispose(); }
            if (deferred is not null) Assert.Empty(await Deadline(deferred));
            if (client is not null)
                try { await client.DisposeAsync(); }
                catch (IOException) when (deferred is not null) { }
            server?.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private static StdioClientTransportOptions Options(string directory, params string[] arguments)
    {
#if NETFRAMEWORK
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
        Assert.True(File.Exists(fixture));
        return new() { Command = fixture, Arguments = arguments, WorkingDirectory = directory, ShutdownTimeout = TimeSpan.FromSeconds(1) };
#else
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture));
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return new() { Command = Path.Combine(root.FullName, Path.DirectorySeparatorChar == '\\' ? "dotnet.exe" : "dotnet"), Arguments = new[] { fixture }.Concat(arguments).ToArray(), WorkingDirectory = directory, ShutdownTimeout = TimeSpan.FromSeconds(1) };
#endif
    }
    private static async Task WaitForMarkerAsync(string marker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(marker)) await Task.Delay(25, deadline.Token);
    }
    private static async Task<T> Deadline<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(25))));
        return await task;
    }
    private static async Task Deadline(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(25))));
        await task;
    }
}
