using System.Text;
using Inkwright.Cos;
using Inkwright.Functions;
using Xunit;

namespace Inkwright.Tests;

public class FunctionTests
{
    [Fact]
    public void RunsAStraightLineBetweenTwoColours()
    {
        var dictionary = new PdfDictionary(4);
        dictionary.Set(PdfName.Get("FunctionType"), PdfValue.Integer(2));
        dictionary.Set(PdfName.Get("Domain"), PdfValue.Array(PdfArray.OfNumbers(0, 1)));
        dictionary.Set(PdfName.Get("C0"), PdfValue.Array(PdfArray.OfNumbers(1, 0, 0)));
        dictionary.Set(PdfName.Get("C1"), PdfValue.Array(PdfArray.OfNumbers(0, 0, 1)));

        PdfFunction function = PdfFunction.Read(PdfValue.Dictionary(dictionary))!;

        Assert.Equal(3, function.OutputCount);
        Assert.Equal([1, 0, 0], function.Evaluate(0));
        Assert.Equal([0.5, 0, 0.5], function.Evaluate(0.5));
        Assert.Equal([0, 0, 1], function.Evaluate(1));

        // Arguments outside the domain are clipped to it rather than extrapolated.
        Assert.Equal([1, 0, 0], function.Evaluate(-3));
    }

    [Fact]
    public void LaysStitchedPartsEndToEnd()
    {
        var first = Exponential(0, 1);
        var second = Exponential(1, 0);

        var parts = new PdfArray(2);
        parts.Add(PdfValue.Dictionary(first));
        parts.Add(PdfValue.Dictionary(second));

        var dictionary = new PdfDictionary(5);
        dictionary.Set(PdfName.Get("FunctionType"), PdfValue.Integer(3));
        dictionary.Set(PdfName.Get("Domain"), PdfValue.Array(PdfArray.OfNumbers(0, 1)));
        dictionary.Set(PdfName.Get("Functions"), PdfValue.Array(parts));
        dictionary.Set(PdfName.Get("Bounds"), PdfValue.Array(PdfArray.OfNumbers(0.5)));
        dictionary.Set(PdfName.Get("Encode"), PdfValue.Array(PdfArray.OfNumbers(0, 1, 0, 1)));

        PdfFunction function = PdfFunction.Read(PdfValue.Dictionary(dictionary))!;

        // A triangle: up across the first half, back down across the second.
        Assert.Equal(0, function.Evaluate(0)[0], 6);
        Assert.Equal(0.5, function.Evaluate(0.25)[0], 6);
        Assert.Equal(1, function.Evaluate(0.5)[0], 6);
        Assert.Equal(0.5, function.Evaluate(0.75)[0], 6);
        Assert.Equal(0, function.Evaluate(1)[0], 6);
    }

    [Fact]
    public void InterpolatesBetweenTabulatedSamples()
    {
        // Four eight-bit samples along one input: 0, 85, 170, 255.
        PdfFunction function = Sampled([0x00, 0x55, 0xAA, 0xFF], size: 4, bits: 8);

        Assert.Equal(0, function.Evaluate(0)[0], 3);
        Assert.Equal(1, function.Evaluate(1)[0], 3);
        Assert.Equal(1.0 / 3, function.Evaluate(1.0 / 3)[0], 2);

        // Halfway between the first two samples is halfway between their values.
        Assert.Equal(1.0 / 6, function.Evaluate(1.0 / 6)[0], 2);
    }

    [Fact]
    public void PassesThroughEverySampleWhenInterpolatingCubically()
    {
        // The same four samples, now with Order 3: a cubic spline still hits each sample exactly.
        PdfFunction function = Sampled([0x00, 0x55, 0xAA, 0xFF], size: 4, bits: 8, order: 3);

        Assert.Equal(0, function.Evaluate(0)[0], 3);
        Assert.Equal(1.0 / 3, function.Evaluate(1.0 / 3)[0], 3);
        Assert.Equal(2.0 / 3, function.Evaluate(2.0 / 3)[0], 3);
        Assert.Equal(1, function.Evaluate(1)[0], 3);
    }

    [Fact]
    public void BendsBetweenSamplesRatherThanRunningStraight()
    {
        // A curve that rises steeply then flattens: 0, 0.25, 0.9, 1. Between the last two samples a
        // cubic overshoots the straight line because it carries the momentum of the steep rise.
        PdfFunction linear = Sampled([0x00, 0x40, 0xE6, 0xFF], size: 4, bits: 8, order: 1);
        PdfFunction cubic = Sampled([0x00, 0x40, 0xE6, 0xFF], size: 4, bits: 8, order: 3);

        double straight = linear.Evaluate(5.0 / 6)[0];
        double curved = cubic.Evaluate(5.0 / 6)[0];

        // Both pass through the samples at the ends of the span.
        Assert.Equal(0.9, linear.Evaluate(2.0 / 3)[0], 2);
        Assert.Equal(0.9, cubic.Evaluate(2.0 / 3)[0], 2);

        // But between them the cubic does not merely average the two, so it differs from the line.
        Assert.NotEqual(straight, curved, 3);
    }

