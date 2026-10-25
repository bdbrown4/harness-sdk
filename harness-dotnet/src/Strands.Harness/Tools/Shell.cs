using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tools;

/// <summary>Model-facing configuration for the sandbox-routed shell tool.</summary>
public sealed record ShellConfig
{
    /// <summary>An optional tool description replacing the shared default.</summary>
    public string? Description { get; init; }
    /// <summary>The requested combined capture limit, defaulting to 1 MiB. The sandbox's hard limit always takes precedence.</summary>
    public int MaxOutputBytes { get; init; } = 1024 * 1024;
}

/// <summary>Creates a stateless command tool bound to an explicit command-capable sandbox.</summary>
public static class Shell
{
    /// <summary>The shared Python/TypeScript description of fresh shell execution.</summary>
    public const string DefaultDescription = "Executes shell commands and returns output (stdout), error (stderr), and exit_code (non-zero means the command failed). Each call runs in a fresh shell; state such as variables and the working directory does not persist across calls.";
    /// <summary>Creates a shell tool. File-only sandboxes cannot acquire host command authority through this factory.</summary>
    public static IRichTool Create(ICommandSandbox sandbox, ShellConfig? config = null, string name = "shell") => new ShellTool(sandbox, config ?? new(), name);

    private sealed class ShellTool : IRichTool
    {
        private readonly ICommandSandbox _sandbox;
        private readonly int _maxOutputBytes;
        internal ShellTool(ICommandSandbox sandbox, ShellConfig config, string name)
        {
            Guard.NotNull(sandbox, nameof(sandbox));
            Guard.Positive(config.MaxOutputBytes, nameof(config.MaxOutputBytes));
            _sandbox = sandbox;
            _maxOutputBytes = config.MaxOutputBytes;
            Spec = new(name, config.Description ?? DefaultDescription, JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "The shell command to execute." },
                    timeout = new { type = "number", exclusiveMinimum = 0, description = "Timeout in seconds (default: 120)." },
                },
                required = new[] { "command" },
            }));
        }
        public ToolSpec Spec { get; }
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
        public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String) throw new ArgumentException("command must be a string.");
                var timeout = TimeSpan.FromSeconds(120);
                if (input.TryGetProperty("timeout", out var configured))
                {
                    if (configured.ValueKind != JsonValueKind.Number || !configured.TryGetDouble(out var seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
                        throw new ArgumentOutOfRangeException(nameof(input), "timeout must be a positive finite number of seconds.");
                    timeout = TimeSpan.FromSeconds(seconds);
                }
                var result = await _sandbox.ExecuteAsync(command.GetString()!, new() { Timeout = timeout, MaxOutputBytes = _maxOutputBytes }, cancellationToken).ConfigureAwait(false);
                return Result(context, result, ToolResultStatus.Success);
            }
            catch (SandboxTimeoutException exception) { return Result(context, exception.PartialResult, ToolResultStatus.Error, exception.Message + "\n"); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { return new(context.ToolUse.ToolUseId, "Shell execution failed: " + exception.Message, ToolResultStatus.Error); }
        }
        private static ToolResultBlock Result(ToolContext context, SandboxExecutionResult result, ToolResultStatus status, string prefix = "")
        {
            var json = JsonSerializer.SerializeToElement(new
            {
                output = result.StandardOutput + (result.OutputTruncated ? "\n[Shell output truncated by the sandbox's output limit.]" : string.Empty),
                error = result.StandardError,
                exit_code = result.ExitCode,
            });
            return new(context.ToolUse.ToolUseId, prefix + json.GetRawText(), status) { StructuredContent = json };
        }
    }
}
