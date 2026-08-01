using Inkwright.Cos;

namespace Inkwright.Syntax;

/// <summary>
/// Supplies the payload of a stream object. Parsing a stream needs more context than the token
/// sequence carries: the declared length may itself be an indirect reference, and damaged files
/// need the payload located by searching for <c>endstream</c>.
/// </summary>
internal interface IPdfStreamPayloadReader
{
    /// <summary>Locates the payload of a stream whose <c>stream</c> keyword has just been read.</summary>
    /// <param name="dictionary">The stream dictionary parsed immediately before the keyword.</param>
    /// <param name="dataStart">The offset of the first payload byte.</param>
    /// <param name="endPosition">Receives the offset just past <c>endstream</c>.</param>
    /// <returns>The payload bytes, decrypted if the document is encrypted.</returns>
    ReadOnlyMemory<byte> ReadPayload(PdfDictionary dictionary, int dataStart, out int endPosition);
}

/// <summary>
/// Builds <see cref="PdfValue"/> graphs from a token stream. The parser is deliberately total: any
/// malformed construct degrades to the null object rather than throwing, and a depth guard keeps
/// hostile nesting from exhausting the stack.
/// </summary>
internal ref struct PdfObjectParser
{
    private const int MaxDepth = 128;

    private PdfLexer _lexer;
    private readonly IPdfResolver? _resolver;
    private readonly IPdfStreamPayloadReader? _streams;
    private int _depth;

    internal PdfObjectParser(
        ReadOnlySpan<byte> data,
        int position = 0,
        IPdfResolver? resolver = null,
        IPdfStreamPayloadReader? streams = null)
    {
        _lexer = new PdfLexer(data, position);
        _resolver = resolver;
        _streams = streams;
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
        PdfTokenType token = _lexer.Read();
        return ParseFromToken(token);
    }

    /// <summary>Reads the <c>number generation obj</c> header that introduces an indirect object.</summary>
    /// <param name="id">Receives the identity the header declares.</param>
    /// <returns>Whether a well-formed header was present at the current position.</returns>
    internal bool TryReadHeader(out PdfObjectId id)
    {
        id = default;

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

        switch (token)
        {
            case PdfTokenType.EndOfInput:
                return PdfValue.Null;

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
                return PdfValue.Null;
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

        return PdfValue.Null;
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
            SkipToArrayEnd();
            _depth--;
            return PdfValue.Null;
        }

        var array = new PdfArray { Resolver = _resolver };

        while (true)
        {
            PdfTokenType token = _lexer.Read();

            if (token is PdfTokenType.ArrayEnd or PdfTokenType.EndOfInput)
                break;

            // A dictionary end inside an array means the array was never closed; stop before it.
            if (token == PdfTokenType.DictionaryEnd)
            {
                _lexer.Position = _lexer.TokenStart;
                break;
            }

            if (token == PdfTokenType.Keyword && IsObjectBoundary(_lexer.TokenSpan))
            {
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
        if (++_depth > MaxDepth)
        {
            SkipToDictionaryEnd();
            _depth--;
            return PdfValue.Null;
        }

        var dictionary = new PdfDictionary { Resolver = _resolver };

        while (true)
        {
            PdfTokenType token = _lexer.Read();

            if (token is PdfTokenType.DictionaryEnd or PdfTokenType.EndOfInput)
                break;

            if (token != PdfTokenType.Name)
            {
                if (token == PdfTokenType.Keyword && IsObjectBoundary(_lexer.TokenSpan))
                {
                    _lexer.Position = _lexer.TokenStart;
                    break;
                }

                // A value where a key belongs: drop it and resynchronise on the next key.
                continue;
            }

            PdfName key = PdfName.Get(_lexer.NameValue());
            PdfValue value = ParseValue();
            dictionary.Set(key, value);
        }

        _depth--;

        int afterDictionary = _lexer.Position;
        if (_streams is not null && _lexer.Read() == PdfTokenType.Keyword && _lexer.KeywordIs("stream"u8))
        {
            _lexer.SkipEndOfLine();
            ReadOnlyMemory<byte> payload = _streams.ReadPayload(dictionary, _lexer.Position, out int endPosition);
            _lexer.Position = endPosition;
            return PdfValue.Stream(new PdfStream(dictionary, payload));
        }

        _lexer.Position = afterDictionary;
        return PdfValue.Dictionary(dictionary);
    }

    private void SkipToArrayEnd()
    {
        int depth = 1;
        while (depth > 0)
        {
            PdfTokenType token = _lexer.Read();
            if (token == PdfTokenType.EndOfInput)
                return;

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
