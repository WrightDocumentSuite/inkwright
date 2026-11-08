using System.Buffers.Binary;

namespace Inkwright.Fonts.Cff;

/// <summary>
/// The charset of a Compact Font Format program: which name each glyph goes by. Names are stored as
/// identifiers into a shared table of 391 standard strings, with anything unusual appended to the
/// font's own string index (Adobe Technical Note #5176 §13).
/// </summary>
/// <remarks>
/// A subsetter needs this to resolve composite glyphs, which name their parts by code in the
/// standard encoding rather than by glyph number.
/// </remarks>
internal sealed class CffCharset
{
    /// <summary>How many strings the standard table holds, which is where the font's own begin.</summary>
    private const int StandardStringCount = 391;

    private readonly int[] _identifiers;

    private CffCharset(int[] identifiers) => _identifiers = identifiers;

    /// <summary>The string identifier for a glyph, or minus one when there is none.</summary>
    /// <param name="glyph">The glyph index.</param>
    internal int this[int glyph] => glyph >= 0 && glyph < _identifiers.Length ? _identifiers[glyph] : -1;

    /// <summary>Finds the glyph carrying a string identifier, or minus one when none does.</summary>
    /// <param name="identifier">The string identifier to look for.</param>
    internal int GlyphFor(int identifier)
    {
        for (int glyph = 0; glyph < _identifiers.Length; glyph++)
        {
            if (_identifiers[glyph] == identifier)
                return glyph;
        }

        return -1;
    }

    /// <summary>Reads a charset.</summary>
    /// <param name="font">The font it belongs to.</param>
    /// <returns>The charset, or <see langword="null"/> when it cannot be read.</returns>
    internal static CffCharset? Read(CffFont font)
    {
        int glyphs = font.GlyphCount;
        if (glyphs == 0)
            return null;

        int offset = font.TopDictionary.TryGetValue(CffFont.CharsetOperator, out double[]? operands) &&
                     operands.Length > 0
            ? (int)operands[^1]
            : 0;

        // Values below three name one of the predefined charsets. The first is the ISOAdobe one,
        // where glyph n simply carries string n, and that covers nearly every non-CID font.
        if (offset is >= 0 and <= 2)
            return Predefined(glyphs);

        ReadOnlySpan<byte> data = font.Data.Span;
        if (offset >= data.Length)
            return null;

        var identifiers = new int[glyphs];
        int format = data[offset];
        int position = offset + 1;

        // Glyph zero is always the one drawn when nothing matches, and is never listed.
        identifiers[0] = 0;
        int glyph = 1;

        switch (format)
        {
            case 0:
                while (glyph < glyphs && position + 2 <= data.Length)
                {
                    identifiers[glyph++] = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                    position += 2;
                }

                break;

            // Formats one and two list ranges: a first name, then how many follow it in sequence.
            case 1:
            case 2:
                {
                    int size = format == 1 ? 3 : 4;

                    while (glyph < glyphs && position + size <= data.Length)
                    {
                        int first = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                        int left = format == 1
                            ? data[position + 2]
                            : BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);

                        for (int i = 0; i <= left && glyph < glyphs; i++)
                            identifiers[glyph++] = first + i;

                        position += size;
                    }

                    break;
                }

            default:
                return null;
        }

        return new CffCharset(identifiers);
    }

    private static CffCharset Predefined(int glyphs)
    {
        var identifiers = new int[glyphs];
        for (int i = 0; i < glyphs; i++)
            identifiers[i] = i;

        return new CffCharset(identifiers);
    }

    /// <summary>
    /// The string identifier for a name in the standard table, or minus one when the name is not
    /// one of them.
    /// </summary>
    /// <param name="name">The glyph name.</param>
    internal static int StandardIdentifier(string name)
    {
        int index = Array.IndexOf(StandardStrings, name);
        return index >= 0 && index < StandardStringCount ? index : -1;
    }

    /// <summary>
    /// The first 229 of the standard strings, which is every name the standard encoding can reach.
    /// The rest name weights and other things a composite glyph never refers to.
    /// </summary>
    internal static readonly string[] StandardStrings =
    [
        ".notdef", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand",
        "quoteright", "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period",
        "slash", "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "colon", "semicolon", "less", "equal", "greater", "question", "at", "A", "B", "C", "D", "E",
        "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W",
        "X", "Y", "Z", "bracketleft", "backslash", "bracketright", "asciicircum", "underscore",
        "quoteleft", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p",
        "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "braceleft", "bar", "braceright",
        "asciitilde", "exclamdown", "cent", "sterling", "fraction", "yen", "florin", "section",
        "currency", "quotesingle", "quotedblleft", "guillemotleft", "guilsinglleft", "guilsinglright",
        "fi", "fl", "endash", "dagger", "daggerdbl", "periodcentered", "paragraph", "bullet",
        "quotesinglbase", "quotedblbase", "quotedblright", "guillemotright", "ellipsis", "perthousand",
        "questiondown", "grave", "acute", "circumflex", "tilde", "macron", "breve", "dotaccent",
        "dieresis", "ring", "cedilla", "hungarumlaut", "ogonek", "caron", "emdash", "AE",
        "ordfeminine", "Lslash", "Oslash", "OE", "ordmasculine", "ae", "dotlessi", "lslash", "oslash",
        "oe", "germandbls", "onesuperior", "logicalnot", "mu", "trademark", "Eth", "onehalf",
        "plusminus", "Thorn", "onequarter", "divide", "brokenbar", "degree", "thorn",
        "threequarters", "twosuperior", "registered", "minus", "eth", "multiply", "threesuperior",
        "copyright", "Aacute", "Acircumflex", "Adieresis", "Agrave", "Aring", "Atilde", "Ccedilla",
        "Eacute", "Ecircumflex", "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis",
        "Igrave", "Ntilde", "Oacute", "Ocircumflex", "Odieresis", "Ograve", "Otilde", "Scaron",
        "Uacute", "Ucircumflex", "Udieresis", "Ugrave", "Yacute", "Ydieresis", "Zcaron", "aacute",
        "acircumflex", "adieresis", "agrave", "aring", "atilde", "ccedilla", "eacute", "ecircumflex",
        "edieresis", "egrave", "iacute", "icircumflex", "idieresis", "igrave", "ntilde", "oacute",
        "ocircumflex", "odieresis", "ograve", "otilde", "scaron", "uacute", "ucircumflex",
        "udieresis", "ugrave", "yacute", "ydieresis", "zcaron",
    ];
}
