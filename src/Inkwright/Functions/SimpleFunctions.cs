using Inkwright.Cos;

namespace Inkwright.Functions;

/// <summary>
/// A type 2 function (§7.10.3): one number in, a smooth run between two constants out. With an
/// exponent of one it is a straight line, which is what most gradients are made of.
/// </summary>
public sealed class ExponentialFunction : PdfFunction
{
    private readonly double[] _start;
    private readonly double[] _end;
    private readonly double _exponent;

    private ExponentialFunction(double[] domain, double[]? range, double[] start, double[] end, double exponent)
        : base(domain, range)
    {
        _start = start;
        _end = end;
        _exponent = exponent;
    }

    /// <inheritdoc />
    public override int OutputCount => _start.Length;

    /// <inheritdoc />
    public override void Evaluate(ReadOnlySpan<double> inputs, Span<double> outputs)
    {
        double x = Clamp(inputs.Length > 0 ? inputs[0] : 0, Domain[0], Domain[1]);
        double raised = _exponent == 1 ? x : Math.Pow(x, _exponent);

        for (int i = 0; i < _start.Length && i < outputs.Length; i++)
            outputs[i] = _start[i] + (raised * (_end[i] - _start[i]));

        ClampToRange(outputs);
    }

    internal static ExponentialFunction Read(PdfDictionary dictionary, double[] domain, double[]? range)
    {
        double[] start = Numbers(dictionary, PdfNames.C0) ?? [0];
        double[] end = Numbers(dictionary, PdfNames.C1) ?? [1];

        if (start.Length != end.Length)
        {
            int length = Math.Min(start.Length, end.Length);
            start = start[..length];
            end = end[..length];
        }

        double exponent = dictionary.GetNumber(PdfNames.N, 1);
        return new ExponentialFunction(domain, range, start, end, exponent);
    }
}

/// <summary>
/// A type 3 function (§7.10.4): a row of other functions laid end to end across the domain, each
/// handling one stretch of it. This is how a gradient with several colour stops is written.
/// </summary>
public sealed class StitchingFunction : PdfFunction
{
    private readonly PdfFunction[] _parts;
    private readonly double[] _bounds;
    private readonly double[] _encode;

    private StitchingFunction(double[] domain, double[]? range, PdfFunction[] parts, double[] bounds, double[] encode)
        : base(domain, range)
    {
        _parts = parts;
        _bounds = bounds;
        _encode = encode;
    }

    /// <inheritdoc />
    public override int OutputCount => _parts.Length > 0 ? _parts[0].OutputCount : 0;

    /// <inheritdoc />
    public override void Evaluate(ReadOnlySpan<double> inputs, Span<double> outputs)
    {
        if (_parts.Length == 0)
        {
            outputs.Clear();
            return;
        }

        double x = Clamp(inputs.Length > 0 ? inputs[0] : 0, Domain[0], Domain[1]);

        int part = 0;
        while (part < _bounds.Length && x >= _bounds[part])
            part++;

        double low = part == 0 ? Domain[0] : _bounds[part - 1];
        double high = part == _bounds.Length ? Domain[1] : _bounds[part];

        // Each part sees the stretch it owns mapped onto its own encoding, so a function written
        // for the unit interval can serve any stretch of the whole.
        double mapped = Interpolate(x, low, high, _encode[2 * part], _encode[(2 * part) + 1]);

        _parts[part].Evaluate([mapped], outputs);
        ClampToRange(outputs);
    }

    internal static StitchingFunction? Read(PdfDictionary dictionary, double[] domain, double[]? range, int depth)
    {
        if (dictionary.GetArray(PdfNames.Functions) is not { Count: > 0 } array)
            return null;

        var parts = new PdfFunction[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            if (Read(array.Get(i), depth + 1) is not { } part)
                return null;

            parts[i] = part;
        }

        double[] bounds = Numbers(dictionary, PdfNames.Bounds) ?? [];
        if (bounds.Length != parts.Length - 1)
            return null;

        double[] encode = Numbers(dictionary, PdfNames.Encode) ?? Unit(parts.Length);
        if (encode.Length < parts.Length * 2)
            return null;

        return new StitchingFunction(domain, range, parts, bounds, encode);
    }

    private static double[] Unit(int count)
    {
        double[] encode = new double[count * 2];
        for (int i = 0; i < count; i++)
            encode[(2 * i) + 1] = 1;

        return encode;
    }
}
