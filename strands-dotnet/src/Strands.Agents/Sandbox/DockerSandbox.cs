namespace Strands.Agents.Sandbox;

/// <summary>Fixed application-selected Docker container authority.</summary>
public sealed record DockerSandboxOptions
{
    /// <summary>The existing target container name or identifier.</summary>
    public required string Container { get; init; }
    /// <summary>An optional target user; null uses the container configuration.</summary>
    public string? User { get; init; }
    /// <summary>An optional target working directory; null uses the container configuration.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Local Docker client process policy.</summary>
    public SandboxProcessOptions Process { get; init; } = new();
}

/// <summary>Executes POSIX commands in an existing container through a fresh native Docker client per call.</summary>
public sealed class DockerSandbox : PosixShellSandbox
{
    /// <summary>Creates a Docker environment from fixed application authority.</summary>
    public DockerSandbox(DockerSandboxOptions options)
    {
        Guard.NotNull(options, nameof(options));
        SandboxPolicy.Text(options.Container, nameof(options.Container));
        if (options.User is { } user) SandboxPolicy.Text(user, nameof(options.User));
        if (options.WorkingDirectory is { } directory) SandboxPolicy.Text(directory, nameof(options.WorkingDirectory));
        Options = options with { Process = SandboxPolicy.Process(options.Process) };
    }
    /// <summary>A detached snapshot of application configuration.</summary>
    public DockerSandboxOptions Options { get; }
    /// <inheritdoc/>
    public override IReadOnlyList<ITool> GetTools()
    {
        var cwd = Options.WorkingDirectory is { } directory ? " Working directory: " + directory + "." : string.Empty;
        return [
            SandboxFileEditor.Create(this, new() { Name = "sandbox_file_editor", Description = SandboxFileEditor.DefaultDescription + " Files are in Docker container \"" + Options.Container + "\"." }),
            SandboxShell.Create(this, new() { Name = "sandbox_shell", Description = SandboxShell.DefaultDescription + " Runs in Docker container \"" + Options.Container + "\"." + cwd }),
        ];
    }

    /// <inheritdoc/>
    public override IAsyncEnumerable<SandboxStreamEvent> ExecuteStreamingAsync(string command, ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(command, nameof(command));
        if (command.IndexOf('\0') >= 0) throw new ArgumentException("A command cannot contain NUL.", nameof(command));
        options ??= new();
        var environment = SandboxPolicy.Environment(options.Environment, posix: true);
        var arguments = new List<string> { "exec" };
        if (options.StandardInput is not null) arguments.Add("-i");
        if (Options.User is { } user) { arguments.Add("--user"); arguments.Add(user); }
        if ((options.WorkingDirectory ?? Options.WorkingDirectory) is { } directory)
        {
            SandboxPolicy.Text(directory, nameof(options.WorkingDirectory));
            arguments.Add("-w"); arguments.Add(directory);
        }
        foreach (var item in environment) { arguments.Add("-e"); arguments.Add(item.Key + "=" + item.Value); }
        arguments.Add("--"); arguments.Add(Options.Container); arguments.Add("sh"); arguments.Add("-c"); arguments.Add(command);
        return SandboxProcess.Execute("docker", arguments, options, Options.Process, cancellationToken);
    }
}
