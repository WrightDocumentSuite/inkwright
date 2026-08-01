using System.Buffers.Binary;

namespace Inkwright.Images.Jbig2;

/// <summary>Reads bits most significant first, which is how every JBIG2 field is packed.</summary>
internal sealed class Jbig2BitReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;
    private int _bit;

    internal Jbig2BitReader(ReadOnlyMemory<byte> data) => _data = data;

    /// <summary>How many whole bytes have been consumed, counting a part-read byte as consumed.</summary>
    internal int BytePosition => _bit == 0 ? _position : _position + 1;

    /// <summary>Whether every bit of the data has been read.</summary>
    internal bool Exhausted => _position >= _data.Length;

    /// <summary>Reads one bit.</summary>
    internal int ReadBit()
    {
        if (_position >= _data.Length)
            return 0;

        int value = (_data.Span[_position] >> (7 - _bit)) & 1;

        if (++_bit == 8)
        {
            _bit = 0;
            _position++;
        }

        return value;
    }

    /// <summary>Reads a number of bits as an unsigned value.</summary>
    /// <param name="count">How many bits to read, up to 32.</param>
    internal uint ReadBits(int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 1) | (uint)ReadBit();

        return value;
    }

    /// <summary>Discards the rest of the current byte, which several fields are followed by.</summary>
    internal void AlignToByte()
    {
        if (_bit == 0)
            return;

        _bit = 0;
        _position++;
    }

    /// <summary>Moves to a byte offset, used after a field whose length was stated separately.</summary>
    /// <param name="offset">The byte to continue from.</param>
    internal void SeekByte(int offset)
    {
        _position = Math.Clamp(offset, 0, _data.Length);
        _bit = 0;
    }

    /// <summary>The remaining data from the current byte onwards.</summary>
    internal ReadOnlyMemory<byte> Remaining => _data[Math.Min(BytePosition, _data.Length)..];
}

/// <summary>One line of a Huffman table: a prefix, then a field of bits holding an offset.</summary>
/// <param name="PrefixLength">How many bits the prefix takes; zero means the line is unused.</param>
/// <param name="RangeLength">How many bits of offset follow the prefix.</param>
/// <param name="RangeLow">The value the offset counts from.</param>
/// <param name="Kind">Whether the line counts upwards, downwards, or signals out of band.</param>
internal readonly record struct Jbig2HuffmanLine(
    int PrefixLength, int RangeLength, int RangeLow, Jbig2HuffmanLineKind Kind)
{
    internal int Code { get; init; }
}

/// <summary>What a table line means once its prefix has been matched.</summary>
internal enum Jbig2HuffmanLineKind
{
    /// <summary>The offset is added to the line's base value.</summary>
    Normal,

    /// <summary>The offset is subtracted, which is how the open-ended low range is coded.</summary>
    Lower,

    /// <summary>The line carries no value at all; it signals the end of a list.</summary>
    OutOfBand,
}

/// <summary>
/// A Huffman table as ITU-T T.88 Annex B defines it. Each line is a prefix code followed by a fixed
/// field of bits, so a table covers wide ranges cheaply: common small values get short prefixes and
/// short fields, and the open-ended ranges at either end get thirty-two-bit fields nobody pays for
/// unless they are used.
/// </summary>
internal sealed class Jbig2HuffmanTable
{
    private readonly Jbig2HuffmanLine[] _lines;

    private Jbig2HuffmanTable(Jbig2HuffmanLine[] lines) => _lines = lines;

    /// <summary>Whether this table can signal the out-of-band value.</summary>
    internal bool HasOutOfBand { get; private init; }

    /// <summary>Reads one value from the stream (§B.4).</summary>
    /// <param name="reader">The bit reader.</param>
    /// <returns>The value, or <see cref="Jbig2IntDecoder.OutOfBand"/>.</returns>
    /// <exception cref="NotSupportedException">The bits match no line in the table.</exception>
    internal int Decode(Jbig2BitReader reader)
    {
        int code = 0;

        for (int length = 1; length <= 32; length++)
        {
            code = (code << 1) | reader.ReadBit();

            foreach (Jbig2HuffmanLine line in _lines)
            {
                if (line.PrefixLength != length || line.Code != code)
                    continue;

                if (line.Kind == Jbig2HuffmanLineKind.OutOfBand)
                    return Jbig2IntDecoder.OutOfBand;

                int offset = (int)reader.ReadBits(line.RangeLength);

                return line.Kind == Jbig2HuffmanLineKind.Lower
                    ? line.RangeLow - offset
                    : line.RangeLow + offset;
            }

            if (reader.Exhausted && length > 24)
                break;
        }

        throw new NotSupportedException("A JBIG2 Huffman code matched no line in its table.");
    }

