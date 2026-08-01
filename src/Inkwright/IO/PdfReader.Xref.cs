using System.Globalization;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Syntax;

namespace Inkwright.IO;

public sealed partial class PdfReader : IPdfStreamPayloadReader
{
    private void LoadCrossReferences()
    {
        long start = FindStartXref();
        StartXref = start;

        if (start >= 0)
            WalkChain(start);

        if (_entries.Count == 0 || !Trailer.ContainsKey(PdfNames.Root))
        {
            if (!_options.Recover)
            {
                if (_entries.Count == 0)
                    throw new PdfFormatException("The cross-reference data could not be read.");
            }
            else
            {
                RebuildFromScan();
            }
        }
    }

    private long FindStartXref()
    {
        ReadOnlySpan<byte> span = _data;
        int index = span.LastIndexOf("startxref"u8);

        if (index < 0)
        {
            Diagnostics.Add(PdfWarningCode.BadStartXref, "The file has no startxref marker.");
            return -1;
        }

        var lexer = new PdfLexer(span, index + 9);
        if (lexer.Read() != PdfTokenType.Integer)
        {
            Diagnostics.Add(PdfWarningCode.BadStartXref, "The startxref marker is not followed by an offset.",
                offset: index);
            return -1;
        }

        long offset = (long)lexer.NumberValue;
        if (offset < 0 || offset >= span.Length)
        {
            Diagnostics.Add(PdfWarningCode.BadStartXref,
                $"The startxref offset {offset} is outside the file.", offset: index);
            return -1;
        }

        return offset;
    }

    private void WalkChain(long start)
    {
        var visited = new HashSet<long>();
        long offset = start;

        while (offset >= 0 && offset < _data.Length)
        {
            if (!visited.Add(offset))
            {
                Diagnostics.Add(PdfWarningCode.XrefLoop, "The cross-reference chain loops.", offset: offset);
                return;
            }

            PdfDictionary? trailer = ReadSection(offset, out bool isStream);
            if (trailer is null)
            {
                Diagnostics.Add(PdfWarningCode.BadXrefSection,
                    "A cross-reference section could not be read.", offset: offset);
                return;
            }

            MergeTrailer(trailer);

            // A hybrid-reference file hides some objects from pre-1.5 readers in a parallel stream.
            if (!isStream && trailer.TryGetRaw(PdfNames.XRefStm, out PdfValue hybrid) && hybrid.IsNumber)
            {
                long hybridOffset = hybrid.AsInteger;
                if (hybridOffset > 0 && hybridOffset < _data.Length && visited.Add(hybridOffset))
                    _ = ReadSection(hybridOffset, out _);
            }

            offset = trailer.TryGetRaw(PdfNames.Prev, out PdfValue previous) && previous.IsNumber
                ? previous.AsInteger
                : -1;
        }
    }

    private PdfDictionary? ReadSection(long offset, out bool isStream)
    {
        isStream = false;

        var lexer = new PdfLexer(_data, (int)offset);
        if (lexer.Read() == PdfTokenType.Keyword && lexer.KeywordIs("xref"u8))
            return ReadXrefTable(lexer.Position);

        isStream = true;
        return ReadXrefStream(offset);
    }

    private PdfDictionary? ReadXrefTable(int position)
    {
        var lexer = new PdfLexer(_data, position);

        while (true)
        {
            PdfTokenType token = lexer.Read();

            if (token == PdfTokenType.EndOfInput)
                return null;

            if (token == PdfTokenType.Keyword)
            {
                if (!lexer.KeywordIs("trailer"u8))
                    return null;

                var parser = new PdfObjectParser(_data, lexer.Position, this, this);
                PdfValue trailerValue = parser.ParseValue();
                return trailerValue.AsDictionary ?? new PdfDictionary { Resolver = this };
            }

            if (token != PdfTokenType.Integer)
                return null;

            int first = (int)lexer.NumberValue;
            if (lexer.Read() != PdfTokenType.Integer)
                return null;

            int count = (int)lexer.NumberValue;
            if (count < 0 || count > _data.Length / 18 + 16)
                return null;

            for (int i = 0; i < count; i++)
            {
                int entryStart = lexer.Position;
                if (lexer.Read() != PdfTokenType.Integer)
                {
                    lexer.Position = entryStart;
                    break;
                }

                long entryOffset = (long)lexer.NumberValue;

                if (lexer.Read() != PdfTokenType.Integer)
                    break;

                int generation = (int)lexer.NumberValue;

                if (lexer.Read() != PdfTokenType.Keyword)
                    break;

                bool free = lexer.TokenSpan.Length > 0 && lexer.TokenSpan[0] == (byte)'f';
                int number = first + i;

                // A well-known malformation numbers the first subsection from one; the free-list
                // head gives it away, and every entry after it is off by one.
                if (i == 0 && first == 1 && free && generation == 65535)
                    number = 0;

                if (free || number <= 0)
                    continue;

                if (!_entries.ContainsKey(number))
                    _entries[number] = XrefEntry.Normal(entryOffset, generation);
            }
        }
    }

