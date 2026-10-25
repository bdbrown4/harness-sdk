# Docker and SSH tool vending

`DockerSandbox.GetTools()` and `SshSandbox.GetTools()` return fresh, ordered
`sandbox_file_editor` and `sandbox_shell` wrappers bound to that exact sandbox.
Their descriptions identify the configured container/host and the shell working
directory when supplied. Default local/base vending stays empty. User-defined
registered names win through the Agent's atomic sandbox registration path.

Only these two vending overrides are added to the existing Docker/SSH types;
their constructors, process/options policy and execution bodies are unchanged.
Tools and configured sandboxes remain borrowed. Metadata inspection starts no
client process and performs no credential or network lookup.

The original vending tests inspect metadata and registration without starting a client. Four additional tests execute an owned CLI fixture through the selected Docker/SSH process configuration. They verify literal argument boundaries, working directories, nonzero exits and actual NUL-delimited directory output. All four pass on Windows and Linux with .NET 8/10 and on genuine Framework 4.8 x64/x86. These tests do not connect to Docker or SSH services, execute the supplied remote command, or prove remote filesystem persistence and credential integration.