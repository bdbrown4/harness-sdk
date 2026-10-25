using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Strands.Agents.SchemaImplementation;
using Strands.Agents.SchemaImplementation.Keywords;

namespace Strands.Agents;

internal static partial class ToolSchema
{
    private static IKeywordHandler SelectHandler(IKeywordHandler handler) => handler.Name switch
    {
        "type" => ExactIntegerType.Instance,
        "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "multipleOf" => new ExactNumericKeyword(handler),
        "const" or "enum" or "uniqueItems" => new ExactEqualityKeyword(handler),
        _ => handler
    };

    private sealed class ExactNumericKeyword(IKeywordHandler original) : IKeywordHandler
    {
        public string Name => original.Name;
        public object? ValidateKeywordValue(JsonElement value)
        {
            var validated = original.ValidateKeywordValue(value);
            if (Name == "multipleOf" && !new ExactNumber(value).IsPositive)
                throw new JsonSchemaException("'multipleOf' value must be strictly greater than zero.");
            return validated;
        }
        public void BuildSubschemas(KeywordData keyword, BuildContext context) => original.BuildSubschemas(keyword, context);

        public KeywordEvaluation Evaluate(KeywordData keyword, EvaluationContext context)
        {
            if (context.Instance.ValueKind != JsonValueKind.Number) return original.Evaluate(keyword, context);
            var instance = new ExactNumber(context.Instance);
            var limit = new ExactNumber(keyword.RawValue);
            var comparison = Name == "multipleOf" ? 0 : instance.CompareTo(limit);
            var valid = Name switch
            {
                "minimum" => comparison >= 0,
                "maximum" => comparison <= 0,
                "exclusiveMinimum" => comparison > 0,
                "exclusiveMaximum" => comparison < 0,
                _ => instance.IsMultipleOf(limit)
            };
            if (valid) return new KeywordEvaluation { Keyword = Name, IsValid = true };
            var error = Name switch
            {
                "minimum" => ErrorMessages.GetMinimum(context.Options.Culture),
                "maximum" => ErrorMessages.GetMaximum(context.Options.Culture),
                "exclusiveMinimum" => ErrorMessages.GetExclusiveMinimum(context.Options.Culture),
                "exclusiveMaximum" => ErrorMessages.GetExclusiveMaximum(context.Options.Culture),
                _ => ErrorMessages.GetMultipleOf(context.Options.Culture)
            };
            return new KeywordEvaluation
            {
                Keyword = Name,
                IsValid = false,
                Error = error.ReplaceToken("received", context.Instance)
                    .ReplaceToken(Name == "multipleOf" ? "divisor" : "limit", keyword.RawValue)
            };
        }
    }

    // A normalized coefficient and signed decimal-string exponent. Neither
    // comparison nor divisibility expands exponent-sized powers or buffers.
    private sealed class ExactNumber
    {
        private readonly bool _negative;
        private readonly string _digits;
        private readonly DecimalOrder _scale;
        private readonly DecimalOrder _order;

        internal ExactNumber(JsonElement value)
        {
            var raw = value.GetRawText();
            _negative = raw[0] == '-';
            var start = _negative ? 1 : 0;
            var exponentAt = raw.IndexOfAny(new[] { 'e', 'E' });
            var end = exponentAt < 0 ? raw.Length : exponentAt;
            var digits = new char[end - start];
            var length = 0;
            var fractionalDigits = 0;
            var fraction = false;
            for (var index = start; index < end; index++)
            {
                if (raw[index] == '.') { fraction = true; continue; }
                digits[length++] = raw[index];
                if (fraction) fractionalDigits++;
            }
            var first = 0;
            while (first < length && digits[first] == '0') first++;
            var last = length;
            while (last > first && digits[last - 1] == '0') last--;
            _digits = new string(digits, first, last - first);
            var exponent = exponentAt < 0 ? DecimalOrder.Zero : new DecimalOrder(raw.Substring(exponentAt + 1));
            _scale = exponent.Add((long)length - last - fractionalDigits);
            _order = _scale.Add((long)_digits.Length - 1);
        }

        internal bool IsPositive => !_negative && _digits.Length != 0;

        internal int SemanticHash() => _digits.Length == 0 ? 0 : CombineHash(CombineHash(_negative ? 1 : 0, StringComparer.Ordinal.GetHashCode(_digits)), _scale.SemanticHash());

