using System.Buffers.Binary;

namespace Inkwright.Images.Jbig2;

/// <summary>
/// The set of integer decoding procedures a symbol dictionary or text region needs. Each kind of
/// number keeps its own probability estimates, and they all draw decisions from one arithmetic
/// decoder, so they have to travel together (ITU-T T.88 Annex A).
/// </summary>
internal sealed class Jbig2IntDecoders
{
    internal Jbig2IntDecoder DeltaHeight { get; } = new();

    internal Jbig2IntDecoder DeltaWidth { get; } = new();

    internal Jbig2IntDecoder ExportRun { get; } = new();

    internal Jbig2IntDecoder AggregateCount { get; } = new();

    internal Jbig2IntDecoder DeltaT { get; } = new();

    internal Jbig2IntDecoder FirstS { get; } = new();

    internal Jbig2IntDecoder DeltaS { get; } = new();

    internal Jbig2IntDecoder CurrentT { get; } = new();

    internal Jbig2IntDecoder RefinementFlag { get; } = new();

    internal Jbig2IntDecoder RefinementDeltaWidth { get; } = new();

    internal Jbig2IntDecoder RefinementDeltaHeight { get; } = new();

    internal Jbig2IntDecoder RefinementX { get; } = new();

    internal Jbig2IntDecoder RefinementY { get; } = new();
}

/// <summary>The exported symbols and any bitmap coding context retained by one dictionary.</summary>
internal sealed class Jbig2SymbolDictionaryResult
{
    internal Jbig2SymbolDictionaryResult(
        Jbig2Bitmap[] exported,
        Jbig2SymbolCodingContext? retainedContext)
    {
        Exported = exported;
        RetainedContext = retainedContext;
    }

    internal Jbig2Bitmap[] Exported { get; }

    internal Jbig2SymbolCodingContext? RetainedContext { get; }
}

/// <summary>
/// A snapshot of the generic-region and generic-refinement MQ statistics retained at the end of
/// a symbol dictionary (ITU-T T.88 §7.4.2.2). Integer decoder statistics are deliberately absent:
/// the standard resets them for every dictionary, even when bitmap statistics are reused.
/// </summary>
internal sealed class Jbig2SymbolCodingContext
{
    private readonly Jbig2SymbolCodingConfiguration _configuration;
    private readonly MqContext[] _generic;
    private readonly MqContext[] _refinement;

    internal Jbig2SymbolCodingContext(
        Jbig2SymbolCodingConfiguration configuration,
        MqContext[] generic,
        MqContext[] refinement)
    {
        _configuration = configuration;
        _generic = (MqContext[])generic.Clone();
        _refinement = (MqContext[])refinement.Clone();
    }

    /// <summary>
    /// Validates the header fields that define context indices, then gives the new dictionary an
    /// independent copy. A retained dictionary may be referred to more than once, so mutating its
    /// stored snapshot would make decoding depend on segment order.
    /// </summary>
    internal (MqContext[] Generic, MqContext[] Refinement) CopyFor(
        Jbig2SymbolCodingConfiguration configuration)
    {
        configuration.EnsureCompatibleWith(_configuration);
        return ((MqContext[])_generic.Clone(), (MqContext[])_refinement.Clone());
    }
}

/// <summary>The symbol-dictionary header fields that determine bitmap MQ context addressing.</summary>
internal sealed class Jbig2SymbolCodingConfiguration
{
    private readonly (int X, int Y)[] _at;
    private readonly (int X, int Y)[] _refinementAt;

    internal Jbig2SymbolCodingConfiguration(
        bool huffman,
        bool refineAggregate,
        int template,
        int refinementTemplate,
        ReadOnlySpan<(int X, int Y)> at,
        ReadOnlySpan<(int X, int Y)> refinementAt)
    {
        Huffman = huffman;
        RefineAggregate = refineAggregate;
        Template = template;
        RefinementTemplate = refinementTemplate;
        _at = at.ToArray();
        _refinementAt = refinementAt.ToArray();
    }

    private bool Huffman { get; }

    private bool RefineAggregate { get; }

    private int Template { get; }

    private int RefinementTemplate { get; }

