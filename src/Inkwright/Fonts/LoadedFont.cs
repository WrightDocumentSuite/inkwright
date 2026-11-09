using Inkwright.Cos;

namespace Inkwright.Fonts;

/// <summary>
/// A font read out of an existing document, reduced to what reading text needs: how a string splits
/// into codes, how wide each code is, and what text it stands for. The three sources of truth are
/// tried in the order the specification gives them, so a <c>ToUnicode</c> map always wins.
/// </summary>
public sealed class LoadedFont
{
    private readonly PdfDictionary _dictionary;
    private readonly CMap _encodingMap;
    private readonly CMap? _toUnicode;
    private readonly FontEncoding? _simpleEncoding;
    private readonly Standard14Font? _standard;
    private readonly Dictionary<int, double> _widths = [];
    private readonly double _defaultWidth;
    private readonly Dictionary<uint, string?> _textCache = [];
    private Lazy<CMap?>? _collectionText;

    private LoadedFont(
        PdfDictionary dictionary,
        bool composite,
        CMap encodingMap,
        CMap? toUnicode,
        FontEncoding? simpleEncoding,
        Standard14Font? standard,
        double defaultWidth)
    {
        _dictionary = dictionary;
        IsComposite = composite;
        _encodingMap = encodingMap;
        _toUnicode = toUnicode;
        _simpleEncoding = simpleEncoding;
        _standard = standard;
        _defaultWidth = defaultWidth;
    }

    /// <summary>Whether the font addresses glyphs through multi-byte codes.</summary>
    public bool IsComposite { get; }

    /// <summary>The base font name, subset prefix included.</summary>
    public string BaseFont => _dictionary.GetName(PdfNames.BaseFont)?.Value ?? string.Empty;

    /// <summary>Whether the font declares itself symbolic in its descriptor flags.</summary>
    public bool IsSymbolic { get; private init; }

    /// <summary>Reads a font dictionary.</summary>
    /// <param name="dictionary">The font dictionary.</param>
    public static LoadedFont Read(PdfDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);

        PdfName? subtype = dictionary.GetName(PdfNames.Subtype);
        CMap? toUnicode = dictionary.GetStream(PdfNames.ToUnicode) is { } stream
            ? CMap.Parse(stream.DecodedSpan())
            : null;

