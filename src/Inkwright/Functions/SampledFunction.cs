using Inkwright.Cos;

namespace Inkwright.Functions;

/// <summary>
/// A type 0 function (§7.10.2): a table of samples with interpolation between them. The samples sit
/// on a grid of whatever size the function declares, and a value between grid points is the
/// weighted average of the corners of the cell it falls in.
/// </summary>
/// <remarks>
/// The <c>Order</c> entry may ask for cubic spline interpolation. It is evaluated as the tensor
/// product of Catmull-Rom splines through the four nearest samples on every input axis. When any
/// axis contains fewer than four samples, ISO 32000-2 requires <c>Order 3</c> to be ignored.
/// </remarks>
public sealed class SampledFunction : PdfFunction
{
    private readonly int[] _size;
    private readonly double[] _encode;
    private readonly double[] _decode;
    private readonly double[] _samples;
    private readonly int _outputs;
    private readonly int _order;

    private SampledFunction(
        double[] domain, double[] range, int[] size, double[] encode, double[] decode, double[] samples, int order)
        : base(domain, range)
    {
        _size = size;
        _encode = encode;
        _decode = decode;
        _samples = samples;
        _outputs = range.Length / 2;
        _order = order;
    }

    /// <inheritdoc />
    public override int OutputCount => _outputs;

    /// <inheritdoc />
    public override void Evaluate(ReadOnlySpan<double> inputs, Span<double> outputs)
    {
        if (_order == 3 && _size.Length == InputCount && _size.All(size => size >= 4))
        {
            EvaluateCubic(inputs, outputs);
            return;
        }

        int dimensions = Math.Min(InputCount, _size.Length);
        int corners = 1 << dimensions;

        Span<double> weights = stackalloc double[Math.Min(corners, 256)];
        Span<int> offsets = stackalloc int[Math.Min(corners, 256)];

        if (corners > weights.Length)
        {
            outputs.Clear();
            return;
        }

        weights[..corners].Fill(1);
        offsets[..corners].Clear();

        int stride = _outputs;
        int bit = 1;

        for (int i = 0; i < dimensions; i++)
        {
            double low = Domain[2 * i];
            double high = Domain[(2 * i) + 1];
            double x = Clamp(i < inputs.Length ? inputs[i] : low, low, high);

            double e = Clamp(Interpolate(x, low, high, _encode[2 * i], _encode[(2 * i) + 1]), 0, _size[i] - 1);

            // The cell is the one starting at the sample below e, except at the very top of the
            // grid, where it is the last cell and e sits on its upper edge.
            int floor = e < _size[i] - 1 ? (int)Math.Floor(e) : Math.Max(0, _size[i] - 2);
            double upper = e - floor;
            double lower = 1 - upper;

            for (int corner = 0; corner < corners; corner++)
            {
                bool high_ = (corner & bit) != 0;
                weights[corner] *= high_ ? upper : lower;
                offsets[corner] += (floor + (high_ ? 1 : 0)) * stride;
            }

            stride *= _size[i];
            bit <<= 1;
        }

        for (int j = 0; j < _outputs && j < outputs.Length; j++)
        {
            double total = 0;
            for (int corner = 0; corner < corners; corner++)
            {
                int index = offsets[corner] + j;
                if (index >= 0 && index < _samples.Length)
                    total += _samples[index] * weights[corner];
            }

            outputs[j] = Interpolate(total, 0, 1, _decode[2 * j], _decode[(2 * j) + 1]);
        }

        ClampToRange(outputs);
    }