    /// <summary>
    /// T.88 §7.4.2.2 requires every field capable of changing a context index to match the source
    /// dictionary. Treating a mismatch as fresh state would decode plausible but incorrect pixels.
    /// </summary>
    internal void EnsureCompatibleWith(Jbig2SymbolCodingConfiguration retained)
    {
        Require(Huffman == retained.Huffman, "SDHUFF");
        Require(RefineAggregate == retained.RefineAggregate, "SDREFAGG");
        Require(Template == retained.Template, "SDTEMPLATE");
        Require(RefinementTemplate == retained.RefinementTemplate, "SDRTEMPLATE");
        Require(_at.AsSpan().SequenceEqual(retained._at), "SDAT");
        Require(_refinementAt.AsSpan().SequenceEqual(retained._refinementAt), "SDRAT");
    }

    private static void Require(bool condition, string field)
    {
        if (!condition)
        {
            throw new NotSupportedException(
                $"A JBIG2 symbol dictionary cannot reuse a bitmap coding context because {field} " +
                "differs from the retained dictionary.");
        }
    }
}

/// <summary>
/// The symbol dictionary decoding procedure of ITU-T T.88 §6.5. A dictionary holds the distinct
/// shapes a scanned page is built from — usually the letters — and a text region then places them.
/// That split is why JBIG2 compresses text so much harder than a generic bitmap coder can: the
/// letter "e" is stored once and referred to a thousand times.
/// </summary>
/// <remarks>
/// Symbols are grouped into height classes, and within a class only the difference in width from
/// the previous symbol is coded, so a run of similarly sized letters costs almost nothing.
/// </remarks>
internal static class Jbig2SymbolDictionary
{
    /// <summary>How many symbols one dictionary may define before it is taken to be malformed.</summary>
    private const int MaxSymbols = 100_000;

    /// <summary>How many contexts the widest generic-region template addresses.</summary>
    private const int GenericContextCount = 1 << 16;

