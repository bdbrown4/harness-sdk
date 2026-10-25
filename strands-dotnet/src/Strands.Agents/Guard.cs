namespace Strands.Agents;

internal static class Guard
{
    internal static void NotNull(object? value, string parameterName)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
    }

    internal static void NotNullOrWhiteSpace(string? value, string parameterName)
    {
        NotNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("The value cannot be empty or whitespace.", parameterName);
    }

    internal static bool IsAsciiLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
