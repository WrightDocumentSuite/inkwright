using System.Buffers;

namespace Inkwright.Filters;

/// <summary>The <c>ASCIIHexDecode</c> filter of ISO 32000-1 §7.4.2.</summary>
public static class AsciiHexFilter
{
    /// <summary>Decodes hexadecimal text, stopping at the <c>&gt;</c> terminator.</summary>
    /// <param name="data">The encoded bytes.</param>
    public static byte[] Decode(ReadOnlySpan<byte> data)
        => Decode(data, PdfFilterLimits.Cap, strict: false, out _, out _);

    /// <summary>Decodes no more than <paramref name="maxDecodedBytes"/> bytes.</summary>
    internal static byte[] Decode(
        ReadOnlySpan<byte> data,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        Decode(data, maxDecodedBytes, strict: true, out limitExceeded, out invalidData);

    /// <summary>Runs the tolerant decoder under an explicit output bound.</summary>
    internal static byte[] DecodeCompatible(
        ReadOnlySpan<byte> data,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        Decode(data, maxDecodedBytes, strict: false, out limitExceeded, out invalidData);

    private static byte[] Decode(
        ReadOnlySpan<byte> data,
        int maxDecodedBytes,
        bool strict,
        out bool limitExceeded,
        out bool invalidData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        limitExceeded = false;
        invalidData = false;
        int initialCapacity = Math.Min(maxDecodedBytes, Math.Max(1, Math.Min(data.Length / 2, 64 * 1024)));
        var writer = new ArrayBufferWriter<byte>(initialCapacity);
        int high = -1;
        bool terminated = false;

        for (int position = 0; position < data.Length; position++)
        {
            byte b = data[position];
            if (b == (byte)'>')
            {
                terminated = true;
                if (strict && position + 1 != data.Length)
                    invalidData = true;
                break;
            }

            int digit = HexValue(b);
            if (digit < 0)
            {
                if (strict && !IsWhitespace(b))
                    invalidData = true;
                continue;
            }

            if (high < 0)
            {
                high = digit;
            }
            else
            {
                if (writer.WrittenCount >= maxDecodedBytes)
                {
                    limitExceeded = true;
                    break;
                }

                writer.Write([(byte)((high << 4) | digit)]);
                high = -1;
            }
        }

        // An odd final digit is padded with zero, per §7.4.2.
        if (high >= 0)
        {
            if (writer.WrittenCount >= maxDecodedBytes)
                limitExceeded = true;
            else
                writer.Write([(byte)(high << 4)]);
        }

        if (strict && !terminated && !limitExceeded)
            invalidData = true;

        return writer.WrittenSpan.ToArray();
    }

    private static bool IsWhitespace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    /// <summary>Encodes bytes as hexadecimal text with the mandatory terminator.</summary>
    /// <param name="data">The plaintext bytes.</param>
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        byte[] output = new byte[(data.Length * 2) + 1];
        for (int i = 0; i < data.Length; i++)
        {
            output[i * 2] = HexDigit(data[i] >> 4);
            output[(i * 2) + 1] = HexDigit(data[i] & 0x0F);
        }

        output[^1] = (byte)'>';
        return output;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        _ => -1,
    };

    private static byte HexDigit(int value) => (byte)(value < 10 ? '0' + value : 'A' + value - 10);
}

/// <summary>The <c>ASCII85Decode</c> filter of ISO 32000-1 §7.4.3.</summary>
public static class Ascii85Filter
{
    /// <summary>Decodes base-85 text, honouring the <c>z</c> shorthand and the <c>~&gt;</c> terminator.</summary>
    /// <param name="data">The encoded bytes.</param>
    public static byte[] Decode(ReadOnlySpan<byte> data)
        => Decode(data, PdfFilterLimits.Cap, strict: false, out _, out _);