        return subtype == PdfNames.Type0
            ? ReadComposite(dictionary, toUnicode)
            : ReadSimple(dictionary, toUnicode);
    }

    /// <summary>Splits an encoded string into the codes the font addresses glyphs by.</summary>
    /// <param name="bytes">The encoded string from a text-showing operator.</param>
    public IEnumerable<(uint Code, int Length)> ReadCodes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        int index = 0;
        while (index < bytes.Length)
        {
            uint code = _encodingMap.ReadCode(bytes, index, out int consumed);
            yield return (code, consumed);
            index += consumed;
        }
    }

    /// <summary>
    /// Reads one code from an encoded string. This is the allocation-free form of
    /// <see cref="ReadCodes"/>, for the extraction loop that runs once per glyph in a document.
    /// </summary>
    /// <param name="bytes">The encoded string.</param>
    /// <param name="index">Where to start reading.</param>
    /// <param name="code">Receives the code.</param>
    /// <returns>How many bytes the code took.</returns>
    public int ReadCode(ReadOnlySpan<byte> bytes, int index, out uint code)
    {
        code = _encodingMap.ReadCode(bytes, index, out int consumed);
        return consumed;
    }

    /// <summary>The advance width of a code in units of 1/1000 em.</summary>
    /// <param name="code">The character code.</param>
    public double GetWidth(uint code)
    {
        int key = IsComposite ? _encodingMap.GetCid(code) : (int)code;

        if (_widths.TryGetValue(key, out double width))
            return width;

        if (_standard is not null && !IsComposite)
        {
            string? glyph = _simpleEncoding?.GetGlyphName((int)code);
            int metric = glyph is not null ? _standard.GetWidth(glyph) : _standard.GetWidthByBuiltInCode((int)code);
            if (metric > 0)
                return metric;
        }

        return _defaultWidth;
    }

    /// <summary>The text a code stands for, or <see langword="null"/> when nothing maps it.</summary>
    /// <param name="code">The character code.</param>
    public string? GetText(uint code)
    {
        if (_textCache.TryGetValue(code, out string? cached))
            return cached;

        string? text = _toUnicode?.GetText(code);

        if (text is null && IsComposite && _collectionText?.Value is { } collection)
        {
            // ISO 32000-1 9.10.2, third method: the code picks a CID, and the character
            // collection's own UCS2 map says what that CID reads as.
            text = collection.GetText((uint)_encodingMap.GetCid(code));
        }

        if (text is null && !IsComposite && _simpleEncoding is not null)
        {
            string? glyph = _simpleEncoding.GetGlyphName((int)code);
            text = glyph is not null ? FontEncoding.GlyphToText(glyph) : null;

            // A symbolic font with no usable encoding still tends to be ASCII underneath.
            if (text is null && code is >= 32 and < 127)
                text = ((char)code).ToString();
        }

        _textCache[code] = text;
        return text;
    }

    private static LoadedFont ReadSimple(PdfDictionary dictionary, CMap? toUnicode)
    {
        PdfName? subtype = dictionary.GetName(PdfNames.Subtype);
        Standard14Font? standard = Standard14Font.Find(dictionary.GetName(PdfNames.BaseFont)?.Value);
        PdfDictionary? descriptor = dictionary.GetDictionary(PdfNames.FontDescriptor);
        int flags = (int)(descriptor?.GetInteger(PdfNames.Flags) ?? 0);
        bool symbolic = (flags & 4) != 0 && (flags & 32) == 0;

        // Standard fonts can omit FontDescriptor entirely, so its Symbolic flag cannot decide
        // whether to use Symbol's built-in encoding.
        FontEncoding baseEncoding = standard?.DefaultEncoding ?? FontEncoding.Standard;

        PdfValue encodingValue = dictionary.Get(PdfNames.Encoding);
        if (encodingValue.AsName is { } encodingName)
        {
            baseEncoding = FontEncoding.FromName(encodingName.Value) ?? baseEncoding;
        }
        else if (encodingValue.AsDictionary is { } encodingDictionary)
        {
            baseEncoding = FontEncoding.FromName(encodingDictionary.GetName(PdfNames.BaseEncoding)?.Value) ?? baseEncoding;
            baseEncoding = baseEncoding.WithDifferences(encodingDictionary.GetArray(PdfNames.Differences));
        }

        double missing = descriptor?.GetNumber(PdfNames.MissingWidth) ?? 0;
        var font = new LoadedFont(dictionary, false, CMap.SingleByte, toUnicode, baseEncoding, standard, missing)
        {
            IsSymbolic = symbolic,
        };

        int first = (int)dictionary.GetInteger(PdfNames.FirstChar);
        if (dictionary.GetArray(PdfNames.Widths) is { } widths)
        {
            for (int i = 0; i < widths.Count; i++)
            {
                double width = widths.GetNumber(i, double.NaN);
                if (!double.IsNaN(width))
                    font._widths[first + i] = width;
            }
        }

        if (subtype == PdfNames.Type3 && dictionary.GetArray(PdfNames.FontMatrix) is { Count: >= 4 } matrix)
        {
            // Type 3 widths are in glyph space, which the font matrix maps to text space.
            double scale = matrix.GetNumber(0) * 1000;
            foreach (int key in font._widths.Keys.ToArray())
                font._widths[key] *= scale;
        }

        return font;
    }

    private static LoadedFont ReadComposite(PdfDictionary dictionary, CMap? toUnicode)
    {
        CMap encoding = CMap.IdentityTwoByte;
        PdfValue encodingValue = dictionary.Get(PdfNames.Encoding);

        if (encodingValue.AsName is { } name)
            encoding = CMap.Predefined(name.Value) ?? CMap.IdentityTwoByte;
        else if (encodingValue.AsStream is { } stream)
            encoding = CMap.Parse(stream.DecodedSpan());

        PdfDictionary? descendant = dictionary.GetArray(PdfNames.DescendantFonts)?.GetDictionary(0);
        double defaultWidth = descendant?.GetNumber(PdfNames.DW, 1000) ?? 1000;

        var font = new LoadedFont(dictionary, true, encoding, toUnicode, null, null, defaultWidth)
        {
            _collectionText = CollectionText(descendant),
        };

        if (descendant?.GetArray(PdfNames.W) is { } widths)
            ReadCidWidths(widths, font._widths);

        return font;
    }

    /// <summary>
    /// The map from the character collection's CIDs to text, named after the registry and ordering
    /// the descendant font declares. It is resolved on the first code a <c>ToUnicode</c> map fails
    /// to cover, because most documents carry one and never need this.
    /// </summary>
    private static Lazy<CMap?>? CollectionText(PdfDictionary? descendant)
    {
        if (descendant?.GetDictionary(PdfNames.CIDSystemInfo) is not { } info)
            return null;

        if (info.GetTextString(PdfNames.Registry) is not { Length: > 0 } registry ||
            info.GetTextString(PdfNames.Ordering) is not { Length: > 0 } ordering)
        {
            return null;
        }

        return new Lazy<CMap?>(() =>
            CMapProvider.Load($"{registry}-{ordering}-UCS2") ??
            (string.Equals(registry, "Adobe", StringComparison.Ordinal) &&
             string.Equals(ordering, "KR", StringComparison.Ordinal)
                ? CMapProvider.Load("Adobe-Korea1-UCS2")
                : null));
    }

    private static void ReadCidWidths(PdfArray widths, Dictionary<int, double> target)
    {
        int index = 0;
        while (index < widths.Count)
        {
            int first = (int)widths.GetInteger(index++);
            if (index >= widths.Count)
                break;

            PdfValue next = widths.Get(index++);

            if (next.AsArray is { } list)
            {
                for (int i = 0; i < list.Count; i++)
                    target[first + i] = list.GetNumber(i);

                continue;
            }

            if (index >= widths.Count)
                break;

            int last = (int)next.AsInteger;
            double width = widths.GetNumber(index++);

            if (last < first || last - first > 65535)
                continue;

            for (int cid = first; cid <= last; cid++)
                target[cid] = width;
        }
    }
}
