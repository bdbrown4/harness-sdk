using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Strands.Harness;

/// <summary>Detached paging values for one selected-file execution.</summary>
internal sealed class ReadPaging
{
    private readonly ReadPagingProfile _profile;
    private readonly BigInteger? _offset;
    private readonly BigInteger? _limit;
    private readonly bool _hasNumberOffset;
    private readonly double _numberOffset;
    private readonly double _numberLimit;

    private ReadPaging(ReadPagingProfile profile, BigInteger? offset, BigInteger? limit)
    {
        _profile = profile;
        _offset = offset;
        _limit = limit;
    }

    private ReadPaging(bool hasOffset, double offset, double limit)
    {
        _profile = ReadPagingProfile.TypeScript;
        _hasNumberOffset = hasOffset;
        _numberOffset = offset;
        _numberLimit = limit;
    }

    internal static ReadPaging Parse(JsonElement input, ReadPagingProfile profile)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("The read input must be an object.", nameof(input));
        if (profile == ReadPagingProfile.TypeScript)
        {
            var hasOffset = input.TryGetProperty("offset", out var offset);
            return new(hasOffset, hasOffset ? TypeScriptNumber(offset, "offset") : 0,
                input.TryGetProperty("limit", out var limit) ? TypeScriptNumber(limit, "limit") : 2000);
        }
        return new(profile, OptionalInteger(input, "offset", profile), OptionalInteger(input, "limit", profile));
    }

    internal string Render(string[] lines)
    {
        if (_profile == ReadPagingProfile.TypeScript) return RenderNumbers(lines);
        var requestedStart = BigInteger.Max(BigInteger.Zero, (_offset ?? BigInteger.One) - BigInteger.One);
        var requestedEnd = requestedStart + (_limit ?? new BigInteger(2000));
        var start = SliceIndex(requestedStart, lines.Length);
        var end = SliceIndex(requestedEnd, lines.Length);
        if (end <= start) return $"[File has {lines.Length} lines; offset {_offset?.ToString(CultureInfo.InvariantCulture) ?? "None"} is past the end.]";
        var count = end - start;
        var numbered = string.Join("\n", lines.Skip(start).Take(count).Select((line, index) =>
            (start + index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(6) + "\t" + line));
        if (requestedStart > 0 || requestedEnd < lines.Length)
            numbered += $"\n[Showing lines {start + 1}-{start + count} of {lines.Length}. Use offset/limit to read more.]";
        return numbered;
    }

    private string RenderNumbers(string[] lines)
    {
        var rawStart = Math.Max(0d, _numberOffset != 0 ? _numberOffset - 1 : 0);
        var rawEnd = rawStart + _numberLimit;
        var start = SliceNumberIndex(rawStart, lines.Length);
        var end = SliceNumberIndex(rawEnd, lines.Length);
        if (end <= start)
            return $"[File has {lines.Length} lines; offset {(_hasNumberOffset ? ReadPagingNumber.Format(_numberOffset) : "undefined")} is past the end.]";
        var count = end - start;
        var firstLabel = rawStart + 1;
        var numbered = string.Join("\n", lines.Skip(start).Take(count).Select((line, index) =>
            ReadPagingNumber.Format(index + firstLabel).PadLeft(6) + "\t" + line));
        if (rawStart > 0 || rawEnd < lines.Length)
            numbered += $"\n[Showing lines {ReadPagingNumber.Format(firstLabel)}-{ReadPagingNumber.Format(rawStart + count)} of {lines.Length}. Use offset/limit to read more.]";
        return numbered;
    }

    private static int SliceIndex(BigInteger value, int length)
    {
        if (value.Sign < 0) value += length;
        return value <= 0 ? 0 : value >= length ? length : (int)value;
    }

    private static int SliceNumberIndex(double value, int length)
    {
        // Addition occurs before endpoint truncation; infinities can arise from finite operands.
        if (value >= length) return length;
        if (value <= -length) return 0;
        var whole = Math.Truncate(value);
        if (whole < 0) whole += length;
        return whole <= 0 ? 0 : (int)whole;
    }

    private static double TypeScriptNumber(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number))
            throw new ArgumentException($"{name} must be a finite number.", nameof(value));
        return number;
    }

    private static BigInteger? OptionalInteger(JsonElement input, string name, ReadPagingProfile profile)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (profile == ReadPagingProfile.Python) return Strands.Agents.Pydantic25Integer.Parse(value, name);
        if (value.ValueKind != JsonValueKind.Number || !BigInteger.TryParse(value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result))
            throw new ArgumentException($"{name} must be an integer literal or null.", nameof(input));
        return result;
    }


}

/// <summary>Shortest decimal labels for finite binary64 values under the ECMAScript rules.</summary>
internal static class ReadPagingNumber
{
    internal static string Format(double value) => Strands.Agents.EcmaScriptNumber.Format(value);
}
