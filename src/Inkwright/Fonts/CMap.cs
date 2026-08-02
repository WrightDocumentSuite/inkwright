using System.Globalization;
using Inkwright.Cos;
using Inkwright.Syntax;

namespace Inkwright.Fonts;

/// <summary>
/// A character map: how many bytes make up a code, what CID each code selects, and what text each
/// code stands for. The same parser reads both the <c>ToUnicode</c> maps that make text searchable
/// and the embedded encoding maps that composite fonts use.
/// </summary>
public sealed class CMap
{
    private readonly List<(int Length, uint Low, uint High)> _codespaces = [];
    private readonly Dictionary<uint, string> _text = [];
    private readonly Dictionary<uint, int> _cids = [];
    private readonly List<(uint Low, uint High, int FirstCid)> _cidRanges = [];
    private List<string?>? _includedNames;

    /// <summary>The identity map every <c>Identity-H</c> font uses: two-byte codes equal to CIDs.</summary>
    public static CMap IdentityTwoByte { get; } = CreateIdentity(vertical: false);

    /// <summary>The identity map <c>Identity-V</c> names, which differs only in writing mode.</summary>
    public static CMap IdentityTwoByteVertical { get; } = CreateIdentity(vertical: true);

    /// <summary>The single-byte map a simple font implies.</summary>
    public static CMap SingleByte { get; } = CreateSingleByte();

    /// <summary>Whether the map only ever produces one-byte codes.</summary>
    public bool IsSingleByte => _codespaces.Count > 0 && _codespaces.TrueForAll(space => space.Length == 1);

    /// <summary>Whether the map carries any text mappings.</summary>
    public bool HasText => _text.Count > 0;

    /// <summary>Whether the map selects vertical writing mode, as its <c>WMode</c> entry says.</summary>
    public bool IsVertical { get; private set; }

    /// <summary>Every operand supplied to <c>usecmap</c>; null records a non-name or missing operand.</summary>
    internal IReadOnlyList<string?> IncludedNames => _includedNames ?? [];

    /// <summary>The final inherited name, which is the one folded into this parsed map.</summary>
    private string? IncludedName => _includedNames is { Count: > 0 } ? _includedNames[^1] : null;

    /// <summary>
    /// Reads the next code from a string, using the codespace ranges to decide how many bytes it
    /// spans. Unmatched bytes fall back to one byte at a time, which is what viewers do.
    /// </summary>
    /// <param name="bytes">The encoded string.</param>
    /// <param name="index">Where to start reading.</param>
    /// <param name="consumed">Receives how many bytes the code took.</param>
    public uint ReadCode(ReadOnlySpan<byte> bytes, int index, out int consumed)
    {
        uint value = 0;

        for (int length = 1; length <= 4 && index + length <= bytes.Length; length++)
        {
            value = (value << 8) | bytes[index + length - 1];

            foreach ((int spaceLength, uint low, uint high) in _codespaces)
            {
                if (spaceLength == length && value >= low && value <= high)
                {
                    consumed = length;
                    return value;
                }
            }
        }

        consumed = 1;
        return index < bytes.Length ? bytes[index] : 0u;
    }

    /// <summary>The CID a code selects, or the code itself when the map does not say.</summary>
    /// <param name="code">The character code.</param>
    public int GetCid(uint code)
    {
        if (_cids.TryGetValue(code, out int cid))
            return cid;

        foreach ((uint low, uint high, int first) in _cidRanges)
        {
            if (code >= low && code <= high)
                return first + (int)(code - low);
        }

        return (int)code;
    }

    /// <summary>The text a code stands for, or <see langword="null"/> when the map does not say.</summary>
    /// <param name="code">The character code.</param>
    public string? GetText(uint code) => _text.GetValueOrDefault(code);

