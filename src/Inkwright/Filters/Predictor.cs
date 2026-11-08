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
        => UndoCore(
            data,
            predictor,
            colors,
            bitsPerComponent,
            columns,
            PdfFilterLimits.Cap,
            strict: false,
            out _,
            out _);

    /// <summary>Reverses a predictor without allocating more than the supplied output bound.</summary>
    internal static byte[] Undo(
        ReadOnlySpan<byte> data,
        int predictor,
        int colors,
        int bitsPerComponent,
        int columns,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        UndoCore(
            data,
            predictor,
            colors,
            bitsPerComponent,
            columns,
            maxDecodedBytes,
            strict: true,
            out limitExceeded,
            out invalidData);

    /// <summary>
    /// Runs the forgiving public predictor semantics under an explicit output bound. Ordinary PDF
    /// reads use this path so adding a resource ledger does not turn damaged final rows or unknown
    /// row tags into protected-reader syntax failures.
    /// </summary>
    internal static byte[] UndoCompatible(
        ReadOnlySpan<byte> data,
        int predictor,
        int colors,
        int bitsPerComponent,
        int columns,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        UndoCore(
            data,
            predictor,
            colors,
            bitsPerComponent,
            columns,
            maxDecodedBytes,
            strict: false,
            out limitExceeded,
            out invalidData);

    private static byte[] UndoCore(
        ReadOnlySpan<byte> data,
        int predictor,
        int colors,
        int bitsPerComponent,
        int columns,
        int maxDecodedBytes,
        bool strict,
        out bool limitExceeded,
        out bool invalidData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        limitExceeded = false;
        invalidData = false;

        if (predictor <= 1 || data.IsEmpty)
        {
            if (data.Length > maxDecodedBytes)
            {
                limitExceeded = true;
                return data[..maxDecodedBytes].ToArray();
            }

            return data.ToArray();
        }

        colors = Math.Max(1, colors);
        bitsPerComponent = bitsPerComponent switch { 1 or 2 or 4 or 8 or 16 => bitsPerComponent, _ => 8 };
        columns = Math.Max(1, columns);

        if (predictor == 2)
        {
            if (data.Length > maxDecodedBytes)
            {
                limitExceeded = true;
                return data[..maxDecodedBytes].ToArray();
            }

            long samplesPerRow = (long)columns * colors;
            if (samplesPerRow > (maxDecodedBytes * 8L) / bitsPerComponent)
            {
                limitExceeded = true;
                return [];
            }

            if (samplesPerRow > int.MaxValue)
            {
                limitExceeded = true;
                return [];
            }

            int tiffRowLength = (int)(((samplesPerRow * bitsPerComponent) + 7) / 8);
            if (strict && data.Length % tiffRowLength != 0)
            {
                invalidData = true;
                return [];
            }

            return UndoTiff(
                data,
                colors,
                bitsPerComponent,
                (int)samplesPerRow,
                tiffRowLength);
        }

        long samples = (long)columns * colors;
        if (samples > (maxDecodedBytes * 8L) / bitsPerComponent)
        {
            limitExceeded = true;
            return [];
        }

        long rowLengthValue = ((samples * bitsPerComponent) + 7) / 8;
        long stride = rowLengthValue + 1;
        long rows = data.Length / stride;
        long tail = data.Length - (rows * stride);
        long outputLength = (rows + (tail > 1 ? 1 : 0)) * rowLengthValue;

        if (rowLengthValue >= int.MaxValue || outputLength > maxDecodedBytes || outputLength > int.MaxValue)
        {
            limitExceeded = true;
            return [];
        }

        int rowLength = (int)rowLengthValue;
        int bytesPerPixel = Math.Max(1, (int)((((long)colors * bitsPerComponent) + 7) / 8));
        int pngStride = rowLength + 1;
        if (strict && data.Length % pngStride != 0)
        {
            invalidData = true;
            return [];
        }

        return UndoPng(data, rowLength, bytesPerPixel, strict, out invalidData);
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

    private static byte[] UndoTiff(
        ReadOnlySpan<byte> data,
        int colors,
        int bitsPerComponent,
        int samplesPerRow,
        int rowLength)
    {
        byte[] output = data.ToArray();
        for (int offset = 0; rowLength > 0 && offset <= output.Length - rowLength; offset += rowLength)
        {
            Span<byte> row = output.AsSpan(offset, rowLength);
            switch (bitsPerComponent)
            {
                case 8:
                    for (int sample = colors; sample < samplesPerRow; sample++)
                        row[sample] += row[sample - colors];
                    break;

                case 16:
                    for (int sample = colors; sample < samplesPerRow; sample++)
                    {
                        int position = sample * 2;
                        int leftPosition = (sample - colors) * 2;
                        int current = (row[position] << 8) | row[position + 1];
                        int left = (row[leftPosition] << 8) | row[leftPosition + 1];
                        int restored = (current + left) & 0xFFFF;
                        row[position] = (byte)(restored >> 8);
                        row[position + 1] = (byte)restored;
                    }

                    break;

                default:
                    int mask = (1 << bitsPerComponent) - 1;
                    for (int sample = colors; sample < samplesPerRow; sample++)
                    {
                        int current = PackedSample(row, sample, bitsPerComponent);
                        int left = PackedSample(row, sample - colors, bitsPerComponent);
                        SetPackedSample(
                            row,
                            sample,
                            bitsPerComponent,
                            (current + left) & mask);
                    }

                    break;
            }
        }

        return output;
    }

    private static int PackedSample(ReadOnlySpan<byte> row, int sample, int bitsPerComponent)
    {
        long bit = (long)sample * bitsPerComponent;
        int shift = 8 - bitsPerComponent - ((int)bit & 7);
        return (row[(int)(bit >> 3)] >> shift) & ((1 << bitsPerComponent) - 1);
    }

    private static void SetPackedSample(
        Span<byte> row,
        int sample,
        int bitsPerComponent,
        int value)
    {
        long bit = (long)sample * bitsPerComponent;
        int index = (int)(bit >> 3);
        int shift = 8 - bitsPerComponent - ((int)bit & 7);
        int mask = ((1 << bitsPerComponent) - 1) << shift;
        row[index] = (byte)((row[index] & ~mask) | ((value << shift) & mask));
    }

    private static byte[] UndoPng(
        ReadOnlySpan<byte> data,
        int rowLength,
        int bytesPerPixel,
        bool strict,
        out bool invalidData)
    {
        invalidData = false;
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
                    if (strict)
                    {
                        invalidData = true;
                        return output;
                    }

                    // Tolerant public decoding treats an unknown tag as an unfiltered row.
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