    /// <summary>Decodes a symbol dictionary segment.</summary>
    /// <param name="data">The segment's data part.</param>
    /// <param name="imported">Symbols this dictionary imports from the dictionaries it refers to.</param>
    /// <param name="custom">The tables this segment's referred-to table segments supply, in order.</param>
    /// <param name="reusedContext">
    /// The last referred-to symbol dictionary's retained bitmap context, or null when it retained none.
    /// </param>
    /// <returns>The exported symbols and, when requested, the retained bitmap coding context.</returns>
    /// <exception cref="NotSupportedException">The dictionary uses a variant this decoder refuses.</exception>
    internal static Jbig2SymbolDictionaryResult Decode(
        ReadOnlyMemory<byte> data,
        IReadOnlyList<Jbig2Bitmap> imported,
        IReadOnlyList<Jbig2HuffmanTable> custom,
        Jbig2SymbolCodingContext? reusedContext = null)
    {
        ReadOnlySpan<byte> span = data.Span;

        if (span.Length < 2)
            throw new NotSupportedException("A JBIG2 symbol dictionary segment is truncated.");

        int flags = BinaryPrimitives.ReadUInt16BigEndian(span);
        bool huffman = (flags & 1) != 0;
        bool refineAggregate = ((flags >> 1) & 1) != 0;
        int deltaHeightChoice = (flags >> 2) & 3;
        int deltaWidthChoice = (flags >> 4) & 3;
        int bitmapSizeChoice = (flags >> 6) & 1;
        int aggregateChoice = (flags >> 7) & 1;
        bool contextUsed = ((flags >> 8) & 1) != 0;
        bool contextRetained = ((flags >> 9) & 1) != 0;
        int template = (flags >> 10) & 3;
        int refinementTemplate = (flags >> 12) & 1;

        if (huffman && !refineAggregate && (contextUsed || contextRetained))
        {
            string flagName = contextUsed ? "used" : "retained";
            throw new NotSupportedException(
                $"The JBIG2 bitmap coding context {flagName} bit must be zero when " +
                "SDHUFF=1 and SDREFAGG=0.");
        }

        int position = 2;
        Span<(int X, int Y)> at = stackalloc (int, int)[4];
        int atCount = 0;

        // The template pixels are only present in the arithmetic variant.
        if (!huffman)
        {
            atCount = template == 0 ? 4 : 1;
            if (position + (atCount * 2) > span.Length)
                throw new NotSupportedException("A JBIG2 symbol dictionary segment is truncated.");

            for (int i = 0; i < atCount; i++)
            {
                at[i] = ((sbyte)span[position], (sbyte)span[position + 1]);
                position += 2;
            }
        }

        Span<(int X, int Y)> refinementAt = stackalloc (int, int)[2];
        int refinementAtCount = 0;

        if (refineAggregate && refinementTemplate == 0)
        {
            refinementAtCount = 2;
            if (position + 4 > span.Length)
                throw new NotSupportedException("A JBIG2 symbol dictionary segment is truncated.");

            for (int i = 0; i < 2; i++)
            {
                refinementAt[i] = ((sbyte)span[position], (sbyte)span[position + 1]);
                position += 2;
            }
        }

        if (position + 8 > span.Length)
            throw new NotSupportedException("A JBIG2 symbol dictionary segment is truncated.");

        int exported = (int)BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
        int defined = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(position + 4)..]);
        position += 8;

        if (defined < 0 || defined > MaxSymbols || exported < 0 || exported > MaxSymbols)
            throw new NotSupportedException($"A JBIG2 symbol dictionary claims {defined} symbols.");

        var configuration = new Jbig2SymbolCodingConfiguration(
            huffman,
            refineAggregate,
            template,
            refinementTemplate,
            at[..atCount],
            refinementAt[..refinementAtCount]);

        MqContext[] genericContexts;
        MqContext[] refinementContexts;

        if (contextUsed)
        {
            if (reusedContext is null)
            {
                throw new NotSupportedException(
                    "A JBIG2 symbol dictionary requests bitmap coding-context reuse, but its last " +
                    "referred-to symbol dictionary did not retain one.");
            }

            (genericContexts, refinementContexts) = reusedContext.CopyFor(configuration);
        }
        else
        {
            genericContexts = MqDecoder.CreateContexts(GenericContextCount);
            refinementContexts = MqDecoder.CreateContexts(Jbig2TextRegion.RefinementContextCount);
        }

        int codeLength = Jbig2TextRegion.CodeLength(imported.Count + defined);
        ReadOnlyMemory<byte> body = data[position..];

        Jbig2ValueReader reader;
        Jbig2BitReader? bits = null;

        if (huffman)
        {
            int taken = 0;
            Jbig2HuffmanTable Next() => taken < custom.Count
                ? custom[taken++]
                : throw new NotSupportedException("A JBIG2 symbol dictionary names a table segment it does not refer to.");

            var tables = new Jbig2HuffmanTables
            {
                DeltaHeight = deltaHeightChoice == 3 ? Next() : Jbig2StandardTables.Get(deltaHeightChoice == 0 ? 4 : 5),
                DeltaWidth = deltaWidthChoice == 3 ? Next() : Jbig2StandardTables.Get(deltaWidthChoice == 0 ? 2 : 3),
                BitmapSize = bitmapSizeChoice == 1 ? Next() : Jbig2StandardTables.Get(1),
                AggregateCount = aggregateChoice == 1 ? Next() : Jbig2StandardTables.Get(1),

                // The nested text region of an aggregate symbol always uses these (Table 17).
                FirstS = Jbig2StandardTables.Get(6),
                DeltaS = Jbig2StandardTables.Get(8),
                DeltaT = Jbig2StandardTables.Get(11),
                RefinementDeltaWidth = Jbig2StandardTables.Get(15),
                RefinementDeltaHeight = Jbig2StandardTables.Get(15),
                RefinementX = Jbig2StandardTables.Get(15),
                RefinementY = Jbig2StandardTables.Get(15),
                RefinementSize = Jbig2StandardTables.Get(1),
                SymbolIds = Jbig2HuffmanTable.FromLengths(
                    [.. Enumerable.Repeat(Math.Max(codeLength, 1), imported.Count + defined)]),
            };

            bits = new Jbig2BitReader(body);
            reader = new Jbig2HuffmanReader(bits, tables);
        }
        else
        {
            reader = new Jbig2ArithmeticReader(
                new MqDecoder(body), new Jbig2IntDecoders(), new Jbig2IdDecoder(codeLength));
        }

        var builder = new Jbig2SymbolReader(
            reader,
            bits,
            imported,
            defined,
            template,
            at[..atCount],
            refineAggregate,
            refinementTemplate,
            refinementAt[..refinementAtCount],
            genericContexts,
            refinementContexts);

        Jbig2Bitmap[] symbols = builder.Run(exported);
        Jbig2SymbolCodingContext? retained = contextRetained
            ? new Jbig2SymbolCodingContext(configuration, genericContexts, refinementContexts)
            : null;

        return new Jbig2SymbolDictionaryResult(symbols, retained);
    }
}

/// <summary>Carries the state of one symbol dictionary through its height classes.</summary>
internal sealed class Jbig2SymbolReader
{
    private const long MaxSymbolPixels = 16L * 1024 * 1024;