    [Fact]
    public void IgnoresOrderThreeWhenAnAxisHasFewerThanFourSamples()
    {
        PdfFunction linear = Sampled([0x00, 0xFF], size: 2, bits: 8, order: 1);
        PdfFunction requestedCubic = Sampled([0x00, 0xFF], size: 2, bits: 8, order: 3);

        Assert.Equal(linear.Evaluate(0.25)[0], requestedCubic.Evaluate(0.25)[0], 8);
        Assert.Equal(linear.Evaluate(0.75)[0], requestedCubic.Evaluate(0.75)[0], 8);
    }

    [Fact]
    public void InterpolatesOrderThreeAcrossEveryInputAxis()
    {
        // Dimension zero is the fast axis. Repeating the same nonlinear row along dimension one
        // makes its midpoint neutral while still requiring the multidimensional cubic path.
        byte[] row = [0x00, 0x40, 0xE6, 0xFF];
        byte[] samples = [.. row, .. row, .. row, .. row];
        PdfFunction linear = Sampled2D(samples, order: 1);
        PdfFunction cubic = Sampled2D(samples, order: 3);

        Assert.NotEqual(linear.Evaluate(5.0 / 6, 0.5)[0], cubic.Evaluate(5.0 / 6, 0.5)[0], 3);
    }

    [Theory]
    [InlineData("{ 2 mul }", 3, 6)]
    [InlineData("{ 1 add 2 div }", 3, 2)]
    [InlineData("{ dup mul }", 4, 16)]
    [InlineData("{ abs }", -5, 5)]
    [InlineData("{ neg }", 5, -5)]
    [InlineData("{ 3 exp }", 2, 8)]
    [InlineData("{ sqrt }", 9, 3)]
    [InlineData("{ truncate }", 3.7, 3)]
    [InlineData("{ round }", 3.5, 4)]
    [InlineData("{ ceiling }", 3.2, 4)]
    [InlineData("{ floor }", 3.8, 3)]
    [InlineData("{ 4 idiv }", 9, 2)]
    [InlineData("{ 4 mod }", 9, 1)]
    [InlineData("{ pop 7 }", 1, 7)]
    public void RunsTheArithmeticOfTheCalculatorLanguage(string program, double input, double expected) =>
        Assert.Equal(expected, Calculator(program).Evaluate(input)[0], 6);

