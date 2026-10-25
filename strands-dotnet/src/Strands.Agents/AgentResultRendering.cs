using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Strands.Agents;

/// <summary>Opt-in source-derived result presentation without changing the legacy ToString contract.</summary>
public static class AgentResultRendering
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Renders pending interrupts, accepted structured JSON, or selected message text.</summary>
    /// <param name="result">The result to render.</param>
    /// <param name="profile">The presentation policy.</param>
    /// <returns>The selected text presentation.</returns>
    public static string Render(AgentResult result, AgentResultOutputProfile profile = AgentResultOutputProfile.TypeScript)
    {
        Guard.NotNull(result, nameof(result));
        AgentResultJson.ValidateProfile(profile);
        if (result.Interrupts.Count > 0)
            return profile == AgentResultOutputProfile.TypeScript ? InterruptJson(result.Interrupts) : InterruptPython(result.Interrupts);
        if (result.StructuredOutput is { } structured) return JsonSerializer.Serialize(structured, Json);
        if (result.Message is null) return string.Empty;
        var parts = new List<string>();
        foreach (var block in result.Message.Content)
        {
            if (block is TextBlock text) parts.Add(text.Text);
            else if (block is ReasoningBlock reasoning && profile == AgentResultOutputProfile.TypeScript && !string.IsNullOrEmpty(reasoning.Text))
                parts.Add("💭 Reasoning:\n   " + reasoning.Text.Replace("\n", "\n   "));
            else if (block is CitationsBlock citations) parts.AddRange(citations.Content.Select(content => content.Text));
        }
        return profile == AgentResultOutputProfile.TypeScript ? string.Join("\n", parts) : string.Concat(parts.Select(text => text + "\n"));
    }

    private static string InterruptJson(IReadOnlyList<Interrupt> interrupts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (var interrupt in interrupts)
            {
                writer.WriteStartObject();
                writer.WriteString("id", interrupt.Id);
                writer.WriteString("name", interrupt.Name);
                if (interrupt.Reason is { } reason) { writer.WritePropertyName("reason"); reason.WriteTo(writer); }
                writer.WriteString("source", interrupt.Source switch
                {
                    InterruptSource.Tool => "tool",
                    InterruptSource.Hook => "hook",
                    InterruptSource.Middleware => "middleware",
                    InterruptSource.OrchestratorHook => "multiagent-hook",
                    InterruptSource.RemoteAgent => "remote-agent",
                    _ => throw new InvalidOperationException("Unknown native interrupt source.")
                });
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string InterruptPython(IReadOnlyList<Interrupt> interrupts)
        => "[" + string.Join(", ", interrupts.Select(value => "{'id': " + PythonString(value.Id) + ", 'name': " + PythonString(value.Name) +
            ", 'reason': " + (value.Reason is { } reason ? PythonValue(reason) : "None") + ", 'response': None}")) + "]";

    internal static string PythonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "None",
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.String => PythonString(PythonJsonString(value)),
        JsonValueKind.Number => PythonNumber(value),
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(PythonValue)) + "]",
        JsonValueKind.Object => PythonObject(value),
        _ => throw new JsonException("An interrupt reason requires defined JSON.")
    };

    private static string PythonObject(JsonElement value)
    {
        // json.loads produces a dictionary: the last value wins while the first
        // insertion position survives. This projection never rewrites the JSON.
        var fields = new List<KeyValuePair<string, JsonElement>>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            string name;
            try { name = property.Name; }
            catch (InvalidOperationException) { name = DecodeLeadingJsonString(property.ToString()); }
            var field = new KeyValuePair<string, JsonElement>(name, property.Value);
            if (positions.TryGetValue(name, out var index)) fields[index] = field;
            else { positions.Add(name, fields.Count); fields.Add(field); }
        }
        return "{" + string.Join(", ", fields.Select(field => PythonString(field.Key) + ": " + PythonValue(field.Value))) + "}";
    }

    private static string PythonJsonString(JsonElement value)
    {
        try { return value.GetString()!; }
        catch (InvalidOperationException) { return DecodeLeadingJsonString(value.GetRawText()); }
    }

    // JsonDocument accepts escaped lone surrogate code units, as does Python's
    // JSON decoder. GetString/Name reject those units; read only that validated
    // string token here so PythonString can escape them without losing data.
    // JsonProperty.ToString starts at its name's opening quote and retains the
    // uninterpreted JSON, so the same reader also handles an escaped lone key.
    private static string DecodeLeadingJsonString(string raw)
    {
        var text = new StringBuilder();
        for (var index = 1; index < raw.Length; index++)
        {
            var current = raw[index];
            if (current == '"') return text.ToString();
            if (current != '\\') { text.Append(current); continue; }
            switch (raw[++index])
            {
                case '"': text.Append('"'); break;
                case '\\': text.Append('\\'); break;
                case '/': text.Append('/'); break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'u':
                    text.Append((char)int.Parse(raw.Substring(index + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    index += 4;
                    break;
                default: throw new JsonException("Invalid escape in a validated JSON string.");
            }
        }
        throw new JsonException("Missing closing quote in a validated JSON string.");
    }

    private static string PythonNumber(JsonElement value)
    {
        var raw = value.GetRawText();
        // Valid JSON integer tokens already have canonical digits, apart from -0.
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0) return raw == "-0" ? "0" : raw;
        var negative = raw[0] == '-';
        var coefficientEnd = raw.IndexOfAny(['e', 'E']);
        if (coefficientEnd < 0) coefficientEnd = raw.Length;
        var nonzero = false;
        for (var index = 0; index < coefficientEnd; index++)
            if (raw[index] is >= '1' and <= '9') { nonzero = true; break; }
        if (!nonzero) return negative ? "-0.0" : "0.0";
        var bits = PythonFloatBits(raw);
        if (bits == 0x7ff0000000000000) return negative ? "-inf" : "inf";
        if (bits == 0) return negative ? "-0.0" : "0.0";
        var value64 = BitConverter.Int64BitsToDouble((long)bits);

        // Reuse the held portable shortest finite coefficient; change notation only.
        var shortest = EcmaScriptNumber.Format(Math.Abs(value64));
        var exponentAt = shortest.IndexOf('e');
        var mantissa = exponentAt < 0 ? shortest : shortest.Substring(0, exponentAt);
        var power = exponentAt < 0 ? 0 : int.Parse(shortest.Substring(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        var point = (dot < 0 ? mantissa.Length : dot) + power;
        var digits = mantissa.Replace(".", string.Empty);
        var first = 0;
        while (digits[first] == '0') first++;
        point -= first;
        digits = digits.Substring(first).TrimEnd('0');
        string text;
        if (point > 0 && point <= 16)
            text = point >= digits.Length ? digits + new string('0', point - digits.Length) + ".0" : digits.Insert(point, ".");
        else if (point <= 0 && point > -4)
            text = "0." + new string('0', -point) + digits;
        else
        {
            var exponent = point - 1;
            text = digits.Substring(0, 1) + (digits.Length == 1 ? string.Empty : "." + digits.Substring(1)) + "e" +
                (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
        }
        return (negative ? "-" : string.Empty) + text;
    }

    // The input is a validated nonzero JSON float token. Decimal-to-binary
    // conversion must not inherit the host runtime's older parsing/rounding.
    private static ulong PythonFloatBits(string raw)
    {
        var exponentAt = raw.IndexOfAny(['e', 'E']);
        var coefficientEnd = exponentAt < 0 ? raw.Length : exponentAt;
        var start = raw[0] == '-' ? 1 : 0;
        var dot = raw.IndexOf('.');
        var fractionDigits = dot < 0 ? 0 : coefficientEnd - dot - 1;
        var digits = raw.Substring(start, coefficientEnd - start).Replace(".", string.Empty).TrimStart('0');
        var last = digits.Length - 1;
        while (digits[last] == '0') last--;
        var trailingZeros = digits.Length - last - 1;
        digits = digits.Substring(0, last + 1);

        long exponent = 0;
        if (exponentAt >= 0)
        {
            var index = exponentAt + 1;
            var negativeExponent = raw[index] == '-';
            if (raw[index] is '+' or '-') index++;
            // Beyond this bound, no coefficient/decimal-point position in this
            // input can cancel the exponent back into binary64's finite range.
            var limit = raw.Length + 400L;
            for (; index < raw.Length; index++)
            {
                var digit = raw[index] - '0';
                exponent = exponent > (limit - digit) / 10 ? limit : exponent * 10 + digit;
            }
            if (negativeExponent) exponent = -exponent;
        }
        var power = exponent - fractionDigits + trailingZeros;
        var order = digits.Length - 1L + power;
        if (order > 308) return 0x7ff0000000000000;
        if (order < -324) return 0;

        var numerator = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        var denominator = BigInteger.One;
        if (power >= 0) numerator *= BigInteger.Pow(10, (int)power);
        else if (order >= 0) denominator = BigInteger.Pow(10, digits.Length - 1 - (int)order);
        else denominator = BigInteger.Pow(10, digits.Length - 1) * BigInteger.Pow(10, -(int)order);

        // Every finite binary64 value is an integer multiple of 2^-1074.
        // Search monotone positive bit patterns without any floating arithmetic.
        var scaledNumerator = numerator << 1074;
        var lower = 0UL;
        var upper = 0x7fefffffffffffffUL;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower + 1) >> 1);
            if (scaledNumerator >= denominator * PythonBinary64Units(middle)) lower = middle;
            else upper = middle - 1;
        }
        // At the upper edge the infinity bit pattern denotes virtual 2^1024
        // only for this midpoint comparison. Ties choose the even endpoint.
        var midpoint = denominator * (PythonBinary64Units(lower) + PythonBinary64Units(lower + 1));
        var comparison = (scaledNumerator << 1).CompareTo(midpoint);
        return comparison > 0 || comparison == 0 && (lower & 1) != 0 ? lower + 1 : lower;
    }

    private static BigInteger PythonBinary64Units(ulong bits)
    {
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = new BigInteger((bits & 0xfffffffffffff) | (exponent == 0 ? 0UL : 1UL << 52));
        return exponent == 0 ? significand : significand << (exponent - 1);
    }

    private static string PythonString(string value)
    {
        var quote = value.IndexOf('\'') >= 0 && value.IndexOf('"') < 0 ? '"' : '\'';
        var text = new StringBuilder().Append(quote);
        for (var index = 0; index < value.Length;)
        {
            var width = char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]) ? 2 : 1;
            var scalar = width == 2 ? char.ConvertToUtf32(value, index) : value[index];
            if (scalar == quote || scalar == '\\') { text.Append('\\').Append((char)scalar); index += width; continue; }
            switch (scalar)
            {
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case '\b': text.Append("\\x08"); break;
                case '\f': text.Append("\\x0c"); break;
                default:
                    if (!PythonUnicode.IsPrintable(scalar))
                        text.Append(scalar <= byte.MaxValue ? "\\x" : scalar <= char.MaxValue ? "\\u" : "\\U")
                            .Append(scalar.ToString(scalar <= byte.MaxValue ? "x2" : scalar <= char.MaxValue ? "x4" : "x8", CultureInfo.InvariantCulture));
                    else text.Append(value, index, width);
                    break;
            }
            index += width;
        }
        return text.Append(quote).ToString();
    }
}