    /// <summary>
    /// Assigns prefix codes to lines from their lengths (§B.3). This is the canonical Huffman
    /// assignment: codes of each length run consecutively, and each length starts where the
    /// previous one left off, doubled.
    /// </summary>
    internal static Jbig2HuffmanTable Build(IReadOnlyList<Jbig2HuffmanLine> lines)
    {
        int longest = 0;
        foreach (Jbig2HuffmanLine line in lines)
            longest = Math.Max(longest, line.PrefixLength);

        var counts = new int[longest + 2];
        foreach (Jbig2HuffmanLine line in lines)
        {
            if (line.PrefixLength > 0)
                counts[line.PrefixLength]++;
        }

        Jbig2HuffmanLine[] assigned = [.. lines];

        int first = 0;
        for (int length = 1; length <= longest; length++)
        {
            first = (first + counts[length - 1]) << 1;
            int current = first;

            for (int i = 0; i < assigned.Length; i++)
            {
                if (assigned[i].PrefixLength == length)
                    assigned[i] = assigned[i] with { Code = current++ };
            }
        }

        bool outOfBand = false;
        foreach (Jbig2HuffmanLine line in assigned)
            outOfBand |= line.Kind == Jbig2HuffmanLineKind.OutOfBand;

        return new Jbig2HuffmanTable(assigned) { HasOutOfBand = outOfBand };
    }

    /// <summary>
    /// Takes lines whose codes are already known, which is the case for the standard tables because
    /// the standard prints them.
    /// </summary>
    /// <param name="lines">The lines, each carrying its prefix code.</param>
    internal static Jbig2HuffmanTable FromAssigned(IReadOnlyList<Jbig2HuffmanLine> lines)
    {
        bool outOfBand = false;
        foreach (Jbig2HuffmanLine line in lines)
            outOfBand |= line.Kind == Jbig2HuffmanLineKind.OutOfBand;

        return new Jbig2HuffmanTable([.. lines]) { HasOutOfBand = outOfBand };
    }

    /// <summary>
    /// Builds a table from code lengths alone, where line <c>i</c> stands for the value <c>i</c>.
    /// This is how symbol identifiers and run codes are coded.
    /// </summary>
    /// <param name="lengths">The prefix length for each value; zero means the value is unused.</param>
    internal static Jbig2HuffmanTable FromLengths(IReadOnlyList<int> lengths)
    {
        var lines = new Jbig2HuffmanLine[lengths.Count];

        for (int i = 0; i < lengths.Count; i++)
            lines[i] = new Jbig2HuffmanLine(lengths[i], 0, i, Jbig2HuffmanLineKind.Normal);

        return Build(lines);
    }

    /// <summary>
    /// Reads a table that a segment of type 53 carries (§B.2). A file supplies its own table when
    /// none of the fifteen standard ones fits the distribution of its values.
    /// </summary>
    /// <param name="data">The table segment's data part.</param>
    internal static Jbig2HuffmanTable ReadCustom(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;

        if (span.Length < 9)
            throw new NotSupportedException("A JBIG2 code table segment is truncated.");

        byte flags = span[0];
        bool outOfBand = (flags & 1) != 0;
        int prefixBits = ((flags >> 1) & 7) + 1;
        int rangeBits = ((flags >> 4) & 7) + 1;

        int low = BinaryPrimitives.ReadInt32BigEndian(span[1..]);
        int high = BinaryPrimitives.ReadInt32BigEndian(span[5..]);

        var reader = new Jbig2BitReader(data[9..]);
        var lines = new List<Jbig2HuffmanLine>();

        long current = low;
        while (current < high && lines.Count < 4096)
        {
            int prefixLength = (int)reader.ReadBits(prefixBits);
            int rangeLength = (int)reader.ReadBits(rangeBits);

            lines.Add(new Jbig2HuffmanLine(prefixLength, rangeLength, (int)current, Jbig2HuffmanLineKind.Normal));
            current += rangeLength >= 32 ? uint.MaxValue : 1L << rangeLength;
        }

        // The two open-ended lines cover everything below the stated low bound and above the high.
        lines.Add(new Jbig2HuffmanLine((int)reader.ReadBits(prefixBits), 32, low - 1, Jbig2HuffmanLineKind.Lower));
        lines.Add(new Jbig2HuffmanLine((int)reader.ReadBits(prefixBits), 32, high, Jbig2HuffmanLineKind.Normal));

        if (outOfBand)
            lines.Add(new Jbig2HuffmanLine((int)reader.ReadBits(prefixBits), 0, 0, Jbig2HuffmanLineKind.OutOfBand));

        return Build(lines);
    }
}