    [Theory]
    [InlineData("{ sin }", 0, 0)]
    [InlineData("{ sin }", 90, 1)]
    [InlineData("{ sin }", 30, 0.5)]
    [InlineData("{ cos }", 0, 1)]
    [InlineData("{ cos }", 180, -1)]
    [InlineData("{ cos }", 60, 0.5)]
    public void MeasuresAnglesInDegreesAsTableFortyTwoRequires(string program, double input, double expected) =>
        Assert.Equal(expected, Calculator(program).Evaluate(input)[0], 6);

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 90)]
    [InlineData(0, -1, 180)]
    [InlineData(-1, 0, 270)]
    public void ReportsArcTangentsBetweenZeroAndThreeHundredAndSixty(double numerator, double denominator, double expected)
    {
        PdfFunction function = Calculator(
            "{ atan }", domain: [-100, 100, -100, 100], range: [0, 360]);

        Assert.Equal(expected, function.Evaluate(numerator, denominator)[0], 6);
    }

    [Theory]
    [InlineData("{ 5 gt { 1 } { 0 } ifelse }", 6, 1)]
    [InlineData("{ 5 gt { 1 } { 0 } ifelse }", 4, 0)]
    [InlineData("{ 0 gt { 99 } if }", 1, 99)]
    [InlineData("{ dup 0 lt { neg } if }", -8, 8)]
    [InlineData("{ dup 0 lt { neg } if }", 8, 8)]
    public void ChoosesBetweenBranches(string program, double input, double expected) =>
        Assert.Equal(expected, Calculator(program).Evaluate(input)[0], 6);

    [Fact]
    public void ReordersTheStack()
    {
        // The result is whatever is left on top. Pushing 1 leaves it there; exch puts the input
        // back on top, so the two programs answer differently.
        Assert.Equal(1, Calculator("{ 1 }").Evaluate(5)[0], 6);
        Assert.Equal(5, Calculator("{ 1 exch }").Evaluate(5)[0], 6);

        // Three copies of the input, added twice, is three times the input.
        Assert.Equal(15, Calculator("{ dup dup add add }").Evaluate(5)[0], 6);

        // index reaches past the 7 to copy the input back to the top.
        Assert.Equal(5, Calculator("{ 7 1 index }").Evaluate(5)[0], 6);

        // roll rotates the top three right by one: 1 2 3 becomes 3 1 2.
        Assert.Equal(2, Calculator("{ pop 1 2 3 3 1 roll }").Evaluate(5)[0], 6);
        Assert.Equal(1, Calculator("{ pop 1 2 3 3 1 roll pop }").Evaluate(5)[0], 6);
        Assert.Equal(3, Calculator("{ pop 1 2 3 3 1 roll pop pop }").Evaluate(5)[0], 6);
    }

    [Fact]
    public void SurvivesAProgramThatMakesNoSense()
    {
        // Too few operands and unknown words leave the stack alone rather than throwing.
        Assert.Equal(0, Calculator("{ add add add }").Evaluate(0)[0], 6);
        Assert.Equal(3, Calculator("{ nonsense 3 }").Evaluate(0)[0], 6);
        Assert.Null(PdfFunction.Read(PdfValue.Dictionary(new PdfDictionary(1))));
    }

    private static PdfDictionary Exponential(double from, double to)
    {
        var dictionary = new PdfDictionary(4);
        dictionary.Set(PdfName.Get("FunctionType"), PdfValue.Integer(2));
        dictionary.Set(PdfName.Get("Domain"), PdfValue.Array(PdfArray.OfNumbers(0, 1)));
        dictionary.Set(PdfName.Get("C0"), PdfValue.Array(PdfArray.OfNumbers(from)));
        dictionary.Set(PdfName.Get("C1"), PdfValue.Array(PdfArray.OfNumbers(to)));
        return dictionary;
    }

    private static PdfFunction Sampled(byte[] samples, int size, int bits, int order = 1)
    {
        var stream = new PdfStream(new PdfDictionary(7), samples);
        stream.Dictionary.Set(PdfName.Get("FunctionType"), PdfValue.Integer(0));
        stream.Dictionary.Set(PdfName.Get("Domain"), PdfValue.Array(PdfArray.OfNumbers(0, 1)));
        stream.Dictionary.Set(PdfName.Get("Range"), PdfValue.Array(PdfArray.OfNumbers(0, 1)));
        stream.Dictionary.Set(PdfName.Get("Size"), PdfValue.Array(PdfArray.OfNumbers(size)));
        stream.Dictionary.Set(PdfName.Get("BitsPerSample"), PdfValue.Integer(bits));

        if (order != 1)
            stream.Dictionary.Set(PdfName.Get("Order"), PdfValue.Integer(order));

        return PdfFunction.Read(PdfValue.Stream(stream))!;
    }

    private static PdfFunction Sampled2D(byte[] samples, int order)
    {
        var stream = new PdfStream(new PdfDictionary(7), samples);
        stream.Dictionary.Set(PdfName.Get("FunctionType"), PdfValue.Integer(0));
        stream.Dictionary.Set(PdfName.Get("Domain"), PdfValue.Array(PdfArray.OfNumbers(0, 1, 0, 1)));
        stream.Dictionary.Set(PdfName.Get("Range"), PdfValue.Array(PdfArray.OfNumbers(0, 1)));
        stream.Dictionary.Set(PdfName.Get("Size"), PdfValue.Array(PdfArray.OfNumbers(4, 4)));
        stream.Dictionary.Set(PdfName.Get("BitsPerSample"), PdfValue.Integer(8));
        stream.Dictionary.Set(PdfName.Get("Order"), PdfValue.Integer(order));
        return PdfFunction.Read(PdfValue.Stream(stream))!;
    }

    private static PdfFunction Calculator(string program, double[]? domain = null, double[]? range = null)
    {
        var stream = new PdfStream(new PdfDictionary(4), Encoding.ASCII.GetBytes(program));
        stream.Dictionary.Set(PdfName.Get("FunctionType"), PdfValue.Integer(4));
        stream.Dictionary.Set(PdfName.Get("Domain"), PdfValue.Array(PdfArray.OfNumbers(domain ?? [-1000, 1000])));
        stream.Dictionary.Set(PdfName.Get("Range"), PdfValue.Array(PdfArray.OfNumbers(range ?? [-1000, 1000])));

        return PdfFunction.Read(PdfValue.Stream(stream))!;
    }
}
