using System.Buffers;

namespace Inkwright.Filters;

/// <summary>
/// The <c>LZWDecode</c> filter of ISO 32000-1 §7.4.4.2, including the <c>EarlyChange</c> quirk that
/// makes PDF's variant differ from TIFF's by one code.
/// </summary>
public static class LzwFilter
{
    private const int ClearCode = 256;
    private const int EndOfData = 257;
    private const int MaxCode = 4096;

    /// <summary>Decodes an LZW payload.</summary>
    /// <param name="data">The compressed bytes.</param>
    /// <param name="earlyChange">Whether code width grows one code early, as most producers do.</param>
    /// <returns>The decoded bytes, truncated where the code stream stopped making sense.</returns>
    public static byte[] Decode(ReadOnlySpan<byte> data, bool earlyChange = true)
        => Decode(data, earlyChange, PdfFilterLimits.Cap, strict: false, out _, out _);

    /// <summary>Decodes no more than <paramref name="maxDecodedBytes"/> bytes.</summary>
    internal static byte[] Decode(
        ReadOnlySpan<byte> data,
        bool earlyChange,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        Decode(data, earlyChange, maxDecodedBytes, strict: true, out limitExceeded, out invalidData);

    private static byte[] Decode(
        ReadOnlySpan<byte> data,
        bool earlyChange,
        int maxDecodedBytes,
        bool strict,
        out bool limitExceeded,
        out bool invalidData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        limitExceeded = false;
        invalidData = false;
        int cap = maxDecodedBytes;
        int initialCapacity = Math.Min(cap, Math.Max(1, Math.Min(data.Length, 64 * 1024)));
        var writer = new ArrayBufferWriter<byte>(initialCapacity);

        // Dictionary entries are (prefix, suffix) pairs, expanded on demand into a scratch buffer.
        int[] prefixes = ArrayPool<int>.Shared.Rent(MaxCode);
        byte[] suffixes = ArrayPool<byte>.Shared.Rent(MaxCode);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(MaxCode);

        try
        {
            int next = EndOfData + 1;
            int codeWidth = 9;
            int previous = -1;
            int bitBuffer = 0;
            int bitCount = 0;
            int early = earlyChange ? 1 : 0;
            bool firstCode = true;

            for (int i = 0; i <= data.Length; i++)
            {
                if (i < data.Length)
                {
                    bitBuffer = (bitBuffer << 8) | data[i];
                    bitCount += 8;
                }
                else if (bitCount < codeWidth)
                {
                    break;
                }

                while (bitCount >= codeWidth)
                {
                    int code = (bitBuffer >> (bitCount - codeWidth)) & ((1 << codeWidth) - 1);
                    bitCount -= codeWidth;

                    if (firstCode)
                    {
                        firstCode = false;
                        if (strict && code != ClearCode)
                        {
                            invalidData = true;
                            return writer.WrittenSpan.ToArray();
                        }
                    }

                    if (code == EndOfData)
                    {
                        // §7.4.4.2 permits only zero padding after EOD in the final encoded byte.
                        // Extra bytes or non-zero pad bits mean the declared stream contains more
                        // encoded data than the LZW member consumes. Public callers still receive
                        // the decoded prefix; the bounded path observes invalidData and fails closed.
                        int paddingMask = bitCount == 0 ? 0 : (1 << bitCount) - 1;
                        if (strict && (i != data.Length - 1 || (bitBuffer & paddingMask) != 0))
                            invalidData = true;
                        return writer.WrittenSpan.ToArray();
                    }

                    if (code == ClearCode)
                    {
                        next = EndOfData + 1;
                        codeWidth = 9;
                        previous = -1;
                        continue;
                    }

                    int length;
                    if (code < next)
                    {
                        length = Expand(code, prefixes, suffixes, scratch);
                    }
                    else if (code == next && previous >= 0 && next < MaxCode)
                    {
                        // The KwKwK case: the entry being defined is emitted before it exists.
                        length = Expand(previous, prefixes, suffixes, scratch);
                        scratch[length] = scratch[0];
                        length++;
                    }
                    else
                    {
                        invalidData = true;
                        return writer.WrittenSpan.ToArray();
                    }

                    if (length > cap - writer.WrittenCount)
                    {
                        limitExceeded = true;
                        return writer.WrittenSpan.ToArray();
                    }

                    writer.Write(scratch.AsSpan(0, length));

                    if (previous >= 0 && next < MaxCode)
                    {
                        prefixes[next] = previous;
                        suffixes[next] = scratch[0];
                        next++;
                    }

                    previous = code;

                    if (next + early >= (1 << codeWidth) && codeWidth < 12)
                        codeWidth++;
                }
            }

            invalidData = true;
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(prefixes);
            ArrayPool<byte>.Shared.Return(suffixes);
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static int Expand(int code, int[] prefixes, byte[] suffixes, byte[] scratch)
    {
        int position = scratch.Length;
        int guard = 0;

        while (code >= ClearCode + 2 && guard++ < MaxCode)
        {
            scratch[--position] = suffixes[code];
            code = prefixes[code];
        }

        scratch[--position] = (byte)code;

        int length = scratch.Length - position;
        scratch.AsSpan(position, length).CopyTo(scratch);
        return length;
    }
}