    /// <summary>Decodes no more than <paramref name="maxDecodedBytes"/> bytes.</summary>
    internal static byte[] Decode(
        ReadOnlySpan<byte> data,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        Decode(data, maxDecodedBytes, strict: true, out limitExceeded, out invalidData);

    /// <summary>Runs the tolerant decoder under an explicit output bound.</summary>
    internal static byte[] DecodeCompatible(
        ReadOnlySpan<byte> data,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        Decode(data, maxDecodedBytes, strict: false, out limitExceeded, out invalidData);

    private static byte[] Decode(
        ReadOnlySpan<byte> data,
        int maxDecodedBytes,
        bool strict,
        out bool limitExceeded,
        out bool invalidData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        limitExceeded = false;
        invalidData = false;
        int initialCapacity = Math.Min(maxDecodedBytes, Math.Max(1, Math.Min(data.Length, 64 * 1024)));
        var writer = new ArrayBufferWriter<byte>(initialCapacity);

        if (data.Length >= 2 && data[0] == (byte)'<' && data[1] == (byte)'~')
        {
            if (strict)
                invalidData = true;
            data = data[2..];
        }

        Span<byte> group = stackalloc byte[5];
        int count = 0;
        bool terminated = false;

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];

            if (b == (byte)'~')
            {
                if (i + 1 < data.Length && data[i + 1] == (byte)'>')
                {
                    terminated = true;
                    if (strict && i + 2 != data.Length)
                        invalidData = true;
                }
                else if (strict)
                {
                    invalidData = true;
                }

                break;
            }

            if (b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20)
                continue;

            if (b == (byte)'z' && count == 0)
            {
                if (!TryWrite(writer, "\0\0\0\0"u8, maxDecodedBytes))
                {
                    limitExceeded = true;
                    break;
                }

                continue;
            }

            if (b == (byte)'z')
            {
                if (strict)
                    invalidData = true;
                continue;
            }

            if (b is < (byte)'!' or > (byte)'u')
            {
                if (strict)
                    invalidData = true;
                continue;
            }

            group[count++] = (byte)(b - (byte)'!');
            if (count != 5)
                continue;

            if (!WriteGroup(writer, group, 5, maxDecodedBytes, strict, out bool validGroup))
            {
                limitExceeded = true;
                break;
            }
            if (!validGroup)
                invalidData = true;

            count = 0;
        }

        if (!limitExceeded && count == 1 && strict)
        {
            invalidData = true;
        }
        else if (!limitExceeded && count > 1)
        {
            for (int i = count; i < 5; i++)
                group[i] = 84;

            if (!WriteGroup(writer, group, count, maxDecodedBytes, strict, out bool validGroup))
                limitExceeded = true;
            if (!validGroup)
                invalidData = true;
        }

        if (strict && !terminated && !limitExceeded)
            invalidData = true;

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes bytes as base-85 text with the mandatory terminator.</summary>
    /// <param name="data">The plaintext bytes.</param>
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var writer = new ArrayBufferWriter<byte>(Math.Max(16, data.Length * 5 / 4));
        Span<byte> encoded = stackalloc byte[5];

        int position = 0;
        while (position < data.Length)
        {
            int take = Math.Min(4, data.Length - position);
            uint value = 0;
            for (int i = 0; i < 4; i++)
                value = (value << 8) | (i < take ? data[position + i] : 0u);

            if (value == 0 && take == 4)
            {
                writer.Write("z"u8);
            }
            else
            {
                for (int i = 4; i >= 0; i--)
                {
                    encoded[i] = (byte)('!' + (value % 85));
                    value /= 85;
                }

                writer.Write(encoded[..(take + 1)]);
            }

            position += take;
        }

        writer.Write("~>"u8);
        return writer.WrittenSpan.ToArray();
    }

    private static bool WriteGroup(
        ArrayBufferWriter<byte> writer,
        ReadOnlySpan<byte> group,
        int available,
        int maxDecodedBytes,
        bool strict,
        out bool validGroup)
    {
        ulong value = 0;
        for (int i = 0; i < 5; i++)
            value = (value * 85) + group[i];

        validGroup = !strict || value <= uint.MaxValue;

        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(value >> 24);
        bytes[1] = (byte)(value >> 16);
        bytes[2] = (byte)(value >> 8);
        bytes[3] = (byte)value;
        return TryWrite(writer, bytes[..(available - 1)], maxDecodedBytes);
    }

