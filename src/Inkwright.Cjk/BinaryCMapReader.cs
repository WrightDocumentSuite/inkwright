using Inkwright.Fonts;

namespace Inkwright.Cjk;

/// <summary>
/// Reads the compact binary form of a CMap. Adobe publishes the predefined maps as PostScript
/// programs of a few hundred kilobytes each; this is the packed encoding the Mozilla pdf.js project
/// compiles them into, where every number is a variable-length group of seven-bit digits and every
/// run of codes is stored as a delta from the run before it. The whole set fits in about a megabyte.
/// </summary>
/// <remarks>
/// Ported from <c>src/core/binary_cmap.js</c> of pdf.js, which Mozilla licenses under Apache 2.0.
/// </remarks>
internal ref struct BinaryCMapReader
{
    /// <summary>The widest number the format admits, in bytes.</summary>
    private const int MaxNumberSize = 16;

    /// <summary>How many seven-bit digits it takes to spell out <see cref="MaxNumberSize"/> bytes.</summary>
    private const int MaxEncodedNumberSize = 19;

    /// <summary>An upper bound on a single run, matching what pdf.js refuses to expand.</summary>
    private const uint MaxRange = 0xFFFFFF;

    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    private BinaryCMapReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>Reads a binary CMap into a map object.</summary>
    /// <param name="data">The <c>.bcmap</c> contents.</param>
    /// <param name="map">The map to fill in.</param>
    /// <returns>The name of the map this one inherits from, or <see langword="null"/> when it stands alone.</returns>
    /// <exception cref="InvalidDataException">The data ends early or names a record type that does not exist.</exception>
    public static string? Read(ReadOnlySpan<byte> data, CMap map)
    {
        var reader = new BinaryCMapReader(data);
        return reader.Process(map);
    }

    private string? Process(CMap map)
    {
        int header = ReadByte();
        if (header < 0)
            throw new InvalidDataException("The binary CMap is empty.");

        map.SetVertical((header & 1) != 0);

        string? useCMap = null;
        Span<byte> start = stackalloc byte[MaxNumberSize];
        Span<byte> end = stackalloc byte[MaxNumberSize];
        Span<byte> character = stackalloc byte[MaxNumberSize];
        Span<byte> charCode = stackalloc byte[MaxNumberSize];
        Span<byte> scratch = stackalloc byte[MaxNumberSize];

        int b;
        while ((b = ReadByte()) >= 0)
        {
            int type = b >> 5;

            if (type == 7)
            {
                // A metadata record: either a comment to skip or the name of the inherited map.
                string text = ReadString();
                if ((b & 0x1F) == 1)
                    useCMap = text;

                continue;
            }

            bool sequence = (b & 0x10) != 0;
            int size = b & 15;
            if (size + 1 > MaxNumberSize)
                throw new InvalidDataException($"The binary CMap declares a {size + 1}-byte number.");

            int count = (int)ReadNumber();

            switch (type)
            {
                case 0:
                    ReadCodespaces(map, count, size, start, end);
                    break;
                case 1:
                    SkipNotDefRanges(count, size, start, end);
                    break;
                case 2:
                    ReadCidChars(map, count, size, sequence, character, scratch);
                    break;
                case 3:
                    ReadCidRanges(map, count, size, sequence, start, end);
                    break;
                case 4:
                    ReadTextChars(map, count, size, sequence, character, charCode, scratch);
                    break;
                case 5:
                    ReadTextRanges(map, count, size, sequence, start, end, charCode);
                    break;
                default:
                    throw new InvalidDataException($"The binary CMap holds a record of unknown type {type}.");
            }
        }

        return useCMap;
    }

    private void ReadCodespaces(CMap map, int count, int size, scoped Span<byte> start, scoped Span<byte> end)
    {
        ReadHex(start, size);
        ReadHexNumber(end, size);
        AddHex(end, start, size);
        map.AddCodespace(size + 1, ToInt(start, size), ToInt(end, size));

        for (int i = 1; i < count; i++)
        {
            IncHex(end, size);
            ReadHexNumber(start, size);
            AddHex(start, end, size);
            ReadHexNumber(end, size);
            AddHex(end, start, size);
            map.AddCodespace(size + 1, ToInt(start, size), ToInt(end, size));
        }
    }

    private void SkipNotDefRanges(int count, int size, scoped Span<byte> start, scoped Span<byte> end)
    {
        // Codes a range leaves undefined fall back to the font's notdef glyph, which extraction has
        // nothing to say about; the records still have to be stepped over to stay in sync.
        ReadHex(start, size);
        ReadHexNumber(end, size);
        AddHex(end, start, size);
        ReadNumber();

        for (int i = 1; i < count; i++)
        {
            IncHex(end, size);
            ReadHexNumber(start, size);
            AddHex(start, end, size);
            ReadHexNumber(end, size);
            AddHex(end, start, size);
            ReadNumber();
        }
    }

    private void ReadCidChars(CMap map, int count, int size, bool sequence, scoped Span<byte> character, scoped Span<byte> scratch)
    {
        ReadHex(character, size);
        long code = ReadNumber();
        map.MapCid(ToInt(character, size), (int)code);

        for (int i = 1; i < count; i++)
        {
            IncHex(character, size);
            if (!sequence)
            {
                ReadHexNumber(scratch, size);
                AddHex(character, scratch, size);
            }

            code += ReadSigned() + 1;
            map.MapCid(ToInt(character, size), (int)code);
        }
    }

    private void ReadCidRanges(CMap map, int count, int size, bool sequence, scoped Span<byte> start, scoped Span<byte> end)
    {
        ReadHex(start, size);
        ReadHexNumber(end, size);
        AddHex(end, start, size);
        map.MapCidRange(ToInt(start, size), ToInt(end, size), (int)ReadNumber());

        for (int i = 1; i < count; i++)
        {
            IncHex(end, size);
            if (sequence)
                end[..(size + 1)].CopyTo(start);
            else
            {
                ReadHexNumber(start, size);
                AddHex(start, end, size);
            }

            ReadHexNumber(end, size);
            AddHex(end, start, size);
            map.MapCidRange(ToInt(start, size), ToInt(end, size), (int)ReadNumber());
        }
    }

    private void ReadTextChars(
        CMap map, int count, int size, bool sequence, scoped Span<byte> character, scoped Span<byte> charCode, scoped Span<byte> scratch)
    {
        // The key of a text mapping is always a two-byte value; only the target varies in width.
        const int KeySize = 1;

        ReadHex(character, KeySize);
        ReadHex(charCode, size);
        map.MapTextBytes(ToInt(character, KeySize), charCode[..(size + 1)]);

        for (int i = 1; i < count; i++)
        {
            IncHex(character, KeySize);
            if (!sequence)
            {
                ReadHexNumber(scratch, KeySize);
                AddHex(character, scratch, KeySize);
            }

            IncHex(charCode, size);
            ReadHexSigned(scratch, size);
            AddHex(charCode, scratch, size);
            map.MapTextBytes(ToInt(character, KeySize), charCode[..(size + 1)]);
        }
    }

    private void ReadTextRanges(
        CMap map, int count, int size, bool sequence, scoped Span<byte> start, scoped Span<byte> end, scoped Span<byte> charCode)
    {
        const int KeySize = 1;

        ReadHex(start, KeySize);
        ReadHexNumber(end, KeySize);
        AddHex(end, start, KeySize);
        ReadHex(charCode, size);
        MapTextRange(map, ToInt(start, KeySize), ToInt(end, KeySize), charCode[..(size + 1)]);

        for (int i = 1; i < count; i++)
        {
            IncHex(end, KeySize);
            if (sequence)
                end[..(KeySize + 1)].CopyTo(start);
            else
            {
                ReadHexNumber(start, KeySize);
                AddHex(start, end, KeySize);
            }

            ReadHexNumber(end, KeySize);
            AddHex(end, start, KeySize);
            ReadHex(charCode, size);
            MapTextRange(map, ToInt(start, KeySize), ToInt(end, KeySize), charCode[..(size + 1)]);
        }
    }

    /// <summary>
    /// Maps a run of codes onto consecutive text values. The target is a byte string that carries
    /// once per code, incrementing in its last byte and borrowing into the one before it.
    /// </summary>
    private static void MapTextRange(CMap map, uint low, uint high, Span<byte> target)
    {
        if (high < low || high - low > MaxRange || target.Length == 0)
            return;

        int last = target.Length - 1;

        for (uint code = low; ; code++)
        {
            map.MapTextBytes(code, target);
            if (code == high)
                return;

            if (target[last] == 0xFF && last >= 1)
            {
                target[last - 1]++;
                target[last] = 0;
            }
            else
                target[last]++;
        }
    }

    private int ReadByte() => _position < _data.Length ? _data[_position++] : -1;

    private uint ReadNumber()
    {
        uint value = 0;
        bool last;

        do
        {
            int b = ReadByte();
            if (b < 0)
                throw new InvalidDataException("The binary CMap ends in the middle of a number.");

            last = (b & 0x80) == 0;
            value = (value << 7) | (uint)(b & 0x7F);
        }
        while (!last);

        return value;
    }

    private int ReadSigned()
    {
        uint value = ReadNumber();
        return (value & 1) != 0 ? ~(int)(value >> 1) : (int)(value >> 1);
    }

    private void ReadHex(scoped Span<byte> number, int size)
    {
        for (int i = 0; i <= size; i++)
        {
            int b = ReadByte();
            if (b < 0)
                throw new InvalidDataException("The binary CMap ends in the middle of a code.");

            number[i] = (byte)b;
        }
    }

    /// <summary>Reads a variable-length number into a big-endian byte array of a fixed width.</summary>
    private void ReadHexNumber(scoped Span<byte> number, int size)
    {
        Span<byte> digits = stackalloc byte[MaxEncodedNumberSize];
        int count = 0;
        bool last;

        do
        {
            int b = ReadByte();
            if (b < 0)
                throw new InvalidDataException("The binary CMap ends in the middle of a number.");

            if (count == MaxEncodedNumberSize)
                throw new InvalidDataException("The binary CMap holds a number too wide for the format.");

            last = (b & 0x80) == 0;
            digits[count++] = (byte)(b & 0x7F);
        }
        while (!last);

        int buffer = 0;
        int bits = 0;

        for (int i = size; i >= 0; i--)
        {
            while (bits < 8)
            {
                // Digits run out before the width does whenever the number is small; the remaining
                // high bytes are simply zero.
                buffer |= (--count >= 0 ? digits[count] : 0) << bits;
                bits += 7;
            }

            number[i] = (byte)buffer;
            buffer >>= 8;
            bits -= 8;
        }
    }

    private void ReadHexSigned(scoped Span<byte> number, int size)
    {
        ReadHexNumber(number, size);

        int sign = (number[size] & 1) != 0 ? 0xFF : 0;
        int carry = 0;

        for (int i = 0; i <= size; i++)
        {
            carry = ((carry & 1) << 8) | number[i];
            number[i] = (byte)((carry >> 1) ^ sign);
        }
    }

    private string ReadString()
    {
        int length = (int)ReadNumber();
        if (length is < 0 or > 1024)
            throw new InvalidDataException("The binary CMap holds a string of implausible length.");

        if (length == 0)
            return string.Empty;

        Span<char> chars = length <= 128 ? stackalloc char[length] : new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = (char)ReadNumber();

        return new string(chars);
    }

    private static uint ToInt(ReadOnlySpan<byte> number, int size)
    {
        uint value = 0;
        for (int i = 0; i <= size; i++)
            value = (value << 8) | number[i];

        return value;
    }

    private static void AddHex(Span<byte> target, ReadOnlySpan<byte> addend, int size)
    {
        int carry = 0;
        for (int i = size; i >= 0; i--)
        {
            carry += target[i] + addend[i];
            target[i] = (byte)carry;
            carry >>= 8;
        }
    }

    private static void IncHex(Span<byte> target, int size)
    {
        int carry = 1;
        for (int i = size; i >= 0 && carry > 0; i--)
        {
            carry += target[i];
            target[i] = (byte)carry;
            carry >>= 8;
        }
    }
}
