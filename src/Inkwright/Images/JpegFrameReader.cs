namespace Inkwright.Images;

/// <summary>
/// Walks a JPEG's markers and gathers the tables and the frame header, stopping at the scan. Only
/// baseline and extended sequential frames are accepted; a progressive or arithmetic-coded one is
/// refused, because decoding it wrongly is worse than not decoding it.
/// </summary>
internal ref struct JpegFrameReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    internal JpegFrameReader(ReadOnlySpan<byte> data) => _data = data;

    /// <summary>Reads every header up to and including the scan header.</summary>
    /// <exception cref="NotSupportedException">The file is a coding this decoder does not read.</exception>
    internal JpegFrame Read()
    {
        var frame = new JpegFrame();
        _position = 0;

        while (_position + 1 < _data.Length)
        {
            if (_data[_position] != 0xFF)
            {
                _position++;
                continue;
            }

            byte marker = _data[_position + 1];
            _position += 2;

            if (marker is 0xD8 or 0x01 or 0xFF || (marker >= 0xD0 && marker <= 0xD7))
                continue;

            if (marker == 0xD9)
                break;

            if (_position + 1 >= _data.Length)
                break;

            int length = (_data[_position] << 8) | _data[_position + 1];
            if (length < 2 || _position + length > _data.Length)
                break;

            ReadOnlySpan<byte> segment = _data.Slice(_position + 2, length - 2);
            _position += length;

            switch (marker)
            {
                case 0xC0 or 0xC1:
                    ReadFrameHeader(frame, segment);
                    break;

                case 0xC2:
                    throw new NotSupportedException("Progressive JPEG images are not decoded.");

                case 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF:
                    throw new NotSupportedException($"JPEG frames of type {marker:X2} are not decoded.");

                case 0xC4:
                    ReadHuffmanTables(frame, segment);
                    break;

                case 0xDB:
                    ReadQuantizationTables(frame, segment);
                    break;

                case 0xDD when segment.Length >= 2:
                    frame.RestartInterval = (segment[0] << 8) | segment[1];
                    break;

                case 0xEE when segment.Length >= 12 && segment[..5].SequenceEqual("Adobe"u8):
                    frame.HasAdobe = true;
                    frame.AdobeTransform = segment[^1];
                    break;

                case 0xDA:
                    ReadScanHeader(frame, segment);
                    frame.ScanData = _data[_position..].ToArray();
                    return frame;

                default:
                    break;
            }
        }

        if (frame.Components.Length == 0)
            throw new NotSupportedException("The JPEG data has no frame header.");

        throw new NotSupportedException("The JPEG data has no scan.");
    }

    private static void ReadFrameHeader(JpegFrame frame, ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 6)
            throw new NotSupportedException("The JPEG frame header is truncated.");

        frame.Height = (segment[1] << 8) | segment[2];
        frame.Width = (segment[3] << 8) | segment[4];

        int count = segment[5];
        if (count is < 1 or > 4 || segment.Length < 6 + (count * 3))
            throw new NotSupportedException($"A JPEG of {count} components is not decoded.");

        var components = new JpegComponent[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = segment.Slice(6 + (i * 3), 3);
            components[i] = new JpegComponent
            {
                Id = entry[0],
                HorizontalSampling = Math.Max(1, entry[1] >> 4),
                VerticalSampling = Math.Max(1, entry[1] & 15),
                QuantizationTable = entry[2] & 3,
            };
        }

        frame.Components = components;
    }

    private static void ReadHuffmanTables(JpegFrame frame, ReadOnlySpan<byte> segment)
    {
        int position = 0;

        while (position + 17 <= segment.Length)
        {
            int specification = segment[position++];
            ReadOnlySpan<byte> counts = segment.Slice(position, 16);
            position += 16;

            int total = 0;
            foreach (byte count in counts)
                total += count;

            if (position + total > segment.Length)
                return;

            var table = new JpegHuffmanTable(counts, segment.Slice(position, total).ToArray());
            position += total;

            int index = specification & 3;
            if ((specification >> 4) == 0)
                frame.DcTables[index] = table;
            else
                frame.AcTables[index] = table;
        }
    }

    private static void ReadQuantizationTables(JpegFrame frame, ReadOnlySpan<byte> segment)
    {
        int position = 0;

        while (position < segment.Length)
        {
            int specification = segment[position++];
            int index = specification & 3;
            bool wide = (specification >> 4) != 0;
            int size = wide ? 128 : 64;

            if (position + size > segment.Length)
                return;

            ushort[] table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                table[i] = wide
                    ? (ushort)((segment[position + (i * 2)] << 8) | segment[position + (i * 2) + 1])
                    : segment[position + i];
            }

            frame.Quantization[index] = table;
            position += size;
        }
    }

    private static void ReadScanHeader(JpegFrame frame, ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 1)
            return;

        int count = segment[0];
        for (int i = 0; i < count && 1 + (i * 2) + 1 < segment.Length; i++)
        {
            int id = segment[1 + (i * 2)];
            int tables = segment[1 + (i * 2) + 1];

            foreach (JpegComponent component in frame.Components)
            {
                if (component.Id != id)
                    continue;

                component.DcTable = tables >> 4;
                component.AcTable = tables & 15;
            }
        }
    }
}
