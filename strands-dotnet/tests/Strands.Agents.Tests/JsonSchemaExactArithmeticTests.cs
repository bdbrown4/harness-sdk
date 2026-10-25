using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class JsonSchemaExactArithmeticTests
{
    public static IEnumerable<object[]> Bounds()
    {
        var exponent = new string('9', 160);
        var successor = "1" + new string('0', 160);
        var predecessor = new string('9', 159) + "8";
        yield return ["max-adjacent-large-orders", "maximum", "1e" + successor, "1e" + exponent, true];
        yield return ["max-adjacent-large-orders-denied", "maximum", "1e" + exponent, "1e" + successor, false];
        yield return ["min-adjacent-large-orders", "minimum", "1e" + exponent, "1e" + successor, true];
        yield return ["min-adjacent-large-orders-denied", "minimum", "1e" + successor, "1e" + exponent, false];
        yield return ["max-negative-large-orders", "maximum", "-1e" + exponent, "-1e" + successor, true];
        yield return ["min-negative-large-orders", "minimum", "-1e" + exponent, "-1e" + successor, false];
        yield return ["max-adjacent-tiny-orders", "maximum", "1e-" + exponent, "1e-" + successor, true];
        yield return ["min-adjacent-tiny-orders", "minimum", "1e-" + exponent, "1e-" + successor, false];
        yield return ["max-equivalent-coefficients", "maximum", "123e" + predecessor, "12.30e" + exponent, true];
        yield return ["min-equivalent-coefficients", "minimum", "123e" + predecessor, "12.30e" + exponent, true];
        yield return ["exclusive-max-equivalent-coefficients", "exclusiveMaximum", "123e" + predecessor, "12.30e" + exponent, false];
        yield return ["exclusive-min-equivalent-coefficients", "exclusiveMinimum", "123e" + predecessor, "12.30e" + exponent, false];
        yield return ["exclusive-max-adjacent-orders", "exclusiveMaximum", "1e" + successor, "9e" + exponent, true];
        yield return ["exclusive-min-adjacent-orders", "exclusiveMinimum", "1e" + successor, "9e" + exponent, false];
        yield return ["same-order-coefficient", "maximum", "9.8e" + exponent, "9.9e" + exponent, false];
        yield return ["same-order-negative-coefficient", "maximum", "-9.8e" + exponent, "-9.9e" + exponent, true];
        yield return ["zero-representation-equal", "maximum", "-0.00e-" + successor, "0e" + exponent, true];
        yield return ["zero-exclusivity", "exclusiveMinimum", "-0.00e-" + successor, "0e" + exponent, false];
        yield return ["zero-above-negative", "minimum", "-1e-" + successor, "-0e" + exponent, true];
        yield return ["zero-below-positive", "maximum", "1e-" + successor, "-0e" + exponent, true];
        yield return ["exponent-leading-zeroes", "maximum", "10e+0000000000000000000000000", "1e1", true];
        yield return ["fraction-leading-zeroes", "minimum", "0.000001", "1e-6", true];
        yield return ["negative-exclusive-equal", "exclusiveMaximum", "-1.000e-2", "-0.01", false];
        yield return ["positive-exclusive-equal", "exclusiveMinimum", "100.000", "1e2", false];
    }

    public static IEnumerable<object[]> Multiples()
    {
        var exponent = new string('9', 160);
        var successor = "1" + new string('0', 160);
        yield return ["large-factor-two", "2", "1e" + exponent, true];
        yield return ["large-factor-five", "5", "1e" + exponent, true];
        yield return ["large-residual-three", "3", "1e" + exponent, false];
        yield return ["large-residual-seven", "7", "1e" + exponent, false];
        yield return ["large-scale-lower", "1e" + successor, "1e" + exponent, false];
        yield return ["large-scale-higher", "1e" + exponent, "1e" + successor, true];
        yield return ["tiny-divisor", "1e-" + exponent, "1", true];
        yield return ["tiny-value", "1", "1e-" + exponent, false];
        yield return ["same-tiny-scale", "5e-" + exponent, "15e-" + exponent, true];
        yield return ["zero-with-tiny-divisor", "3e-" + successor, "-0e" + exponent, true];
        yield return ["fractional-exact", "0.04", "0.12", true];
        yield return ["fractional-inexact", "0.04", "0.1", false];
        yield return ["negative-exact", "0.04", "-0.12", true];
        yield return ["power-factor-enough", "8", "1e3", true];
        yield return ["power-factor-insufficient", "8", "1e2", false];
        yield return ["large-coefficient-factor", BigInteger.Pow(2, 256).ToString(CultureInfo.InvariantCulture), "1e" + exponent, true];
    }

    [Theory]
    [MemberData(nameof(Bounds))]
    public async Task RegisteredBoundsCompareSignsOrdersAndCoefficientsExactly(string scenario, string keyword, string limit, string value, bool admitted)
        => await AssertAdmission(scenario, keyword, limit, value, admitted);

    [Theory]
    [MemberData(nameof(Multiples))]
    public async Task RegisteredDivisibilityDependsOnCoefficientsAndScalesRatherThanExponentExpansion(string scenario, string limit, string value, bool admitted)
        => await AssertAdmission(scenario, "multipleOf", limit, value, admitted);

    [Fact]
    public void BoundedDivisibilityMatchesAnIndependentRationalOracle()
    {
        var comparisons = 0;
        for (var divisor = 1; divisor <= 7; divisor++)
            for (var divisorExponent = -2; divisorExponent <= 2; divisorExponent++)
            {
                var divisorText = divisor.ToString(CultureInfo.InvariantCulture) + "e" + divisorExponent.ToString(CultureInfo.InvariantCulture);
                using var schemaDocument = JsonDocument.Parse("{\"multipleOf\":" + divisorText + "}");
                var schema = ToolSchema.Compile(schemaDocument.RootElement);
                var denominator = Rational(divisor, divisorExponent);
                for (var value = -7; value <= 7; value++)
                    for (var exponent = -3; exponent <= 3; exponent++)
                    {
                        var numerator = Rational(value, exponent);
                        var expected = (numerator.Numerator * denominator.Denominator) % (numerator.Denominator * denominator.Numerator) == 0;
                        using var input = JsonDocument.Parse(value.ToString(CultureInfo.InvariantCulture) + "e" + exponent.ToString(CultureInfo.InvariantCulture));
                        Assert.Equal(expected, schema.Evaluate(input.RootElement).IsValid);
                        comparisons++;
                    }
            }
        Assert.Equal(3675, comparisons);
    }

    [Fact]
    public void BoundedBoundsMatchIndependentRationalCrossMultiplication()
    {
        var values = new[] { (-31, -2), (-2, 3), (-1, 0), (-1, -2), (0, 3), (0, -3), (1, -3), (1, 0), (125, -2), (3, -2), (22, 1) };
        var comparisons = 0;
        foreach (var keyword in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" })
            foreach (var bound in values)
            {
                var boundText = bound.Item1.ToString(CultureInfo.InvariantCulture) + "e" + bound.Item2.ToString(CultureInfo.InvariantCulture);
                using var schemaDocument = JsonDocument.Parse("{\"" + keyword + "\":" + boundText + "}");
                var schema = ToolSchema.Compile(schemaDocument.RootElement);
                var limit = Rational(bound.Item1, bound.Item2);
                foreach (var item in values)
                {
                    var value = Rational(item.Item1, item.Item2);
                    var comparison = (value.Numerator * limit.Denominator).CompareTo(limit.Numerator * value.Denominator);
                    var expected = keyword switch { "minimum" => comparison >= 0, "maximum" => comparison <= 0, "exclusiveMinimum" => comparison > 0, _ => comparison < 0 };
                    using var input = JsonDocument.Parse(item.Item1.ToString(CultureInfo.InvariantCulture) + "e" + item.Item2.ToString(CultureInfo.InvariantCulture));
                    Assert.Equal(expected, schema.Evaluate(input.RootElement).IsValid);
                    comparisons++;
                }
            }
        Assert.Equal(484, comparisons);
    }

    private static (BigInteger Numerator, BigInteger Denominator) Rational(int coefficient, int exponent)
        => exponent < 0 ? (new BigInteger(coefficient), BigInteger.Pow(10, -exponent)) : (new BigInteger(coefficient) * BigInteger.Pow(10, exponent), BigInteger.One);

    private static async Task AssertAdmission(string scenario, string keyword, string limit, string value, bool admitted)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\",\"" + keyword + "\":" + limit + "}},\"required\":[\"value\"],\"additionalProperties\":false}");
        var calls = 0;
        var tool = new FunctionTool(new("exact_arithmetic", "A selected exact numeric capability.", document.RootElement), (_, _, _) => { calls++; return new("accepted"); });
        var model = new UnusedModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using var input = JsonDocument.Parse("{\"value\":" + value + "}");
        var original = input.RootElement.GetRawText();
        var result = await agent.InvokeToolAsync(new("exact", tool.Spec.Name, input.RootElement));
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, calls);
        Assert.Equal(original, input.RootElement.GetRawText());
        Assert.Same(tool, Assert.Single(agent.Tools));
        Assert.Equal(0, model.Calls);
    }

    private sealed class UnusedModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Exact arithmetic tests never execute a model, command or interpreter.");
        }
    }
}
