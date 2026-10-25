using System.Globalization;
using System.Numerics;

namespace Strands.Agents;

/// <summary>Shortest decimal labels for finite binary64 values under the ECMAScript rules.</summary>
internal static class EcmaScriptNumber
{
    private const ulong MaximumFiniteBits = 0x7fefffffffffffff;

    internal static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0) return "0";
        var negative = value < 0;
        value = Math.Abs(value);
        // All ordinary integral line labels are exact, uniquely represented safe integers.
        if (value <= 9007199254740991d && Math.Truncate(value) == value)
            return (negative ? "-" : string.Empty) + new BigInteger(value).ToString(CultureInfo.InvariantCulture);
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        var exact = BinaryValue(bits);
        var previous = BinaryValue(bits - 1);
        var next = bits == MaximumFiniteBits ? default : BinaryValue(bits + 1);
        var exponent = (int)Math.Floor(Math.Log10(value));
        while (ComparePower(exact, exponent) < 0) exponent--;
        while (ComparePower(exact, exponent + 1) >= 0) exponent++;
        for (var precision = 1; precision <= 17; precision++)
        {
            var power = exponent - precision + 1;
            var scaledNumerator = exact.Numerator;
            var scaledDenominator = exact.Denominator;
            if (power >= 0) scaledDenominator *= BigInteger.Pow(10, power);
            else scaledNumerator *= BigInteger.Pow(10, -power);
            var nearest = BigInteger.DivRem(scaledNumerator, scaledDenominator, out var remainder);
            var halfway = (remainder * 2).CompareTo(scaledDenominator);
            if (halfway > 0 || halfway == 0 && !nearest.IsEven) nearest++;
            Candidate? best = null;
            for (var delta = -1; delta <= 1; delta++)
            {
                var coefficient = nearest + delta;
                if (coefficient.Sign <= 0) continue;
                var candidatePower = power;
                while (coefficient % 10 == 0) { coefficient /= 10; candidatePower++; }
                var digits = coefficient.ToString(CultureInfo.InvariantCulture);
                if (digits.Length > precision) continue;
                var candidateValue = DecimalValue(coefficient, candidatePower);
                var difference = candidateValue.Numerator * exact.Denominator - exact.Numerator * candidateValue.Denominator;
                var direction = difference.Sign;
                difference = BigInteger.Abs(difference);
                if (direction != 0)
                {
                    var neighbour = direction < 0 || bits == MaximumFiniteBits ? previous : next;
                    var gap = BigInteger.Abs(exact.Numerator * neighbour.Denominator - neighbour.Numerator * exact.Denominator);
                    var relation = (difference * 2 * neighbour.Denominator).CompareTo(gap * candidateValue.Denominator);
                    if (relation > 0 || relation == 0 && (bits & 1) != 0) continue;
                }
                var candidate = new Candidate(coefficient, digits, candidatePower, difference, candidateValue.Denominator);
                if (best is null || IsBetter(candidate, best)) best = candidate;
            }
            if (best is not null) return (negative ? "-" : string.Empty) + Render(best.Digits, best.Power);
        }
        throw new InvalidOperationException("No round-tripping binary64 decimal label was found.");
    }

    private static bool IsBetter(Candidate candidate, Candidate current)
    {
        if (candidate.Digits.Length != current.Digits.Length) return candidate.Digits.Length < current.Digits.Length;
        var distance = (candidate.Difference * current.Denominator).CompareTo(current.Difference * candidate.Denominator);
        return distance < 0 || distance == 0 && candidate.Coefficient.IsEven && !current.Coefficient.IsEven;
    }

    private static string Render(string digits, int power)
    {
        var point = digits.Length + power;
        if (point > 0 && point <= 21)
            return point >= digits.Length ? digits + new string('0', point - digits.Length) : digits.Insert(point, ".");
        if (point > -6 && point <= 0) return "0." + new string('0', -point) + digits;
        var exponent = point - 1;
        return digits.Substring(0, 1) + (digits.Length == 1 ? string.Empty : "." + digits.Substring(1)) + "e" +
            (exponent >= 0 ? "+" : string.Empty) + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static Fraction BinaryValue(ulong bits)
    {
        if (bits == 0) return new(BigInteger.Zero, BigInteger.One);
        var encodedExponent = (int)((bits >> 52) & 0x7ff);
        var significand = new BigInteger((bits & 0xfffffffffffff) | (encodedExponent == 0 ? 0UL : 1UL << 52));
        var exponent = encodedExponent == 0 ? -1074 : encodedExponent - 1075;
        return exponent >= 0 ? new(significand << exponent, BigInteger.One) : new(significand, BigInteger.One << -exponent);
    }

    private static Fraction DecimalValue(BigInteger coefficient, int power) => power >= 0
        ? new(coefficient * BigInteger.Pow(10, power), BigInteger.One)
        : new(coefficient, BigInteger.Pow(10, -power));

    private static int ComparePower(Fraction value, int power) => power >= 0
        ? value.Numerator.CompareTo(value.Denominator * BigInteger.Pow(10, power))
        : (value.Numerator * BigInteger.Pow(10, -power)).CompareTo(value.Denominator);

    private readonly record struct Fraction(BigInteger Numerator, BigInteger Denominator);
    private sealed record Candidate(BigInteger Coefficient, string Digits, int Power, BigInteger Difference, BigInteger Denominator);
}
