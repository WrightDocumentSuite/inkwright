using System.Buffers.Binary;

namespace Inkwright.Fonts.Cff;

/// <summary>An INDEX: the count, where each object lies, and where the whole structure ends.</summary>
/// <param name="Start">Where the INDEX begins in the font.</param>
/// <param name="End">The first byte past the INDEX.</param>
/// <param name="Offsets">Where each object begins, with one extra entry marking the end of the last.</param>
internal readonly record struct CffIndex(int Start, int End, int[] Offsets)
{
    /// <summary>How many objects the INDEX holds; zero for an INDEX that was never read.</summary>
    internal int Count => Offsets is null ? 0 : Offsets.Length - 1;

    /// <summary>The bytes of one object.</summary>
    /// <param name="data">The whole font.</param>
    /// <param name="index">Which object to take.</param>
    internal ReadOnlySpan<byte> Item(ReadOnlySpan<byte> data, int index) =>
        data[Offsets[index]..Offsets[index + 1]];
}

/// <summary>
/// A Compact Font Format program, read far enough to subset it: where the charstrings are, where
/// the private data and subroutines are, and which of the other structures the top dictionary
/// points at. Adobe Technical Note #5176 defines the layout.
/// </summary>
internal sealed class CffFont
{
    /// <summary>The operators of the top dictionary whose operand is an offset into the font.</summary>
    internal const int CharsetOperator = 15;
    internal const int EncodingOperator = 16;
    internal const int CharStringsOperator = 17;
    internal const int PrivateOperator = 18;
    internal const int RegistryOrderingSupplementOperator = 1230;
    internal const int FdArrayOperator = 1236;
    internal const int FdSelectOperator = 1237;

    private CffFont(ReadOnlyMemory<byte> data)
    {
        Data = data;
        TopDictionary = [];
    }

    /// <summary>The font program.</summary>
    internal ReadOnlyMemory<byte> Data { get; }

    /// <summary>The name INDEX, which holds the font's PostScript name.</summary>
    internal CffIndex Names { get; private init; }

    /// <summary>The top dictionary INDEX, which holds one dictionary per font in the file.</summary>
    internal CffIndex TopDictionaries { get; private init; }

    /// <summary>The string INDEX, holding every name the font uses beyond the standard ones.</summary>
    internal CffIndex Strings { get; private init; }

    /// <summary>The global subroutine INDEX, shared by every font in the file.</summary>
    internal CffIndex GlobalSubroutines { get; private init; }

    /// <summary>The charstring INDEX: one outline program per glyph, in glyph order.</summary>
    internal CffIndex CharStrings { get; private init; }

    /// <summary>The operands of the first top dictionary, keyed by operator.</summary>
    internal Dictionary<int, double[]> TopDictionary { get; private init; }

    /// <summary>Whether the font is keyed by character identifier rather than by glyph name.</summary>
    internal bool IsCidKeyed => TopDictionary.ContainsKey(RegistryOrderingSupplementOperator);

    /// <summary>How many glyphs the font defines.</summary>
    internal int GlyphCount => CharStrings.Count;

    /// <summary>Reads a font program.</summary>
    /// <param name="data">The bytes of the <c>CFF</c> table or a bare CFF file.</param>
    /// <returns>The font, or <see langword="null"/> when the bytes are not one this reader handles.</returns>
    internal static CffFont? Read(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;

        if (span.Length < 4 || span[0] != 1)
            return null;

        int position = span[2];
        if (position is < 4 or > 255 || position >= span.Length)
            return null;

        if (!TryReadIndex(span, ref position, out CffIndex names) ||
            !TryReadIndex(span, ref position, out CffIndex tops) ||
            !TryReadIndex(span, ref position, out CffIndex strings) ||
            !TryReadIndex(span, ref position, out CffIndex global))
        {
            return null;
        }

        if (tops.Count < 1)
            return null;

        Dictionary<int, double[]> top = ReadDictionary(tops.Item(span, 0));

        if (!top.TryGetValue(CharStringsOperator, out double[]? charStringsOffset))
            return null;

        int charStrings = (int)charStringsOffset[^1];
        if (charStrings <= 0 || charStrings >= span.Length)
            return null;

        int cursor = charStrings;
        if (!TryReadIndex(span, ref cursor, out CffIndex charStringIndex))
            return null;

        return new CffFont(data)
        {
            Names = names,
            TopDictionaries = tops,
            Strings = strings,
            GlobalSubroutines = global,
            CharStrings = charStringIndex,
            TopDictionary = top,
        };
    }

