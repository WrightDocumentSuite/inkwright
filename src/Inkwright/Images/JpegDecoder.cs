namespace Inkwright.Images;

/// <summary>A decoded JPEG: samples interleaved at full resolution, eight bits each.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Components">One for grey, three for red-green-blue, four for cyan-magenta-yellow-black.</param>
/// <param name="Samples">The samples, <paramref name="Components"/> bytes per pixel.</param>
internal readonly record struct JpegImage(int Width, int Height, int Components, byte[] Samples);

/// <summary>
/// A baseline JPEG decoder, enough to turn the <c>DCTDecode</c> images a PDF carries back into
/// pixels. Sequential Huffman-coded images are decoded; progressive and arithmetic-coded ones are
/// refused outright rather than decoded wrongly.
/// </summary>
internal static class JpegDecoder
{
    /// <summary>The order coefficients are stored in within a block.</summary>
    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>Decodes a JPEG file.</summary>
    /// <param name="data">The whole file, starting at the start-of-image marker.</param>
    /// <exception cref="NotSupportedException">The file uses a coding this decoder does not read.</exception>
    internal static JpegImage Decode(ReadOnlySpan<byte> data)
    {
        var reader = new JpegFrameReader(data);
        JpegFrame frame = reader.Read();

        byte[] planes = JpegScanDecoder.Decode(frame);
        return Combine(frame, planes);
    }

    /// <summary>
    /// Interleaves the component planes and applies the colour transform. Which transform applies
    /// is decided the way every decoder decides it: by the Adobe marker if there is one, and by the
    /// number of components if there is not.
    /// </summary>
    private static JpegImage Combine(JpegFrame frame, byte[] planes)
    {
        int width = frame.Width;
        int height = frame.Height;
        int count = frame.Components.Length;
        byte[] samples = new byte[width * height * count];

        bool ycc = count switch
        {
            3 => frame.AdobeTransform != 0,
            4 => frame.AdobeTransform == 2,
            _ => false,
        };

        for (int i = 0; i < width * height; i++)
        {
            int offset = i * count;

            if (ycc)
            {
                YccToRgb(planes, offset, samples, offset);

                // A four-component image carries its black plane past the three colour ones.
                if (count == 4)
                    samples[offset + 3] = planes[offset + 3];
            }
            else
            {
                for (int c = 0; c < count; c++)
                    samples[offset + c] = planes[offset + c];
            }
        }

        // Adobe writes four-component data inverted, which is the one convention no standard states.
        if (count == 4 && frame.HasAdobe)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (byte)(255 - samples[i]);
        }

        return new JpegImage(width, height, count, samples);
    }

    /// <summary>The conversion of ITU-T T.871: luminance and two colour differences back to red, green and blue.</summary>
    private static void YccToRgb(byte[] source, int from, byte[] target, int to)
    {
        double y = source[from];
        double cb = source[from + 1] - 128.0;
        double cr = source[from + 2] - 128.0;

        target[to] = Clamp(y + (1.402 * cr));
        target[to + 1] = Clamp(y - (0.344136 * cb) - (0.714136 * cr));
        target[to + 2] = Clamp(y + (1.772 * cb));
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    /// <summary>Reverses the zig-zag ordering into a plain row-major block.</summary>
    internal static void Unzig(ReadOnlySpan<short> coefficients, Span<double> block, ReadOnlySpan<ushort> quantization)
    {
        block.Clear();

        for (int i = 0; i < 64; i++)
            block[ZigZag[i]] = coefficients[i] * (double)quantization[i];
    }

    /// <summary>
    /// The inverse discrete cosine transform, done as two passes of the one-dimensional transform.
    /// This is the plain definition rather than a fast approximation: it costs more per block and
    /// gets exactly the answer the standard describes.
    /// </summary>
    internal static void InverseTransform(Span<double> block, Span<byte> output)
    {
        Span<double> intermediate = stackalloc double[64];

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                for (int u = 0; u < 8; u++)
                    sum += Scale(u) * block[(y * 8) + u] * Cosine(x, u);

                intermediate[(y * 8) + x] = sum / 2;
            }
        }

        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                double sum = 0;
                for (int v = 0; v < 8; v++)
                    sum += Scale(v) * intermediate[(v * 8) + x] * Cosine(y, v);

                output[(y * 8) + x] = Clamp((sum / 2) + 128);
            }
        }
    }

    private static double Scale(int u) => u == 0 ? 1 / Math.Sqrt(2) : 1;

    private static double Cosine(int x, int u) => CosineTable[(x * 8) + u];

    private static readonly double[] CosineTable = BuildCosines();

    private static double[] BuildCosines()
    {
        double[] table = new double[64];
        for (int x = 0; x < 8; x++)
        {
            for (int u = 0; u < 8; u++)
                table[(x * 8) + u] = Math.Cos((2 * x + 1) * u * Math.PI / 16);
        }

        return table;
    }
}
