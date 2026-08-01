using System.Buffers;

namespace Inkwright.Filters;

/// <summary>The <c>ASCIIHexDecode</c> filter of ISO 32000-1 §7.4.2.</summary>
public static class AsciiHexFilter
{
    /// <summary>Decodes hexadecimal text, stopping at the <c>&gt;</c> terminator.</summary>
    /// <param name="data">The encoded bytes.</param>
    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        var writer = new ArrayBufferWriter<byte>(Math.Max(16, data.Length / 2));
        int high = -1;

        foreach (byte b in data)
        {
            if (b == (byte)'>')
                break;

            int digit = HexValue(b);
            if (digit < 0)
                continue;

            if (high < 0)
            {
                high = digit;
            }
            else
            {
                writer.Write([(byte)((high << 4) | digit)]);
                high = -1;
            }
        }

        // An odd final digit is padded with zero, per §7.4.2.
        if (high >= 0)
            writer.Write([(byte)(high << 4)]);

        return writer.WrittenSpan.ToArray();
    }

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
    {
        var writer = new ArrayBufferWriter<byte>(Math.Max(16, data.Length * 4 / 5));

        if (data.Length >= 2 && data[0] == (byte)'<' && data[1] == (byte)'~')
            data = data[2..];

        Span<byte> group = stackalloc byte[5];
        int count = 0;

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];

            if (b == (byte)'~')
                break;

            if (b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20)
                continue;

            if (b == (byte)'z' && count == 0)
            {
                writer.Write("\0\0\0\0"u8);
                continue;
            }

            if (b is < (byte)'!' or > (byte)'u')
                continue;

            group[count++] = (byte)(b - (byte)'!');
            if (count != 5)
                continue;

            WriteGroup(writer, group, 5);
            count = 0;
        }

        if (count > 1)
        {
            for (int i = count; i < 5; i++)
                group[i] = 84;

            WriteGroup(writer, group, count);
        }

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

    private static void WriteGroup(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> group, int available)
    {
        uint value = 0;
        for (int i = 0; i < 5; i++)
            value = (value * 85) + group[i];

        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(value >> 24);
        bytes[1] = (byte)(value >> 16);
        bytes[2] = (byte)(value >> 8);
        bytes[3] = (byte)value;
        writer.Write(bytes[..(available - 1)]);
    }
}

/// <summary>The <c>RunLengthDecode</c> filter of ISO 32000-1 §7.4.5.</summary>
public static class RunLengthFilter
{
    /// <summary>Decodes a run-length payload.</summary>
    /// <param name="data">The encoded bytes.</param>
    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        int cap = PdfFilterLimits.Cap;
        var writer = new ArrayBufferWriter<byte>(Math.Min(cap, Math.Max(16, data.Length * 2)));
        int position = 0;

        while (position < data.Length && writer.WrittenCount < cap)
        {
            int length = data[position++];
            if (length == 128)
                break;

            if (length < 128)
            {
                int take = Math.Min(length + 1, data.Length - position);
                if (take <= 0)
                    break;

                writer.Write(data.Slice(position, take));
                position += take;
            }
            else
            {
                if (position >= data.Length)
                    break;

                byte repeated = data[position++];
                Span<byte> destination = writer.GetSpan(257 - length)[..(257 - length)];
                destination.Fill(repeated);
                writer.Advance(destination.Length);
            }
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