    /// <summary>Reads an INDEX at a position, leaving the position just past it.</summary>
    internal static bool TryReadIndex(ReadOnlySpan<byte> data, ref int position, out CffIndex index)
    {
        index = default;

        if (position + 2 > data.Length)
            return false;

        int start = position;
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
        position += 2;

        if (count == 0)
        {
            index = new CffIndex(start, position, [0]);
            return true;
        }

        if (position >= data.Length)
            return false;

        int offSize = data[position++];
        if (offSize is < 1 or > 4)
            return false;

        if (position + ((count + 1) * offSize) > data.Length)
            return false;

        int[] offsets = new int[count + 1];
        int table = position;
        position += (count + 1) * offSize;

        // Offsets count from the byte before the object data, so the first is always one.
        int origin = position - 1;

        for (int i = 0; i <= count; i++)
        {
            int value = 0;
            for (int b = 0; b < offSize; b++)
                value = (value << 8) | data[table + (i * offSize) + b];

            offsets[i] = origin + value;
            if (offsets[i] < 0 || offsets[i] > data.Length)
                return false;
        }

        index = new CffIndex(start, offsets[count], offsets);
        position = offsets[count];
        return true;
    }

    /// <summary>
    /// Reads a dictionary: operands first, then the operator they belong to. A two-byte operator is
    /// escaped with twelve, and is recorded here as twelve hundred plus the second byte.
    /// </summary>
    internal static Dictionary<int, double[]> ReadDictionary(ReadOnlySpan<byte> data)
    {
        var dictionary = new Dictionary<int, double[]>();
        List<double> operands = [];

        for (int i = 0; i < data.Length;)
        {
            int b = data[i];

            if (b <= 21)
            {
                int op = b;
                i++;

                if (b == 12 && i < data.Length)
                    op = 1200 + data[i++];

                dictionary[op] = [.. operands];
                operands.Clear();
                continue;
            }

            if (!TryReadOperand(data, ref i, out double value))
                break;

            if (operands.Count < 48)
                operands.Add(value);
        }

        return dictionary;
    }

    private static bool TryReadOperand(ReadOnlySpan<byte> data, ref int i, out double value)
    {
        value = 0;
        int b = data[i];

        switch (b)
        {
            case >= 32 and <= 246:
                value = b - 139;
                i++;
                return true;

            case >= 247 and <= 250 when i + 1 < data.Length:
                value = ((b - 247) * 256) + data[i + 1] + 108;
                i += 2;
                return true;

            case >= 251 and <= 254 when i + 1 < data.Length:
                value = (-(b - 251) * 256) - data[i + 1] - 108;
                i += 2;
                return true;

            case 28 when i + 2 < data.Length:
                value = BinaryPrimitives.ReadInt16BigEndian(data[(i + 1)..]);
                i += 3;
                return true;

            case 29 when i + 4 < data.Length:
                value = BinaryPrimitives.ReadInt32BigEndian(data[(i + 1)..]);
                i += 5;
                return true;

            case 30:
                return TryReadReal(data, ref i, out value);

            default:
                i++;
                return false;
        }
    }

    /// <summary>Reads a real number, which is written as a string of nibbles rather than binary.</summary>
    private static bool TryReadReal(ReadOnlySpan<byte> data, ref int i, out double value)
    {
        value = 0;
        var text = new System.Text.StringBuilder(16);
        i++;

        while (i < data.Length)
        {
            int packed = data[i++];

            for (int half = 0; half < 2; half++)
            {
                int nibble = half == 0 ? packed >> 4 : packed & 15;

                switch (nibble)
                {
                    case <= 9:
                        text.Append((char)('0' + nibble));
                        break;
                    case 10:
                        text.Append('.');
                        break;
                    case 11:
                        text.Append('E');
                        break;
                    case 12:
                        text.Append("E-");
                        break;
                    case 14:
                        text.Append('-');
                        break;
                    case 15:
                        return double.TryParse(
                            text.ToString(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out value);
                    default:
                        break;
                }
            }
        }

        return false;
    }
}
