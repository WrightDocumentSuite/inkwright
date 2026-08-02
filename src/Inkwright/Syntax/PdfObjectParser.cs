using Inkwright.Cos;
using Inkwright.Diagnostics;

namespace Inkwright.Syntax;

/// <summary>
/// Supplies the payload of a stream object. Parsing a stream needs more context than the token
/// sequence carries: the declared length may itself be an indirect reference, and damaged files
/// need the payload located by searching for <c>endstream</c>.
/// </summary>
internal interface IPdfStreamDecodePolicyProvider
{
    /// <summary>The decode policy attached to streams parsed from this reader, when any.</summary>
    IPdfStreamDecodePolicy? DecodePolicy { get; }
}

internal interface IPdfStreamPayloadReader : IPdfStreamDecodePolicyProvider
{

    /// <summary>Locates the payload of a stream whose <c>stream</c> keyword has just been read.</summary>
    /// <param name="dictionary">The stream dictionary parsed immediately before the keyword.</param>
    /// <param name="dataStart">The offset of the first payload byte.</param>
    /// <param name="endPosition">Receives the offset just past <c>endstream</c>.</param>
    /// <returns>The payload bytes, decrypted if the document is encrypted.</returns>
    ReadOnlyMemory<byte> ReadPayload(PdfDictionary dictionary, int dataStart, out int endPosition);
}

