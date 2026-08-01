using System.Runtime.Intrinsics;

namespace Inkwright.Filters;

/// <summary>
/// Undoes the predictor functions of ISO 32000-1 §7.4.4.4: TIFF horizontal differencing and the
/// five PNG line filters that cross-reference stream and image payloads lean on.
/// </summary>
public static class Predictor
{
    /// <summary>Reverses a predictor applied to filtered data.</summary>
    /// <param name="data">The data as it came out of the compression filter.</param>
    /// <param name="predictor">The <c>Predictor</c> value: 1 for none, 2 for TIFF, 10 and above for PNG.</param>
    /// <param name="colors">The number of interleaved colour components.</param>
    /// <param name="bitsPerComponent">The bit depth of each component.</param>
    /// <param name="columns">The number of samples per row.</param>
    /// <returns>The unpredicted data, or the input when no predictor applies.</returns>
    public static byte[] Undo(ReadOnlySpan<byte> data, int predictor, int colors, int bitsPerComponent, int columns)
    {
        if (predictor <= 1 || data.IsEmpty)
            return data.ToArray();

        colors = Math.Clamp(colors, 1, 32);
        bitsPerComponent = bitsPerComponent switch { 1 or 2 or 4 or 8 or 16 => bitsPerComponent, _ => 8 };
        columns = Math.Max(1, columns);

        return predictor == 2
            ? UndoTiff(data, colors, bitsPerComponent, columns)
            : UndoPng(data, colors, bitsPerComponent, columns);
    }

    /// <summary>
    /// Applies the PNG "Up" filter, the predictor worth using when writing: it costs one pass and
    /// makes vertically coherent images compress markedly better.
    /// </summary>
    /// <param name="data">The raw sample rows without filter bytes.</param>
    /// <param name="rowLength">The number of bytes in one row.</param>
    /// <returns>Rows prefixed with the PNG filter type byte.</returns>
    public static byte[] ApplyPngUp(ReadOnlySpan<byte> data, int rowLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowLength);

        int rows = data.Length / rowLength;
        byte[] output = new byte[rows * (rowLength + 1)];

        for (int row = 0; row < rows; row++)
        {
            ReadOnlySpan<byte> current = data.Slice(row * rowLength, rowLength);
            ReadOnlySpan<byte> above = row == 0 ? [] : data.Slice((row - 1) * rowLength, rowLength);
            Span<byte> target = output.AsSpan((row * (rowLength + 1)) + 1, rowLength);

            output[row * (rowLength + 1)] = 2;
            for (int i = 0; i < rowLength; i++)
                target[i] = (byte)(current[i] - (above.IsEmpty ? 0 : above[i]));
        }

        return output;
    }

    private static byte[] UndoTiff(ReadOnlySpan<byte> data, int colors, int bitsPerComponent, int columns)
    {
        byte[] output = data.ToArray();
        if (bitsPerComponent != 8)
            return output; // Sub-byte TIFF differencing is rare enough to leave untouched.

        int rowLength = columns * colors;
        for (int offset = 0; offset + rowLength <= output.Length; offset += rowLength)
        {
            Span<byte> row = output.AsSpan(offset, rowLength);
            for (int i = colors; i < rowLength; i++)
                row[i] += row[i - colors];
        }

        return output;
    }

    private static byte[] UndoPng(ReadOnlySpan<byte> data, int colors, int bitsPerComponent, int columns)
    {
        int bytesPerPixel = Math.Max(1, colors * bitsPerComponent / 8);
        int rowLength = ((columns * colors * bitsPerComponent) + 7) / 8;
        int stride = rowLength + 1;
        int rows = data.Length / stride;

        // A truncated final row still carries usable samples.
        int tail = data.Length - (rows * stride);
        int extraRow = tail > 1 ? 1 : 0;

        byte[] output = new byte[(rows + extraRow) * rowLength];
        Span<byte> previous = [];

        for (int row = 0; row < rows + extraRow; row++)
        {
            int sourceOffset = row * stride;
            int available = Math.Min(rowLength, data.Length - sourceOffset - 1);
            if (available <= 0)
                break;

            byte filter = data[sourceOffset];
            ReadOnlySpan<byte> source = data.Slice(sourceOffset + 1, available);
            Span<byte> current = output.AsSpan(row * rowLength, rowLength);
            source.CopyTo(current);

            switch (filter)
            {
                case 0:
                    break;

                case 1:
                    for (int i = bytesPerPixel; i < available; i++)
                        current[i] += current[i - bytesPerPixel];
                    break;

                case 2:
                    if (!previous.IsEmpty)
                        AddRow(current[..available], previous[..available]);
                    break;

                case 3:
                    for (int i = 0; i < available; i++)
                    {
                        int left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                        int up = previous.IsEmpty ? 0 : previous[i];
                        current[i] += (byte)((left + up) / 2);
                    }

                    break;

                case 4:
                    for (int i = 0; i < available; i++)
                    {
                        int left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                        int up = previous.IsEmpty ? 0 : previous[i];
                        int upLeft = previous.IsEmpty || i < bytesPerPixel ? 0 : previous[i - bytesPerPixel];
                        current[i] += (byte)Paeth(left, up, upLeft);
                    }

                    break;

                default:
                    // Unknown filter byte: treat the row as unfiltered rather than dropping it.
                    break;
            }

            previous = current;
        }

        return output;
    }

    private static void AddRow(Span<byte> current, ReadOnlySpan<byte> above)
    {
        int i = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            int limit = current.Length - Vector128<byte>.Count;
            for (; i <= limit; i += Vector128<byte>.Count)
            {
                Vector128<byte> sum = Vector128.LoadUnsafe(ref current[i]) + Vector128.LoadUnsafe(in above[i]);
                sum.StoreUnsafe(ref current[i]);
            }
        }

        for (; i < current.Length; i++)
            current[i] += above[i];
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int distanceLeft = Math.Abs(estimate - left);
        int distanceUp = Math.Abs(estimate - up);
        int distanceUpLeft = Math.Abs(estimate - upLeft);

        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft)
            return left;

        return distanceUp <= distanceUpLeft ? up : upLeft;
    }
}
