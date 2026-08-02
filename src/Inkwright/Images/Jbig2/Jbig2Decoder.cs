using System.Buffers.Binary;

namespace Inkwright.Images.Jbig2;

/// <summary>
/// Decodes the JBIG2 images a PDF may carry. ISO 32000-2 §7.4.7 narrows the format sharply: the
/// embedded organisation with no file header, sequential order, one page, no colour palette. What
/// is left is a page bitmap and a handful of region segments drawn onto it.
/// </summary>
/// <remarks>
/// Generic, text, refinement and halftone regions are decoded, together with their symbol and
/// pattern dictionaries. Unknown optional extension segments are ignored; an unknown necessary
/// extension is rejected because omitting it could change the intended page image.
/// </remarks>
internal static class Jbig2Decoder
{
    /// <summary>The most pixels one region may cover, which stops a hostile header allocating wildly.</summary>
    private const long MaxRegionPixels = 128L * 1024 * 1024;

    /// <summary>How many contexts the widest template addresses.</summary>
    private const int ContextCount = 1 << 16;

    /// <summary>Decodes a JBIG2 image into packed rows of one bit per pixel.</summary>
    /// <param name="data">The image XObject's segment stream.</param>
    /// <param name="globals">The shared global segments, or empty when there are none.</param>
    /// <param name="width">The image width the dictionary declares.</param>
    /// <param name="height">The image height the dictionary declares.</param>
    /// <returns>The rows, a set bit meaning black.</returns>
    /// <exception cref="NotSupportedException">The image uses a region kind this decoder refuses.</exception>
    internal static byte[] Decode(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> globals, int width, int height)
    {
        if ((long)width * height > MaxRegionPixels)
            throw new NotSupportedException($"The JBIG2 image is {width} by {height}, which is too large to decode.");

        List<Jbig2Segment> segments = [.. Jbig2Segments.Read(globals), .. Jbig2Segments.Read(data)];

        var page = new Jbig2Bitmap(width, height);

        // A text region names the dictionaries holding its symbols by segment number, so what each
        // dictionary exported has to stay available until every region that refers to it is done.
        var dictionaries = new Dictionary<uint, Jbig2SymbolDictionaryResult>();
        var patterns = new Dictionary<uint, Jbig2Bitmap[]>();
        var tables = new Dictionary<uint, Jbig2HuffmanTable>();

        // An intermediate region is not drawn: its result waits in a buffer for a later segment to
        // refine, and only the immediate segment at the end of that chain reaches the page (§8.2).
        var buffers = new Dictionary<uint, Jbig2Bitmap>();
        bool drawn = false;
        bool hasPageInformation = false;

        foreach (Jbig2Segment segment in segments)
        {
            switch (segment.Type)
            {
                case Jbig2SegmentType.PageInformation:
                    ApplyPageDefault(page, segment.Data.Span);
                    hasPageInformation = true;
                    break;

                case Jbig2SegmentType.SymbolDictionary:
                    dictionaries[segment.Number] = Jbig2SymbolDictionary.Decode(
                        segment.Data,
                        Gather(dictionaries, segment),
                        GatherTables(tables, segment),
                        LastReferredDictionary(dictionaries, segment)?.RetainedContext);

                    break;

                case Jbig2SegmentType.PatternDictionary:
                    patterns[segment.Number] = Jbig2PatternDictionary.Decode(segment.Data);
                    break;

                case Jbig2SegmentType.ImmediateTextRegion:
                case Jbig2SegmentType.ImmediateLosslessTextRegion:
                case Jbig2SegmentType.IntermediateTextRegion:
                case Jbig2SegmentType.ImmediateHalftoneRegion:
                case Jbig2SegmentType.ImmediateLosslessHalftoneRegion:
                case Jbig2SegmentType.IntermediateHalftoneRegion:
                case Jbig2SegmentType.ImmediateGenericRegion:
                case Jbig2SegmentType.ImmediateLosslessGenericRegion:
                case Jbig2SegmentType.IntermediateGenericRegion:
                case Jbig2SegmentType.ImmediateRefinementRegion:
                case Jbig2SegmentType.ImmediateLosslessRefinementRegion:
                case Jbig2SegmentType.IntermediateRefinementRegion:
                    {
                        RegionInfo info = ReadRegionInfo(segment.Data.Span);

                        Jbig2Bitmap region = segment.Type switch
                        {
                            Jbig2SegmentType.ImmediateTextRegion or
                            Jbig2SegmentType.ImmediateLosslessTextRegion or
                            Jbig2SegmentType.IntermediateTextRegion => DecodeTextRegion(
                                segment, info, Gather(dictionaries, segment), GatherTables(tables, segment)),

                            Jbig2SegmentType.ImmediateHalftoneRegion or
                            Jbig2SegmentType.ImmediateLosslessHalftoneRegion or
                            Jbig2SegmentType.IntermediateHalftoneRegion => Jbig2HalftoneRegion.Decode(
                                segment.Data[17..], GatherPatterns(patterns, segment), info.Width, info.Height),

                            Jbig2SegmentType.ImmediateRefinementRegion or
                            Jbig2SegmentType.ImmediateLosslessRefinementRegion or
                            Jbig2SegmentType.IntermediateRefinementRegion => DecodeRefinementRegion(
                                page, segment, info, buffers),

                            _ => DecodeGenericRegion(segment.Data, info),
                        };

                        if (IsIntermediate(segment.Type))
                        {
                            buffers[segment.Number] = region;
                            break;
                        }

                        page.Combine(region, info.X, info.Y, info.Combination);
                        drawn = true;
                        break;
                    }

                case Jbig2SegmentType.Tables:
                    tables[segment.Number] = Jbig2HuffmanTable.ReadCustom(segment.Data);
                    break;

                case Jbig2SegmentType.EndOfPage:
                case Jbig2SegmentType.EndOfStripe:
                case Jbig2SegmentType.EndOfFile:
                case Jbig2SegmentType.Profiles:
                    break;

                case Jbig2SegmentType.Extension:
                    ValidateExtension(segment.Data.Span);
                    break;

                case Jbig2SegmentType.ColourPalette:
                    throw new NotSupportedException(
                        "The JBIG2 image carries a colour palette segment, which ISO 32000-2 7.4.7 forbids in PDF.");

                default:
                    throw new NotSupportedException(
                        $"JBIG2 {Describe(segment.Type)} are not decoded.");
            }
        }

        // A Page Information segment is itself sufficient to define a page. T.88 §7.4.8 allows
        // it to have no region segments, in which case the result is entirely the default pixel.
        if (!drawn && !hasPageInformation)
            throw new NotSupportedException("The JBIG2 image holds no region to decode.");

        return page.ToPackedRows();
    }