    private readonly Jbig2ValueReader _reader;
    private readonly Jbig2BitReader? _bits;
    private readonly IReadOnlyList<Jbig2Bitmap> _imported;
    private readonly int _defined;
    private readonly int _template;
    private readonly (int X, int Y)[] _at;
    private readonly bool _refineAggregate;
    private readonly int _refinementTemplate;
    private readonly (int X, int Y)[] _refinementAt;
    private readonly MqContext[] _generic;
    private readonly MqContext[] _refinement;

    internal Jbig2SymbolReader(
        Jbig2ValueReader reader,
        Jbig2BitReader? bits,
        IReadOnlyList<Jbig2Bitmap> imported,
        int defined,
        int template,
        ReadOnlySpan<(int X, int Y)> at,
        bool refineAggregate,
        int refinementTemplate,
        ReadOnlySpan<(int X, int Y)> refinementAt,
        MqContext[] genericContexts,
        MqContext[] refinementContexts)
    {
        _reader = reader;
        _bits = bits;
        _imported = imported;
        _defined = defined;
        _template = template;
        _at = at.ToArray();
        _refineAggregate = refineAggregate;
        _refinementTemplate = refinementTemplate;
        _refinementAt = refinementAt.ToArray();
        _generic = genericContexts;
        _refinement = refinementContexts;
    }

    /// <summary>Decodes every height class, then works out which symbols are exported (§6.5.5).</summary>
    /// <param name="exported">How many symbols the header says are exported.</param>
    internal Jbig2Bitmap[] Run(int exported)
    {
        var created = new List<Jbig2Bitmap>(_defined);
        int height = 0;

        // The Huffman variant without refinement codes a whole height class as one wide bitmap,
        // so the widths are collected first and the pixels split out afterwards.
        bool collective = _reader.IsHuffman && !_refineAggregate;

        while (created.Count < _defined)
        {
            int deltaHeight = _reader.DeltaHeight();
            if (Jbig2IntDecoder.IsOutOfBand(deltaHeight))
                break;

            height += deltaHeight;
            if (height < 0 || height > 1 << 14)
                throw new NotSupportedException($"A JBIG2 symbol height class claims a height of {height}.");

            int width = 0;
            int totalWidth = 0;
            int classStart = created.Count;
            var widths = new List<int>();

            while (true)
            {
                int deltaWidth = _reader.DeltaWidth();

                // Out of band ends the height class rather than naming a width.
                if (Jbig2IntDecoder.IsOutOfBand(deltaWidth))
                    break;

                width += deltaWidth;

                if (width <= 0 || (long)width * height > MaxSymbolPixels || created.Count >= _defined)
                    throw new NotSupportedException($"A JBIG2 symbol claims to be {width} by {height}.");

                totalWidth += width;
                widths.Add(width);

                created.Add(collective
                    ? new Jbig2Bitmap(width, height)
                    : DecodeSymbol(width, height, created));
            }

            if (collective)
                SplitCollective(created, classStart, widths, totalWidth, height);
        }

        return SelectExported(created, exported);
    }

    /// <summary>
    /// Reads a height class coded as one bitmap and cuts the symbols out of it (§6.5.9). The class
    /// is either raw or fax-coded, and a stated size says which and how long it runs.
    /// </summary>
    private void SplitCollective(
        List<Jbig2Bitmap> created, int classStart, List<int> widths, int totalWidth, int height)
    {
        if (widths.Count == 0 || _bits is null || _reader is not Jbig2HuffmanReader huffman)
            return;

        int size = huffman.BitmapSize();
        _bits.AlignToByte();

        Jbig2Bitmap collective;

        if (size == 0)
        {
            // A size of zero means the rows are stored plainly, each padded to a byte boundary.
            int stride = (totalWidth + 7) / 8;
            collective = new Jbig2Bitmap(totalWidth, height);
            ReadOnlySpan<byte> raw = _bits.Remaining.Span;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < totalWidth; x++)
                {
                    int index = (y * stride) + (x >> 3);
                    if (index < raw.Length)
                        collective[x, y] = (raw[index] >> (7 - (x & 7))) & 1;
                }
            }

