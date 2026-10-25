using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Strands.Agents;

/// <summary>JSON transport integers under the documented Pydantic 2.5/core 2.14.1 reference.</summary>
internal static class Pydantic25Integer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static BigInteger Parse(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.True) return BigInteger.One;
        if (value.ValueKind == JsonValueKind.False) return BigInteger.Zero;
        if (value.ValueKind == JsonValueKind.String) return PythonString(value.GetString()!, name);
        if (value.ValueKind == JsonValueKind.Number)
        {
            var raw = value.GetRawText();
            if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) return integer;
            if (value.TryGetDouble(out var number) && !double.IsNaN(number) && !double.IsInfinity(number) && number % 1 == 0 &&
                (double)long.MinValue < number && number < (double)long.MaxValue) return new BigInteger(number);
        }
        throw PythonIntegerError(name);
    }

    private static BigInteger PythonString(string text, string name)
    {
        // Pydantic 2.5/core 2.14.1 selects its parser using the original UTF-8 byte count.
        var bytes = StrictUtf8.GetByteCount(text);
        if (bytes > 4300) throw PythonIntegerError(name);
        if (TryPythonInteger(text, bytes, out var result)) return result;
        var point = text.IndexOf('.');
        if (point >= 0 && text.Skip(point + 1).All(c => c == '0'))
        {
            if (TryPythonInteger(text.Substring(0, point), bytes, out result)) return result;
            throw PythonIntegerError(name);
        }
        if (!text.StartsWith("_", StringComparison.Ordinal) && !text.EndsWith("_", StringComparison.Ordinal) &&
            text.IndexOf('_') >= 0 && text.IndexOf("__", StringComparison.Ordinal) < 0 &&
            TryPythonInteger(text.Replace("_", string.Empty), bytes, out result)) return result;
        throw PythonIntegerError(name);
    }

    private static bool TryPythonInteger(string text, int originalBytes, out BigInteger result)
    {
        result = BigInteger.Zero;
        var start = text.Length > 0 && (text[0] is '+' or '-') ? 1 : 0;
        if (start == text.Length || text[start] is < '0' or > '9') return false;
        for (var index = start + 1; index < text.Length; index++)
            if ((text[index] is < '0' or > '9') && !(originalBytes >= 19 && text[index] == '_')) return false;
        if (originalBytes < 19)
        {
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var small)) return false;
            result = new(small);
            return true;
        }
        return BigInteger.TryParse(text.Replace("_", string.Empty), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    private static ArgumentException PythonIntegerError(string name) => new($"{name} is not an integer accepted by the Python/Pydantic 2.5 reference.", "input");
}
