using Inkwright.Cos;

namespace Inkwright.Functions;

/// <summary>
/// A function object of ISO 32000-1 §7.10: a mapping from numbers to numbers that a document uses
/// wherever a value has to vary. Shadings ask one what colour a point is, a separation colour space
/// asks one what ink mixture stands for a tint, and a transfer function asks one how to bend a
/// component. All four kinds the specification defines are read here.
/// </summary>
public abstract class PdfFunction
{
    /// <summary>How many functions may nest before a stitching chain is taken to be looping.</summary>
    private const int MaxDepth = 8;

    private protected PdfFunction(double[] domain, double[]? range)
    {
        Domain = domain;
        Range = range;
    }

    /// <summary>The interval each input is clipped to, as pairs of bounds.</summary>
    public IReadOnlyList<double> Domain { get; }

    /// <summary>The interval each output is clipped to, or <see langword="null"/> when unbounded.</summary>
    public IReadOnlyList<double>? Range { get; }

    /// <summary>How many numbers the function takes.</summary>
    public int InputCount => Domain.Count / 2;

    /// <summary>How many numbers the function returns.</summary>
    public abstract int OutputCount { get; }

    /// <summary>Evaluates the function.</summary>
    /// <param name="inputs">The arguments, one per input.</param>
    /// <returns>The results, one per output.</returns>
    public double[] Evaluate(params ReadOnlySpan<double> inputs)
    {
        double[] outputs = new double[OutputCount];
        Evaluate(inputs, outputs);
        return outputs;
    }

    /// <summary>Evaluates the function into a caller's buffer.</summary>
    /// <param name="inputs">The arguments, one per input.</param>
    /// <param name="outputs">Where to put the results; at least <see cref="OutputCount"/> long.</param>
    public abstract void Evaluate(ReadOnlySpan<double> inputs, Span<double> outputs);

    /// <summary>Reads a function object.</summary>
    /// <param name="value">The function dictionary or stream.</param>
    /// <returns>The function, or <see langword="null"/> when the value is not one this library reads.</returns>
    public static PdfFunction? Read(PdfValue value) => Read(value, 0);

    /// <summary>
    /// Reads an entry that is either one function returning several numbers, or an array of
    /// functions each returning one. Shadings and colour spaces both allow either spelling.
    /// </summary>
    /// <param name="value">The function entry.</param>
    public static IReadOnlyList<PdfFunction> ReadMany(PdfValue value)
    {
        if (value.AsArray is not { } array)
            return Read(value) is { } single ? [single] : [];

        List<PdfFunction> functions = new(array.Count);
        for (int i = 0; i < array.Count; i++)
        {
            if (Read(array.Get(i)) is { } function)
                functions.Add(function);
        }

        return functions;
    }

    /// <summary>
    /// Evaluates a list of functions as one, concatenating what each returns. A colour space that
    /// names one function per component behaves exactly like one function per colour.
    /// </summary>
    /// <param name="functions">The functions to evaluate.</param>
    /// <param name="inputs">The arguments each is given.</param>
    /// <param name="outputs">Where to put the results.</param>
    public static void EvaluateMany(
        IReadOnlyList<PdfFunction> functions, ReadOnlySpan<double> inputs, Span<double> outputs)
    {
        ArgumentNullException.ThrowIfNull(functions);

        if (functions.Count == 1)
        {
            functions[0].Evaluate(inputs, outputs);
            return;
        }

        int written = 0;
        foreach (PdfFunction function in functions)
        {
            int count = Math.Min(function.OutputCount, outputs.Length - written);
            if (count <= 0)
                break;

            function.Evaluate(inputs, outputs.Slice(written, count));
            written += count;
        }
    }

    /// <summary>Clips a value to a pair of bounds, tolerating a pair given the wrong way round.</summary>
    private protected static double Clamp(double value, double low, double high) =>
        low <= high ? Math.Clamp(value, low, high) : Math.Clamp(value, high, low);

    /// <summary>Maps a value from one interval onto another.</summary>
    private protected static double Interpolate(double x, double xLow, double xHigh, double yLow, double yHigh) =>
        xHigh == xLow ? yLow : yLow + ((x - xLow) * ((yHigh - yLow) / (xHigh - xLow)));

    /// <summary>Clips every output to the function's range, when it declares one.</summary>
    private protected void ClampToRange(Span<double> outputs)
    {
        if (Range is not { } range)
            return;

        for (int i = 0; i < outputs.Length && (2 * i) + 1 < range.Count; i++)
            outputs[i] = Clamp(outputs[i], range[2 * i], range[(2 * i) + 1]);
    }

    private protected static double[]? Numbers(PdfDictionary dictionary, PdfName key)
    {
        if (dictionary.GetArray(key) is not { Count: > 0 } array)
            return null;

        double[] numbers = new double[array.Count];
        for (int i = 0; i < array.Count; i++)
            numbers[i] = array.GetNumber(i);

        return numbers;
    }

    internal static PdfFunction? Read(PdfValue value, int depth)
    {
        if (depth > MaxDepth)
            return null;

        PdfStream? stream = value.AsStream;
        PdfDictionary? dictionary = stream?.Dictionary ?? value.AsDictionary;

        if (dictionary is null || Numbers(dictionary, PdfNames.Domain) is not { } domain)
            return null;

        double[]? range = Numbers(dictionary, PdfNames.Range);

        return dictionary.GetInteger(PdfNames.FunctionType, -1) switch
        {
            0 when stream is not null && range is not null =>
                SampledFunction.Read(dictionary, stream, domain, range),
            2 => ExponentialFunction.Read(dictionary, domain, range),
            3 => StitchingFunction.Read(dictionary, domain, range, depth),
            4 when stream is not null && range is not null =>
                PostScriptFunction.Read(stream, domain, range),
            _ => null,
        };
    }
}
