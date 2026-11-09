using System.Collections.Frozen;
using System.Globalization;
using Inkwright.Cos;

namespace Inkwright.Fonts;

/// <summary>
/// A simple font's encoding: the map from single-byte codes to glyph names, and from glyph names to
/// the characters they stand for. Includes the four predefined encodings of ISO 32000-1 Annex D
/// and the built-in Symbol encoding.
/// </summary>
public sealed class FontEncoding
{
    private static readonly Lazy<Tables> Data = new(Parse, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string?[] _names;

    private FontEncoding(string name, string?[] names)
    {
        Name = name;
        _names = names;
    }

    /// <summary>The encoding's name, as it appears in a font dictionary.</summary>
    public string Name { get; }

    /// <summary>The encoding Adobe's Type 1 fonts use by default.</summary>
    public static FontEncoding Standard => Data.Value.Standard;

    /// <summary>The encoding matching Windows code page 1252.</summary>
    public static FontEncoding WinAnsi => Data.Value.WinAnsi;

    /// <summary>The encoding matching the classic Mac OS Roman character set.</summary>
    public static FontEncoding MacRoman => Data.Value.MacRoman;

    /// <summary>The encoding PDF text strings use outside of content streams.</summary>
    public static FontEncoding PdfDoc => Data.Value.PdfDoc;

    /// <summary>The built-in encoding of the standard Symbol font.</summary>
    public static FontEncoding Symbol => Data.Value.Symbol;

    /// <summary>The glyph name a code maps to, or <see langword="null"/> when the code is unassigned.</summary>
    /// <param name="code">The single-byte character code.</param>
    public string? GetGlyphName(int code) => (uint)code < 256 ? _names[code] : null;

    /// <summary>The character a code stands for, or <see langword="null"/> when it maps to nothing.</summary>
    /// <param name="code">The single-byte character code.</param>
    public string? GetText(int code) => GetGlyphName(code) is { } glyph ? GlyphToText(glyph) : null;

    /// <summary>The code that produces a glyph, or -1 when the encoding cannot reach it.</summary>
    /// <param name="glyphName">The glyph name.</param>
    public int GetCode(string glyphName)
    {
        for (int code = 0; code < 256; code++)
        {
            if (string.Equals(_names[code], glyphName, StringComparison.Ordinal))
                return code;
        }

        return -1;
    }

    /// <summary>Applies a <c>Differences</c> array on top of this encoding.</summary>
    /// <param name="differences">Alternating codes and glyph names, as §9.6.6.1 defines them.</param>
    public FontEncoding WithDifferences(PdfArray? differences)
    {
        if (differences is null || differences.Count == 0)
            return this;

        string?[] names = (string?[])_names.Clone();
        int code = 0;

        for (int i = 0; i < differences.Count; i++)
        {
            PdfValue entry = differences.Get(i);
            if (entry.IsNumber)
                code = (int)entry.AsInteger;
            else if (entry.AsName is { } glyph && (uint)code < 256)
                names[code++] = glyph.Value;
        }

        return new FontEncoding(Name + "+Differences", names);
    }

    /// <summary>Looks up a predefined encoding by the name a font dictionary uses.</summary>
    /// <param name="name">The encoding name, such as <c>WinAnsiEncoding</c>.</param>
    public static FontEncoding? FromName(string? name) => name switch
    {
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        "StandardEncoding" => Standard,
        "PDFDocEncoding" => PdfDoc,
        _ => null,
    };

    /// <summary>
    /// The text a glyph name stands for, understanding the standard Latin and Symbol names and the
    /// <c>uniXXXX</c>, <c>uXXXX</c> and <c>gNN</c> conventions producers rely on.
    /// </summary>
    /// <param name="glyphName">The glyph name.</param>
    public static string? GlyphToText(string glyphName)
    {
        if (string.IsNullOrEmpty(glyphName))
            return null;

        if (Data.Value.GlyphToUnicode.TryGetValue(glyphName, out int known))
            return char.ConvertFromUtf32(known);

        // A suffix after a period is a stylistic variant of the base glyph: "a.sc" is still an "a".
        int period = glyphName.IndexOf('.', StringComparison.Ordinal);
        if (period > 0)
            return GlyphToText(glyphName[..period]);

        if (glyphName.StartsWith("uni", StringComparison.Ordinal) && glyphName.Length >= 7)
            return FromHex(glyphName.AsSpan(3, 4));

        if (glyphName[0] == 'u' && glyphName.Length is >= 5 and <= 7)
            return FromHex(glyphName.AsSpan(1));

        // Ligature names join their parts with underscores.
        if (glyphName.Contains('_', StringComparison.Ordinal))
        {
            string joined = string.Concat(glyphName.Split('_').Select(part => GlyphToText(part) ?? string.Empty));
            return joined.Length > 0 ? joined : null;
        }

        return null;
    }

    private static string? FromHex(ReadOnlySpan<char> hex) =>
        int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) && value is > 0 and <= 0x10FFFF
            ? char.ConvertFromUtf32(value)
            : null;

    private static Tables Parse()
    {
        string?[] standard = new string?[256];
        string?[] mac = new string?[256];
        string?[] win = new string?[256];
        string?[] pdf = new string?[256];
        string?[] symbol = new string?[256];
        var unicode = new Dictionary<string, int>(StringComparer.Ordinal);
        Span<Range> fields = stackalloc Range[6];

        foreach (ReadOnlySpan<char> line in LatinGlyphData.Table.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty)
                continue;

            if (line.Split(fields, ' ', StringSplitOptions.RemoveEmptyEntries) != 6)
                continue;

            string name = line[fields[0]].ToString();
            Assign(standard, line[fields[1]], name);
            Assign(mac, line[fields[2]], name);
            Assign(win, line[fields[3]], name);
            Assign(pdf, line[fields[4]], name);

            ReadOnlySpan<char> code = line[fields[5]];
            if (code[0] != '-' && int.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
                unicode[name] = value;
        }

        // WinAnsiEncoding leaves no code unpainted: undefined positions render as a bullet, and
        // codes 0xA0 and 0xAD are the space and hyphen in disguise (§D.2 notes 1 and 5).
        win[0xA0] = "space";
        win[0xAD] = "hyphen";

        foreach (ReadOnlySpan<char> line in SymbolGlyphData.Table.AsSpan().EnumerateLines())
        {
            if (line.Split(fields, ' ', StringSplitOptions.RemoveEmptyEntries) != 3)
                continue;

            string name = line[fields[0]].ToString();
            int code = int.Parse(line[fields[1]], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            symbol[code] = name;
            // Some glyphs have two Unicode equivalents. Keep the existing Latin mapping (mu,
            // for example); otherwise use the first entry in Adobe's Symbol mapping table.
            unicode.TryAdd(name, int.Parse(line[fields[2]], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        return new Tables(
            new FontEncoding("StandardEncoding", standard),
            new FontEncoding("WinAnsiEncoding", win),
            new FontEncoding("MacRomanEncoding", mac),
            new FontEncoding("PDFDocEncoding", pdf),
            new FontEncoding("Symbol", symbol),
            unicode.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static void Assign(string?[] table, ReadOnlySpan<char> code, string name)
    {
        if (code[0] == '-' || !int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            return;

        if ((uint)value < 256)
            table[value] = name;
    }

    private sealed record Tables(
        FontEncoding Standard,
        FontEncoding WinAnsi,
        FontEncoding MacRoman,
        FontEncoding PdfDoc,
        FontEncoding Symbol,
        FrozenDictionary<string, int> GlyphToUnicode);
}
