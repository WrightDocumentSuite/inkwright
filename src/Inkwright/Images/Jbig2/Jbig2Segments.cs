using System.Buffers.Binary;

namespace Inkwright.Images.Jbig2;

/// <summary>The segment kinds of ITU-T T.88 Table 34, as far as this decoder needs to tell them apart.</summary>
internal enum Jbig2SegmentType
{
    SymbolDictionary = 0,
    IntermediateTextRegion = 4,
    ImmediateTextRegion = 6,
    ImmediateLosslessTextRegion = 7,
    PatternDictionary = 16,
    IntermediateHalftoneRegion = 20,
    ImmediateHalftoneRegion = 22,
    ImmediateLosslessHalftoneRegion = 23,
    IntermediateGenericRegion = 36,
    ImmediateGenericRegion = 38,
    ImmediateLosslessGenericRegion = 39,
    IntermediateRefinementRegion = 40,
    ImmediateRefinementRegion = 42,
    ImmediateLosslessRefinementRegion = 43,
    PageInformation = 48,
    EndOfPage = 49,
    EndOfStripe = 50,
    EndOfFile = 51,
    Profiles = 52,
    Tables = 53,
    ColourPalette = 54,
    Extension = 62,
}

/// <summary>One segment: its header fields and where its data lies in the stream.</summary>
/// <param name="Number">The segment number.</param>
/// <param name="Type">What kind of segment it is.</param>
/// <param name="Page">Which page it belongs to; zero means it belongs to none and is global.</param>
/// <param name="Referred">
/// The numbers of the segments this one builds on. A text region names the symbol dictionaries its
/// symbols come from this way, and a dictionary names the dictionaries it imports from.
/// </param>
/// <param name="Data">The segment's data part.</param>
internal readonly record struct Jbig2Segment(
    uint Number, Jbig2SegmentType Type, uint Page, uint[] Referred, ReadOnlyMemory<byte> Data);

/// <summary>
/// Reads the segment headers of ITU-T T.88 §7.2. In a PDF the stream is always in the sequential
/// organisation with no file header, so parsing starts straight at the first header and each
/// segment's data follows its own header (ISO 32000-2 §7.4.7).
/// </summary>
internal static class Jbig2Segments
{
    /// <summary>How many segments one stream may hold before it is taken to be malformed.</summary>
    private const int MaxSegments = 100_000;

    /// <summary>The identifier a standalone JBIG2 file opens with (Annex D.4.1).</summary>
    private static ReadOnlySpan<byte> FileIdentifier => [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Reads every segment of a stream.</summary>
    /// <param name="data">The segment stream.</param>
    internal static List<Jbig2Segment> Read(ReadOnlyMemory<byte> data)
    {
        List<Jbig2Segment> segments = [];
        int position = 0;

        // ISO 32000-2 7.4.7 says an embedded stream carries no file header, but some producers
        // write one anyway. Stepping over it costs nothing and rescues the file.
        if (data.Length > 9 && data.Span[..8].SequenceEqual(FileIdentifier))
        {
            // The header is the identifier, a flags byte, and a page count unless the flags say
            // the count was unknown when the file was written.
            position = (data.Span[8] & 2) != 0 ? 9 : 13;
        }

        while (position < data.Length && segments.Count < MaxSegments)
        {
            if (!TryReadHeader(data.Span, ref position, out Jbig2Segment header, out long length))
                break;

            if (position > data.Length)
                break;

            // A generic region may declare an unknown length, which means its data runs to a
            // terminating sequence rather than to a stated count (§7.2.7).
            int available = data.Length - position;
            int take = length == 0xFFFFFFFF ? available : (int)Math.Min(length, available);

            segments.Add(header with { Data = data.Slice(position, take) });
            position += take;
        }

        return segments;
    }

    private static bool TryReadHeader(
        ReadOnlySpan<byte> data, ref int position, out Jbig2Segment segment, out long length)
    {
        segment = default;
        length = 0;

        if (position + 11 > data.Length)
            return false;

        uint number = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        position += 4;

        byte flags = data[position++];
        var type = (Jbig2SegmentType)(flags & 0x3F);
        bool longPage = (flags & 0x40) != 0;

        if (!TryReadReferences(data, ref position, number, out uint[] referred))
            return false;

        if (position + (longPage ? 4 : 1) + 4 > data.Length)
            return false;

        uint page = longPage
            ? BinaryPrimitives.ReadUInt32BigEndian(data[position..])
            : data[position];

        position += longPage ? 4 : 1;

        length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        position += 4;

        segment = new Jbig2Segment(number, type, page, referred, ReadOnlyMemory<byte>.Empty);
        return true;
    }

    /// <summary>
    /// Reads the referred-to segment count, steps over the retention flags, and reads the numbers
    /// themselves (§7.2.4 and §7.2.5). The numbers matter: a text region finds the dictionaries
    /// holding its symbols through them, in the order they are listed here.
    /// </summary>
    private static bool TryReadReferences(
        ReadOnlySpan<byte> data, ref int position, uint number, out uint[] referred)
    {
        referred = [];

        if (position >= data.Length)
            return false;

        int count = data[position] >> 5;

        if (count == 7)
        {
            if (position + 4 > data.Length)
                return false;

            count = (int)(BinaryPrimitives.ReadUInt32BigEndian(data[position..]) & 0x1FFFFFFF);
            position += 4;

            // One retention bit per referred-to segment, plus one for the segment itself.
            position += (count + 8) / 8;
        }
        else
            position++;

        // A segment may only refer to lower-numbered segments, so its own number bounds how wide
        // each reference has to be.
        int width = number <= 256 ? 1 : number <= 65536 ? 2 : 4;

        if (count < 0 || position + ((long)count * width) > data.Length)
            return false;

        referred = new uint[count];
        for (int i = 0; i < count; i++)
        {
            referred[i] = width switch
            {
                1 => data[position],
                2 => BinaryPrimitives.ReadUInt16BigEndian(data[position..]),
                _ => BinaryPrimitives.ReadUInt32BigEndian(data[position..]),
            };

            position += width;
        }

        return position <= data.Length;
    }
}
