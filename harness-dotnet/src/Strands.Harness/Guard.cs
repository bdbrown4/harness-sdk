namespace Strands.Harness;

internal static class Guard
{
    internal static void NotNull(object? value, string parameterName)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
    }

    internal static void NotBlank(string? value, string parameterName)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("The value must not be empty or whitespace.", parameterName);
    }

    internal static void Positive(int value, string parameterName)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(parameterName, "The value must be positive.");
    }

    internal static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    internal static bool IsAsciiLetterOrDigit(char value) => IsAsciiLetter(value) || value is >= '0' and <= '9';

    internal static bool IsFullyQualifiedLocalPath(string path)
    {
        if (Path.DirectorySeparatorChar != '\\') return path.StartsWith("/", StringComparison.Ordinal);
        var unc = path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);
        var drive = path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]);
        return unc || drive;
    }

    private static bool IsSeparator(char value) => value is '/' or '\\';
}
