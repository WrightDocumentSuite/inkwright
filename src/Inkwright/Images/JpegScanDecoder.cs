namespace Inkwright.Images;

/// <summary>
/// Decodes the entropy-coded scan of a sequential JPEG: Huffman codes into coefficients, blocks
/// into samples, and the subsampled planes back up to full resolution.
/// </summary>
internal static class JpegScanDecoder
{
    /// <summary>Decodes a frame's scan into interleaved samples at full resolution.</summary>
    /// <param name="frame">The frame, with its tables and scan data already read.</param>
    internal static byte[] Decode(JpegFrame frame)
    {
        DecodePlanes(frame);
        return Interleave(frame);
    }

    /// <summary>
    /// Walks the minimum coded units, decoding every block of every component. A coded unit holds
    /// as many blocks of each component as its sampling factors say, which is how chroma is stored
    /// at a lower resolution than luminance.
    /// </summary>
    private static void DecodePlanes(JpegFrame frame)
    {
        int maxH = frame.MaxHorizontal;
        int maxV = frame.MaxVertical;

        int unitsAcross = (frame.Width + (8 * maxH) - 1) / (8 * maxH);
        int unitsDown = (frame.Height + (8 * maxV) - 1) / (8 * maxV);

        foreach (JpegComponent component in frame.Components)
        {
            component.Width = unitsAcross * component.HorizontalSampling * 8;
            component.Height = unitsDown * component.VerticalSampling * 8;
            component.Samples = new byte[component.Width * component.Height];
        }

        var bits = new JpegBitReader(frame.ScanData.Span);
        int[] previous = new int[frame.Components.Length];

        Span<short> coefficients = stackalloc short[64];
        Span<double> block = stackalloc double[64];
        Span<byte> pixels = stackalloc byte[64];

        int sinceRestart = 0;

        for (int unitY = 0; unitY < unitsDown; unitY++)
        {
            for (int unitX = 0; unitX < unitsAcross; unitX++)
            {
                if (frame.RestartInterval > 0 && sinceRestart == frame.RestartInterval)
                {
                    bits.Restart();
                    Array.Clear(previous);
                    sinceRestart = 0;
                }

                sinceRestart++;

                for (int c = 0; c < frame.Components.Length; c++)
                {
                    JpegComponent component = frame.Components[c];

                    for (int by = 0; by < component.VerticalSampling; by++)
                    {
                        for (int bx = 0; bx < component.HorizontalSampling; bx++)
                        {
                            DecodeBlock(frame, component, ref bits, coefficients, ref previous[c]);

                            JpegDecoder.Unzig(coefficients, block, Quantization(frame, component));
                            JpegDecoder.InverseTransform(block, pixels);

                            Place(
                                component,
                                pixels,
                                ((unitX * component.HorizontalSampling) + bx) * 8,
                                ((unitY * component.VerticalSampling) + by) * 8);
                        }
                    }
                }
            }
        }
    }

    private static ReadOnlySpan<ushort> Quantization(JpegFrame frame, JpegComponent component) =>
        frame.Quantization[component.QuantizationTable] ?? DefaultQuantization;

    private static readonly ushort[] DefaultQuantization = CreateDefaultQuantization();

    private static ushort[] CreateDefaultQuantization()
    {
        ushort[] table = new ushort[64];
        Array.Fill(table, (ushort)1);
        return table;
    }

    /// <summary>
    /// Decodes one eight-by-eight block. The first coefficient is stored as a difference from the
    /// previous block of the same component, and the rest as runs of zeros with a value on the end.
    /// </summary>
    private static void DecodeBlock(
        JpegFrame frame,
        JpegComponent component,
        ref JpegBitReader bits,
        scoped Span<short> coefficients,
        ref int previous)
    {
        coefficients.Clear();

        JpegHuffmanTable? dc = frame.DcTables[component.DcTable & 3];
        JpegHuffmanTable? ac = frame.AcTables[component.AcTable & 3];

        if (dc is null || ac is null)
            return;

        int magnitude = dc.Read(ref bits);
        if (magnitude < 0)
            return;

        previous += bits.ReadSigned(magnitude);
        coefficients[0] = (short)Math.Clamp(previous, short.MinValue, short.MaxValue);

        for (int index = 1; index < 64;)
        {
            int symbol = ac.Read(ref bits);
            if (symbol < 0)
                return;

            int zeros = symbol >> 4;
            int size = symbol & 15;

            if (size == 0)
            {
                // Sixteen zeros with no value is a run; anything else ends the block.
                if (zeros != 15)
                    return;

                index += 16;
                continue;
            }

            index += zeros;
            if (index >= 64)
                return;

            coefficients[index] = (short)Math.Clamp(bits.ReadSigned(size), short.MinValue, short.MaxValue);
            index++;
        }
    }

    private static void Place(JpegComponent component, ReadOnlySpan<byte> pixels, int left, int top)
    {
        for (int y = 0; y < 8; y++)
        {
            int row = top + y;
            if (row >= component.Height)
                return;

            int offset = (row * component.Width) + left;
            int width = Math.Min(8, component.Width - left);

            if (width > 0)
                pixels.Slice(y * 8, width).CopyTo(component.Samples.AsSpan(offset, width));
        }
    }

    /// <summary>
    /// Brings the planes together at full resolution. A subsampled plane is stretched by repeating
    /// its samples, which is what a decoder does when it is not smoothing the result.
    /// </summary>
    private static byte[] Interleave(JpegFrame frame)
    {
        int width = frame.Width;
        int height = frame.Height;
        int count = frame.Components.Length;

        byte[] samples = new byte[width * height * count];
        int maxH = frame.MaxHorizontal;
        int maxV = frame.MaxVertical;

        for (int c = 0; c < count; c++)
        {
            JpegComponent component = frame.Components[c];
            int scaleX = maxH / component.HorizontalSampling;
            int scaleY = maxV / component.VerticalSampling;

            for (int y = 0; y < height; y++)
            {
                int sourceY = Math.Min(y / scaleY, component.Height - 1);

                for (int x = 0; x < width; x++)
                {
                    int sourceX = Math.Min(x / scaleX, component.Width - 1);
                    samples[(((y * width) + x) * count) + c] =
                        component.Samples[(sourceY * component.Width) + sourceX];
                }
            }
        }

        return samples;
    }
}