        internal int CompareTo(ExactNumber other)
        {
            if (_digits.Length == 0) return other._digits.Length == 0 ? 0 : other._negative ? 1 : -1;
            if (other._digits.Length == 0) return _negative ? -1 : 1;
            if (_negative != other._negative) return _negative ? -1 : 1;
            var comparison = _order.CompareTo(other._order);
            if (comparison == 0)
            {
                for (var index = 0; index < Math.Max(_digits.Length, other._digits.Length); index++)
                {
                    var left = index < _digits.Length ? _digits[index] : '0';
                    var right = index < other._digits.Length ? other._digits[index] : '0';
                    if (left != right) { comparison = left.CompareTo(right); break; }
                }
            }
            return _negative ? -comparison : comparison;
        }

        internal bool IsMultipleOf(ExactNumber divisor)
        {
            if (divisor._digits.Length == 0) return false;
            if (_digits.Length == 0) return true;
            // Removing coefficient trailing zeros makes a negative decimal
            // scale difference impossible for a nonzero integral quotient.
            if (_scale.CompareTo(divisor._scale) < 0) return false;
            var numerator = BigInteger.Parse(_digits, NumberStyles.None, CultureInfo.InvariantCulture);
            var denominator = BigInteger.Parse(divisor._digits, NumberStyles.None, CultureInfo.InvariantCulture);
            denominator /= BigInteger.GreatestCommonDivisor(numerator, denominator);
            long twos = 0, fives = 0;
            while (denominator.IsEven) { denominator /= 2; twos++; }
            while (denominator % 5 == 0) { denominator /= 5; fives++; }
            return denominator.IsOne && _scale.CompareTo(divisor._scale.Add(Math.Max(twos, fives))) >= 0;
        }
    }

    // Exact signed decimal order with only a raw-length-bounded offset added.
    // Grade-school operations allocate by exponent text length, never magnitude.
    private readonly struct DecimalOrder : IComparable<DecimalOrder>
    {
        internal static readonly DecimalOrder Zero = new("0");
        private readonly bool _negative;
        private readonly string _digits;

        internal DecimalOrder(string raw)
        {
            var first = raw[0] is '-' or '+' ? 1 : 0;
            while (first < raw.Length - 1 && raw[first] == '0') first++;
            _digits = raw.Substring(first);
            _negative = raw[0] == '-' && _digits != "0";
        }

        private DecimalOrder(bool negative, string digits)
        {
            _digits = digits;
            _negative = negative && digits != "0";
        }

        internal int SemanticHash() => CombineHash(_negative ? 1 : 0, StringComparer.Ordinal.GetHashCode(_digits));

        public int CompareTo(DecimalOrder other)
        {
            if (_negative != other._negative) return _negative ? -1 : 1;
            var comparison = CompareMagnitude(_digits, other._digits);
            return _negative ? -comparison : comparison;
        }

        internal DecimalOrder Add(long offset)
        {
            if (offset == 0) return this;
            var negative = offset < 0;
            var digits = (negative ? -offset : offset).ToString(CultureInfo.InvariantCulture);
            if (_negative == negative) return new DecimalOrder(_negative, AddMagnitude(_digits, digits));
            var comparison = CompareMagnitude(_digits, digits);
            if (comparison == 0) return Zero;
            return comparison > 0
                ? new DecimalOrder(_negative, SubtractMagnitude(_digits, digits))
                : new DecimalOrder(negative, SubtractMagnitude(digits, _digits));
        }

        private static int CompareMagnitude(string left, string right)
            => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

        private static string AddMagnitude(string left, string right)
        {
            var output = new char[Math.Max(left.Length, right.Length) + 1];
            var a = left.Length - 1;
            var b = right.Length - 1;
            var carry = 0;
            for (var index = output.Length - 1; index >= 0; index--)
            {
                var digit = (a >= 0 ? left[a--] - '0' : 0) + (b >= 0 ? right[b--] - '0' : 0) + carry;
                output[index] = (char)('0' + digit % 10);
                carry = digit / 10;
            }
            return output[0] == '0' ? new string(output, 1, output.Length - 1) : new string(output);
        }

        private static string SubtractMagnitude(string left, string right)
        {
            var output = new char[left.Length];
            var b = right.Length - 1;
            var borrow = 0;
            for (var index = left.Length - 1; index >= 0; index--)
            {
                var digit = left[index] - '0' - (b >= 0 ? right[b--] - '0' : 0) - borrow;
                borrow = digit < 0 ? 1 : 0;
                output[index] = (char)('0' + (digit < 0 ? digit + 10 : digit));
            }
            var first = 0;
            while (first < output.Length - 1 && output[first] == '0') first++;
            return new string(output, first, output.Length - first);
        }
    }
}