    /// <summary>Parses a CMap program.</summary>
    /// <param name="data">The CMap source, as found in a <c>ToUnicode</c> or encoding stream.</param>
    public static CMap Parse(ReadOnlySpan<byte> data)
    {
        var map = new CMap();
        var operands = new List<PdfValue>(8);
        var parser = new PdfObjectParser(data);

        while (true)
        {
            PdfTokenType token = parser.ReadToken();
            if (token == PdfTokenType.EndOfInput)
                break;

            if (token != PdfTokenType.Keyword)
            {
                parser.Position = parser.TokenStart;
                operands.Add(parser.ParseValue());
                if (operands.Count > 64)
                    operands.RemoveRange(0, 32);

                continue;
            }

            ReadOnlySpan<byte> keyword = parser.TokenSpan;

            if (keyword.SequenceEqual("begincodespacerange"u8))
                map.ReadCodespaces(ref parser);
            else if (keyword.SequenceEqual("beginbfchar"u8))
                map.ReadBfChars(ref parser);
            else if (keyword.SequenceEqual("beginbfrange"u8))
                map.ReadBfRanges(ref parser);
            else if (keyword.SequenceEqual("begincidchar"u8))
                map.ReadCidChars(ref parser);
            else if (keyword.SequenceEqual("begincidrange"u8))
                map.ReadCidRanges(ref parser);
            else if (keyword.SequenceEqual("usecmap"u8))
                (map._includedNames ??= []).Add(operands.Count > 0 ? operands[^1].AsName?.Value : null);
            else if (keyword.SequenceEqual("def"u8) && operands.Count >= 2 && operands[^2].AsName?.Value == "WMode")
                map.IsVertical = operands[^1].AsInteger == 1;

            operands.Clear();
        }

        // The inherited map is folded in only once the whole program has been read, so that what the
        // map states itself always wins over what it borrows.
        if (map.IncludedName is not null && Predefined(map.IncludedName) is { } parent)
            map.IncludeMap(parent);

        if (map._codespaces.Count == 0)
            map._codespaces.Add((map._text.Keys.Any(code => code > 0xFF) ? 2 : 1, 0, 0xFFFF));

        return map;
    }

    /// <summary>
    /// Looks up one of the predefined CMaps of ISO 32000-1 Table 118 by name. The real tables come
    /// from whatever <see cref="CMapProvider"/> has registered; without one, the CJK names still
    /// resolve to a two-byte identity map, which gets the codespace right even though the CIDs it
    /// reports are only the codes themselves.
    /// </summary>
    /// <param name="name">The CMap name from a font's <c>Encoding</c> entry.</param>
    /// <returns>The map, or <see langword="null"/> when the name is not one the library knows.</returns>
    public static CMap? Predefined(string? name)
    {
        if (name is null)
            return null;

        if (name == "Identity-H")
            return IdentityTwoByte;

        if (name == "Identity-V")
            return IdentityTwoByteVertical;

        if (CMapProvider.Load(name) is { } loaded)
            return loaded;

        return name.Contains("UCS2", StringComparison.Ordinal) || name.StartsWith("Uni", StringComparison.Ordinal)
            ? IdentityTwoByte
            : null;
    }

    /// <summary>Creates an empty map for a reader to fill in.</summary>
    internal static CMap CreateEmpty() => new();

    /// <summary>Records that codes of a given byte length fall in a range.</summary>
    internal void AddCodespace(int length, uint low, uint high) => _codespaces.Add((length, low, high));

    /// <summary>Records the CID a single code selects.</summary>
    internal void MapCid(uint code, int cid) => _cids[code] = cid;

    /// <summary>Records the CIDs a contiguous run of codes selects.</summary>
    internal void MapCidRange(uint low, uint high, int firstCid) => _cidRanges.Add((low, high, firstCid));

    /// <summary>Records the text a code stands for, given as the raw bytes a CMap file stores.</summary>
    internal void MapTextBytes(uint code, ReadOnlySpan<byte> bytes)
    {
        if (Decode(bytes) is { } text)
            _text[code] = text;
    }

    /// <summary>Marks the writing mode a reader found in the map's header.</summary>
    internal void SetVertical(bool vertical) => IsVertical = vertical;

    /// <summary>
    /// Folds another map into this one, the way the <c>usecmap</c> operator asks: the borrowing map
    /// keeps every mapping it declares itself, and inherits the codespace ranges only when it
    /// declares none of its own.
    /// </summary>
    internal void IncludeMap(CMap other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (_codespaces.Count == 0)
            _codespaces.AddRange(other._codespaces);

        _cidRanges.AddRange(other._cidRanges);

        foreach ((uint code, string text) in other._text)
            _text.TryAdd(code, text);

        foreach ((uint code, int cid) in other._cids)
            _cids.TryAdd(code, cid);
    }

    private void ReadCodespaces(ref PdfObjectParser parser)
    {
        while (true)
        {
            PdfValue low = ReadOperand(ref parser, "endcodespacerange"u8, out bool stop);
            if (stop)
                return;

            PdfValue high = ReadOperand(ref parser, "endcodespacerange"u8, out stop);
            if (stop)
                return;

            ReadOnlySpan<byte> lowBytes = low.AsStringBytes;
            if (lowBytes.Length is < 1 or > 4)
                continue;

            _codespaces.Add((lowBytes.Length, ToCode(lowBytes), ToCode(high.AsStringBytes)));
        }
    }

