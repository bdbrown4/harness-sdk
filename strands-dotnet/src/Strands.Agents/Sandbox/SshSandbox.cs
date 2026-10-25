namespace Strands.Agents.Sandbox;

/// <summary>Application-selected SSH authority. A command cannot change the host, credentials, host-key policy, or client executable.</summary>
public sealed record SshSandboxOptions
{
    /// <summary>The SSH destination, such as user@host.</summary>
    public required string Host { get; init; }
    /// <summary>The target working directory.</summary>
    public required string WorkingDirectory { get; init; }
    /// <summary>An optional application-selected private key path for the local SSH client.</summary>
    public string? IdentityFile { get; init; }
    /// <summary>The SSH port, defaulting to 22.</summary>
    public int Port { get; init; } = 22;
    /// <summary>Additional -o options, subject to the upstream allowlist unless explicitly bypassed.</summary>
    public IReadOnlyList<string> SshOptions { get; init; } = Array.Empty<string>();
    /// <summary>Uses StrictHostKeyChecking=no when true; otherwise accept-new trusts first use and rejects changed keys.</summary>
    public bool AllowUnknownHosts { get; init; }
    /// <summary>Explicitly permits options outside the upstream allowlist, which may grant additional local process authority.</summary>
    public bool AllowUnsafeSshOptions { get; init; }
    /// <summary>Local client process policy, independent of the target command environment.</summary>
    public SandboxProcessOptions Process { get; init; } = new();
}

/// <summary>Executes in a POSIX environment through a fresh native OpenSSH client per call.</summary>
public sealed class SshSandbox : PosixShellSandbox
{
    private static readonly HashSet<string> AllowedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "addressfamily", "bindaddress", "bindinterface", "canonicaldomains", "canonicalizefallbacklocal", "canonicalizehostname",
        "canonicalizemaxdots", "canonicalizepermittedcnames", "checkhostip", "ciphers", "compression", "connectionattempts",
        "connecttimeout", "hostkeyalgorithms", "hostname", "identitiesonly", "ipqos", "kbdinteractiveauthentication",
        "kexalgorithms", "loglevel", "macs", "numberofpasswordprompts", "passwordauthentication", "port",
        "preferredauthentications", "pubkeyacceptedalgorithms", "pubkeyauthentication", "rekeylimit", "serveralivecountmax",
        "serveraliveinterval", "tcpkeepalive", "updatehostkeys", "user", "verifyhostkeydns",
    };
    /// <summary>Creates an SSH environment from fixed application authority.</summary>
    public SshSandbox(SshSandboxOptions options)
    {
        Guard.NotNull(options, nameof(options));
        SandboxPolicy.Text(options.Host, nameof(options.Host));
        SandboxPolicy.Text(options.WorkingDirectory, nameof(options.WorkingDirectory));
        if (options.IdentityFile is not null) SandboxPolicy.Text(options.IdentityFile, nameof(options.IdentityFile));
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "SSH ports must be between 1 and 65535.");
        Guard.NotNull(options.SshOptions, nameof(options.SshOptions));
        var copied = options.SshOptions.ToArray();
        foreach (var option in copied)
        {
            SandboxPolicy.Text(option, nameof(options.SshOptions));
            var name = new string(option.TakeWhile(character => character != '=' && !char.IsWhiteSpace(character)).ToArray());
            if (!options.AllowUnsafeSshOptions && !AllowedOptions.Contains(name))
                throw new ArgumentException($"SSH option '{name}' is not allowed. Set AllowUnsafeSshOptions explicitly to bypass the allowlist.", nameof(options));
        }
        Options = options with { SshOptions = Array.AsReadOnly(copied), Process = SandboxPolicy.Process(options.Process) };
    }
    /// <summary>A detached snapshot of application SSH configuration.</summary>
    public SshSandboxOptions Options { get; }
    /// <inheritdoc/>
    public override IReadOnlyList<ITool> GetTools()
    {
        return [
            SandboxFileEditor.Create(this, new() { Name = "sandbox_file_editor", Description = SandboxFileEditor.DefaultDescription + " Files are on host \"" + Options.Host + "\"." }),
            SandboxShell.Create(this, new() { Name = "sandbox_shell", Description = SandboxShell.DefaultDescription + " Runs on host \"" + Options.Host + "\". Working directory: " + Options.WorkingDirectory + "." }),
        ];
    }

    /// <inheritdoc/>
    public override IAsyncEnumerable<SandboxStreamEvent> ExecuteStreamingAsync(string command, ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(command, nameof(command));
        if (command.IndexOf('\0') >= 0) throw new ArgumentException("A command cannot contain NUL.", nameof(command));
        options ??= new();
        var environment = SandboxPolicy.Environment(options.Environment, posix: true);
        var directory = options.WorkingDirectory ?? Options.WorkingDirectory;
        SandboxPolicy.Text(directory, nameof(options.WorkingDirectory));
        var prefix = environment.Count == 0 ? string.Empty : "export " + string.Join(" ", environment.Select(item => item.Key + "=" + ShellQuote(item.Value))) + " && ";
        var remote = "cd " + ShellQuote(directory) + " && " + prefix + command;
        var arguments = new List<string> { "-o", "StrictHostKeyChecking=" + (Options.AllowUnknownHosts ? "no" : "accept-new"), "-o", "BatchMode=yes", "-p", Options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (Options.IdentityFile is { } identity) { arguments.Add("-i"); arguments.Add(identity); }
        foreach (var option in Options.SshOptions) { arguments.Add("-o"); arguments.Add(option); }
        arguments.Add("--"); arguments.Add(Options.Host); arguments.Add(remote);
        return SandboxProcess.Execute("ssh", arguments, options, Options.Process, cancellationToken);
    }

}
