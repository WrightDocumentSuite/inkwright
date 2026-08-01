using System.Collections.Frozen;
using System.Globalization;

namespace Inkwright.Fonts;

/// <summary>
/// One of the fourteen fonts every conforming reader carries (ISO 32000-1 §9.6.2.2). Their metrics
/// are built in, so a document that sticks to them embeds nothing and stays tiny.
/// </summary>
public sealed class Standard14Font
{
    private static readonly Lazy<FrozenDictionary<string, Standard14Font>> Fonts =
        new(Parse, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = "Helvetica",
        ["Arial,Bold"] = "Helvetica-Bold",
        ["Arial,Italic"] = "Helvetica-Oblique",
        ["Arial,BoldItalic"] = "Helvetica-BoldOblique",
        ["Arial-Bold"] = "Helvetica-Bold",
        ["Arial-Italic"] = "Helvetica-Oblique",
        ["ArialMT"] = "Helvetica",
        ["Arial-BoldMT"] = "Helvetica-Bold",
        ["Arial-ItalicMT"] = "Helvetica-Oblique",
        ["Arial-BoldItalicMT"] = "Helvetica-BoldOblique",
        ["Helvetica-Italic"] = "Helvetica-Oblique",
        ["Helvetica-BoldItalic"] = "Helvetica-BoldOblique",
        ["CourierNew"] = "Courier",
        ["CourierNewPSMT"] = "Courier",
        ["CourierNew,Bold"] = "Courier-Bold",
        ["CourierNew,Italic"] = "Courier-Oblique",
        ["CourierNew,BoldItalic"] = "Courier-BoldOblique",
        ["Courier-Italic"] = "Courier-Oblique",
        ["Courier-BoldItalic"] = "Courier-BoldOblique",
        ["TimesNewRoman"] = "Times-Roman",
        ["TimesNewRomanPSMT"] = "Times-Roman",
        ["TimesNewRomanPS-BoldMT"] = "Times-Bold",
        ["TimesNewRomanPS-ItalicMT"] = "Times-Italic",
        ["TimesNewRomanPS-BoldItalicMT"] = "Times-BoldItalic",
        ["TimesNewRoman,Bold"] = "Times-Bold",
        ["TimesNewRoman,Italic"] = "Times-Italic",
        ["TimesNewRoman,BoldItalic"] = "Times-BoldItalic",
        ["Times"] = "Times-Roman",
        ["Times-Regular"] = "Times-Roman",
        ["Symbol,Bold"] = "Symbol",
        ["ZapfDingbats,Bold"] = "ZapfDingbats",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenDictionary<string, int> _widthsByGlyph;
    private readonly int[] _widthsByBuiltInCode;

    private Standard14Font(
        string name,
        int ascender,
        int descender,
        int capHeight,
        double italicAngle,
        PdfRectangle boundingBox,
        FrozenDictionary<string, int> widthsByGlyph,
        int[] widthsByBuiltInCode)
    {
        Name = name;
        Ascender = ascender;
        Descender = descender;
        CapHeight = capHeight;
        ItalicAngle = italicAngle;
        BoundingBox = boundingBox;
        _widthsByGlyph = widthsByGlyph;
        _widthsByBuiltInCode = widthsByBuiltInCode;
    }

    /// <summary>The PostScript name, such as <c>Helvetica-Bold</c>.</summary>
    public string Name { get; }

    /// <summary>The ascender in units of 1/1000 em.</summary>
    public int Ascender { get; }

    /// <summary>The descender in units of 1/1000 em, negative below the baseline.</summary>
    public int Descender { get; }

    /// <summary>The cap height in units of 1/1000 em.</summary>
    public int CapHeight { get; }

    /// <summary>The angle the font slants at, negative for a forward slant.</summary>
    public double ItalicAngle { get; }

    /// <summary>The bounding box of every glyph, in units of 1/1000 em.</summary>
    public PdfRectangle BoundingBox { get; }

    /// <summary>Whether the font has its own encoding rather than the standard Latin one.</summary>
    public bool IsSymbolic => Name is "Symbol" or "ZapfDingbats";

    /// <summary>The encoding the font uses when a dictionary names none.</summary>
    public FontEncoding DefaultEncoding => IsSymbolic ? FontEncoding.Standard : FontEncoding.Standard;

    /// <summary>Helvetica.</summary>
    public static Standard14Font Helvetica => Get("Helvetica");

    /// <summary>Helvetica Bold.</summary>
    public static Standard14Font HelveticaBold => Get("Helvetica-Bold");

    /// <summary>Helvetica Oblique.</summary>
    public static Standard14Font HelveticaOblique => Get("Helvetica-Oblique");

    /// <summary>Helvetica Bold Oblique.</summary>
    public static Standard14Font HelveticaBoldOblique => Get("Helvetica-BoldOblique");

    /// <summary>Times Roman.</summary>
    public static Standard14Font TimesRoman => Get("Times-Roman");

    /// <summary>Times Bold.</summary>
    public static Standard14Font TimesBold => Get("Times-Bold");

    /// <summary>Times Italic.</summary>
    public static Standard14Font TimesItalic => Get("Times-Italic");

    /// <summary>Times Bold Italic.</summary>
    public static Standard14Font TimesBoldItalic => Get("Times-BoldItalic");

    /// <summary>Courier.</summary>
    public static Standard14Font Courier => Get("Courier");

    /// <summary>Courier Bold.</summary>
    public static Standard14Font CourierBold => Get("Courier-Bold");

    /// <summary>Symbol.</summary>
    public static Standard14Font Symbol => Get("Symbol");

    /// <summary>ZapfDingbats.</summary>
    public static Standard14Font ZapfDingbats => Get("ZapfDingbats");

    /// <summary>The names of all fourteen fonts.</summary>
    public static IReadOnlyCollection<string> Names => Fonts.Value.Keys;

    /// <summary>Finds a standard font by name, understanding the usual substitute names.</summary>
    /// <param name="name">The base font name, with or without a subset prefix.</param>
    /// <returns>The font, or <see langword="null"/> when the name is not one of the fourteen.</returns>
    public static Standard14Font? Find(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        // A subset prefix looks like "ABCDEF+".
        if (name.Length > 7 && name[6] == '+')
            name = name[7..];

        if (Fonts.Value.TryGetValue(name, out Standard14Font? font))
            return font;

        return Aliases.TryGetValue(name, out string? alias) ? Fonts.Value[alias] : null;
    }

    /// <summary>The width of a glyph in units of 1/1000 em.</summary>
    /// <param name="glyphName">The glyph name.</param>
    /// <returns>The width, or zero when the font has no such glyph.</returns>
    public int GetWidth(string glyphName) =>
        _widthsByGlyph.TryGetValue(glyphName, out int width) ? width : 0;

    /// <summary>The width of the glyph the font's own encoding puts at a code.</summary>
    /// <param name="code">The character code in the font's built-in encoding.</param>
    public int GetWidthByBuiltInCode(int code) =>
        (uint)code < 256 ? _widthsByBuiltInCode[code] : 0;

    /// <summary>The width of a string laid out in this font.</summary>
    /// <param name="text">The text to measure.</param>
    /// <param name="encoding">The encoding mapping characters to glyphs.</param>
    /// <param name="fontSize">The size in points.</param>
    public double Measure(string text, FontEncoding encoding, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);

        double total = 0;
        foreach (char c in text)
        {
            int code = c < 256 && encoding.GetGlyphName(c) is not null ? c : encoding.GetCode(c.ToString());
            string? glyph = code >= 0 ? encoding.GetGlyphName(code) : null;
            total += glyph is null ? GetWidth("space") : GetWidth(glyph);
        }

        return total * fontSize / 1000;
    }