    /// <summary>
    /// Interpolates with the tensor product of Catmull-Rom splines (§7.10.2, <c>Order</c> 3).
    /// Each axis contributes four neighbouring samples and four spline weights; multiplying those
    /// weights gives the contribution of each point in the local 4-to-the-m neighbourhood.
    /// </summary>
    private void EvaluateCubic(ReadOnlySpan<double> inputs, Span<double> outputs)
    {
        int dimensions = _size.Length;
        int[] floors = new int[dimensions];
        double[] weights = new double[dimensions * 4];
        int points = 1;

        for (int dimension = 0; dimension < dimensions; dimension++)
        {
            double low = Domain[2 * dimension];
            double high = Domain[(2 * dimension) + 1];
            double x = Clamp(dimension < inputs.Length ? inputs[dimension] : low, low, high);
            double e = Clamp(
                Interpolate(x, low, high, _encode[2 * dimension], _encode[(2 * dimension) + 1]),
                0,
                _size[dimension] - 1);

            int floor = e < _size[dimension] - 1
                ? (int)Math.Floor(e)
                : _size[dimension] - 2;
            double t = e - floor;
            double t2 = t * t;
            double t3 = t2 * t;

            floors[dimension] = floor;
            weights[(dimension * 4) + 0] = 0.5 * (-t + (2 * t2) - t3);
            weights[(dimension * 4) + 1] = 0.5 * (2 - (5 * t2) + (3 * t3));
            weights[(dimension * 4) + 2] = 0.5 * (t + (4 * t2) - (3 * t3));
            weights[(dimension * 4) + 3] = 0.5 * (-t2 + t3);
            points = checked(points * 4);
        }

        for (int j = 0; j < _outputs && j < outputs.Length; j++)
        {
            double total = 0;

            for (int point = 0; point < points; point++)
            {
                int choices = point;
                int stride = _outputs;
                int offset = j;
                double weight = 1;

                for (int dimension = 0; dimension < dimensions; dimension++)
                {
                    int neighbour = choices & 3;
                    choices >>= 2;
                    int sample = Math.Clamp(
                        floors[dimension] + neighbour - 1,
                        0,
                        _size[dimension] - 1);

                    offset += sample * stride;
                    stride *= _size[dimension];
                    weight *= weights[(dimension * 4) + neighbour];
                }

                if ((uint)offset < (uint)_samples.Length)
                    total += _samples[offset] * weight;
            }

            outputs[j] = Interpolate(total, 0, 1, _decode[2 * j], _decode[(2 * j) + 1]);
        }

        ClampToRange(outputs);
    }

    internal static SampledFunction? Read(PdfDictionary dictionary, PdfStream stream, double[] domain, double[] range)
    {
        if (Numbers(dictionary, PdfNames.Size) is not { Length: > 0 } sizes)
            return null;

        int bits = (int)dictionary.GetInteger(PdfNames.BitsPerSample);
        if (bits is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32))
            return null;

        int inputs = domain.Length / 2;
        int outputs = range.Length / 2;

        int[] size = new int[Math.Min(inputs, sizes.Length)];
        long count = outputs;

        for (int i = 0; i < size.Length; i++)
        {
            size[i] = (int)sizes[i];
            if (size[i] < 1)
                return null;

            count *= size[i];
            if (count > 32 * 1024 * 1024)
                return null;
        }

        if (size.Length != inputs)
            return null;

        double[] encode = Numbers(dictionary, PdfNames.Encode) ?? Grid(size);
        double[] decode = Numbers(dictionary, PdfNames.Decode) ?? range;

        if (encode.Length < inputs * 2 || decode.Length < outputs * 2)
            return null;

        // Order is 1 (linear) or 3 (cubic); an absent or out-of-range value means linear.
        int order = (int)dictionary.GetInteger(PdfName.Get("Order"), 1);

        return new SampledFunction(domain, range, size, encode, decode, ReadSamples(stream, (int)count, bits), order);
    }

    /// <summary>The default encoding maps each input across the whole extent of its grid axis.</summary>
    private static double[] Grid(int[] size)
    {
        double[] encode = new double[size.Length * 2];
        for (int i = 0; i < size.Length; i++)
            encode[(2 * i) + 1] = size[i] - 1;

        return encode;
    }

    /// <summary>
    /// Unpacks the samples, which are stored back to back at whatever width the function declares
    /// with no padding between them, and scales each to the unit interval.
    /// </summary>
    private static double[] ReadSamples(PdfStream stream, int count, int bits)
    {
        ReadOnlySpan<byte> data = stream.DecodedSpan();
        double[] samples = new double[count];
        double scale = 1.0 / (Math.Pow(2, bits) - 1);

        ulong buffer = 0;
        int held = 0;
        int position = 0;

        for (int i = 0; i < count; i++)
        {
            while (held < bits)
            {
                buffer = (buffer << 8) | (position < data.Length ? data[position] : 0u);
                position++;
                held += 8;
            }

            held -= bits;
            samples[i] = (buffer >> held) * scale;
            buffer &= (1UL << held) - 1;
        }

        return samples;
    }
}