    private void ReadBfChars(ref PdfObjectParser parser)
    {
        while (true)
        {
            PdfValue code = ReadOperand(ref parser, "endbfchar"u8, out bool stop);
            if (stop)
                return;

            PdfValue target = ReadOperand(ref parser, "endbfchar"u8, out stop);
            if (stop)
                return;

            string? text = ToText(target);
            if (text is not null)
                _text[ToCode(code.AsStringBytes)] = text;
        }
    }

    private void ReadBfRanges(ref PdfObjectParser parser)
    {
        while (true)
        {
            PdfValue low = ReadOperand(ref parser, "endbfrange"u8, out bool stop);
            if (stop)
                return;

            PdfValue high = ReadOperand(ref parser, "endbfrange"u8, out stop);
            if (stop)
                return;

            PdfValue target = ReadOperand(ref parser, "endbfrange"u8, out stop);
            if (stop)
                return;

            uint first = ToCode(low.AsStringBytes);
            uint last = ToCode(high.AsStringBytes);
            if (last < first || last - first > 0xFFFF)
                continue;

            if (target.AsArray is { } array)
            {
                for (uint code = first; code <= last && code - first < (uint)array.Count; code++)
                {
                    if (ToText(array.Get((int)(code - first))) is { } item)
                        _text[code] = item;
                }

                continue;
            }

            if (ToText(target) is not { Length: > 0 } start)
                continue;

            for (uint code = first; code <= last; code++)
            {
                // A range's target increments in its last code unit.
                char[] chars = start.ToCharArray();
                chars[^1] = (char)(chars[^1] + (code - first));
                _text[code] = new string(chars);
            }
        }
    }

    private void ReadCidChars(ref PdfObjectParser parser)
    {
        while (true)
        {
            PdfValue code = ReadOperand(ref parser, "endcidchar"u8, out bool stop);
            if (stop)
                return;

            PdfValue cid = ReadOperand(ref parser, "endcidchar"u8, out stop);
            if (stop)
                return;

            _cids[ToCode(code.AsStringBytes)] = (int)cid.AsInteger;
        }
    }

    private void ReadCidRanges(ref PdfObjectParser parser)
    {
        while (true)
        {
            PdfValue low = ReadOperand(ref parser, "endcidrange"u8, out bool stop);
            if (stop)
                return;

            PdfValue high = ReadOperand(ref parser, "endcidrange"u8, out stop);
            if (stop)
                return;

            PdfValue cid = ReadOperand(ref parser, "endcidrange"u8, out stop);
            if (stop)
                return;

            _cidRanges.Add((ToCode(low.AsStringBytes), ToCode(high.AsStringBytes), (int)cid.AsInteger));
        }
    }

    private static PdfValue ReadOperand(ref PdfObjectParser parser, ReadOnlySpan<byte> terminator, out bool stop)
    {
        PdfTokenType token = parser.ReadToken();

        if (token == PdfTokenType.EndOfInput || (token == PdfTokenType.Keyword && parser.TokenSpan.SequenceEqual(terminator)))
        {
            stop = true;
            return PdfValue.Null;
        }

        stop = false;
        parser.Position = parser.TokenStart;
        return parser.ParseValue();
    }

    private static uint ToCode(ReadOnlySpan<byte> bytes)
    {
        uint value = 0;
        foreach (byte b in bytes[..Math.Min(4, bytes.Length)])
            value = (value << 8) | b;

        return value;
    }

    private static string? ToText(PdfValue value)
    {
        if (value.Kind == PdfValueKind.String)
            return Decode(value.AsStringBytes);

        if (value.AsName is { } name)
            return FontEncoding.GlyphToText(name.Value);

        return null;
    }

    private static string? Decode(ReadOnlySpan<byte> bytes) => bytes.Length switch
    {
        0 => null,
        1 => ((char)bytes[0]).ToString(CultureInfo.InvariantCulture),
        _ => PdfTextEncoding.DecodeUtf16Be(bytes),
    };

    private static CMap CreateIdentity(bool vertical)
    {
        var map = new CMap { IsVertical = vertical };
        map._codespaces.Add((2, 0, 0xFFFF));
        return map;
    }

    private static CMap CreateSingleByte()
    {
        var map = new CMap();
        map._codespaces.Add((1, 0, 0xFF));
        return map;
    }
}