    private PdfDictionary? ReadXrefStream(long offset)
    {
        var parser = new PdfObjectParser(_data, (int)offset, this, this);
        if (!parser.TryReadHeader(out PdfObjectId id))
            return null;

        PdfObjectId saved = _currentObject;
        _currentObject = id;
        PdfValue value;
        try
        {
            value = parser.ParseValue();
        }
        finally
        {
            _currentObject = saved;
        }

        if (value.AsStream is not { } stream)
            return null;

        PdfDictionary dictionary = stream.Dictionary;
        PdfArray? widths = dictionary.GetArray(PdfNames.W);
        if (widths is null || widths.Count < 3)
            return null;

        int w0 = (int)widths.GetInteger(0);
        int w1 = (int)widths.GetInteger(1);
        int w2 = (int)widths.GetInteger(2);
        if (w0 < 0 || w1 <= 0 || w2 < 0 || w0 + w1 + w2 == 0)
            return null;

        ReadOnlySpan<byte> data = stream.DecodedSpan();
        int rowLength = w0 + w1 + w2;
        if (rowLength == 0)
            return null;

        List<(int Start, int Count)> ranges = [];
        if (dictionary.GetArray(PdfNames.Index) is { Count: >= 2 } index)
        {
            for (int i = 0; i + 1 < index.Count; i += 2)
                ranges.Add(((int)index.GetInteger(i), (int)index.GetInteger(i + 1)));
        }
        else
        {
            ranges.Add((0, (int)dictionary.GetInteger(PdfNames.Size, data.Length / rowLength)));
        }

        int position = 0;
        foreach ((int start, int count) in ranges)
        {
            for (int i = 0; i < count && position + rowLength <= data.Length; i++, position += rowLength)
            {
                ReadOnlySpan<byte> row = data.Slice(position, rowLength);
                long type = w0 == 0 ? 1 : ReadField(row[..w0]);
                long field2 = ReadField(row.Slice(w0, w1));
                long field3 = ReadField(row.Slice(w0 + w1, w2));

                int number = start + i;
                if (number <= 0 || _entries.ContainsKey(number))
                    continue;

                switch (type)
                {
                    case 1:
                        _entries[number] = XrefEntry.Normal(field2, (int)field3);
                        break;

                    case 2:
                        _entries[number] = XrefEntry.Compressed((int)field2, (int)field3);
                        break;

                    default:
                        break;
                }
            }
        }

        return dictionary;
    }

    private static long ReadField(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        foreach (byte b in bytes)
            value = (value << 8) | b;

        return value;
    }

    private static readonly PdfName[] SectionOnlyKeys =
    [
        PdfNames.Prev, PdfNames.XRefStm, PdfNames.Type, PdfNames.Filter, PdfNames.DecodeParms,
        PdfNames.Length, PdfNames.W, PdfNames.Index,
    ];

    private void MergeTrailer(PdfDictionary section)
    {
        foreach (KeyValuePair<PdfName, PdfValue> entry in section)
        {
            // A cross-reference stream's dictionary doubles as a trailer, but its stream plumbing
            // belongs to that one section and must not travel with the merged trailer.
            if (System.Array.IndexOf(SectionOnlyKeys, entry.Key) >= 0)
                continue;

            Trailer.TryAdd(entry.Key, entry.Value);
        }
    }