    /// <summary>
    /// Reads the extension header of §7.4.14. Comments and unknown optional extensions cannot alter
    /// rendering and may be skipped. Bit 31 says the extension is necessary to reproduce the page,
    /// so an unrecognised necessary type must fail closed rather than return an incomplete bitmap.
    /// </summary>
    private static void ValidateExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length < sizeof(uint))
            throw new NotSupportedException("A JBIG2 extension header is truncated.");

        uint extensionType = BinaryPrimitives.ReadUInt32BigEndian(data);
        bool knownComment = extensionType is 0x20000000u or 0x20000002u;
        if (!knownComment && (extensionType & 0x80000000u) != 0)
        {
            throw new NotSupportedException(
                $"JBIG2 necessary extension type 0x{extensionType:X8} is not decoded.");
        }
    }

    /// <summary>
    /// Whether a region's result waits in a buffer for a later segment rather than reaching the
    /// page. The three intermediate kinds exist so that a region can be refined before it is shown.
    /// </summary>
    private static bool IsIntermediate(Jbig2SegmentType type) => type is
        Jbig2SegmentType.IntermediateTextRegion or
        Jbig2SegmentType.IntermediateHalftoneRegion or
        Jbig2SegmentType.IntermediateGenericRegion or
        Jbig2SegmentType.IntermediateRefinementRegion;

    /// <summary>
    /// Collects the symbols a segment inherits, in the order it names the dictionaries holding
    /// them. The order matters: symbol identifiers index this list.
    /// </summary>
    private static List<Jbig2Bitmap> Gather(
        Dictionary<uint, Jbig2SymbolDictionaryResult> dictionaries, Jbig2Segment segment)
    {
        var symbols = new List<Jbig2Bitmap>();

        foreach (uint number in segment.Referred)
        {
            if (dictionaries.TryGetValue(number, out Jbig2SymbolDictionaryResult? dictionary))
                symbols.AddRange(dictionary.Exported);
        }

        return symbols;
    }

    /// <summary>
    /// Gets the last symbol dictionary named by this segment. Context reuse is specifically tied
    /// to that dictionary; a retained context from an earlier reference must not be substituted
    /// when the last dictionary did not retain one (ITU-T T.88 §7.4.2.2).
    /// </summary>
    private static Jbig2SymbolDictionaryResult? LastReferredDictionary(
        Dictionary<uint, Jbig2SymbolDictionaryResult> dictionaries,
        Jbig2Segment segment)
    {
        for (int i = segment.Referred.Length - 1; i >= 0; i--)
        {
            if (dictionaries.TryGetValue(segment.Referred[i], out Jbig2SymbolDictionaryResult? dictionary))
                return dictionary;
        }

        return null;
    }

    /// <summary>
    /// Collects the custom Huffman tables a segment refers to, in order. A segment that selects a
    /// user-supplied table takes them from this list one at a time, in the order its flags ask.
    /// </summary>
    private static List<Jbig2HuffmanTable> GatherTables(
        Dictionary<uint, Jbig2HuffmanTable> tables, Jbig2Segment segment)
    {
        var collected = new List<Jbig2HuffmanTable>();

        foreach (uint number in segment.Referred)
        {
            if (tables.TryGetValue(number, out Jbig2HuffmanTable? table))
                collected.Add(table);
        }

        return collected;
    }

    private static List<Jbig2Bitmap> GatherPatterns(
        Dictionary<uint, Jbig2Bitmap[]> patterns, Jbig2Segment segment)
    {
        var collected = new List<Jbig2Bitmap>();

        foreach (uint number in segment.Referred)
        {
            if (patterns.TryGetValue(number, out Jbig2Bitmap[]? defined))
                collected.AddRange(defined);
        }

        return collected;
    }

    /// <summary>
    /// Reads the page information segment far enough to learn the default pixel value (§7.4.8).
    /// A page whose default is black starts filled, and regions are drawn over it.
    /// </summary>
    private static void ApplyPageDefault(Jbig2Bitmap page, ReadOnlySpan<byte> data)
    {
        // Width, height, x resolution and y resolution take sixteen bytes; the flags follow.
        if (data.Length < 19)
            throw new NotSupportedException("The JBIG2 page information segment is truncated.");

        int flags = data[16];
        if (((flags >> 2) & 1) != 0)
            page.Fill(1);
    }

    /// <summary>
    /// The region segment information field every region begins with (§7.4.1): where the region
    /// goes on the page, how big it is, and how it combines with what is there.
    /// </summary>
    /// <param name="Width">The region width.</param>
    /// <param name="Height">The region height.</param>
    /// <param name="X">Where the region's left edge sits on the page.</param>
    /// <param name="Y">Where the region's top edge sits on the page.</param>
    /// <param name="Combination">How the region is drawn onto the page.</param>
    private readonly record struct RegionInfo(
        int Width, int Height, int X, int Y, Jbig2CombinationOperator Combination);

    private static RegionInfo ReadRegionInfo(ReadOnlySpan<byte> span)
    {
        if (span.Length < 17)
            throw new NotSupportedException("A JBIG2 region segment is truncated.");

        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(span);
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        int x = (int)BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
        int y = (int)BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
        byte flags = span[16];

        if (((flags >> 3) & 1) != 0)
            throw new NotSupportedException(
                "The JBIG2 region sets the colour extension flag, which ISO 32000-2 7.4.7 forbids in PDF.");

        if (width <= 0 || height <= 0 || (long)width * height > MaxRegionPixels)
            throw new NotSupportedException($"A JBIG2 region claims to be {width} by {height}.");

        return new RegionInfo(width, height, x, y, (Jbig2CombinationOperator)(flags & 7));
    }

    /// <summary>Reads a text region segment and decodes its bitmap (§7.4.3).</summary>
    private static Jbig2Bitmap DecodeTextRegion(
        Jbig2Segment segment,
        RegionInfo info,
        List<Jbig2Bitmap> symbols,
        List<Jbig2HuffmanTable> custom)
    {
        ReadOnlyMemory<byte> data = segment.Data;
        ReadOnlySpan<byte> span = data.Span;

        if (span.Length < 19)
            throw new NotSupportedException("A JBIG2 text region segment is truncated.");

        int flags = BinaryPrimitives.ReadUInt16BigEndian(span[17..]);
        bool huffman = (flags & 1) != 0;
        bool refine = ((flags >> 1) & 1) != 0;
        int logStrips = (flags >> 2) & 3;
        var corner = (Jbig2ReferenceCorner)((flags >> 4) & 3);
        bool transposed = ((flags >> 6) & 1) != 0;
        var combination = (Jbig2CombinationOperator)((flags >> 7) & 3);
        int defaultPixel = (flags >> 9) & 1;
        int refinementTemplate = (flags >> 15) & 1;

        // The offset is a signed five-bit field, so the top bit of it is the sign.
        int sOffset = (flags >> 10) & 0x1F;
        if (sOffset > 15)
            sOffset -= 32;

        int position = 19;
        int huffmanFlags = 0;

        if (huffman)
        {
            if (position + 2 > span.Length)
                throw new NotSupportedException("A JBIG2 text region segment is truncated.");

            huffmanFlags = BinaryPrimitives.ReadUInt16BigEndian(span[position..]);
            position += 2;
        }

        var refinementAt = new (int X, int Y)[2];

        if (refine && refinementTemplate == 0)
        {
            if (position + 4 > span.Length)
                throw new NotSupportedException("A JBIG2 text region segment is truncated.");

            for (int i = 0; i < 2; i++)
            {
                refinementAt[i] = ((sbyte)span[position], (sbyte)span[position + 1]);
                position += 2;
            }
        }

        if (position + 4 > span.Length)
            throw new NotSupportedException("A JBIG2 text region segment is truncated.");

        int instances = (int)BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
        position += 4;

        var parameters = new Jbig2TextRegionParameters
        {
            Width = info.Width,
            Height = info.Height,
            Instances = instances,
            Strips = 1 << logStrips,
            LogStrips = logStrips,
            Symbols = symbols,
            DefaultPixel = defaultPixel,
            Combination = combination,
            Transposed = transposed,
            ReferenceCorner = corner,
            SOffset = sOffset,
            Refine = refine,
            RefinementTemplate = refinementTemplate,
            RefinementAt = refinementAt,
        };

        Jbig2ValueReader reader = huffman
            ? BuildHuffmanReader(data[position..], huffmanFlags, symbols.Count, custom)
            : new Jbig2ArithmeticReader(
                new MqDecoder(data[position..]),
                new Jbig2IntDecoders(),
                new Jbig2IdDecoder(Jbig2TextRegion.CodeLength(symbols.Count)));

        return Jbig2TextRegion.Decode(
            parameters, reader, MqDecoder.CreateContexts(Jbig2TextRegion.RefinementContextCount));
    }

    /// <summary>
    /// Selects the tables a Huffman text region asked for (§7.4.3.1.2) and reads the symbol
    /// identifier codes that follow the header (§7.4.3.1.7).
    /// </summary>
    private static Jbig2ValueReader BuildHuffmanReader(
        ReadOnlyMemory<byte> body, int flags, int symbolCount, IReadOnlyList<Jbig2HuffmanTable> custom)
    {
        int taken = 0;
        Jbig2HuffmanTable Next() => taken < custom.Count
            ? custom[taken++]
            : throw new NotSupportedException("A JBIG2 text region names a table segment it does not refer to.");

        Jbig2HuffmanTable Choose(int choice, params int[] standard) =>
            choice < standard.Length ? Jbig2StandardTables.Get(standard[choice]) : Next();

        Jbig2HuffmanTable firstS = Choose(flags & 3, 6, 7);
        Jbig2HuffmanTable deltaS = Choose((flags >> 2) & 3, 8, 9, 10);
        Jbig2HuffmanTable deltaT = Choose((flags >> 4) & 3, 11, 12, 13);
        Jbig2HuffmanTable deltaWidth = Choose((flags >> 6) & 3, 14, 15);
        Jbig2HuffmanTable deltaHeight = Choose((flags >> 8) & 3, 14, 15);
        Jbig2HuffmanTable offsetX = Choose((flags >> 10) & 3, 14, 15);
        Jbig2HuffmanTable offsetY = Choose((flags >> 12) & 3, 14, 15);
        Jbig2HuffmanTable size = ((flags >> 14) & 1) == 0 ? Jbig2StandardTables.Get(1) : Next();

        var bits = new Jbig2BitReader(body);

        var tables = new Jbig2HuffmanTables
        {
            DeltaHeight = deltaHeight,
            DeltaWidth = deltaWidth,
            BitmapSize = size,
            AggregateCount = Jbig2StandardTables.Get(1),
            FirstS = firstS,
            DeltaS = deltaS,
            DeltaT = deltaT,
            RefinementDeltaWidth = deltaWidth,
            RefinementDeltaHeight = deltaHeight,
            RefinementX = offsetX,
            RefinementY = offsetY,
            RefinementSize = size,
            SymbolIds = Jbig2SymbolCodes.Read(bits, symbolCount),
        };

        return new Jbig2HuffmanReader(bits, tables);
    }

    /// <summary>
    /// Reads a refinement region segment (§7.4.7). It refines either the buffer of the region it
    /// names, or, when it names none, whatever the page already holds under it (§7.4.7.4).
    /// </summary>
    private static Jbig2Bitmap DecodeRefinementRegion(
        Jbig2Bitmap page, Jbig2Segment segment, RegionInfo info, Dictionary<uint, Jbig2Bitmap> buffers)
    {
        ReadOnlyMemory<byte> data = segment.Data;
        ReadOnlySpan<byte> span = data.Span;

        if (span.Length < 18)
            throw new NotSupportedException("A JBIG2 refinement region segment is truncated.");

        byte flags = span[17];
        int template = flags & 1;
        bool typicalPrediction = ((flags >> 1) & 1) != 0;

        int position = 18;
        var at = new (int X, int Y)[2];

        if (template == 0)
        {
            if (position + 4 > span.Length)
                throw new NotSupportedException("A JBIG2 refinement region segment is truncated.");

            for (int i = 0; i < 2; i++)
            {
                at[i] = ((sbyte)span[position], (sbyte)span[position + 1]);
                position += 2;
            }
        }

        Jbig2Bitmap? reference = null;
        foreach (uint number in segment.Referred)
        {
            if (buffers.TryGetValue(number, out Jbig2Bitmap? buffer))
            {
                reference = buffer;
                break;
            }
        }

        reference ??= page.Crop(info.X, info.Y, info.Width, info.Height);

        return Jbig2RefinementRegion.Decode(
            new MqDecoder(data[position..]),
            MqDecoder.CreateContexts(Jbig2TextRegion.RefinementContextCount),
            info.Width,
            info.Height,
            template,
            reference,
            dx: 0,
            dy: 0,
            at,
            typicalPrediction);
    }

    private static Jbig2Bitmap DecodeGenericRegion(ReadOnlyMemory<byte> data, RegionInfo info)
    {
        ReadOnlySpan<byte> span = data.Span;

        if (span.Length < 18)
            throw new NotSupportedException("A JBIG2 generic region segment is truncated.");

        (int width, int height, int _, int _, Jbig2CombinationOperator _) = info;

        byte flags = span[17];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool typicalPrediction = ((flags >> 3) & 1) != 0;
        bool extendedTemplate = ((flags >> 4) & 1) != 0;

        if (extendedTemplate)
            throw new NotSupportedException("JBIG2 extended templates are not decoded.");

        int position = 18;
        Span<(int X, int Y)> at = stackalloc (int, int)[4];

        if (!mmr)
        {
            int pairs = template == 0 ? 4 : 1;
            if (position + (pairs * 2) > span.Length)
                throw new NotSupportedException("A JBIG2 generic region segment is truncated.");

            for (int i = 0; i < pairs; i++)
            {
                at[i] = ((sbyte)span[position], (sbyte)span[position + 1]);
                position += 2;
            }
        }

        ReadOnlyMemory<byte> body = data[position..];

        return mmr
            ? Jbig2GenericRegion.DecodeMmr(body.Span, width, height)
            : Jbig2GenericRegion.Decode(
                new MqDecoder(body),
                MqDecoder.CreateContexts(ContextCount),
                width,
                height,
                template,
                at[..(template == 0 ? 4 : 1)],
                typicalPrediction);
    }

    private static string Describe(Jbig2SegmentType type) => type switch
    {
        Jbig2SegmentType.IntermediateRefinementRegion or
        Jbig2SegmentType.ImmediateRefinementRegion or
        Jbig2SegmentType.ImmediateLosslessRefinementRegion => "standalone refinement regions",
        _ => $"segments of type {(int)type}",
    };
}
