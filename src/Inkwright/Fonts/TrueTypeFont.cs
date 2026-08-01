using System.Buffers.Binary;
using System.Text;

namespace Inkwright.Fonts;

/// <summary>
/// A parsed TrueType or OpenType font program. Only the tables embedding and measuring need are
/// read, and the file bytes are kept so that subsetting works from the original data.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private ushort[]? _advanceWidths;
    private Dictionary<int, ushort>? _cmap;
    private int[]? _loca;

    private TrueTypeFont(byte[] data) => _data = data;

    /// <summary>The whole font file.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>The PostScript name from the <c>name</c> table, or a placeholder when it has none.</summary>
    public string PostScriptName { get; private set; } = "Untitled";

    /// <summary>The family name from the <c>name</c> table.</summary>
    public string FamilyName { get; private set; } = "Untitled";

    /// <summary>Design units per em, the denominator for every metric below.</summary>
    public int UnitsPerEm { get; private set; } = 1000;

    /// <summary>The number of glyphs in the font.</summary>
    public int GlyphCount { get; private set; }

    /// <summary>The typographic ascender in design units.</summary>
    public int Ascender { get; private set; }

    /// <summary>The typographic descender in design units, negative below the baseline.</summary>
    public int Descender { get; private set; }

    /// <summary>The recommended gap between lines in design units.</summary>
    public int LineGap { get; private set; }

    /// <summary>The cap height in design units, falling back to the ascender when the font omits it.</summary>
    public int CapHeight { get; private set; }

    /// <summary>The angle the font slants at, negative for a forward slant.</summary>
    public double ItalicAngle { get; private set; }

    /// <summary>The bounding box of every glyph, in design units.</summary>
    public PdfRectangle BoundingBox { get; private set; }

    /// <summary>The weight class from the <c>OS/2</c> table, 400 for regular and 700 for bold.</summary>
    public int Weight { get; private set; } = 400;

    /// <summary>Whether every glyph has the same advance width.</summary>
    public bool IsFixedPitch { get; private set; }

    /// <summary>Whether the font's outlines are compact font format rather than TrueType.</summary>
    public bool IsCompactFontFormat { get; private set; }

    /// <summary>
    /// Whether the embedding permissions in <c>OS/2</c> forbid embedding. The library reports the
    /// flag rather than acting on it: honouring a licence is the caller's decision to make.
    /// </summary>
    public bool EmbeddingRestricted { get; private set; }

    /// <summary>Reads a font from a file.</summary>
    /// <param name="path">The font file.</param>
    public static TrueTypeFont Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Reads a font from memory.</summary>
    /// <param name="data">The font file; the array is kept for subsetting.</param>
    public static TrueTypeFont Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 12)
            throw new InvalidDataException("The font file is too short to hold a table directory.");

        var font = new TrueTypeFont(data);
        font.ReadTableDirectory();
        font.ReadMetrics();
        return font;
    }

    /// <summary>Whether the font carries a table.</summary>
    /// <param name="tag">The four-character table tag.</param>
    public bool HasTable(string tag) => _tables.ContainsKey(tag);

    /// <summary>Where a table lies in the font, for callers that need the bytes as memory.</summary>
    /// <param name="tag">The four-character table tag.</param>
    /// <returns>The offset and length, or <see langword="null"/> when there is no such table.</returns>
    internal (int Offset, int Length)? GetTableRange(string tag) =>
        _tables.TryGetValue(tag, out (int Offset, int Length) entry)
            ? (entry.Offset, Math.Min(entry.Length, _data.Length - entry.Offset))
            : null;

    /// <summary>The bytes of a table.</summary>
    /// <param name="tag">The four-character table tag.</param>
    public ReadOnlySpan<byte> GetTable(string tag) =>
        _tables.TryGetValue(tag, out (int Offset, int Length) entry)
            ? _data.AsSpan(entry.Offset, Math.Min(entry.Length, _data.Length - entry.Offset))
            : [];

    /// <summary>The glyph a character maps to, or zero when the font has no glyph for it.</summary>
    /// <param name="codePoint">The Unicode code point.</param>
    public ushort GetGlyph(int codePoint)
    {
        _cmap ??= ReadCharacterMap();
        return _cmap.TryGetValue(codePoint, out ushort glyph) ? glyph : (ushort)0;
    }

    /// <summary>The advance width of a glyph in design units.</summary>
    /// <param name="glyph">The glyph index.</param>
    public int GetAdvanceWidth(ushort glyph)
    {
        _advanceWidths ??= ReadAdvanceWidths();

        if (_advanceWidths.Length == 0)
            return UnitsPerEm / 2;

        return glyph < _advanceWidths.Length ? _advanceWidths[glyph] : _advanceWidths[^1];
    }

    /// <summary>The advance width of a glyph in units of 1/1000 em, the scale PDF uses.</summary>
    /// <param name="glyph">The glyph index.</param>
    public int GetAdvanceWidthPerMille(ushort glyph) =>
        (int)Math.Round(GetAdvanceWidth(glyph) * 1000.0 / UnitsPerEm);

    /// <summary>Scales a value from design units to units of 1/1000 em.</summary>
    /// <param name="designUnits">The value to scale.</param>
    public int ToPerMille(int designUnits) => (int)Math.Round(designUnits * 1000.0 / UnitsPerEm);

    /// <summary>The byte range of a glyph's outline in the <c>glyf</c> table.</summary>
    /// <param name="glyph">The glyph index.</param>
    internal (int Offset, int Length) GetGlyphRange(ushort glyph)
    {
        _loca ??= ReadLocations();

        if (_loca.Length == 0 || glyph + 1 >= _loca.Length)
            return (0, 0);

        int start = _loca[glyph];
        int end = _loca[glyph + 1];
        return end <= start ? (0, 0) : (start, end - start);
    }

    /// <summary>The glyphs a composite glyph is built from.</summary>
    /// <param name="glyph">The glyph index.</param>
    internal List<ushort> GetComponents(ushort glyph)
    {
        List<ushort> components = [];

        (int offset, int length) = GetGlyphRange(glyph);
        if (length < 10)
            return components;

        ReadOnlySpan<byte> glyf = GetTable("glyf");
        if (offset + length > glyf.Length)
            return components;

        ReadOnlySpan<byte> outline = glyf.Slice(offset, length);
        if (BinaryPrimitives.ReadInt16BigEndian(outline) >= 0)
            return components;

        int position = 10;
        while (position + 4 <= outline.Length)
        {
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(outline[position..]);
            components.Add(BinaryPrimitives.ReadUInt16BigEndian(outline[(position + 2)..]));

            position += 4;
            position += (flags & 0x0001) != 0 ? 4 : 2;      // ARG_1_AND_2_ARE_WORDS
            if ((flags & 0x0008) != 0)
                position += 2;                              // WE_HAVE_A_SCALE
            else if ((flags & 0x0040) != 0)
                position += 4;                              // WE_HAVE_AN_X_AND_Y_SCALE
            else if ((flags & 0x0080) != 0)
                position += 8;                              // WE_HAVE_A_TWO_BY_TWO

            if ((flags & 0x0020) == 0)                      // MORE_COMPONENTS
                break;
        }

        return components;
    }

    /// <summary>Whether glyph locations are stored as two-byte or four-byte offsets.</summary>
    internal bool LongLocationFormat { get; private set; }

    private void ReadTableDirectory()
    {
        ReadOnlySpan<byte> span = _data;
        uint version = BinaryPrimitives.ReadUInt32BigEndian(span);

        int directoryOffset = 0;
        if (version == 0x74746366)
        {
            // A TrueType collection: take the first font in it.
            if (span.Length < 16)
                throw new InvalidDataException("The font collection has no entries.");

            directoryOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
            version = BinaryPrimitives.ReadUInt32BigEndian(span[directoryOffset..]);
        }

        IsCompactFontFormat = version == 0x4F54544F;

        int count = BinaryPrimitives.ReadUInt16BigEndian(span[(directoryOffset + 4)..]);
        int position = directoryOffset + 12;

        for (int i = 0; i < count && position + 16 <= span.Length; i++, position += 16)
        {
            string tag = Encoding.ASCII.GetString(span.Slice(position, 4));
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(position + 8)..]);
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(position + 12)..]);

            if (offset >= 0 && offset < span.Length && length >= 0)
                _tables[tag] = (offset, Math.Min(length, span.Length - offset));
        }
    }

    private void ReadMetrics()
    {
        ReadOnlySpan<byte> head = GetTable("head");
        if (head.Length >= 54)
        {
            UnitsPerEm = Math.Max(16, (int)BinaryPrimitives.ReadUInt16BigEndian(head[18..]));
            BoundingBox = new PdfRectangle(
                BinaryPrimitives.ReadInt16BigEndian(head[36..]),
                BinaryPrimitives.ReadInt16BigEndian(head[38..]),
                BinaryPrimitives.ReadInt16BigEndian(head[40..]),
                BinaryPrimitives.ReadInt16BigEndian(head[42..]));
            LongLocationFormat = BinaryPrimitives.ReadInt16BigEndian(head[50..]) != 0;
        }

        ReadOnlySpan<byte> maxp = GetTable("maxp");
        if (maxp.Length >= 6)
            GlyphCount = BinaryPrimitives.ReadUInt16BigEndian(maxp[4..]);

        ReadOnlySpan<byte> hhea = GetTable("hhea");
        if (hhea.Length >= 36)
        {
            Ascender = BinaryPrimitives.ReadInt16BigEndian(hhea[4..]);
            Descender = BinaryPrimitives.ReadInt16BigEndian(hhea[6..]);
            LineGap = BinaryPrimitives.ReadInt16BigEndian(hhea[8..]);
        }

        ReadOnlySpan<byte> os2 = GetTable("OS/2");
        if (os2.Length >= 78)
        {
            Weight = BinaryPrimitives.ReadUInt16BigEndian(os2[4..]);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(os2[8..]);
            EmbeddingRestricted = (type & 0x000F) == 2;

            short typoAscender = BinaryPrimitives.ReadInt16BigEndian(os2[68..]);
            short typoDescender = BinaryPrimitives.ReadInt16BigEndian(os2[70..]);
            if (typoAscender != 0)
                Ascender = typoAscender;

            if (typoDescender != 0)
                Descender = typoDescender;
        }

        CapHeight = os2.Length >= 90 ? BinaryPrimitives.ReadInt16BigEndian(os2[88..]) : 0;
        if (CapHeight <= 0)
            CapHeight = (int)(Ascender * 0.7);

        ReadOnlySpan<byte> post = GetTable("post");
        if (post.Length >= 16)
        {
            ItalicAngle = BinaryPrimitives.ReadInt32BigEndian(post[4..]) / 65536.0;
            IsFixedPitch = BinaryPrimitives.ReadUInt32BigEndian(post[12..]) != 0;
        }

        ReadNames();

        if (Ascender == 0)
            Ascender = (int)(UnitsPerEm * 0.75);

        if (Descender == 0)
            Descender = -(int)(UnitsPerEm * 0.25);
    }

    private void ReadNames()
    {
        ReadOnlySpan<byte> name = GetTable("name");
        if (name.Length < 6)
            return;

        int count = BinaryPrimitives.ReadUInt16BigEndian(name[2..]);
        int stringOffset = BinaryPrimitives.ReadUInt16BigEndian(name[4..]);

        for (int i = 0; i < count; i++)
        {
            int record = 6 + (i * 12);
            if (record + 12 > name.Length)
                break;

            int platform = BinaryPrimitives.ReadUInt16BigEndian(name[record..]);
            int nameId = BinaryPrimitives.ReadUInt16BigEndian(name[(record + 6)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(name[(record + 8)..]);
            int offset = stringOffset + BinaryPrimitives.ReadUInt16BigEndian(name[(record + 10)..]);

            if (nameId is not (1 or 6) || offset + length > name.Length || length == 0)
                continue;

            ReadOnlySpan<byte> value = name.Slice(offset, length);
            string text = platform == 1 ? Encoding.ASCII.GetString(value) : PdfTextEncodingHelper(value);

            if (nameId == 6)
                PostScriptName = text;
            else
                FamilyName = text;
        }

        if (PostScriptName == "Untitled" && FamilyName != "Untitled")
            PostScriptName = FamilyName.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static string PdfTextEncodingHelper(ReadOnlySpan<byte> value) =>
        Encoding.BigEndianUnicode.GetString(value.Length % 2 == 0 ? value : value[..^1]);

    private ushort[] ReadAdvanceWidths()
    {
        ReadOnlySpan<byte> hhea = GetTable("hhea");
        ReadOnlySpan<byte> hmtx = GetTable("hmtx");

        if (hhea.Length < 36 || hmtx.IsEmpty)
            return [];

        int metrics = BinaryPrimitives.ReadUInt16BigEndian(hhea[34..]);
        metrics = Math.Clamp(metrics, 0, hmtx.Length / 4);
        if (metrics == 0)
            return [];

        ushort[] widths = new ushort[metrics];
        for (int i = 0; i < metrics; i++)
            widths[i] = BinaryPrimitives.ReadUInt16BigEndian(hmtx[(i * 4)..]);

        return widths;
    }

    private int[] ReadLocations()
    {
        ReadOnlySpan<byte> loca = GetTable("loca");
        if (loca.IsEmpty)
            return [];

        int count = LongLocationFormat ? loca.Length / 4 : loca.Length / 2;
        int[] offsets = new int[count];

        for (int i = 0; i < count; i++)
        {
            offsets[i] = LongLocationFormat
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca[(i * 4)..])
                : BinaryPrimitives.ReadUInt16BigEndian(loca[(i * 2)..]) * 2;
        }

        return offsets;
    }

    private Dictionary<int, ushort> ReadCharacterMap()
    {
        var map = new Dictionary<int, ushort>(512);
        ReadOnlySpan<byte> cmap = GetTable("cmap");
        if (cmap.Length < 4)
            return map;

        int tables = BinaryPrimitives.ReadUInt16BigEndian(cmap[2..]);
        int best = -1;
        int bestScore = -1;

        for (int i = 0; i < tables; i++)
        {
            int record = 4 + (i * 8);
            if (record + 8 > cmap.Length)
                break;

            int platform = BinaryPrimitives.ReadUInt16BigEndian(cmap[record..]);
            int encoding = BinaryPrimitives.ReadUInt16BigEndian(cmap[(record + 2)..]);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap[(record + 4)..]);

            // Prefer full Unicode, then the basic plane, then anything at all.
            int score = (platform, encoding) switch
            {
                (3, 10) => 5,
                (0, 4) or (0, 6) => 5,
                (3, 1) => 4,
                (0, _) => 3,
                (3, 0) => 2,
                (1, 0) => 1,
                _ => 0,
            };

            if (score > bestScore && offset > 0 && offset < cmap.Length)
            {
                bestScore = score;
                best = offset;
            }
        }

        if (best < 0)
            return map;

        ReadSubtable(cmap[best..], map);
        return map;
    }

    private static void ReadSubtable(ReadOnlySpan<byte> table, Dictionary<int, ushort> map)
    {
        if (table.Length < 4)
            return;

        int format = BinaryPrimitives.ReadUInt16BigEndian(table);

        switch (format)
        {
            case 0:
                for (int code = 0; code < 256 && 6 + code < table.Length; code++)
                    map.TryAdd(code, table[6 + code]);

                break;

            case 4:
            {
                int segmentsX2 = BinaryPrimitives.ReadUInt16BigEndian(table[6..]);
                int segments = segmentsX2 / 2;
                int endsAt = 14;
                int startsAt = endsAt + segmentsX2 + 2;
                int deltasAt = startsAt + segmentsX2;
                int rangesAt = deltasAt + segmentsX2;

                for (int i = 0; i < segments; i++)
                {
                    if (rangesAt + (i * 2) + 2 > table.Length)
                        break;

                    int end = BinaryPrimitives.ReadUInt16BigEndian(table[(endsAt + (i * 2))..]);
                    int start = BinaryPrimitives.ReadUInt16BigEndian(table[(startsAt + (i * 2))..]);
                    short delta = BinaryPrimitives.ReadInt16BigEndian(table[(deltasAt + (i * 2))..]);
                    int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(table[(rangesAt + (i * 2))..]);

                    if (start > end)
                        continue;

                    for (int code = start; code <= end && code != 0xFFFF; code++)
                    {
                        ushort glyph;
                        if (rangeOffset == 0)
                        {
                            glyph = (ushort)((code + delta) & 0xFFFF);
                        }
                        else
                        {
                            int index = rangesAt + (i * 2) + rangeOffset + ((code - start) * 2);
                            if (index + 2 > table.Length)
                                continue;

                            glyph = BinaryPrimitives.ReadUInt16BigEndian(table[index..]);
                            if (glyph != 0)
                                glyph = (ushort)((glyph + delta) & 0xFFFF);
                        }

                        if (glyph != 0)
                            map.TryAdd(code, glyph);
                    }
                }

                break;
            }

            case 6:
            {
                int first = BinaryPrimitives.ReadUInt16BigEndian(table[6..]);
                int count = BinaryPrimitives.ReadUInt16BigEndian(table[8..]);
                for (int i = 0; i < count && 10 + (i * 2) + 2 <= table.Length; i++)
                    map.TryAdd(first + i, BinaryPrimitives.ReadUInt16BigEndian(table[(10 + (i * 2))..]));

                break;
            }

            case 12:
            {
                if (table.Length < 16)
                    break;

                int groups = (int)BinaryPrimitives.ReadUInt32BigEndian(table[12..]);
                for (int i = 0; i < groups; i++)
                {
                    int record = 16 + (i * 12);
                    if (record + 12 > table.Length)
                        break;

                    uint start = BinaryPrimitives.ReadUInt32BigEndian(table[record..]);
                    uint end = BinaryPrimitives.ReadUInt32BigEndian(table[(record + 4)..]);
                    uint glyph = BinaryPrimitives.ReadUInt32BigEndian(table[(record + 8)..]);

                    if (end < start || end - start > 0x10FFFF)
                        continue;

                    for (uint code = start; code <= end; code++)
                        map.TryAdd((int)code, (ushort)(glyph + (code - start)));
                }

                break;
            }

            default:
                break;
        }
    }
}