    private void RebuildFromScan()
    {
        EnsureScanned();

        if (_scannedOffsets!.Count == 0)
        {
            if (_entries.Count == 0)
                throw new PdfFormatException("No indirect objects could be found in the file.");

            return;
        }

        WasRebuilt = true;
        Diagnostics.Add(PdfWarningCode.XrefRebuilt,
            $"The cross-reference data was unusable; {_scannedOffsets.Count} objects were located by scanning.");

        _cache.Clear();
        _objectStreams.Clear();
        _entries.Clear();

        foreach ((int number, long offset) in _scannedOffsets)
            _entries[number] = XrefEntry.Normal(offset, 0);

        // Objects hidden inside object streams are invisible to a byte scan; index them explicitly.
        foreach (int number in _scannedOffsets.Keys.ToArray())
        {
            PdfValue value = GetObject(number);
            if (value.AsStream is not { } stream || stream.Dictionary.GetName(PdfNames.Type) != PdfNames.ObjStm)
                continue;

            ObjectStreamIndex? index = ReadObjectStream(number);
            if (index is null)
                continue;

            for (int i = 0; i < index.Numbers.Length; i++)
            {
                int contained = index.Numbers[i];
                if (contained > 0 && !_entries.ContainsKey(contained))
                    _entries[contained] = XrefEntry.Compressed(number, i);
            }
        }

        foreach (PdfDictionary trailer in ScanTrailers())
            MergeTrailer(trailer);
    }

    private IEnumerable<PdfDictionary> ScanTrailers()
    {
        List<PdfDictionary> trailers = [];
        ReadOnlySpan<byte> span = _data;
        int position = span.Length;

        // Later trailers are newer, so walk backwards and let the first find win.
        while (position > 0)
        {
            int index = span[..position].LastIndexOf("trailer"u8);
            if (index < 0)
                break;

            var parser = new PdfObjectParser(_data, index + 7, this, this);
            if (parser.ParseValue().AsDictionary is { } dictionary)
                trailers.Add(dictionary);

            position = index;
        }

        return trailers;
    }

    private void EnsureScanned()
    {
        if (_scannedOffsets is not null)
            return;

        var found = new Dictionary<int, long>();
        ReadOnlySpan<byte> span = _data;
        int position = 0;

        while (position < span.Length)
        {
            int index = span[position..].IndexOf("obj"u8);
            if (index < 0)
                break;

            int at = position + index;
            position = at + 3;

            if (at + 3 < span.Length && PdfCharacters.IsRegular(span[at + 3]))
                continue;

            int i = at - 1;
            if (i < 0 || !PdfCharacters.IsWhitespace(span[i]))
                continue;

            while (i >= 0 && PdfCharacters.IsWhitespace(span[i]))
                i--;

            int generationEnd = i + 1;
            while (i >= 0 && char.IsAsciiDigit((char)span[i]))
                i--;

            int generationStart = i + 1;
            if (generationStart == generationEnd || i < 0 || !PdfCharacters.IsWhitespace(span[i]))
                continue;

            while (i >= 0 && PdfCharacters.IsWhitespace(span[i]))
                i--;

            int numberEnd = i + 1;
            while (i >= 0 && char.IsAsciiDigit((char)span[i]))
                i--;

            int numberStart = i + 1;
            if (numberStart == numberEnd || numberEnd - numberStart > 10)
                continue;

            if (!TryParseAsciiInt(span[numberStart..numberEnd], out int number) || number <= 0)
                continue;

            found[number] = numberStart;
        }

        _scannedOffsets = found;
    }