            _bits.SeekByte(_bits.BytePosition + (stride * height));
        }
        else
        {
            ReadOnlyMemory<byte> coded = _bits.Remaining;
            int take = Math.Min(size, coded.Length);

            collective = Jbig2GenericRegion.DecodeMmr(coded[..take].Span, totalWidth, height);
            _bits.SeekByte(_bits.BytePosition + take);
        }

        int left = 0;
        for (int i = 0; i < widths.Count; i++)
        {
            created[classStart + i] = collective.Crop(left, 0, widths[i], height);
            left += widths[i];
        }
    }

    private Jbig2Bitmap DecodeSymbol(int width, int height, List<Jbig2Bitmap> created)
    {
        if (!_refineAggregate)
        {
            MqDecoder plain = _reader.Arithmetic
                ?? throw new NotSupportedException("A JBIG2 symbol bitmap needs the arithmetic coder.");

            return Jbig2GenericRegion.Decode(
                plain, _generic, width, height, _template, _at, typicalPrediction: false);
        }

        int instances = _reader.AggregateCount();

        // The symbols available to refine against are the imported ones followed by everything
        // this dictionary has defined so far (§6.5.8.2.4).
        var available = new List<Jbig2Bitmap>(_imported.Count + created.Count);
        available.AddRange(_imported);
        available.AddRange(created);

        if (instances == 1)
            return RefineOne(width, height, available);

        if (instances < 1)
            throw new NotSupportedException($"A JBIG2 aggregate symbol claims {instances} instances.");

        // More than one instance means the symbol is assembled by a text region of its own, which
        // reads from the same source this dictionary is reading from.
        return Jbig2TextRegion.Decode(
            new Jbig2TextRegionParameters
            {
                Width = width,
                Height = height,
                Instances = instances,
                Strips = 1,
                LogStrips = 0,
                Symbols = available,
                DefaultPixel = 0,
                Combination = Jbig2CombinationOperator.Or,
                Transposed = false,
                ReferenceCorner = Jbig2ReferenceCorner.TopLeft,
                SOffset = 0,
                Refine = true,
                RefinementTemplate = _refinementTemplate,
                RefinementAt = _refinementAt,
            },
            _reader,
            _refinement);
    }

    /// <summary>
    /// Decodes a symbol that refines exactly one earlier symbol (§6.5.8.2.2). The values a text
    /// region would decode are known here, so only the offsets and the refinement itself are read.
    /// </summary>
    private Jbig2Bitmap RefineOne(int width, int height, List<Jbig2Bitmap> available)
    {
        int id = _reader.SymbolId();
        int dx = _reader.RefinementX();
        int dy = _reader.RefinementY();

        Jbig2Bitmap reference = id >= 0 && id < available.Count
            ? available[id]
            : new Jbig2Bitmap(width, height);

        // Even in the Huffman variant the refinement itself is arithmetically coded, so the bit
        // reader hands over at a byte boundary and picks up again after the stated length.
        int size = 0;
        MqDecoder decoder;

        if (_reader.IsHuffman && _bits is not null)
        {
            size = _reader.RefinementSize();
            _bits.AlignToByte();
            decoder = new MqDecoder(_bits.Remaining);
        }
        else
        {
            decoder = _reader.Arithmetic
                ?? throw new NotSupportedException("A JBIG2 symbol refinement needs the arithmetic coder.");
        }

        Jbig2Bitmap refined = Jbig2RefinementRegion.Decode(
            decoder,
            _refinement,
            width,
            height,
            _refinementTemplate,
            reference,
            dx,
            dy,
            _refinementAt,
            typicalPrediction: false);

        _bits?.SeekByte(_bits.BytePosition + size);
        return refined;
    }

    /// <summary>
    /// Reads the export flags as alternating runs (§6.5.10). A dictionary may export symbols it
    /// imported and withhold symbols it defined, which is how one dictionary republishes another's.
    /// </summary>
    private Jbig2Bitmap[] SelectExported(List<Jbig2Bitmap> created, int exported)
    {
        int total = _imported.Count + created.Count;
        var flags = new bool[total];

        int index = 0;
        bool current = false;

        while (index < total)
        {
            int run = _reader.ExportRun();

            if (Jbig2IntDecoder.IsOutOfBand(run) || run < 0 || index + run > total)
                break;

            if (current)
            {
                for (int i = index; i < index + run; i++)
                    flags[i] = true;
            }

            index += run;
            current = !current;
        }

        var result = new List<Jbig2Bitmap>(Math.Max(exported, 0));

        for (int i = 0; i < total; i++)
        {
            if (!flags[i])
                continue;

            result.Add(i < _imported.Count ? _imported[i] : created[i - _imported.Count]);
        }

        // A dictionary whose flags are unreadable still has to hand something back, and the newly
        // defined symbols are the best guess at what it meant to export.
        if (result.Count == 0 && created.Count > 0)
            return [.. created];

        return [.. result];
    }
}
