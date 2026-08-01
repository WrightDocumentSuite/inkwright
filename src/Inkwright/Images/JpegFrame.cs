namespace Inkwright.Images;

/// <summary>One colour plane of a JPEG frame.</summary>
internal sealed class JpegComponent
{
    /// <summary>The identifier the scan header refers to the component by.</summary>
    internal int Id { get; init; }

    /// <summary>How many data units the component contributes across a minimum coded unit.</summary>
    internal int HorizontalSampling { get; init; } = 1;

    /// <summary>How many data units the component contributes down a minimum coded unit.</summary>
    internal int VerticalSampling { get; init; } = 1;

    /// <summary>Which quantization table dequantizes this component.</summary>
    internal int QuantizationTable { get; init; }

    /// <summary>Which table decodes the difference of each block's first coefficient.</summary>
    internal int DcTable { get; set; }

    /// <summary>Which table decodes the rest of each block's coefficients.</summary>
    internal int AcTable { get; set; }

    /// <summary>The decoded samples of this plane, at its own resolution.</summary>
    internal byte[] Samples { get; set; } = [];

    /// <summary>The width of this plane in samples, which chroma subsampling makes smaller.</summary>
    internal int Width { get; set; }

    /// <summary>The height of this plane in samples.</summary>
    internal int Height { get; set; }
}

/// <summary>Everything the headers of a JPEG say, gathered before any entropy-coded data is read.</summary>
internal sealed class JpegFrame
{
    /// <summary>The image width in pixels.</summary>
    internal int Width { get; set; }

    /// <summary>The image height in pixels.</summary>
    internal int Height { get; set; }

    /// <summary>The colour planes, in the order the frame header lists them.</summary>
    internal JpegComponent[] Components { get; set; } = [];

    /// <summary>The quantization tables, by index.</summary>
    internal ushort[]?[] Quantization { get; } = new ushort[]?[4];

    /// <summary>The Huffman tables for first coefficients, by index.</summary>
    internal JpegHuffmanTable?[] DcTables { get; } = new JpegHuffmanTable?[4];

    /// <summary>The Huffman tables for the remaining coefficients, by index.</summary>
    internal JpegHuffmanTable?[] AcTables { get; } = new JpegHuffmanTable?[4];

    /// <summary>How many minimum coded units pass between restart markers, or zero for none.</summary>
    internal int RestartInterval { get; set; }

    /// <summary>Whether the file carries Adobe's marker, which changes how four planes are read.</summary>
    internal bool HasAdobe { get; set; }

    /// <summary>What that marker says the colour transform is.</summary>
    internal int AdobeTransform { get; set; } = -1;

    /// <summary>The entropy-coded data of the scan.</summary>
    internal ReadOnlyMemory<byte> ScanData { get; set; }

    /// <summary>The widest sampling factor of any component, which sets the size of a coded unit.</summary>
    internal int MaxHorizontal => Components.Length == 0 ? 1 : Components.Max(c => c.HorizontalSampling);

    /// <summary>The tallest sampling factor of any component.</summary>
    internal int MaxVertical => Components.Length == 0 ? 1 : Components.Max(c => c.VerticalSampling);
}

/// <summary>
/// A Huffman table as JPEG stores it: how many codes there are of each length, and what each code
/// means. Decoding walks the code lengths from shortest to longest, which needs no tree.
/// </summary>
internal sealed class JpegHuffmanTable
{
    private readonly int[] _minimum = new int[17];
    private readonly int[] _maximum = new int[17];
    private readonly int[] _start = new int[17];
    private readonly byte[] _values;

    internal JpegHuffmanTable(ReadOnlySpan<byte> counts, byte[] values)
    {
        _values = values;

        int code = 0;
        int index = 0;

        for (int length = 1; length <= 16; length++)
        {
            _start[length] = index;
            _minimum[length] = code;

            code += counts[length - 1];
            index += counts[length - 1];

            _maximum[length] = counts[length - 1] == 0 ? -1 : code - 1;
            code <<= 1;
        }
    }

    /// <summary>Reads one value, taking bits until the code so far is one this table knows.</summary>
    /// <param name="bits">The bit reader positioned at the start of a code.</param>
    /// <returns>The value the code stands for, or -1 when no code of any length matches.</returns>
    internal int Read(ref JpegBitReader bits)
    {
        int code = 0;

        for (int length = 1; length <= 16; length++)
        {
            code = (code << 1) | bits.ReadBit();

            if (_maximum[length] >= 0 && code <= _maximum[length] && code >= _minimum[length])
            {
                int index = _start[length] + code - _minimum[length];
                return index < _values.Length ? _values[index] : -1;
            }
        }

        return -1;
    }
}

/// <summary>
/// Reads the entropy-coded data a bit at a time. The byte value <c>0xFF</c> is followed by a zero
/// wherever it occurs in the data itself, so that a marker can always be told from a coincidence.
/// </summary>
internal ref struct JpegBitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;
    private int _bit;

    /// <summary>Where in the data the reader has got to.</summary>
    internal readonly int Position => _position;

    /// <summary>Reads one bit, reporting zero once the data runs out.</summary>
    internal int ReadBit()
    {
        if (_position >= _data.Length)
            return 0;

        byte current = _data[_position];
        int value = (current >> (7 - _bit)) & 1;

        if (++_bit == 8)
        {
            _bit = 0;
            _position++;

            // Skip the zero that was stuffed in after a 0xFF byte of real data.
            if (current == 0xFF && _position < _data.Length && _data[_position] == 0x00)
                _position++;
        }

        return value;
    }

    /// <summary>Reads several bits as one number, most significant first.</summary>
    internal int Read(int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 1) | ReadBit();

        return value;
    }

    /// <summary>
    /// Turns a Huffman-coded magnitude into the signed number it stands for. The top half of the
    /// range is positive and the bottom half negative, which is how JPEG avoids a sign bit.
    /// </summary>
    internal int ReadSigned(int count)
    {
        if (count == 0)
            return 0;

        int value = Read(count);
        return value < 1 << (count - 1) ? value - (1 << count) + 1 : value;
    }

    /// <summary>Moves to the next byte boundary and past any restart marker sitting there.</summary>
    internal void Restart()
    {
        if (_bit != 0)
        {
            _bit = 0;
            _position++;
        }

        while (_position + 1 < _data.Length && _data[_position] == 0xFF &&
               _data[_position + 1] >= 0xD0 && _data[_position + 1] <= 0xD7)
        {
            _position += 2;
        }
    }
}