    private static Standard14Font Get(string name) => Fonts.Value[name];

    private static FrozenDictionary<string, Standard14Font> Parse()
    {
        var fonts = new Dictionary<string, Standard14Font>(StringComparer.OrdinalIgnoreCase);

        foreach (ReadOnlySpan<char> line in Standard14Data.Table.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty)
                continue;

            var widths = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] byCode = new int[256];

            MemoryExtensions.SpanSplitEnumerator<char> parts = line.Split(' ');
            string name = Next(line, ref parts);
            int ascender = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);
            int descender = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);
            int capHeight = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);
            int italic = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);
            double x1 = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);
            double y1 = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);
            double x2 = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);
            double y2 = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);

            while (true)
            {
                string glyph = Next(line, ref parts);
                if (glyph.Length == 0)
                    break;

                int width = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);
                int code = int.Parse(Next(line, ref parts), CultureInfo.InvariantCulture);

                widths[glyph] = width;
                if ((uint)code < 256)
                    byCode[code] = width;
            }

            fonts[name] = new Standard14Font(
                name, ascender, descender, capHeight, italic,
                new PdfRectangle(x1, y1, x2, y2),
                widths.ToFrozenDictionary(StringComparer.Ordinal),
                byCode);
        }

        return fonts.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static string Next(ReadOnlySpan<char> line, ref MemoryExtensions.SpanSplitEnumerator<char> parts)
    {
        while (parts.MoveNext())
        {
            ReadOnlySpan<char> value = line[parts.Current];
            if (!value.IsEmpty)
                return value.ToString();
        }

        return string.Empty;
    }
}