/// <summary>
/// Builds <see cref="PdfValue"/> graphs from a token stream. Compatibility reads degrade malformed
/// constructs to the null object; a protected reader instead fails closed. A depth guard keeps
/// hostile nesting from exhausting the stack in either mode.
/// </summary>
internal ref struct PdfObjectParser
{
    private const int MaxDepth = 128;

    private PdfLexer _lexer;
    private readonly IPdfResolver? _resolver;
    private readonly IPdfStreamPayloadReader? _streams;
    private readonly IPdfStreamDecodePolicy? _decodePolicy;
    private int _depth;
    private bool _mayReadIndirectStream;

    internal PdfObjectParser(
        ReadOnlySpan<byte> data,
        int position = 0,
        IPdfResolver? resolver = null,
        IPdfStreamPayloadReader? streams = null)
    {
        _resolver = resolver;
        _streams = streams;
        _decodePolicy = streams?.DecodePolicy ??
                        (resolver as IPdfStreamDecodePolicyProvider)?.DecodePolicy;
        _lexer = new PdfLexer(data, position, strict: _decodePolicy is not null);
    }

    /// <summary>The offset the parser will read from next.</summary>
    internal int Position
    {
        get => _lexer.Position;
        set => _lexer.Position = value;
    }

    /// <summary>The bytes being parsed.</summary>
    internal readonly ReadOnlySpan<byte> Data => _lexer.Data;

    /// <summary>The keyword of the token that terminated the last <see cref="ParseValue"/>.</summary>
    internal ReadOnlySpan<byte> LastKeyword { get; private set; }

    /// <summary>Parses one object.</summary>
    /// <returns>The parsed object, or the null object at end of input or on a stray keyword.</returns>
    internal PdfValue ParseValue()
    {
        bool isTopLevel = _depth == 0;
        PdfTokenType token = _lexer.Read();
        try
        {
            return ParseFromToken(token);
        }
        finally
        {
            if (isTopLevel)
                _mayReadIndirectStream = false;
        }
    }

    /// <summary>Reads the <c>number generation obj</c> header that introduces an indirect object.</summary>
    /// <param name="id">Receives the identity the header declares.</param>
    /// <returns>Whether a well-formed header was present at the current position.</returns>
    internal bool TryReadHeader(out PdfObjectId id)
    {
        id = default;
        _mayReadIndirectStream = false;

        if (_lexer.Read() != PdfTokenType.Integer)
            return false;

        double number = _lexer.NumberValue;

        if (_lexer.Read() != PdfTokenType.Integer)
            return false;

        double generation = _lexer.NumberValue;

        if (_lexer.Read() != PdfTokenType.Keyword || !_lexer.KeywordIs("obj"u8))
            return false;

        if (number is < 1 or > int.MaxValue || generation is < 0 or > ushort.MaxValue)
            return false;

        id = new PdfObjectId((int)number, (int)generation);
        _mayReadIndirectStream = true;
        return true;
    }

    /// <summary>Reads the next token, exposing the raw lexer for callers that scan file structure.</summary>
    internal PdfTokenType ReadToken() => _lexer.Read();

    /// <summary>The raw bytes of the token most recently read.</summary>
    internal readonly ReadOnlySpan<byte> TokenSpan => _lexer.TokenSpan;

    /// <summary>The value of a name token, with <c>#xx</c> escapes resolved.</summary>
    internal readonly byte[] TokenName() => _lexer.NameValue();

    /// <summary>The numeric value of the token most recently read.</summary>
    internal readonly double TokenNumber => _lexer.NumberValue;

    /// <summary>The offset the token most recently read starts at.</summary>
    internal readonly int TokenStart => _lexer.TokenStart;

    /// <summary>Parses a sequence of objects until the input or the enclosing construct ends.</summary>
    /// <param name="values">Receives the parsed objects.</param>
    /// <param name="stopKeyword">A keyword that ends the sequence, such as <c>endobj</c>.</param>
    internal void ParseSequence(List<PdfValue> values, ReadOnlySpan<byte> stopKeyword)
    {
        while (true)
        {
            PdfTokenType token = _lexer.Read();
            if (token == PdfTokenType.EndOfInput)
                return;

            if (token == PdfTokenType.Keyword && _lexer.KeywordIs(stopKeyword))
                return;

            values.Add(ParseFromToken(token));
        }
    }

    private PdfValue ParseFromToken(PdfTokenType token)
    {
        LastKeyword = default;

        if (token != PdfTokenType.EndOfInput)
            _decodePolicy?.ConsumeParsedValues(1);

        switch (token)
        {
            case PdfTokenType.EndOfInput:
                return ProtectedFailure("A protected object value ended before a value was read.");

            case PdfTokenType.Integer:
                return ParseIntegerOrReference();

            case PdfTokenType.Real:
                return PdfValue.Real(_lexer.NumberValue);

            case PdfTokenType.Name:
                return PdfValue.Name(PdfName.Get(_lexer.NameValue()));

            case PdfTokenType.LiteralString:
                return PdfValue.String(_lexer.StringValue());

            case PdfTokenType.HexString:
                return PdfValue.String(_lexer.StringValue(), hex: true);

            case PdfTokenType.ArrayStart:
                return ParseArray();

            case PdfTokenType.DictionaryStart:
                return ParseDictionaryOrStream();

            case PdfTokenType.Keyword:
                return ParseKeyword();

            default:
                return ProtectedFailure("A protected object value contains a stray closing token.");
        }
    }

    private PdfValue ParseKeyword()
    {
        ReadOnlySpan<byte> keyword = _lexer.TokenSpan;
        LastKeyword = keyword;

        if (keyword.SequenceEqual("true"u8))
            return PdfValue.True;

        if (keyword.SequenceEqual("false"u8))
            return PdfValue.False;

        if (keyword.SequenceEqual("null"u8))
            return PdfValue.Null;

        return ProtectedFailure(
            $"A protected object value contains the unknown keyword '{Ascii(keyword)}'.");
    }

    private PdfValue ParseIntegerOrReference()
    {
        long first = (long)_lexer.NumberValue;
        int afterFirst = _lexer.Position;

        if (_lexer.Read() == PdfTokenType.Integer)
        {
            long generation = (long)_lexer.NumberValue;
            if (_lexer.Read() == PdfTokenType.Keyword && _lexer.KeywordIs("R"u8) &&
                first is > 0 and <= int.MaxValue && generation is >= 0 and <= ushort.MaxValue)
            {
                return PdfValue.Reference((int)first, (int)generation);
            }
        }

        _lexer.Position = afterFirst;
        return PdfValue.Integer(first);
    }

    private PdfValue ParseArray()
    {
        if (++_depth > MaxDepth)
        {
            if (_decodePolicy is not null)
            {
                _depth--;
                throw new PdfFormatException(
                    $"A protected array exceeds the parser nesting limit of {MaxDepth} levels.");
            }

            SkipToArrayEnd();
            _depth--;
            return PdfValue.Null;
        }

        var array = new PdfArray { Resolver = _resolver };

        while (true)
        {
            PdfTokenType token = _lexer.Read();

            if (token == PdfTokenType.ArrayEnd)
                break;

            if (token == PdfTokenType.EndOfInput)
            {
                if (_decodePolicy is not null)
                    throw new PdfFormatException("A protected array is not closed with ].");

                break;
            }

            // A dictionary end inside an array means the array was never closed; stop before it.
            if (token == PdfTokenType.DictionaryEnd)
            {
                if (_decodePolicy is not null)
                    throw new PdfFormatException("A protected array is not closed with ].");

                _lexer.Position = _lexer.TokenStart;
                break;
            }

            if (token == PdfTokenType.Keyword && IsObjectBoundary(_lexer.TokenSpan))
            {
                if (_decodePolicy is not null)
                    throw new PdfFormatException("A protected array is not closed with ].");

                _lexer.Position = _lexer.TokenStart;
                break;
            }

            array.Add(ParseFromToken(token));
        }

        _depth--;
        return PdfValue.Array(array);
    }

    private PdfValue ParseDictionaryOrStream()
    {
        bool mayBeIndirectStream = _depth == 0 && _mayReadIndirectStream;
        if (++_depth > MaxDepth)
        {
            if (_decodePolicy is not null)
            {
                _depth--;
                throw new PdfFormatException(
                    $"A protected dictionary exceeds the parser nesting limit of {MaxDepth} levels.");
            }

            SkipToDictionaryEnd();
            _depth--;
            return PdfValue.Null;
        }

        var dictionary = new PdfDictionary { Resolver = _resolver };

        while (true)
        {
            PdfTokenType token = _lexer.Read();

            if (token == PdfTokenType.DictionaryEnd)
                break;

            if (token == PdfTokenType.EndOfInput)
            {
                if (_decodePolicy is not null)
                    throw new PdfFormatException("A protected dictionary is not closed with >>.");

                break;
            }

            if (token != PdfTokenType.Name)
            {
                if (token == PdfTokenType.Keyword && IsObjectBoundary(_lexer.TokenSpan))
                {
                    if (_decodePolicy is not null)
                        throw new PdfFormatException("A protected dictionary is not closed with >>.");

                    _lexer.Position = _lexer.TokenStart;
                    break;
                }

                if (_decodePolicy is not null)
                {
                    throw new PdfFormatException(
                        "A protected dictionary contains a value where a name key is required.");
                }

                // A value where a key belongs: drop it and resynchronise on the next key.
                // It is still parser work, so a protected reader must charge it even though it
                // cannot become part of the resulting value graph.
                _decodePolicy?.ConsumeParsedValues(1);
                continue;
            }

            PdfName key = PdfName.Get(_lexer.NameValue());
            if (_decodePolicy is not null && dictionary.ContainsKey(key))
            {
                throw new PdfFormatException(
                    $"A protected dictionary contains the duplicate key /{key.Value}.");
            }

            _decodePolicy?.ConsumeParsedValues(1);
            PdfValue value = ParseValue();
            dictionary.Set(key, value);
        }

        _depth--;

        int afterDictionary = _lexer.Position;
        if (_streams is not null && _lexer.Read() == PdfTokenType.Keyword && _lexer.KeywordIs("stream"u8))
        {
            if (_decodePolicy is not null && !mayBeIndirectStream)
            {
                throw new PdfFormatException(
                    "A protected stream shall be the top-level value of an indirect object.");
            }

            int separator = _lexer.Position;
            _lexer.SkipEndOfLine();
            if (_decodePolicy is not null &&
                _lexer.Position == separator &&
                separator < _lexer.Data.Length &&
                PdfCharacters.IsWhitespace(_lexer.Data[separator]))
            {
                // Keep a malformed but unambiguous stream inspectable so the PDF/A byte rule can
                // report the missing EOL against its exact clause. The separator is not payload.
                _lexer.Position++;
            }

            ReadOnlyMemory<byte> payload = _streams.ReadPayload(dictionary, _lexer.Position, out int endPosition);
            _lexer.Position = endPosition;
            _decodePolicy?.ConsumeParsedValues(1);
            return PdfValue.Stream(new PdfStream(dictionary, payload, _decodePolicy));
        }

        _lexer.Position = afterDictionary;
        return PdfValue.Dictionary(dictionary);
    }

    private PdfValue ProtectedFailure(string message)
    {
        if (_decodePolicy is not null)
            throw new PdfFormatException(message);

        return PdfValue.Null;
    }

    private static string Ascii(ReadOnlySpan<byte> bytes)
    {
        int length = Math.Min(bytes.Length, 64);
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++)
            chars[i] = bytes[i] is >= 0x20 and <= 0x7E ? (char)bytes[i] : '\uFFFD';

        return new string(chars);
    }

    private void SkipToArrayEnd()
    {
        int depth = 1;
        while (depth > 0)
        {
            PdfTokenType token = _lexer.Read();
            if (token == PdfTokenType.EndOfInput)
                return;

            _decodePolicy?.ConsumeParsedValues(1);

            if (token == PdfTokenType.ArrayStart)
                depth++;
            else if (token == PdfTokenType.ArrayEnd)
                depth--;
        }
    }

    private void SkipToDictionaryEnd()
    {
        int depth = 1;
        while (depth > 0)
        {
            PdfTokenType token = _lexer.Read();
            if (token == PdfTokenType.EndOfInput)
                return;

            _decodePolicy?.ConsumeParsedValues(1);

            if (token == PdfTokenType.DictionaryStart)
                depth++;
            else if (token == PdfTokenType.DictionaryEnd)
                depth--;
        }
    }

    private static bool IsObjectBoundary(ReadOnlySpan<byte> keyword) =>
        keyword.SequenceEqual("endobj"u8) || keyword.SequenceEqual("obj"u8) ||
        keyword.SequenceEqual("stream"u8) || keyword.SequenceEqual("trailer"u8) ||
        keyword.SequenceEqual("startxref"u8) || keyword.SequenceEqual("xref"u8);
}
