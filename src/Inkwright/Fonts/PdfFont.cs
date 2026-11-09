using System.Globalization;
using System.Text;
using Inkwright.Cos;

namespace Inkwright.Fonts;

/// <summary>
/// A font a document can draw with. The two shapes that matter in practice are the fourteen
/// built-in fonts, which embed nothing, and an embedded TrueType font addressed through a composite
/// encoding, which handles the whole of Unicode and ships only the glyphs that were used.
/// </summary>
public abstract class PdfFont
{
    private protected PdfFont(PdfDocument document, string baseFont)
    {
        Document = document;
        BaseFont = baseFont;
    }

    /// <summary>The document the font belongs to.</summary>
    public PdfDocument Document { get; }

    /// <summary>The PostScript name written into the font dictionary.</summary>
    public string BaseFont { get; }

    /// <summary>The identity of the font dictionary.</summary>
    public PdfObjectId Id { get; private protected set; }

    /// <summary>A reference to the font dictionary, ready for a resource entry.</summary>
    public PdfValue Reference => PdfValue.Reference(Id);

    /// <summary>The height above the baseline, in units of 1/1000 em.</summary>
    public abstract int Ascent { get; }

    /// <summary>The depth below the baseline, negative, in units of 1/1000 em.</summary>
    public abstract int Descent { get; }

    /// <summary>A sensible distance between baselines at a given size.</summary>
    /// <param name="fontSize">The size in points.</param>
    public virtual double LineHeight(double fontSize) => (Ascent - Descent) * fontSize / 1000 * 1.15;

    /// <summary>Encodes text into the bytes a text-showing operator expects.</summary>
    /// <param name="text">The text to encode.</param>
    public abstract byte[] Encode(string text);

    /// <summary>The width of a code point in units of 1/1000 em.</summary>
    /// <param name="codePoint">The Unicode code point.</param>
    public abstract int GetWidth(int codePoint);

    /// <summary>Whether the font can draw a code point.</summary>
    /// <param name="codePoint">The Unicode code point.</param>
    public abstract bool Supports(int codePoint);

    /// <summary>The width of a string at a given size, in points.</summary>
    /// <param name="text">The text to measure.</param>
    /// <param name="fontSize">The size in points.</param>
    public double Measure(string text, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);

        double total = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int codePoint = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i]))
                i++;

            total += GetWidth(codePoint);
        }

        return total * fontSize / 1000;
    }

    /// <summary>Called before the document is written, so a font can finish embedding itself.</summary>
    private protected virtual void Finish()
    {
    }

    internal void FinishForSave() => Finish();
}

/// <summary>One of the fourteen built-in fonts, written as a simple Type 1 dictionary.</summary>
public sealed class Standard14PdfFont : PdfFont
{
    private readonly Standard14Font _metrics;
    private readonly FontEncoding _encoding;

    internal Standard14PdfFont(PdfDocument document, Standard14Font metrics)
        : base(document, metrics.Name)
    {
        _metrics = metrics;
        _encoding = metrics.IsSymbolic ? FontEncoding.Standard : FontEncoding.WinAnsi;

        var dictionary = new PdfDictionary(4);
        dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.Font));
        dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Type1));
        dictionary.Set(PdfNames.BaseFont, PdfValue.Name(PdfName.Get(metrics.Name)));

        if (!metrics.IsSymbolic)
            dictionary.Set(PdfNames.Encoding, PdfValue.Name(PdfNames.WinAnsiEncoding));

        Id = document.AddObject(PdfValue.Dictionary(dictionary));
    }

    /// <summary>The built-in metrics behind this font.</summary>
    public Standard14Font Metrics => _metrics;

    /// <inheritdoc />
    public override int Ascent => _metrics.Ascender;

    /// <inheritdoc />
    public override int Descent => _metrics.Descender;

    /// <inheritdoc />
    public override byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        byte[] bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
            bytes[i] = (byte)EncodeChar(text[i]);

        return bytes;
    }

    /// <inheritdoc />
    public override int GetWidth(int codePoint)
    {
        if (codePoint > 0xFFFF)
            return _metrics.GetWidth("space");

        int code = EncodeChar((char)codePoint);
        string? glyph = _encoding.GetGlyphName(code);
        return glyph is null ? _metrics.GetWidth("space") : _metrics.GetWidth(glyph);
    }

    /// <inheritdoc />
    public override bool Supports(int codePoint)
    {
        if (codePoint > 0xFFFF)
            return false;

        if (_metrics.IsSymbolic)
            return _metrics.GetWidthByBuiltInCode(codePoint) > 0;

        return _encoding.GetCode(GlyphNameOf((char)codePoint) ?? string.Empty) >= 0;
    }

    private int EncodeChar(char c)
    {
        if (_metrics.IsSymbolic)
            return c < 256 ? c : '?';

        // WinAnsi is Latin-1 for the low range, which covers most text without a lookup.
        if (c is < (char)128 or (>= (char)0xA0 and <= (char)0xFF))
            return c;

        string? glyph = GlyphNameOf(c);
        int code = glyph is null ? -1 : _encoding.GetCode(glyph);
        return code >= 0 ? code : '?';
    }

    private static string? GlyphNameOf(char c)
    {
        foreach (int code in Enumerable.Range(0, 256))
        {
            string? glyph = FontEncoding.WinAnsi.GetGlyphName(code);
            if (glyph is not null && FontEncoding.GlyphToText(glyph) is { Length: 1 } text && text[0] == c)
                return glyph;
        }

        return null;
    }
}

