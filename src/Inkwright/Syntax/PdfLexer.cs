using System.Buffers.Text;
using System.Globalization;
using Inkwright.Diagnostics;

namespace Inkwright.Syntax;

/// <summary>The kinds of lexical token that make up PDF syntax (ISO 32000-1 §7.2 and §7.3).</summary>
internal enum PdfTokenType : byte
{
    EndOfInput,
    Integer,
    Real,
    LiteralString,
    HexString,
    Name,
    ArrayStart,
    ArrayEnd,
    DictionaryStart,
    DictionaryEnd,
    BraceOpen,
    BraceClose,

    /// <summary>A bare keyword or content-stream operator: <c>obj</c>, <c>R</c>, <c>true</c>, <c>Tj</c>, …</summary>
    Keyword,
}

/// <summary>
/// A forward-only tokeniser over the bytes of a file or content stream. It never allocates for
/// numbers, keywords or plain names; strings and escaped names materialise a byte array only when
/// the caller asks for their value.
/// </summary>
internal ref struct PdfLexer
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly bool _strict;
    private int _position;

    internal PdfLexer(ReadOnlySpan<byte> data, int position = 0, bool strict = false)
    {
        _data = data;
        _strict = strict;
        _position = position;
        TokenType = PdfTokenType.EndOfInput;
        TokenSpan = default;
        TokenStart = position;
    }

    /// <summary>The byte offset the lexer will read from next.</summary>
    internal int Position
    {
        readonly get => _position;
        set => _position = value;
    }

    /// <summary>The whole input, so callers can slice stream payloads out of it.</summary>
    internal readonly ReadOnlySpan<byte> Data => _data;

    /// <summary>The kind of the token most recently read.</summary>
    internal PdfTokenType TokenType { get; private set; }

    /// <summary>
    /// The raw bytes of the token: the digits of a number, the characters of a keyword, the name
    /// after its solidus, or the body of a string between its delimiters.
    /// </summary>
    internal ReadOnlySpan<byte> TokenSpan { get; private set; }

    /// <summary>The offset the current token starts at.</summary>
    internal int TokenStart { get; private set; }

    /// <summary>Whether the input is exhausted.</summary>
    internal readonly bool IsAtEnd => _position >= _data.Length;

    /// <summary>Advances past white space and comments.</summary>
    internal void SkipWhitespace()
    {
        while (_position < _data.Length)
        {
            byte b = _data[_position];

            if (PdfCharacters.IsWhitespace(b))
            {
                _position++;
                continue;
            }

            if (b == (byte)'%')
            {
                while (_position < _data.Length && !PdfCharacters.IsEndOfLine(_data[_position]))
                    _position++;

                continue;
            }

            return;
        }
    }

    /// <summary>Reads the next token.</summary>
    /// <returns>The kind of token read, or <see cref="PdfTokenType.EndOfInput"/>.</returns>
    internal PdfTokenType Read()
    {
        while (true)
        {
            SkipWhitespace();
            TokenStart = _position;

            if (_position >= _data.Length)
            {
                TokenType = PdfTokenType.EndOfInput;
                TokenSpan = default;
                return TokenType;
            }

            byte b = _data[_position];
            switch (b)
            {
                case (byte)'[':
                    _position++;
                    return Set(PdfTokenType.ArrayStart, 1);

                case (byte)']':
                    _position++;
                    return Set(PdfTokenType.ArrayEnd, 1);

                case (byte)'{':
                    _position++;
                    return Set(PdfTokenType.BraceOpen, 1);

                case (byte)'}':
                    _position++;
                    return Set(PdfTokenType.BraceClose, 1);

                case (byte)'/':
                    return ReadName();

                case (byte)'(':
                    return ReadLiteralString();

                case (byte)'<':
                    if (_position + 1 < _data.Length && _data[_position + 1] == (byte)'<')
                    {
                        _position += 2;
                        return Set(PdfTokenType.DictionaryStart, 2);
                    }

                    return ReadHexString();

                case (byte)'>':
                    if (_position + 1 < _data.Length && _data[_position + 1] == (byte)'>')
                    {
                        _position += 2;
                        return Set(PdfTokenType.DictionaryEnd, 2);
                    }

                    if (_strict)
                    {
                        throw new PdfFormatException(
                            "A protected object value contains the stray closing delimiter '>'.");
                    }

                    // A stray '>' or ')' is noise from a damaged file; step over it and retry.
                    _position++;
                    continue;

                case (byte)')':
                    if (_strict)
                    {
                        throw new PdfFormatException(
                            "A protected object value contains the stray closing delimiter ')'.");
                    }

                    _position++;
                    continue;
            }

            return PdfCharacters.NumberStart.Contains(b) ? ReadNumber() : ReadKeyword();
        }
    }

    /// <summary>
    /// Reports the kind of the next token without consuming it. The token fields are clobbered, so
    /// callers must call <see cref="Read"/> again before reading a value.
    /// </summary>
    internal PdfTokenType PeekType()
    {
        int saved = _position;
        PdfTokenType type = Read();
        _position = saved;
        return type;
    }

    /// <summary>The value of the current numeric token.</summary>
    internal double NumberValue { get; private set; }

    /// <summary>The integer value of the current numeric token, truncating a real.</summary>
    internal readonly long AsInteger => TokenType == PdfTokenType.Integer
        ? (long)NumberValue
        : (long)Math.Round(NumberValue, MidpointRounding.ToZero);

    /// <summary>Whether the current keyword token equals the given ASCII text.</summary>
    /// <param name="keyword">The keyword to compare with.</param>
    internal readonly bool KeywordIs(ReadOnlySpan<byte> keyword) => TokenSpan.SequenceEqual(keyword);

    /// <summary>Materialises the value of a name token, resolving <c>#xx</c> escapes.</summary>
    internal readonly byte[] NameValue()
    {
        ReadOnlySpan<byte> raw = TokenSpan;
        if (raw.IndexOf((byte)'#') < 0)
            return raw.ToArray();

        Span<byte> buffer = raw.Length <= 128 ? stackalloc byte[raw.Length] : new byte[raw.Length];
        int length = 0;

        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'#' && i + 2 < raw.Length)
            {
                int high = PdfCharacters.HexValue(raw[i + 1]);
                int low = PdfCharacters.HexValue(raw[i + 2]);
                if (high >= 0 && low >= 0)
                {
                    buffer[length++] = (byte)((high << 4) | low);
                    i += 2;
                    continue;
                }
            }

            buffer[length++] = raw[i];
        }

        return buffer[..length].ToArray();
    }

    /// <summary>Materialises the value of a string token, resolving escapes or hex digits.</summary>
    internal readonly byte[] StringValue() =>
        TokenType == PdfTokenType.HexString ? DecodeHexString(TokenSpan) : DecodeLiteralString(TokenSpan);

    /// <summary>Reads to the end of the current line and returns the bytes before the terminator.</summary>
    internal ReadOnlySpan<byte> ReadLine()
    {
        int start = _position;
        while (_position < _data.Length && !PdfCharacters.IsEndOfLine(_data[_position]))
            _position++;

        ReadOnlySpan<byte> line = _data[start.._position];
        SkipEndOfLine();
        return line;
    }

    /// <summary>Steps over a CR, LF or CRLF sequence if one is next.</summary>
    internal void SkipEndOfLine()
    {
        if (_position < _data.Length && _data[_position] == 0x0D)
            _position++;

        if (_position < _data.Length && _data[_position] == 0x0A)
            _position++;
    }

    private PdfTokenType Set(PdfTokenType type, int length)
    {
        TokenType = type;
        TokenSpan = _data.Slice(TokenStart, length);
        return type;
    }

    private PdfTokenType ReadName()
    {
        _position++; // the solidus
        int start = _position;

        while (_position < _data.Length && PdfCharacters.IsRegular(_data[_position]))
            _position++;

        TokenType = PdfTokenType.Name;
        TokenSpan = _data[start.._position];

        if (_strict)
            ValidateStrictName(TokenSpan);

        return TokenType;
    }

    private static void ValidateStrictName(ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != (byte)'#')
                continue;

            if (i + 2 >= name.Length ||
                PdfCharacters.HexValue(name[i + 1]) < 0 ||
                PdfCharacters.HexValue(name[i + 2]) < 0)
            {
                throw new PdfFormatException(
                    "A protected name contains # without exactly two hexadecimal digits.");
            }

            int value = (PdfCharacters.HexValue(name[i + 1]) << 4) |
                        PdfCharacters.HexValue(name[i + 2]);
            if (value == 0)
            {
                throw new PdfFormatException(
                    "A protected name shall not contain an encoded null byte.");
            }

            i += 2;
        }
    }

    private PdfTokenType ReadNumber()
    {
        int start = _position;
        bool isReal = false;

        if (_strict)
        {
            // ISO 32000-2 7.2.3 makes every consecutive run of regular characters one token.
            // Scanning the whole run is important: accepting only the numeric prefix of `12abc`
            // would silently turn one malformed token into two different objects.
            while (_position < _data.Length && PdfCharacters.IsRegular(_data[_position]))
                _position++;

            TokenSpan = _data[start.._position];
            if (!TryParseStrictNumber(TokenSpan, out double value, out isReal))
            {
                throw new PdfFormatException(
                    "A protected numeric token is not a complete PDF integer or real number.");
            }

            TokenType = isReal ? PdfTokenType.Real : PdfTokenType.Integer;
            NumberValue = value;

            if (!isReal && (NumberValue > long.MaxValue || NumberValue < long.MinValue))
                TokenType = PdfTokenType.Real;

            return TokenType;
        }

        while (_position < _data.Length)
        {
            byte b = _data[_position];
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                _position++;
            }
            else if (b is (byte)'.' or (byte)'-' or (byte)'+' or (byte)'e' or (byte)'E')
            {
                isReal |= b == (byte)'.';
                _position++;
            }
            else
            {
                break;
            }
        }

        TokenSpan = _data[start.._position];
        TokenType = isReal ? PdfTokenType.Real : PdfTokenType.Integer;
        NumberValue = ParseNumber(TokenSpan);

        if (!isReal && (NumberValue > long.MaxValue || NumberValue < long.MinValue))
            TokenType = PdfTokenType.Real;

        return TokenType;
    }

    private static bool TryParseStrictNumber(
        ReadOnlySpan<byte> text,
        out double value,
        out bool isReal)
    {
        value = 0;
        isReal = false;
        if (text.IsEmpty)
            return false;

        int position = text[0] is (byte)'+' or (byte)'-' ? 1 : 0;
        int digits = 0;

        while (position < text.Length && text[position] is >= (byte)'0' and <= (byte)'9')
        {
            position++;
            digits++;
        }

        if (position < text.Length && text[position] == (byte)'.')
        {
            isReal = true;
            position++;

            while (position < text.Length && text[position] is >= (byte)'0' and <= (byte)'9')
            {
                position++;
                digits++;
            }
        }

        if (digits == 0 || position != text.Length)
            return false;

        return Utf8Parser.TryParse(text, out value, out int consumed) &&
               consumed == text.Length &&
               double.IsFinite(value);
    }

    private PdfTokenType ReadKeyword()
    {
        int start = _position;

        while (_position < _data.Length && PdfCharacters.IsRegular(_data[_position]))
            _position++;

        if (_position == start)
            _position++; // never stall on an unexpected delimiter

        TokenType = PdfTokenType.Keyword;
        TokenSpan = _data[start.._position];
        return TokenType;
    }

    private PdfTokenType ReadLiteralString()
    {
        _position++; // the opening parenthesis
        int start = _position;
        int depth = 1;

        while (_position < _data.Length)
        {
            byte b = _data[_position];

            if (b == (byte)'\\')
            {
                _position += 2;
                continue;
            }

            if (b == (byte)'(')
                depth++;
            else if (b == (byte)')' && --depth == 0)
                break;

            _position++;
        }

        int end = Math.Min(_position, _data.Length);
        TokenSpan = _data[start..end];
        TokenType = PdfTokenType.LiteralString;

        if (_strict && (_position >= _data.Length || depth != 0))
        {
            throw new PdfFormatException(
                "A protected literal string is not closed with a right parenthesis.");
        }

        if (_position < _data.Length)
            _position++; // the closing parenthesis

        return TokenType;
    }

    private PdfTokenType ReadHexString()
    {
        _position++; // the opening angle bracket
        int start = _position;

        int end = _data[_position..].IndexOf((byte)'>');
        _position = end < 0 ? _data.Length : _position + end;

        TokenSpan = _data[start.._position];
        TokenType = PdfTokenType.HexString;

        if (_strict && end < 0)
        {
            throw new PdfFormatException(
                "A protected hexadecimal string is not closed with >.");
        }

        if (_strict)
        {
            foreach (byte value in TokenSpan)
            {
                if (PdfCharacters.HexValue(value) < 0 && !PdfCharacters.IsWhitespace(value))
                {
                    throw new PdfFormatException(
                        "A protected hexadecimal string contains a non-hexadecimal byte.");
                }
            }
        }

        if (_position < _data.Length)
            _position++; // the closing angle bracket

        return TokenType;
    }

    private static double ParseNumber(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
            return 0;

        // "--5", "-.002-" and "1.0e" all appear in the wild; parse the leading valid prefix.
        if (Utf8Parser.TryParse(text, out double value, out int consumed) && consumed > 0)
            return double.IsFinite(value) ? value : 0;

        Span<char> chars = stackalloc char[Math.Min(text.Length, 64)];
        int length = 0;
        bool seenDigit = false;
        bool seenDot = false;

        for (int i = 0; i < text.Length && length < chars.Length; i++)
        {
            byte b = text[i];
            if (b is (byte)'-' or (byte)'+')
            {
                if (length == 0)
                    chars[length++] = (char)b;

                continue;
            }

            if (b == (byte)'.')
            {
                if (seenDot)
                    break;

                seenDot = true;
                chars[length++] = '.';
                continue;
            }

            if (b is >= (byte)'0' and <= (byte)'9')
            {
                seenDigit = true;
                chars[length++] = (char)b;
                continue;
            }

            break;
        }

        return seenDigit && double.TryParse(chars[..length], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0;
    }

    private static byte[] DecodeHexString(ReadOnlySpan<byte> body)
    {
        Span<byte> buffer = body.Length <= 512 ? stackalloc byte[(body.Length / 2) + 1] : new byte[(body.Length / 2) + 1];
        int length = 0;
        int high = -1;

        foreach (byte b in body)
        {
            int digit = PdfCharacters.HexValue(b);
            if (digit < 0)
                continue;

            if (high < 0)
            {
                high = digit;
            }
            else
            {
                buffer[length++] = (byte)((high << 4) | digit);
                high = -1;
            }
        }

        if (high >= 0)
            buffer[length++] = (byte)(high << 4);

        return buffer[..length].ToArray();
    }

    private static byte[] DecodeLiteralString(ReadOnlySpan<byte> body)
    {
        if (body.IndexOf((byte)'\\') < 0)
            return body.ToArray();

        Span<byte> buffer = body.Length <= 512 ? stackalloc byte[body.Length] : new byte[body.Length];
        int length = 0;

        for (int i = 0; i < body.Length; i++)
        {
            byte b = body[i];
            if (b != (byte)'\\')
            {
                buffer[length++] = b;
                continue;
            }

            if (++i >= body.Length)
                break;

            byte escape = body[i];
            switch (escape)
            {
                case (byte)'n': buffer[length++] = 0x0A; break;
                case (byte)'r': buffer[length++] = 0x0D; break;
                case (byte)'t': buffer[length++] = 0x09; break;
                case (byte)'b': buffer[length++] = 0x08; break;
                case (byte)'f': buffer[length++] = 0x0C; break;
                case (byte)'(': buffer[length++] = (byte)'('; break;
                case (byte)')': buffer[length++] = (byte)')'; break;
                case (byte)'\\': buffer[length++] = (byte)'\\'; break;

                case 0x0D:
                    // A backslash before a line break continues the string; swallow CRLF as one.
                    if (i + 1 < body.Length && body[i + 1] == 0x0A)
                        i++;
                    break;

                case 0x0A:
                    break;

                case >= (byte)'0' and <= (byte)'7':
                    int value = escape - (byte)'0';
                    for (int digit = 0; digit < 2 && i + 1 < body.Length; digit++)
                    {
                        byte next = body[i + 1];
                        if (next is < (byte)'0' or > (byte)'7')
                            break;

                        value = (value << 3) | (next - (byte)'0');
                        i++;
                    }

                    buffer[length++] = (byte)value;
                    break;

                default:
                    buffer[length++] = escape;
                    break;
            }
        }

        return buffer[..length].ToArray();
    }
}