    private static bool TryWrite(
        ArrayBufferWriter<byte> writer,
        ReadOnlySpan<byte> bytes,
        int maxDecodedBytes)
    {
        int remaining = maxDecodedBytes - writer.WrittenCount;
        if (bytes.Length <= remaining)
        {
            writer.Write(bytes);
            return true;
        }

        if (remaining > 0)
            writer.Write(bytes[..remaining]);

        return false;
    }

}

/// <summary>The <c>RunLengthDecode</c> filter of ISO 32000-1 §7.4.5.</summary>
public static class RunLengthFilter
{
    /// <summary>Decodes a run-length payload.</summary>
    /// <param name="data">The encoded bytes.</param>
    public static byte[] Decode(ReadOnlySpan<byte> data)
        => Decode(data, PdfFilterLimits.Cap, strict: false, out _, out _);

    /// <summary>Decodes no more than <paramref name="maxDecodedBytes"/> bytes.</summary>
    internal static byte[] Decode(
        ReadOnlySpan<byte> data,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        Decode(data, maxDecodedBytes, strict: true, out limitExceeded, out invalidData);

    /// <summary>Runs the tolerant decoder under an explicit output bound.</summary>
    internal static byte[] DecodeCompatible(
        ReadOnlySpan<byte> data,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        Decode(data, maxDecodedBytes, strict: false, out limitExceeded, out invalidData);

    private static byte[] Decode(
        ReadOnlySpan<byte> data,
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
        int position = 0;
        bool terminated = false;

        while (position < data.Length)
        {
            if (writer.WrittenCount >= cap)
            {
                if (data[position] == 128)
                {
                    terminated = true;
                    position++;
                }
                else
                {
                    limitExceeded = true;
                }

                break;
            }

            int length = data[position++];
            if (length == 128)
            {
                terminated = true;
                break;
            }

            if (length < 128)
            {
                int take = Math.Min(length + 1, data.Length - position);
                if (take <= 0)
                {
                    if (strict)
                        invalidData = true;
                    break;
                }

                if (strict && take != length + 1)
                    invalidData = true;

                int remaining = cap - writer.WrittenCount;
                if (take > remaining)
                {
                    writer.Write(data.Slice(position, remaining));
                    limitExceeded = true;
                    break;
                }

                writer.Write(data.Slice(position, take));
                position += take;
            }
            else
            {
                if (position >= data.Length)
                {
                    if (strict)
                        invalidData = true;
                    break;
                }

                byte repeated = data[position++];
                int runLength = 257 - length;
                int remaining = cap - writer.WrittenCount;
                int write = Math.Min(runLength, remaining);
                Span<byte> destination = writer.GetSpan(write)[..write];
                destination.Fill(repeated);
                writer.Advance(destination.Length);
                if (write < runLength)
                {
                    limitExceeded = true;
                    break;
                }
            }
        }

        if (strict)
        {
            if (!terminated && !limitExceeded)
                invalidData = true;
            else if (terminated && position != data.Length)
                invalidData = true;
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes bytes with run-length compression.</summary>
    /// <param name="data">The plaintext bytes.</param>
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var writer = new ArrayBufferWriter<byte>(Math.Max(16, data.Length));
        int position = 0;

        while (position < data.Length)
        {
            int runLength = 1;
            while (runLength < 128 && position + runLength < data.Length && data[position + runLength] == data[position])
                runLength++;

            if (runLength >= 2)
            {
                writer.Write([(byte)(257 - runLength), data[position]]);
                position += runLength;
                continue;
            }

            int literalLength = 1;
            while (literalLength < 128 && position + literalLength < data.Length &&
                   (position + literalLength + 1 >= data.Length ||
                    data[position + literalLength] != data[position + literalLength + 1]))
            {
                literalLength++;
            }

            writer.Write([(byte)(literalLength - 1)]);
            writer.Write(data.Slice(position, literalLength));
            position += literalLength;
        }

        writer.Write([(byte)128]);
        return writer.WrittenSpan.ToArray();
    }
}