    private static bool TryParseAsciiInt(ReadOnlySpan<byte> bytes, out int value)
    {
        Span<char> chars = stackalloc char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = (char)bytes[i];

        return int.TryParse(chars, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private PdfValue LoadObject(int number)
    {
        if (!_entries.TryGetValue(number, out XrefEntry entry))
        {
            if (!_options.Recover)
                return PdfValue.Null;

            EnsureScanned();
            if (!_scannedOffsets!.TryGetValue(number, out long scanned))
            {
                Diagnostics.Add(PdfWarningCode.DanglingReference,
                    "A reference pointed at an object that is not in the file.", new PdfObjectId(number, 0));
                return PdfValue.Null;
            }

            entry = XrefEntry.Normal(scanned, 0);
            _entries[number] = entry;
        }

        return entry.Type switch
        {
            XrefEntryType.Normal => LoadFromFile(number, entry),
            XrefEntryType.Compressed => LoadFromObjectStream(number, entry),
            _ => PdfValue.Null,
        };
    }

    private PdfValue LoadFromFile(int number, XrefEntry entry)
    {
        if (!TryLocate(number, entry, out int start))
        {
            Diagnostics.Add(PdfWarningCode.ObjectUnparsable,
                "The object could not be located.", new PdfObjectId(number, entry.Generation), entry.Offset);
            return PdfValue.Null;
        }

        var parser = new PdfObjectParser(_data, start, this, this);
        if (!parser.TryReadHeader(out PdfObjectId id))
            return PdfValue.Null;

        PdfObjectId saved = _currentObject;
        _currentObject = id;

        try
        {
            PdfValue value = parser.ParseValue();
            return DecryptStrings(value, id, 0);
        }
        finally
        {
            _currentObject = saved;
        }
    }

    private bool TryLocate(int number, XrefEntry entry, out int start)
    {
        if (MatchesHeader(entry.Offset, number, out start))
            return true;

        if (_headerOffset > 0 && MatchesHeader(entry.Offset + _headerOffset, number, out start))
        {
            Diagnostics.Add(PdfWarningCode.ObjectOffsetMismatch,
                "Object offsets are relative to the header rather than the file.", new PdfObjectId(number, 0));
            return true;
        }

        if (!_options.Recover)
            return false;

        EnsureScanned();
        if (_scannedOffsets!.TryGetValue(number, out long scanned) && MatchesHeader(scanned, number, out start))
        {
            Diagnostics.Add(PdfWarningCode.ObjectOffsetMismatch,
                $"The object was at {scanned}, not at the recorded {entry.Offset}.",
                new PdfObjectId(number, 0), entry.Offset);
            return true;
        }

        start = 0;
        return false;
    }

    private bool MatchesHeader(long offset, int number, out int start)
    {
        start = 0;
        if (offset < 0 || offset >= _data.Length)
            return false;

        var parser = new PdfObjectParser(_data, (int)offset);
        if (!parser.TryReadHeader(out PdfObjectId id) || id.Number != number)
            return false;

        start = (int)offset;
        return true;
    }

    private int FindObjectEnd(int start)
    {
        var parser = new PdfObjectParser(_data, start, this, this);
        if (!parser.TryReadHeader(out _))
            return -1;

        _ = parser.ParseValue();
        int position = parser.Position;

        var lexer = new PdfLexer(_data, position);
        if (lexer.Read() == PdfTokenType.Keyword && lexer.KeywordIs("endobj"u8))
            return lexer.Position;

        return position;
    }

    private PdfValue LoadFromObjectStream(int number, XrefEntry entry)
    {
        ObjectStreamIndex? index = ReadObjectStream((int)entry.Offset);
        if (index is null)
            return PdfValue.Null;

        int slot = entry.Generation;
        if (slot < 0 || slot >= index.Numbers.Length || index.Numbers[slot] != number)
        {
            slot = System.Array.IndexOf(index.Numbers, number);
            if (slot < 0)
                return PdfValue.Null;
        }

        int start = index.First + index.Offsets[slot];
        if (start < 0 || start >= index.Data.Length)
            return PdfValue.Null;

        // Strings inside an object stream were encrypted as part of the container, not on their own.
        var parser = new PdfObjectParser(index.Data.Span, start, this);
        return parser.ParseValue();
    }

    private ObjectStreamIndex? ReadObjectStream(int number)
    {
        if (_objectStreams.TryGetValue(number, out ObjectStreamIndex? cached))
            return cached;

        _objectStreams[number] = null;

        if (GetObject(number).AsStream is not { } stream)
        {
            Diagnostics.Add(PdfWarningCode.ObjectStreamUnreadable,
                "An object stream could not be read.", new PdfObjectId(number, 0));
            return null;
        }

        int count = (int)stream.Dictionary.GetInteger(PdfNames.N);
        int first = (int)stream.Dictionary.GetInteger(PdfNames.First);
        ReadOnlyMemory<byte> data = stream.Decode();

        if (count <= 0 || first < 0 || first > data.Length)
            return null;

        int[] numbers = new int[count];
        int[] offsets = new int[count];
        var lexer = new PdfLexer(data.Span);

        for (int i = 0; i < count; i++)
        {
            if (lexer.Read() != PdfTokenType.Integer)
            {
                System.Array.Resize(ref numbers, i);
                System.Array.Resize(ref offsets, i);
                break;
            }

            numbers[i] = (int)lexer.NumberValue;

            if (lexer.Read() != PdfTokenType.Integer)
            {
                System.Array.Resize(ref numbers, i);
                System.Array.Resize(ref offsets, i);
                break;
            }

            offsets[i] = (int)lexer.NumberValue;
        }

        var index = new ObjectStreamIndex(data, numbers, offsets, first);
        _objectStreams[number] = index;
        return index;
    }

    ReadOnlyMemory<byte> IPdfStreamPayloadReader.ReadPayload(
        PdfDictionary dictionary, int dataStart, out int endPosition)
    {
        ReadOnlySpan<byte> span = _data;
        long declared = ReadDeclaredLength(dictionary);
        int length = -1;

        if (declared >= 0 && dataStart + declared <= span.Length &&
            IsEndstreamAt(span, dataStart + (int)declared))
        {
            length = (int)declared;
        }

        if (length < 0)
        {
            int found = span[dataStart..].IndexOf("endstream"u8);
            if (found >= 0)
            {
                length = TrimTrailingEol(span, dataStart, found);
                if (declared >= 0)
                {
                    Diagnostics.Add(PdfWarningCode.StreamLengthMismatch,
                        $"The declared length {declared} did not match the {length} bytes before endstream.",
                        _currentObject, dataStart);
                }
            }
            else if (declared >= 0 && dataStart + declared <= span.Length)
            {
                length = (int)declared;
            }
            else
            {
                length = span.Length - dataStart;
            }
        }

        endPosition = Math.Min(span.Length, dataStart + length);
        int marker = span[endPosition..].IndexOf("endstream"u8);
        endPosition = marker >= 0 ? endPosition + marker + 9 : span.Length;

        ReadOnlyMemory<byte> payload = _memory.Slice(dataStart, length);

        if (_security is null || !ShouldDecrypt(dictionary))
            return payload;

        return _security.DecryptStream(_currentObject, payload.Span, dictionary);
    }

    private bool ShouldDecrypt(PdfDictionary dictionary)
    {
        PdfName? type = dictionary.GetName(PdfNames.Type);

        // Cross-reference streams are read before any key exists and are never encrypted.
        if (type == PdfNames.XRef)
            return false;

        if (type == PdfNames.Metadata && _security is { EncryptMetadata: false })
            return false;

        return true;
    }

    private long ReadDeclaredLength(PdfDictionary dictionary)
    {
        if (!dictionary.TryGetRaw(PdfNames.Length, out PdfValue length))
            return -1;

        if (length.IsNumber)
            return length.AsInteger;

        if (!length.IsReference)
            return -1;

        // Resolving the length can re-enter the reader; the guard in GetObject keeps that finite.
        PdfValue resolved = Resolve(length);
        return resolved.IsNumber ? resolved.AsInteger : -1;
    }

    private static bool IsEndstreamAt(ReadOnlySpan<byte> span, int position)
    {
        while (position < span.Length && PdfCharacters.IsWhitespace(span[position]))
            position++;

        return span[position..].StartsWith("endstream"u8);
    }

    private static int TrimTrailingEol(ReadOnlySpan<byte> span, int dataStart, int length)
    {
        int end = dataStart + length;

        if (end > dataStart && span[end - 1] == 0x0A)
            end--;

        if (end > dataStart && span[end - 1] == 0x0D)
            end--;

        return end - dataStart;
    }

    private PdfValue DecryptStrings(PdfValue value, PdfObjectId id, int depth)
    {
        if (_security is null || depth > 64)
            return value;

        switch (value.Kind)
        {
            case PdfValueKind.String:
                return PdfValue.String(_security.DecryptString(id, value.AsStringBytes), value.IsHexString);

            case PdfValueKind.Array:
            {
                PdfArray array = value.AsArray!;
                for (int i = 0; i < array.Count; i++)
                    array[i] = DecryptStrings(array[i], id, depth + 1);

                return value;
            }

            case PdfValueKind.Dictionary:
            case PdfValueKind.Stream:
            {
                PdfDictionary dictionary = value.AsDictionary!;

                // The signed blob was never encrypted (§7.6.2), so decrypting it would turn a valid
                // signature into noise that no verifier can even parse.
                bool signature = IsSignature(dictionary);

                foreach (PdfName key in dictionary.Keys.ToArray())
                {
                    if (signature && key == PdfNames.Contents)
                        continue;

                    if (dictionary.TryGetRaw(key, out PdfValue entry))
                        dictionary.Set(key, DecryptStrings(entry, id, depth + 1));
                }

                return value;
            }

            default:
                return value;
        }
    }

    private static bool IsSignature(PdfDictionary dictionary)
    {
        if (dictionary.GetName(PdfNames.Type) is { } type)
            return type.Value is "Sig" or "DocTimeStamp";

        // Some producers leave the type out; a byte range beside the contents says the same thing.
        return dictionary.ContainsKey(PdfNames.ByteRange) && dictionary.ContainsKey(PdfNames.Contents);
    }

    private sealed record ObjectStreamIndex(ReadOnlyMemory<byte> Data, int[] Numbers, int[] Offsets, int First);
}
