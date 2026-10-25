using System.Collections.ObjectModel;

namespace Strands.Agents.Sandbox;

internal static class SandboxPolicy
{
    internal static ExecuteOptions CodeOptions(string code, string language, ExecuteOptions? options, CancellationToken cancellationToken)
    {
        Guard.NotNull(code, nameof(code));
        SandboxPolicy.Language(language, nameof(language));
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        if (options.StandardInput is not null) throw new ArgumentException("Source execution owns standard input; do not supply separate input bytes.", nameof(options));
        SandboxPolicy.Duration(options.Timeout, nameof(options.Timeout));
        if (options.MaximumOutputBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaximumOutputBytes must be positive.");
        var snapshot = options with
        {
            StandardInput = System.Text.Encoding.UTF8.GetBytes(code),
            Environment = SandboxPolicy.Environment(options.Environment, posix: true),
        };
        return snapshot;
    }

    internal static void Language(string value, string parameter)
    {
        Guard.NotNull(value, parameter);
        if (value.Length == 0 || value.Any(character => !Letter(character) && character is not (>= '0' and <= '9') && character is not '.' and not '_' and not '-'))
            throw new ArgumentException("Language must contain only ASCII letters, digits, dots, underscores or hyphens.", parameter);
    }

    internal static void Text(string value, string parameter)
    {
        Guard.NotNullOrWhiteSpace(value, parameter);
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("Sandbox values cannot contain NUL.", parameter);
    }

    internal static SandboxProcessOptions Process(SandboxProcessOptions options)
    {
        Guard.NotNull(options, nameof(options));
        if (options.MaximumOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaximumOutputBytes must be positive.");
        Duration(options.MaximumDuration, nameof(options.MaximumDuration));
        Duration(options.CleanupTimeout, nameof(options.CleanupTimeout));
        if (options.ExecutablePath is { } executable)
        {
            Text(executable, nameof(options.ExecutablePath));
            var root = Path.GetPathRoot(executable);
            if (!Path.IsPathRooted(executable) || Path.DirectorySeparatorChar == '\\' && (root is null || root.Length < 3))
                throw new ArgumentException("ExecutablePath must be fully qualified.", nameof(options));
        }
        return options with { Environment = Environment(options.Environment, posix: false) };
    }

    internal static void Duration(TimeSpan? value, string parameter)
    {
        if (value is { } duration && (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue))
            throw new ArgumentOutOfRangeException(parameter, "Duration must be positive and no greater than Int32.MaxValue milliseconds.");
    }

    internal static IReadOnlyDictionary<string, string> Environment(IReadOnlyDictionary<string, string> values, bool posix)
    {
        Guard.NotNull(values, nameof(values));
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            Text(item.Key, nameof(values));
            Guard.NotNull(item.Value, nameof(values));
            if (item.Key.IndexOf('=') >= 0 || item.Value.IndexOf('\0') >= 0) throw new ArgumentException("Environment keys cannot contain '=' and values cannot contain NUL.", nameof(values));
            if (posix && (!Letter(item.Key[0]) && item.Key[0] != '_' || item.Key.Skip(1).Any(character => !Letter(character) && character is not (>= '0' and <= '9') && character != '_')))
                throw new ArgumentException("Invalid POSIX environment variable name: " + item.Key, nameof(values));
            copy.Add(item.Key, item.Value);
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static bool Letter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