/// <summary>
/// An embedded TrueType font addressed through <c>Identity-H</c>, which sidesteps single-byte
/// encodings entirely: text is written as glyph indices, so any script the font covers works, and a
/// <c>ToUnicode</c> map keeps the result searchable.
/// </summary>
public sealed class EmbeddedTrueTypeFont : PdfFont
{
    private readonly TrueTypeFont _font;
    private readonly Dictionary<int, ushort> _glyphs = [];
    private readonly PdfDictionary _type0;
    private readonly PdfDictionary _cidFont;
    private readonly PdfDictionary _descriptor;
    private readonly PdfObjectId _cidFontId;
    private readonly PdfObjectId _descriptorId;
    private readonly PdfObjectId _fontFileId;
    private readonly PdfObjectId _toUnicodeId;
    private readonly bool _subset;

    internal EmbeddedTrueTypeFont(PdfDocument document, TrueTypeFont font, bool subset)
        : base(document, font.PostScriptName)
    {
        _font = font;
        _subset = subset;

        _descriptor = new PdfDictionary(12);
        _fontFileId = document.AddObject(PdfValue.Null);
        _descriptorId = document.AddObject(PdfValue.Dictionary(_descriptor));

        _cidFont = new PdfDictionary(8);
        _cidFontId = document.AddObject(PdfValue.Dictionary(_cidFont));

        _toUnicodeId = document.AddObject(PdfValue.Null);

        _type0 = new PdfDictionary(6);
        _type0.Set(PdfNames.Type, PdfValue.Name(PdfNames.Font));
        _type0.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Type0));
        _type0.Set(PdfNames.Encoding, PdfValue.Name(PdfNames.IdentityH));
        _type0.Set(PdfNames.DescendantFonts, PdfValue.Array(new PdfArray(PdfValue.Reference(_cidFontId))));
        _type0.Set(PdfNames.ToUnicode, PdfValue.Reference(_toUnicodeId));

        Id = document.AddObject(PdfValue.Dictionary(_type0));
        document.RegisterFont(this);
    }

    private EmbeddedTrueTypeFont(PdfDocument document, EmbeddedTrueTypeFont source)
        : base(document, source.BaseFont)
    {
        _font = source._font;
        _subset = source._subset;
        foreach (KeyValuePair<int, ushort> glyph in source._glyphs)
            _glyphs.Add(glyph.Key, glyph.Value);

        Id = source.Id;
        _cidFontId = source._cidFontId;
        _descriptorId = source._descriptorId;
        _fontFileId = source._fontFileId;
        _toUnicodeId = source._toUnicodeId;
        _type0 = RequireDictionary(Id);
        _cidFont = RequireDictionary(_cidFontId);
        _descriptor = RequireDictionary(_descriptorId);
        document.RegisterFont(this);

        PdfDictionary RequireDictionary(PdfObjectId id) =>
            document.GetObject(id).AsDictionary ?? throw new InvalidOperationException(
                $"The embedded font snapshot is missing object {id}.");
    }

    /// <summary>Copies unfinished subset state into an isolated document snapshot.</summary>
    internal void CloneForSnapshot(PdfDocument document) => _ = new EmbeddedTrueTypeFont(document, this);

    /// <summary>The font program behind this font.</summary>
    public TrueTypeFont Program => _font;

    /// <inheritdoc />
    public override int Ascent => _font.ToPerMille(_font.Ascender);

    /// <inheritdoc />
    public override int Descent => _font.ToPerMille(_font.Descender);

    /// <inheritdoc />
    public override byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = new List<byte>(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            int codePoint = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i]))
                i++;

            ushort glyph = Register(codePoint);
            bytes.Add((byte)(glyph >> 8));
            bytes.Add((byte)glyph);
        }

        return [.. bytes];
    }

    /// <inheritdoc />
    public override int GetWidth(int codePoint) => _font.GetAdvanceWidthPerMille(_font.GetGlyph(codePoint));

    /// <inheritdoc />
    public override bool Supports(int codePoint) => _font.GetGlyph(codePoint) != 0;

    private ushort Register(int codePoint)
    {
        if (_glyphs.TryGetValue(codePoint, out ushort cached))
            return cached;

        ushort glyph = _font.GetGlyph(codePoint);
        _glyphs[codePoint] = glyph;
        return glyph;
    }

    private protected override void Finish()
    {
        SortedSet<ushort> present;
        byte[] program;

        if (_subset)
        {
            program = TrueTypeSubsetter.Subset(_font, [.. _glyphs.Values], out present);
        }
        else
        {
            program = _font.Data.ToArray();
            present = [];
            for (ushort glyph = 0; glyph < _font.GlyphCount; glyph++)
                present.Add(glyph);
        }

        string name = _subset ? SubsetPrefix() + "+" + _font.PostScriptName : _font.PostScriptName;
        PdfName baseFont = PdfName.Get(name);

        _type0.Set(PdfNames.BaseFont, PdfValue.Name(baseFont));

        _cidFont.Set(PdfNames.Type, PdfValue.Name(PdfNames.Font));
        _cidFont.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.CIDFontType2));
        _cidFont.Set(PdfNames.BaseFont, PdfValue.Name(baseFont));
        _cidFont.Set(PdfNames.CIDSystemInfo, PdfValue.Dictionary(BuildSystemInfo()));
        _cidFont.Set(PdfNames.FontDescriptor, PdfValue.Reference(_descriptorId));
        _cidFont.Set(PdfNames.DW, PdfValue.Integer(1000));
        _cidFont.Set(PdfNames.W, PdfValue.Array(BuildWidths()));
        _cidFont.Set(PdfNames.CIDToGIDMap, PdfValue.Name(PdfNames.Identity));

        _descriptor.Set(PdfNames.Type, PdfValue.Name(PdfNames.FontDescriptor));
        _descriptor.Set(PdfNames.FontName, PdfValue.Name(baseFont));
        _descriptor.Set(PdfNames.Flags, PdfValue.Integer(BuildFlags()));
        _descriptor.Set(PdfNames.FontBBox, PdfValue.Array(PdfArray.OfNumbers(
            _font.ToPerMille((int)_font.BoundingBox.Left),
            _font.ToPerMille((int)_font.BoundingBox.Bottom),
            _font.ToPerMille((int)_font.BoundingBox.Right),
            _font.ToPerMille((int)_font.BoundingBox.Top))));
        _descriptor.Set(PdfNames.ItalicAngle, PdfValue.Number(_font.ItalicAngle));
        _descriptor.Set(PdfNames.Ascent, PdfValue.Integer(Ascent));
        _descriptor.Set(PdfNames.Descent, PdfValue.Integer(Descent));
        _descriptor.Set(PdfNames.CapHeight, PdfValue.Integer(_font.ToPerMille(_font.CapHeight)));
        _descriptor.Set(PdfNames.StemV, PdfValue.Integer(_font.Weight >= 600 ? 160 : 80));

        var fontFile = PdfStream.FromData(program);
        if (_font.IsCompactFontFormat)
        {
            fontFile.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.OpenType));
            _descriptor.Set(PdfNames.FontFile3, PdfValue.Reference(_fontFileId));
        }
        else
        {
            fontFile.Dictionary.Set(PdfNames.Length1, PdfValue.Integer(program.Length));
            _descriptor.Set(PdfNames.FontFile2, PdfValue.Reference(_fontFileId));
        }

        Document.SetObject(_fontFileId, PdfValue.Stream(fontFile));
        Document.SetObject(_toUnicodeId, PdfValue.Stream(PdfStream.FromData(BuildToUnicode())));

        // PDF/A-1 requires a subset CID font to say which CIDs its program defines, and every part
        // requires the statement to be exact once it is made. Identity mapping makes CID and glyph
        // index the same number. TrueType subsetting retains maxp.numGlyphs and empty loca
        // entries to preserve those indices: even an empty glyph is still in the program.
        if (!_font.IsCompactFontFormat)
        {
            present.Clear();
            for (int glyph = 0; glyph < _font.GlyphCount; glyph++)
                present.Add((ushort)glyph);
        }

        var cidSet = PdfStream.FromData(BuildCidSet(present));
        _descriptor.Set(PdfNames.CIDSet, PdfValue.Reference(Document.AddObject(PdfValue.Stream(cidSet))));
    }

    /// <summary>The bit string of Table 124: one bit per CID, the first byte holding CIDs 0 to 7.</summary>
    private static byte[] BuildCidSet(SortedSet<ushort> present)
    {
        if (present.Count == 0)
            return [];

        byte[] bits = new byte[(present.Max >> 3) + 1];
        foreach (ushort cid in present)
            bits[cid >> 3] |= (byte)(0x80 >> (cid & 7));

        return bits;
    }

    private static PdfDictionary BuildSystemInfo()
    {
        var info = new PdfDictionary(3);
        info.Set(PdfNames.Registry, PdfValue.String(Encoding.ASCII.GetBytes("Adobe")));
        info.Set(PdfNames.Ordering, PdfValue.String(Encoding.ASCII.GetBytes("Identity")));
        info.Set(PdfNames.Supplement, PdfValue.Integer(0));
        return info;
    }

    private int BuildFlags()
    {
        int flags = 4; // Symbolic: an Identity encoding is never the standard Latin set.

        if (_font.IsFixedPitch)
            flags |= 1;

        if (_font.ItalicAngle != 0)
            flags |= 64;

        if (_font.Weight >= 600)
            flags |= 1 << 18;

        return flags;
    }

    private PdfArray BuildWidths()
    {
        var widths = new PdfArray();
        SortedSet<ushort> glyphs = [.. _glyphs.Values];

        // Consecutive glyphs share one run: "first [w1 w2 …]".
        List<ushort> run = [];
        int runStart = -1;

        void Flush()
        {
            if (run.Count == 0)
                return;

            widths.Add(PdfValue.Integer(runStart));
            var values = new PdfArray(run.Count);
            foreach (ushort glyph in run)
                values.Add(PdfValue.Integer(_font.GetAdvanceWidthPerMille(glyph)));

            widths.Add(PdfValue.Array(values));
            run.Clear();
        }

        int previous = -2;
        foreach (ushort glyph in glyphs)
        {
            if (glyph != previous + 1)
            {
                Flush();
                runStart = glyph;
            }

            run.Add(glyph);
            previous = glyph;
        }

        Flush();
        return widths;
    }

    private byte[] BuildToUnicode()
    {
        var text = new StringBuilder(1024);
        text.Append("""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
            /CMapName /Adobe-Identity-UCS def
            /CMapType 2 def
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange

            """);

        var entries = _glyphs
            .Where(pair => pair.Value != 0)
            .GroupBy(pair => pair.Value)
            .Select(group => (Glyph: group.Key, CodePoint: group.Min(pair => pair.Key)))
            .OrderBy(entry => entry.Glyph)
            .ToList();

        for (int i = 0; i < entries.Count; i += 100)
        {
            List<(ushort Glyph, int CodePoint)> batch = entries.Skip(i).Take(100).ToList();
            text.Append(CultureInfo.InvariantCulture, $"{batch.Count} beginbfchar\n");

            foreach ((ushort glyph, int codePoint) in batch)
            {
                string utf16 = string.Concat(
                    char.ConvertFromUtf32(codePoint).Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
                text.Append(CultureInfo.InvariantCulture, $"<{glyph:X4}> <{utf16}>\n");
            }

            text.Append("endbfchar\n");
        }

        text.Append("""
            endcmap
            CMapName currentdict /CMap defineresource pop
            end
            end
            """);

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private string SubsetPrefix()
    {
        // Six letters derived from the glyph set, so two different subsets never share a name.
        uint hash = 2166136261;
        foreach (ushort glyph in _glyphs.Values.Order())
            hash = (hash ^ glyph) * 16777619;

        Span<char> prefix = stackalloc char[6];
        for (int i = 0; i < 6; i++)
        {
            prefix[i] = (char)('A' + (hash % 26));
            hash /= 26;
        }

        return new string(prefix);
    }
}
