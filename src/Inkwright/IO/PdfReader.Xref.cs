using System.Globalization;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Syntax;

namespace Inkwright.IO;

public sealed partial class PdfReader : IPdfStreamPayloadReader
{
    private static readonly PdfName[] ProtectedStandardHintTableKeys =
    [
        PdfName.Get("S"),
        PdfName.Get("T"),
        PdfName.Get("O"),
        PdfName.Get("A"),
        PdfName.Get("E"),
        PdfName.Get("V"),
        PdfName.Get("I"),
        PdfName.Get("C"),
        PdfName.Get("L"),
        PdfName.Get("R"),
        PdfName.Get("B"),
    ];

    IPdfStreamDecodePolicy? IPdfStreamDecodePolicyProvider.DecodePolicy => _options.StreamDecodePolicy;

    private bool _protectedObjectZeroSeen;
    private bool _protectedObjectZeroInvalid;
    private XrefEntry? _protectedObjectZeroEntry;
    private int _protectedFinalStartXrefKeyword = -1;
    private int _protectedSectionEnd = -1;
    private int _protectedFinalSectionEnd = -1;
    private int _protectedLinearizedFirstSectionOffset = -1;
    private int _protectedLinearizedOriginalLength = -1;
    private int _protectedLinearizedOriginalStartXrefKeyword = -1;
    private long _protectedLinearizedMainSectionOffset = -1;
    private bool _protectedLinearizedForwardLinkFollowed;
    private int _protectedSectionObjectNumber = -1;
    private int _protectedCurrentXrefRevision;
    private ProtectedLinearizationInfo? _protectedLinearization;
    private ProtectedClassicXrefJournal? _protectedLinearizedFirstXref;
    private ProtectedClassicXrefJournal? _protectedLinearizedMainXref;
    private readonly List<ProtectedXrefSectionRange> _protectedXrefSections = [];
    private readonly List<ProtectedXrefRowRecord> _protectedXrefRows = [];
    private readonly List<ProtectedRevisionBody> _protectedRevisionBodies = [];
    private readonly List<ProtectedHistoricalObjectRange> _protectedHistoricalObjectRanges = [];
    private readonly HashSet<(PdfObjectId ObjectId, int Start, int End)>
        _protectedHistoricalObjectRangeKeys = [];
    private readonly List<ProtectedStreamPayloadRange> _protectedParsedStreamPayloadRanges = [];
    private readonly HashSet<(int Start, int End, int ObjectNumber)>
        _protectedStreamPayloadRangeKeys = [];

    private readonly record struct ProtectedHintRange(int Offset, int Length);

    private sealed record ProtectedLinearizationInfo(
        PdfObjectId DictionaryId,
        int DictionaryStart,
        int DictionaryEnd,
        int DeclaredLength,
        int FirstPageObjectNumber,
        int FirstPageEnd,
        int PageCount,
        int MainFirstEntryWhitespace,
        int FirstPageNumber,
        ProtectedHintRange PrimaryHint,
        ProtectedHintRange? OverflowHint);

    private readonly record struct ProtectedClassicXrefRow(
        long Offset,
        int Generation,
        bool Free,
        int RowStart);

    private sealed record ProtectedClassicXrefJournal(
        int SectionStart,
        IReadOnlyList<(int First, int Count)> Subsections,
        IReadOnlyDictionary<int, ProtectedClassicXrefRow> Rows,
        PdfDictionary Trailer);

    private readonly record struct ProtectedXrefSectionRange(
        int Start,
        int End,
        bool IsStream,
        int ObjectNumber,
        int Revision);

    private readonly record struct ProtectedXrefRowRecord(
        int Revision,
        int ParseOrder,
        int Number,
        XrefEntry Entry,
        bool IsEffective);

    private readonly record struct ProtectedRevisionBody(
        int Revision,
        int Start,
        int PrimaryXrefStart);

    private readonly record struct ProtectedRevisionXrefEntry(
        int Revision,
        XrefEntry Entry);

    private readonly record struct ProtectedHistoricalObjectRange(
        int Revision,
        PdfObjectId ObjectId,
        int Start,
        int End);

    private readonly record struct ProtectedStreamPayloadRange(
        int Start,
        int End,
        int ObjectNumber,
        int Revision);

    /// <summary>
    /// The exact offsets of classic cross-reference sections reached through startxref, Prev, or
    /// XRefStm processing. Byte-oriented validators shall inspect these tokens rather than search
    /// arbitrary stream and string bytes for the text <c>xref</c>.
    /// </summary>
    internal IEnumerable<int> ProtectedClassicXrefSectionOffsets
    {
        get
        {
            foreach (ProtectedXrefSectionRange section in _protectedXrefSections)
            {
                if (!section.IsStream)
                    yield return section.Start;
            }
        }
    }

    private void LoadCrossReferences()
    {
        _protectedObjectZeroSeen = false;
        _protectedObjectZeroInvalid = false;
        _protectedObjectZeroEntry = null;
        _protectedFinalStartXrefKeyword = -1;
        _protectedSectionEnd = -1;
        _protectedFinalSectionEnd = -1;
        _protectedLinearizedFirstSectionOffset = -1;
        _protectedLinearizedOriginalLength = -1;
        _protectedLinearizedOriginalStartXrefKeyword = -1;
        _protectedLinearizedMainSectionOffset = -1;
        _protectedLinearizedForwardLinkFollowed = false;
        _protectedSectionObjectNumber = -1;
        _protectedCurrentXrefRevision = 0;
        _protectedLinearization = null;
        _protectedLinearizedFirstXref = null;
        _protectedLinearizedMainXref = null;
        _protectedXrefSections.Clear();
        _protectedXrefRows.Clear();
        _protectedRevisionBodies.Clear();
        _protectedHistoricalObjectRanges.Clear();
        _protectedHistoricalObjectRangeKeys.Clear();
        _protectedParsedStreamPayloadRanges.Clear();
        _protectedStreamPayloadRangeKeys.Clear();

        if (_options.StreamDecodePolicy is not null)
            LocateProtectedLinearizedRevision();

        long start = FindStartXref();
        StartXref = start;

        if (start >= 0)
            WalkChain(start);

        if (_options.StreamDecodePolicy is not null)
            ValidateProtectedFinalSectionTail();

        if (_options.StreamDecodePolicy is not null &&
            (!_protectedObjectZeroSeen || _protectedObjectZeroInvalid))
        {
            throw new PdfFormatException(
                "A protected cross-reference chain requires object 0 to be a free entry " +
                "with generation 65535.");
        }

        if (_options.StreamDecodePolicy is not null)
            ValidateProtectedCombinedSize();

        if (_options.StreamDecodePolicy is not null)
            ValidateProtectedFreeList();

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

        if (_options.StreamDecodePolicy is not null &&
            (!Trailer.TryGetRaw(PdfNames.Root, out PdfValue root) || !root.IsReference))
        {
            throw new PdfFormatException(
                "A protected trailer Root entry must be an indirect reference.");
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

        long offset;
        if (_options.StreamDecodePolicy is not null)
        {
            _protectedFinalStartXrefKeyword = index;
            if (!TryReadProtectedFinalStartXref(span, index, out offset))
            {
                throw new PdfFormatException(
                    "A protected PDF requires the physical final lines to be startxref, its " +
                    "decimal offset, and %%EOF.");
            }
        }
        else
        {
            var lexer = new PdfLexer(span, index + 9);
            if (lexer.Read() != PdfTokenType.Integer)
            {
                Diagnostics.Add(PdfWarningCode.BadStartXref,
                    "The startxref marker is not followed by an offset.", offset: index);
                return -1;
            }

            offset = (long)lexer.NumberValue;
        }

        if (offset < 0 || offset >= span.Length)
        {
            Diagnostics.Add(PdfWarningCode.BadStartXref,
                $"The startxref offset {offset} is outside the file.", offset: index);
            return -1;
        }

        return offset;
    }

    private static bool TryReadProtectedFinalStartXref(
        ReadOnlySpan<byte> data,
        int keywordStart,
        out long offset)
    {
        offset = -1;
        if (keywordStart > 0 && !PdfCharacters.IsEndOfLine(data[keywordStart - 1]))
            return false;

        int position = keywordStart + "startxref".Length;
        if (!TryConsumeEndOfLine(data, ref position))
            return false;

        int offsetStart = position;
        while (position < data.Length && data[position] is >= (byte)'0' and <= (byte)'9')
            position++;

        if (!TryParseUnsigned(data[offsetStart..position], out offset) ||
            !TryConsumeEndOfLine(data, ref position) ||
            position + 5 > data.Length ||
            !data.Slice(position, 5).SequenceEqual("%%EOF"u8))
        {
            return false;
        }

        position += 5;
        return position == data.Length ||
               (TryConsumeEndOfLine(data, ref position) && position == data.Length);
    }

    private void LocateProtectedLinearizedRevision()
    {
        ReadOnlySpan<byte> data = _data;
        if (!data.StartsWith("%PDF-"u8))
            return;

        var firstToken = new PdfLexer(data, strict: true);
        if (firstToken.Read() != PdfTokenType.Integer)
            return;

        int dictionaryStart = firstToken.TokenStart;
        var parser = new PdfObjectParser(data, dictionaryStart, resolver: null, streams: this);
        if (!parser.TryReadHeader(out PdfObjectId id) ||
            id.Generation != 0 ||
            parser.Position > 1024 ||
            !LooksLikeStandaloneLinearizationDictionary(data, parser.Position))
        {
            return;
        }

        PdfValue value = parser.ParseValue();
        if (value.AsDictionary is not { } dictionary ||
            parser.ReadToken() != PdfTokenType.Keyword ||
            !parser.TokenSpan.SequenceEqual("endobj"u8) ||
            parser.Position > 1024 ||
            !IsFullyDirect(value))
        {
            return;
        }

        PdfName lengthName = PdfName.Get("L");
        PdfName hintsName = PdfName.Get("H");
        PdfName endName = PdfName.Get("E");
        PdfName firstEntryName = PdfName.Get("T");

        if (!dictionary.TryGetRaw(PdfNames.Linearized, out PdfValue linearized) ||
            !linearized.IsNumber || linearized.AsNumber is < 1 or >= 2 ||
            !TryGetDirectInteger(dictionary, lengthName, 1, data.Length, out long declaredLength) ||
            !TryGetDirectInteger(dictionary, PdfNames.O, 1, int.MaxValue, out long firstPageObject) ||
            !TryGetDirectInteger(dictionary, endName, 1, declaredLength, out long firstPageEnd) ||
            !TryGetDirectInteger(dictionary, PdfNames.N, 1, int.MaxValue, out long pageCount) ||
            !TryGetDirectInteger(
                dictionary,
                firstEntryName,
                0,
                declaredLength - 1,
                out long mainFirstEntryWhitespace) ||
            !TryGetLinearizedHintRanges(
                dictionary,
                hintsName,
                declaredLength,
                out ProtectedHintRange primaryHint,
                out ProtectedHintRange? overflowHint))
        {
            return;
        }

        long firstPageNumber = 0;
        if (dictionary.TryGetRaw(PdfNames.P, out PdfValue firstPage) &&
            (firstPage.Kind != PdfValueKind.Integer ||
             firstPage.AsInteger < 0 || firstPage.AsInteger >= pageCount))
        {
            return;
        }
        else if (dictionary.TryGetRaw(PdfNames.P, out firstPage))
        {
            firstPageNumber = firstPage.AsInteger;
        }

        int originalLength = (int)declaredLength;
        ReadOnlySpan<byte> originalRevision = data[..originalLength];
        int originalStartXrefKeyword = originalRevision.LastIndexOf("startxref"u8);
        if (originalStartXrefKeyword < 0 ||
            !TryReadProtectedFinalStartXref(
                originalRevision,
                originalStartXrefKeyword,
                out long firstSectionOffset) ||
            firstSectionOffset <= parser.Position ||
            firstSectionOffset >= originalStartXrefKeyword ||
            firstSectionOffset > int.MaxValue)
        {
            return;
        }

        _protectedLinearizedFirstSectionOffset = (int)firstSectionOffset;
        _protectedLinearizedOriginalLength = originalLength;
        _protectedLinearizedOriginalStartXrefKeyword = originalStartXrefKeyword;
        _protectedLinearization = new ProtectedLinearizationInfo(
            id,
            dictionaryStart,
            parser.Position,
            originalLength,
            checked((int)firstPageObject),
            checked((int)firstPageEnd),
            checked((int)pageCount),
            checked((int)mainFirstEntryWhitespace),
            checked((int)firstPageNumber),
            primaryHint,
            overflowHint);
    }

    private static bool LooksLikeStandaloneLinearizationDictionary(
        ReadOnlySpan<byte> data,
        int position)
    {
        var lexer = new PdfLexer(data, position);
        if (lexer.Read() != PdfTokenType.DictionaryStart)
            return false;

        int depth = 1;
        bool found = false;
        while (depth > 0 && lexer.Position <= 1024)
        {
            PdfTokenType token = lexer.Read();
            if (token == PdfTokenType.EndOfInput)
                return false;

            if (token == PdfTokenType.DictionaryStart)
            {
                depth++;
            }
            else if (token == PdfTokenType.DictionaryEnd)
            {
                depth--;
            }
            else if (depth == 1 && token == PdfTokenType.Name &&
                     lexer.NameValue().AsSpan().SequenceEqual("Linearized"u8))
            {
                found = true;
            }
        }

        if (!found || depth != 0 || lexer.Position > 1024)
            return false;

        return lexer.Read() is not PdfTokenType.Keyword || !lexer.KeywordIs("stream"u8);
    }

    private static bool IsFullyDirect(PdfValue value)
    {
        if (value.IsReference || value.Kind == PdfValueKind.Stream)
            return false;

        if (value.AsArray is { } array)
        {
            foreach (PdfValue item in array.AsSpan())
            {
                if (!IsFullyDirect(item))
                    return false;
            }
        }
        else if (value.AsDictionary is { } dictionary)
        {
            foreach (KeyValuePair<PdfName, PdfValue> entry in dictionary)
            {
                if (!IsFullyDirect(entry.Value))
                    return false;
            }
        }

        return true;
    }

    private static bool TryGetDirectInteger(
        PdfDictionary dictionary,
        PdfName key,
        long minimum,
        long maximum,
        out long result)
    {
        result = 0;
        if (!dictionary.TryGetRaw(key, out PdfValue value) ||
            value.Kind != PdfValueKind.Integer ||
            value.AsInteger < minimum || value.AsInteger > maximum)
        {
            return false;
        }

        result = value.AsInteger;
        return true;
    }

    private static bool TryGetLinearizedHintRanges(
        PdfDictionary dictionary,
        PdfName key,
        long declaredLength,
        out ProtectedHintRange primary,
        out ProtectedHintRange? overflow)
    {
        primary = default;
        overflow = null;
        if (!dictionary.TryGetRaw(key, out PdfValue value) ||
            value.AsArray is not { } ranges ||
            ranges.Count is not (2 or 4))
        {
            return false;
        }

        ReadOnlySpan<PdfValue> items = ranges.AsSpan();
        for (int i = 0; i < items.Length; i += 2)
        {
            if (items[i].Kind != PdfValueKind.Integer ||
                items[i + 1].Kind != PdfValueKind.Integer ||
                items[i].AsInteger <= 0 ||
                items[i + 1].AsInteger <= 0 ||
                items[i].AsInteger > declaredLength - items[i + 1].AsInteger)
            {
                return false;
            }
        }

        primary = new ProtectedHintRange(
            checked((int)items[0].AsInteger),
            checked((int)items[1].AsInteger));
        if (items.Length == 4)
        {
            overflow = new ProtectedHintRange(
                checked((int)items[2].AsInteger),
                checked((int)items[3].AsInteger));
        }

        return true;
    }

    private void WalkChain(long start)
    {
        var visited = new HashSet<long>();
        long offset = start;
        long? newerProtectedSize = null;
        long pendingHistoricalPrevTarget = -1;
        int pendingHistoricalNewerBoundary = -1;
        int pendingHistoricalNewerRevision = -1;
        int sections = 0;
        int revision = 0;

        while (offset >= 0 && offset < _data.Length)
        {
            if (_options.StreamDecodePolicy is { } policy && ++sections > policy.MaxStructuralEntries)
            {
                throw new PdfFormatException(
                    $"The cross-reference chain exceeds the protected reader limit of " +
                    $"{policy.MaxStructuralEntries} sections.");
            }

            if (!visited.Add(offset))
            {
                if (_options.StreamDecodePolicy is not null)
                    throw new PdfFormatException("The cross-reference Prev chain contains a loop.");

                Diagnostics.Add(PdfWarningCode.XrefLoop, "The cross-reference chain loops.", offset: offset);
                return;
            }

            _protectedCurrentXrefRevision = revision;
            PdfDictionary? trailer = ReadSection(offset, out bool isStream);
            int currentSectionEnd = _protectedSectionEnd;
            if (_options.StreamDecodePolicy is not null && sections == 1)
                _protectedFinalSectionEnd = currentSectionEnd;

            if (trailer is null)
            {
                if (_options.StreamDecodePolicy is not null)
                    throw new PdfFormatException($"The cross-reference section at byte {offset} is malformed.");

                Diagnostics.Add(PdfWarningCode.BadXrefSection,
                    "A cross-reference section could not be read.", offset: offset);
                return;
            }

            if (_options.StreamDecodePolicy is not null &&
                pendingHistoricalPrevTarget == offset)
            {
                ValidateProtectedHistoricalPrevTarget(
                    currentSectionEnd,
                    (int)offset,
                    pendingHistoricalNewerBoundary,
                    pendingHistoricalNewerRevision);
                pendingHistoricalPrevTarget = -1;
                pendingHistoricalNewerBoundary = -1;
                pendingHistoricalNewerRevision = -1;
            }

            bool isLinearizedMainSection =
                _options.StreamDecodePolicy is not null &&
                offset == _protectedLinearizedMainSectionOffset;
            if (isLinearizedMainSection)
            {
                if (trailer.ContainsKey(PdfNames.Prev))
                {
                    throw new PdfFormatException(
                        "A protected linearized PDF requires its main cross-reference section " +
                        "to have no Prev entry.");
                }

                ValidateProtectedSectionTail(
                    currentSectionEnd,
                    _protectedLinearizedOriginalStartXrefKeyword,
                    "A protected linearized PDF requires its main cross-reference section " +
                    "to precede the original startxref directly.");

                if (StartXref == _protectedLinearizedFirstSectionOffset &&
                    _data.Length == _protectedLinearizedOriginalLength)
                {
                    _protectedFinalSectionEnd = currentSectionEnd;
                }
            }

            long currentProtectedSize = 0;
            if (_options.StreamDecodePolicy is not null)
            {
                currentProtectedSize = GetProtectedSectionSize(trailer);
                if (newerProtectedSize is { } newerSize && currentProtectedSize > newerSize)
                {
                    throw new PdfFormatException(
                        "A protected cross-reference section's Size does not cover the full " +
                        "history inherited from its previous section.");
                }

                newerProtectedSize = currentProtectedSize;
            }

            MergeTrailer(trailer);

            // A hybrid-reference file hides some objects from pre-1.5 readers in a parallel stream.
            if (!isStream && trailer.TryGetRaw(PdfNames.XRefStm, out PdfValue hybrid))
            {
                long hybridOffset = hybrid.IsNumber ? hybrid.AsInteger : -1;
                if (_options.StreamDecodePolicy is not null &&
                    (hybrid.Kind != PdfValueKind.Integer ||
                     hybridOffset <= 0 || hybridOffset >= _data.Length ||
                     hybridOffset >= offset ||
                     !visited.Add(hybridOffset)))
                {
                    throw new PdfFormatException(
                        "The protected reader found a malformed, non-earlier, or looping " +
                        "XRefStm entry.");
                }

                if (hybridOffset > 0 && hybridOffset < _data.Length &&
                    (_options.StreamDecodePolicy is not null || visited.Add(hybridOffset)))
                {
                    PdfDictionary? hybridSection = ReadSection(hybridOffset, out bool hybridIsStream);
                    if (_options.StreamDecodePolicy is not null &&
                        (hybridSection is null || !hybridIsStream))
                    {
                        throw new PdfFormatException(
                            "The protected reader's XRefStm entry does not point to an xref stream.");
                    }

                    if (_options.StreamDecodePolicy is not null &&
                        GetProtectedSectionSize(hybridSection!) != currentProtectedSize)
                    {
                        throw new PdfFormatException(
                            "A protected hybrid-reference section requires equivalent Size " +
                            "entries in its trailer and XRefStm dictionary.");
                    }
                }
            }

            if (trailer.TryGetRaw(PdfNames.Prev, out PdfValue previous))
            {
                long previousOffset = previous.IsNumber ? previous.AsInteger : -1;
                bool linearizedForwardLink =
                    _options.StreamDecodePolicy is not null &&
                    previous.Kind == PdfValueKind.Integer &&
                    CanFollowProtectedLinearizedForwardLink(offset, previousOffset);
                if (_options.StreamDecodePolicy is not null &&
                    (previous.Kind != PdfValueKind.Integer ||
                     previousOffset <= 0 || previousOffset >= _data.Length ||
                     (previousOffset >= offset && !linearizedForwardLink)))
                {
                    throw new PdfFormatException(
                        "The protected reader found an invalid or non-earlier Prev chain entry.");
                }


                if (linearizedForwardLink)
                {
                    _protectedLinearizedForwardLinkFollowed = true;
                    _protectedLinearizedMainSectionOffset = previousOffset;
                }
                else if (_options.StreamDecodePolicy is not null)
                {
                    pendingHistoricalPrevTarget = previousOffset;
                    pendingHistoricalNewerBoundary = (int)offset;
                    pendingHistoricalNewerRevision = revision;
                    revision++;
                }

                offset = previousOffset;
            }
            else
            {
                if (_options.StreamDecodePolicy is not null && !isLinearizedMainSection)
                {
                    RecordProtectedRevisionBody(
                        revision,
                        FindProtectedHeaderLineEnd(),
                        checked((int)offset));
                }

                offset = -1;
            }
        }
    }

    private void ValidateProtectedHistoricalPrevTarget(
        int sectionEnd,
        int expectedOffset,
        int newerSectionOffset,
        int newerRevision)
    {
        // An incrementally updated linearized file points back to the original first-page xref.
        // Its historical startxref is at /L rather than directly after that early section; the
        // Annex-F recognizer already proved the exact original tail and offset.
        if (expectedOffset == _protectedLinearizedFirstSectionOffset &&
            _protectedLinearizedOriginalLength > 0 &&
            _protectedLinearizedOriginalLength <= newerSectionOffset)
        {
            RecordProtectedRevisionBody(
                newerRevision,
                _protectedLinearizedOriginalLength,
                newerSectionOffset);
            return;
        }

        if (sectionEnd < 0 || newerSectionOffset <= sectionEnd)
            throw HistoricalPrevFailure(expectedOffset);

        int position = sectionEnd;
        while (position < newerSectionOffset)
        {
            if (PdfCharacters.IsWhitespace(_data[position]))
            {
                position++;
                continue;
            }

            if (_data[position] != (byte)'%' || _data.AsSpan(position).StartsWith("%%EOF"u8))
                break;

            while (position < newerSectionOffset && !PdfCharacters.IsEndOfLine(_data[position]))
                position++;
        }

        if (position >= newerSectionOffset ||
            !TryReadProtectedHistoricalStartXref(
                _data.AsSpan(0, newerSectionOffset),
                position,
                out long recordedOffset,
                out int previousEofEnd) ||
            recordedOffset != expectedOffset)
        {
            throw HistoricalPrevFailure(expectedOffset);
        }

        RecordProtectedRevisionBody(newerRevision, previousEofEnd, newerSectionOffset);
    }

    private static PdfFormatException HistoricalPrevFailure(int expectedOffset) =>
        new(
            $"Protected Prev target {expectedOffset} has no directly associated historical " +
            "startxref and %%EOF marker before the next revision.");

    private int FindProtectedHeaderLineEnd()
    {
        if (_headerOffset is < 0 or > int.MaxValue)
            throw new PdfFormatException("A protected PDF has no usable header-line boundary.");

        int position = (int)_headerOffset;
        if (position > _data.Length - "%PDF-".Length ||
            !_data.AsSpan(position).StartsWith("%PDF-"u8))
        {
            throw new PdfFormatException("A protected PDF has no usable header-line boundary.");
        }

        while (position < _data.Length && !PdfCharacters.IsEndOfLine(_data[position]))
            position++;

        if (!TryConsumeEndOfLine(_data, ref position))
            throw new PdfFormatException("A protected PDF header line has no end-of-line marker.");

        return position;
    }

    private void RecordProtectedRevisionBody(int revision, int start, int primaryXrefStart)
    {
        if (revision < 0 || start < 0 || primaryXrefStart < start ||
            primaryXrefStart > _data.Length)
        {
            throw new PdfFormatException("A protected incremental revision has an invalid body range.");
        }

        foreach (ProtectedRevisionBody body in _protectedRevisionBodies)
        {
            if (body.Revision != revision)
                continue;

            if (body.Start != start || body.PrimaryXrefStart != primaryXrefStart)
            {
                throw new PdfFormatException(
                    "A protected incremental revision has inconsistent body boundaries.");
            }

            return;
        }

        _protectedRevisionBodies.Add(new ProtectedRevisionBody(revision, start, primaryXrefStart));
    }

    private static bool TryReadProtectedHistoricalStartXref(
        ReadOnlySpan<byte> data,
        int keywordStart,
        out long offset,
        out int endPosition)
    {
        offset = -1;
        endPosition = keywordStart;
        if (keywordStart < 0 ||
            keywordStart > data.Length - "startxref".Length ||
            (keywordStart > 0 && !PdfCharacters.IsEndOfLine(data[keywordStart - 1])) ||
            !data[keywordStart..].StartsWith("startxref"u8))
        {
            return false;
        }

        int position = keywordStart + "startxref".Length;
        if (!TryConsumeEndOfLine(data, ref position))
            return false;

        int offsetStart = position;
        while (position < data.Length && data[position] is >= (byte)'0' and <= (byte)'9')
            position++;

        if (!TryParseUnsigned(data[offsetStart..position], out offset) ||
            !TryConsumeEndOfLine(data, ref position) ||
            position > data.Length - 5 ||
            !data[position..].StartsWith("%%EOF"u8))
        {
            return false;
        }

        position += 5;
        if (position < data.Length && !PdfCharacters.IsEndOfLine(data[position]))
            return false;

        if (position < data.Length)
            _ = TryConsumeEndOfLine(data, ref position);

        endPosition = position;
        return true;
    }

    private PdfDictionary? ReadSection(long offset, out bool isStream)
    {
        isStream = false;
        _protectedSectionEnd = -1;
        _protectedSectionObjectNumber = -1;

        var lexer = new PdfLexer(_data, (int)offset);
        PdfTokenType token = lexer.Read();
        if (_options.StreamDecodePolicy is not null &&
            (lexer.TokenStart != offset ||
             (offset > 0 && PdfCharacters.IsRegular(_data[(int)offset - 1]))))
        {
            throw new PdfFormatException(
                "A protected cross-reference offset shall point exactly at its first token.");
        }

        PdfDictionary? result;
        if (token == PdfTokenType.Keyword && lexer.KeywordIs("xref"u8))
        {
            result = ReadXrefTable(lexer.Position, checked((int)offset));
        }
        else
        {
            if (_options.StreamDecodePolicy is not null &&
                _protectedLinearization is not null &&
                (offset == _protectedLinearizedFirstSectionOffset ||
                 offset == _protectedLinearizedMainSectionOffset))
            {
                throw new PdfFormatException(
                    "Protected Annex F validation currently requires classic first-page and " +
                    "main xref tables; linearized xref streams are rejected fail-closed.");
            }

            isStream = true;
            result = ReadXrefStream(offset);
        }

        if (_options.StreamDecodePolicy is not null && result is not null)
        {
            if (offset > int.MaxValue || _protectedSectionEnd <= offset)
            {
                throw new PdfFormatException(
                    "A protected cross-reference section has no exact byte range.");
            }

            _protectedXrefSections.Add(
                new ProtectedXrefSectionRange(
                    (int)offset,
                    _protectedSectionEnd,
                    isStream,
                    _protectedSectionObjectNumber,
                    _protectedCurrentXrefRevision));
        }

        return result;
    }

    private PdfDictionary? ReadXrefTable(int position, int sectionStart)
    {
        if (_options.StreamDecodePolicy is not null)
            return ReadProtectedXrefTable(position, sectionStart);

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
                if (_options.StreamDecodePolicy is not null &&
                    trailerValue.Kind != PdfValueKind.Dictionary)
                {
                    throw new PdfFormatException("A protected classic xref trailer is not a dictionary.");
                }

                return trailerValue.AsDictionary ?? new PdfDictionary { Resolver = this };
            }

            if (token != PdfTokenType.Integer)
                return null;

            double firstValue = lexer.NumberValue;
            int first = (int)firstValue;
            if (lexer.Read() != PdfTokenType.Integer)
                return null;

            double countValue = lexer.NumberValue;
            int count = (int)countValue;
            if (count < 0 || count > _data.Length / 18 + 16)
                return null;
            if (_options.StreamDecodePolicy is not null &&
                (firstValue is < 0 or > int.MaxValue ||
                 countValue is < 0 or > int.MaxValue ||
                 (long)first + count > int.MaxValue))
            {
                throw new PdfFormatException("A protected classic xref subsection has an invalid range.");
            }

            bool strict = _options.StreamDecodePolicy is not null;
            if (strict && count > 0)
            {
                // Charge every declared row before walking it. Free rows consume parser time just
                // like live rows, and must not bypass the shared structural-work ledger.
                _options.StreamDecodePolicy!.ConsumeStructuralEntries(count);
            }

            for (int i = 0; i < count; i++)
            {
                int entryStart = lexer.Position;
                if (lexer.Read() != PdfTokenType.Integer)
                {
                    if (_options.StreamDecodePolicy is not null)
                        throw new PdfFormatException("A protected classic xref subsection is truncated.");

                    lexer.Position = entryStart;
                    break;
                }

                double entryOffsetValue = lexer.NumberValue;
                long entryOffset = (long)entryOffsetValue;

                if (lexer.Read() != PdfTokenType.Integer)
                {
                    if (_options.StreamDecodePolicy is not null)
                        throw new PdfFormatException("A protected classic xref entry has no generation number.");

                    break;
                }

                double generationValue = lexer.NumberValue;
                int generation = (int)generationValue;

                if (lexer.Read() != PdfTokenType.Keyword)
                {
                    if (_options.StreamDecodePolicy is not null)
                        throw new PdfFormatException("A protected classic xref entry has no n/f flag.");

                    break;
                }

                if (_options.StreamDecodePolicy is not null &&
                    (!lexer.TokenSpan.SequenceEqual("n"u8) && !lexer.TokenSpan.SequenceEqual("f"u8)))
                {
                    throw new PdfFormatException("A protected classic xref entry has a flag other than n or f.");
                }

                bool free = lexer.TokenSpan.SequenceEqual("f"u8);
                if (strict &&
                    (generationValue is < 0 or > ushort.MaxValue ||
                     entryOffsetValue < 0 ||
                     (!free && entryOffsetValue >= _data.Length) ||
                     (free && entryOffsetValue > int.MaxValue)))
                {
                    throw new PdfFormatException(
                        "A protected classic xref entry has an out-of-range offset or generation.");
                }

                int number = first + i;

                // A well-known malformation numbers the first subsection from one; the free-list
                // head gives it away, and every entry after it is off by one.
                if (!strict && i == 0 && first == 1 && free && generation == 65535)
                    number = 0;

                if (number > 0)
                {
                    TryAddEntry(
                        number,
                        free
                            ? XrefEntry.Free(entryOffset, generation)
                            : XrefEntry.Normal(entryOffset, generation),
                        chargePolicy: !strict);
                }
            }
        }
    }

    private bool CanFollowProtectedLinearizedForwardLink(long currentOffset, long previousOffset) =>
        !_protectedLinearizedForwardLinkFollowed &&
        currentOffset == _protectedLinearizedFirstSectionOffset &&
        previousOffset > currentOffset &&
        previousOffset < _protectedLinearizedOriginalStartXrefKeyword &&
        previousOffset < _protectedLinearizedOriginalLength;

    private static long GetProtectedSectionSize(PdfDictionary section)
    {
        if (!section.TryGetRaw(PdfNames.Size, out PdfValue size) ||
            size.Kind != PdfValueKind.Integer ||
            size.AsInteger is <= 0 or > int.MaxValue)
        {
            throw new PdfFormatException(
                "A protected cross-reference section requires a direct positive integer Size entry.");
        }

        return size.AsInteger;
    }

    /// <summary>
    /// Reads the one deliberately fixed-width part of PDF without routing it through the general
    /// lexer. ISO 32000-2 §7.5.4 requires every classic xref row to occupy exactly 20 bytes; letting
    /// whitespace tokenisation normalise a malformed row would make protected validation accept a
    /// file whose object locations were not represented by the required grammar.
    /// </summary>
    private PdfDictionary ReadProtectedXrefTable(int position, int sectionStart)
    {
        if (!TryConsumeEndOfLine(_data, ref position))
            throw new PdfFormatException("A protected classic xref keyword is not alone on its line.");

        bool captureLinearizedShape =
            _protectedLinearization is not null &&
            (sectionStart == _protectedLinearizedFirstSectionOffset ||
             sectionStart == _protectedLinearizedMainSectionOffset);
        List<(int First, int Count)>? subsections = captureLinearizedShape ? [] : null;
        Dictionary<int, ProtectedClassicXrefRow>? rows = captureLinearizedShape ? [] : null;
        HashSet<int> declaredObjects = [];
        while (position < _data.Length)
        {
            int lineStart = position;
            while (position < _data.Length && !PdfCharacters.IsEndOfLine(_data[position]))
                position++;

            if (position >= _data.Length)
                throw new PdfFormatException("A protected classic xref section is truncated.");

            ReadOnlySpan<byte> line = _data.AsSpan(lineStart, position - lineStart);
            if (!TryConsumeEndOfLine(_data, ref position))
                throw new PdfFormatException("A protected classic xref line has no end-of-line marker.");

            if (line.SequenceEqual("trailer"u8))
            {
                var parser = new PdfObjectParser(_data, position, this, this);
                PdfValue trailerValue = parser.ParseValue();
                if (trailerValue.Kind != PdfValueKind.Dictionary)
                    throw new PdfFormatException("A protected classic xref trailer is not a dictionary.");

                PdfDictionary trailer = trailerValue.AsDictionary!;
                long localSize = ValidateProtectedTrailerSize(trailer);
                foreach (int number in declaredObjects)
                {
                    if (number >= localSize)
                    {
                        throw new PdfFormatException(
                            "A protected classic xref section declares an object number outside " +
                            "its local Size entry.");
                    }
                }

                if (subsections is not null && rows is not null)
                {
                    CaptureProtectedLinearizedClassicXref(
                        sectionStart,
                        subsections,
                        rows,
                        trailer);
                }

                _protectedSectionEnd = parser.Position;
                return trailer;
            }

            int separator = line.IndexOf((byte)' ');
            if (separator <= 0 || separator != line.LastIndexOf((byte)' ') ||
                !TryParseUnsigned(line[..separator], out long firstValue) ||
                !TryParseUnsigned(line[(separator + 1)..], out long countValue) ||
                firstValue > int.MaxValue || countValue > int.MaxValue ||
                firstValue + countValue > int.MaxValue)
            {
                throw new PdfFormatException(
                    "A protected classic xref subsection header must contain two integers " +
                    "separated by one SPACE.");
            }

            int first = (int)firstValue;
            int count = (int)countValue;
            if (count > _data.Length / 20 + 1)
                throw new PdfFormatException("A protected classic xref subsection has an unsafe row count.");

            subsections?.Add((first, count));
            if (count > 0)
                _options.StreamDecodePolicy!.ConsumeStructuralEntries(count);

            for (int i = 0; i < count; i++)
            {
                int rowStart = position;
                if (position + 20 > _data.Length ||
                    !TryParseClassicXrefRow(
                        _data.AsSpan(position, Math.Min(20, _data.Length - position)),
                        out long entryOffset,
                        out int generation,
                        out bool free))
                {
                    throw new PdfFormatException(
                        "A protected classic xref entry does not use the fixed 20-byte grammar.");
                }

                position += 20;
                if (position < _data.Length && PdfCharacters.IsEndOfLine(_data[position]))
                {
                    throw new PdfFormatException(
                        "A protected classic xref entry does not use the fixed 20-byte grammar.");
                }

                int number = first + i;
                if (!declaredObjects.Add(number))
                {
                    throw new PdfFormatException(
                        "A protected classic xref section contains the same object in multiple subsections.");
                }

                if (generation > ushort.MaxValue ||
                    (!free && entryOffset >= _data.Length) ||
                    (free && entryOffset > int.MaxValue))
                {
                    throw new PdfFormatException(
                        "A protected classic xref entry has an out-of-range offset or generation.");
                }

                if (number == 0)
                {
                    if (free && generation == ushort.MaxValue)
                    {
                        _protectedObjectZeroSeen = true;
                        _protectedObjectZeroEntry ??= XrefEntry.Free(entryOffset, generation);
                    }
                    else
                        _protectedObjectZeroInvalid = true;
                }

                rows?.Add(
                    number,
                    new ProtectedClassicXrefRow(entryOffset, generation, free, rowStart));

                XrefEntry parsedEntry = free
                    ? XrefEntry.Free(entryOffset, generation)
                    : XrefEntry.Normal(entryOffset, generation);
                bool isEffective = number > 0 &&
                    TryAddEntry(number, parsedEntry, chargePolicy: false);
                JournalProtectedXrefRow(number, parsedEntry, isEffective);
            }
        }

        throw new PdfFormatException("A protected classic xref section has no trailer.");
    }

    private void CaptureProtectedLinearizedClassicXref(
        int sectionStart,
        List<(int First, int Count)> subsections,
        Dictionary<int, ProtectedClassicXrefRow> rows,
        PdfDictionary trailer)
    {
        var journal = new ProtectedClassicXrefJournal(sectionStart, subsections, rows, trailer);
        if (sectionStart == _protectedLinearizedFirstSectionOffset)
        {
            _protectedLinearizedFirstXref = journal;
            ValidateProtectedLinearizedFirstXref(journal);
            return;
        }

        if (sectionStart == _protectedLinearizedMainSectionOffset)
        {
            _protectedLinearizedMainXref = journal;
            ValidateProtectedLinearizedMainXref(journal);
        }
    }

    private void ValidateProtectedLinearizedFirstXref(ProtectedClassicXrefJournal journal)
    {
        ProtectedLinearizationInfo info = _protectedLinearization!;
        if (journal.Subsections.Count != 1)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires one first-page xref subsection.");
        }

        int count = journal.Subsections[0].Count;
        if (count <= 0 || journal.Rows.Count != count)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires an exact non-empty first-page xref subsection.");
        }

        if (journal.Rows.Values.Any(static row => row.Free))
        {
            throw new PdfFormatException(
                "A protected linearized PDF permits no free row in its first-page xref subsection.");
        }

        if (journal.Rows.Values.Any(static row => row.Generation != 0))
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires generation zero for every first-page xref row.");
        }

        if (!journal.Trailer.TryGetRaw(PdfNames.Root, out PdfValue root) || !root.IsReference)
        {
            throw new PdfFormatException(
                "A protected linearized first-page trailer requires an indirect Root entry.");
        }

        if (!journal.Rows.TryGetValue(info.DictionaryId.Number, out ProtectedClassicXrefRow dictionaryRow) ||
            dictionaryRow.Generation != info.DictionaryId.Generation ||
            dictionaryRow.Offset != info.DictionaryStart)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires an exact normal first-page xref row for " +
                "its linearization dictionary.");
        }

        if (!journal.Rows.TryGetValue(info.FirstPageObjectNumber, out ProtectedClassicXrefRow pageRow) ||
            pageRow.Generation != 0)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires a normal generation-zero first-page xref " +
                "row for O.");
        }

        int primaryNumber = FindProtectedHintRow(journal, info.PrimaryHint.Offset);
        if (info.OverflowHint is { } overflow)
        {
            int overflowNumber = FindProtectedHintRow(journal, overflow.Offset);
            int highest = journal.Rows.Keys.Max();
            if (primaryNumber == overflowNumber ||
                Math.Min(primaryNumber, overflowNumber) != highest - 1 ||
                Math.Max(primaryNumber, overflowNumber) != highest)
            {
                throw new PdfFormatException(
                    "A protected linearized PDF requires its hint-stream rows at the end of " +
                    "the first-page xref subsection.");
            }
        }
        else if (primaryNumber != journal.Rows.Keys.Max())
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires the primary hint-stream row at the end " +
                "of the first-page xref subsection.");
        }
    }

    private static int FindProtectedHintRow(
        ProtectedClassicXrefJournal journal,
        int hintOffset)
    {
        int number = -1;
        foreach (KeyValuePair<int, ProtectedClassicXrefRow> item in journal.Rows)
        {
            if (item.Value.Free || item.Value.Generation != 0 || item.Value.Offset != hintOffset)
                continue;

            if (number >= 0)
            {
                throw new PdfFormatException(
                    "A protected linearized PDF maps multiple first-page xref rows to one hint stream.");
            }

            number = item.Key;
        }

        if (number < 0)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires an exact normal first-page xref row for " +
                "each hint stream.");
        }

        return number;
    }

    private void ValidateProtectedLinearizationStructure()
    {
        if (_protectedLinearization is not { } info)
            return;

        if (_protectedLinearizedFirstXref is not { } firstXref ||
            _protectedLinearizedMainXref is not { } mainXref)
        {
            throw new PdfFormatException(
                "Protected Annex F validation requires exact classic first-page and main xref journals.");
        }

        (int firstGroupStart, int firstGroupCount) = firstXref.Subsections[0];
        (int mainGroupStart, int mainGroupCount) = mainXref.Subsections[0];
        if (mainGroupStart != 0 || firstGroupStart != mainGroupCount)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires adjacent original object-number groups: " +
                "main from zero, then the first-page group.");
        }

        long expectedCombinedSize = (long)firstGroupStart + firstGroupCount;
        if (firstXref.Trailer.GetInteger(PdfNames.Size) != expectedCombinedSize)
        {
            throw new PdfFormatException(
                "A protected linearized first-page trailer Size shall equal the combined " +
                "original object count.");
        }

        var infrastructure = new HashSet<PdfObjectId> { info.DictionaryId };
        ValidateProtectedHintRange(info.PrimaryHint, firstXref, infrastructure, primary: true);
        if (info.OverflowHint is { } overflow)
            ValidateProtectedHintRange(overflow, firstXref, infrastructure, primary: false);

        ValidateProtectedFirstPageRange(info, firstXref);
        ValidateNoProtectedInfrastructureReferences(infrastructure);
    }

    private void ValidateProtectedHintRange(
        ProtectedHintRange hint,
        ProtectedClassicXrefJournal firstXref,
        HashSet<PdfObjectId> infrastructure,
        bool primary)
    {
        int declaredLength = _protectedLinearization!.DeclaredLength;
        int hintNumber = FindProtectedHintRow(firstXref, hint.Offset);
        ProtectedClassicXrefRow hintRow = firstXref.Rows[hintNumber];
        var hintId = new PdfObjectId(hintNumber, hintRow.Generation);
        infrastructure.Add(hintId);

        if (!_protectedObjectRanges.TryGetValue(hintNumber, out var hintObjectRange) ||
            hintObjectRange.Start != hint.Offset ||
            GetObject(hintId).AsStream is not { } hintStream)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires H to identify an indexed hint stream object.");
        }

        if (!hintStream.Dictionary.TryGetRaw(PdfNames.Length, out PdfValue rawLength))
        {
            throw new PdfFormatException(
                "A protected linearized hint stream requires a Length entry.");
        }

        ValidateProtectedHintDictionary(hintStream.Dictionary, primary);

        int lastObjectStart = hintObjectRange.Start;
        int lastObjectEnd = hintObjectRange.End;
        if (rawLength.IsReference)
        {
            PdfObjectId lengthId = rawLength.AsReference;
            if (!_protectedObjectRanges.TryGetValue(lengthId.Number, out var lengthRange) ||
                !firstXref.Rows.TryGetValue(lengthId.Number, out ProtectedClassicXrefRow lengthRow) ||
                lengthRow.Free ||
                lengthRow.Generation != lengthId.Generation ||
                lengthRow.Offset != lengthRange.Start ||
                GetObject(lengthId).Kind != PdfValueKind.Integer ||
                FindNextProtectedStructuralStart(lastObjectStart, declaredLength) != lengthRange.Start ||
                lengthRange.Start < lastObjectEnd ||
                !ContainsOnlyProtectedHintSeparator(
                    _data.AsSpan(lastObjectEnd, lengthRange.Start - lastObjectEnd)))
            {
                throw new PdfFormatException(
                    "A protected linearized hint stream's indirect Length object shall " +
                    "immediately follow the stream object.");
            }

            lastObjectStart = lengthRange.Start;
            lastObjectEnd = lengthRange.End;
        }
        else if (rawLength.Kind != PdfValueKind.Integer)
        {
            throw new PdfFormatException(
                "A protected linearized hint stream requires an integer or indirect Length.");
        }

        int expectedEnd = FindNextProtectedStructuralStart(
            lastObjectStart,
            declaredLength);
        if (expectedEnd < lastObjectEnd ||
            !ContainsOnlyProtectedHintSeparator(
                _data.AsSpan(lastObjectEnd, expectedEnd - lastObjectEnd)) ||
            (long)hint.Offset + hint.Length != expectedEnd)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires H length to end exactly at the next " +
                "physical object or structural boundary.");
        }
    }

    private static void ValidateProtectedHintDictionary(PdfDictionary dictionary, bool primary)
    {
        foreach (PdfName key in ProtectedStandardHintTableKeys)
        {
            if (!dictionary.TryGetRaw(key, out PdfValue position))
                continue;

            if (!primary)
            {
                throw new PdfFormatException(
                    "A protected linearized overflow hint stream shall not contain standard " +
                    "hint-table position entries.");
            }

            if (position.Kind != PdfValueKind.Integer || position.AsInteger < 0)
            {
                throw new PdfFormatException(
                    "A protected linearized primary hint-table position shall be a direct " +
                    "non-negative integer.");
            }
        }

        if (primary &&
            (!dictionary.TryGetRaw(PdfNames.S, out PdfValue sharedPosition) ||
             sharedPosition.Kind != PdfValueKind.Integer ||
             sharedPosition.AsInteger < 0))
        {
            throw new PdfFormatException(
                "A protected linearized primary hint stream requires a direct non-negative S entry.");
        }

        foreach (KeyValuePair<PdfName, PdfValue> item in dictionary)
        {
            if (item.Key != PdfNames.Length && ContainsAnyIndirectReference(item.Value, depth: 0))
            {
                throw new PdfFormatException(
                    "A protected linearized hint-stream dictionary may use an indirect reference " +
                    "only for Length.");
            }
        }
    }

    private static bool ContainsAnyIndirectReference(PdfValue value, int depth)
    {
        if (depth > 128)
            throw new PdfFormatException("A protected linearized hint dictionary is nested too deeply.");

        if (value.IsReference)
            return true;

        if (value.AsArray is { } array)
        {
            foreach (PdfValue item in array.AsSpan())
            {
                if (ContainsAnyIndirectReference(item, depth + 1))
                    return true;
            }

            return false;
        }

        if (value.AsDictionary is { } dictionary)
        {
            foreach (KeyValuePair<PdfName, PdfValue> item in dictionary)
            {
                if (ContainsAnyIndirectReference(item.Value, depth + 1))
                    return true;
            }
        }

        return false;
    }

    private int FindNextProtectedStructuralStart(int objectStart, int infoLimit)
    {
        int next = infoLimit;
        foreach (var range in _protectedObjectRanges.Values)
        {
            if (range.Start > objectStart && range.Start < next)
                next = range.Start;
        }

        foreach (ProtectedXrefSectionRange section in _protectedXrefSections)
        {
            if (section.Start > objectStart && section.Start < next)
                next = section.Start;
        }

        int originalTail = _protectedLinearizedOriginalStartXrefKeyword;
        if (originalTail > objectStart && originalTail < next)
            next = originalTail;

        return next;
    }

    private void ValidateProtectedFirstPageRange(
        ProtectedLinearizationInfo info,
        ProtectedClassicXrefJournal firstXref)
    {
        if (!firstXref.Rows.TryGetValue(
                info.FirstPageObjectNumber,
                out ProtectedClassicXrefRow firstPageRow) ||
            !_protectedObjectRanges.TryGetValue(info.FirstPageObjectNumber, out var pageRange) ||
            firstPageRow.Offset != pageRange.Start ||
            GetObject(new PdfObjectId(
                info.FirstPageObjectNumber,
                firstPageRow.Generation)).AsDictionary is not { } page ||
            page.GetName(PdfNames.Type) != PdfNames.Page)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires O to identify the Page object that starts Part 6.");
        }

        PdfValue resources = page.Get(PdfNames.Resources);
        if (resources.Kind != PdfValueKind.Dictionary)
        {
            throw new PdfFormatException(
                "A protected linearized first page shall explicitly specify a Resources dictionary.");
        }

        if (!IsUsableProtectedMediaBox(page.Get(PdfNames.MediaBox)))
        {
            throw new PdfFormatException(
                "A protected linearized first page shall explicitly specify a finite, " +
                "nondegenerate four-number MediaBox.");
        }

        int end = info.FirstPageEnd;
        if (end <= pageRange.Start || end > info.DeclaredLength ||
            !IsProtectedStructuralStart(end, info.DeclaredLength))
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires E to identify an exact structural boundary.");
        }

        List<(int Number, int Start, int End)> objects = [];
        foreach (KeyValuePair<int, (int Start, int End)> item in _protectedObjectRanges)
        {
            if (item.Value.Start < pageRange.Start || item.Value.Start >= end)
                continue;

            if (!firstXref.Rows.TryGetValue(item.Key, out ProtectedClassicXrefRow row) ||
                row.Free || row.Offset != item.Value.Start)
            {
                throw new PdfFormatException(
                    "A protected linearized first-page byte range contains an object outside " +
                    "the first-page xref table.");
            }

            objects.Add((item.Key, item.Value.Start, item.Value.End));
        }

        objects.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        if (objects.Count == 0 ||
            objects[0].Number != info.FirstPageObjectNumber ||
            objects[0].Start != pageRange.Start)
        {
            throw new PdfFormatException(
                "A protected linearized first-page byte range does not start with O.");
        }

        for (int i = 0; i < objects.Count; i++)
        {
            var current = objects[i];
            int nextStart = i + 1 < objects.Count ? objects[i + 1].Start : end;
            if (current.End > nextStart ||
                !ContainsOnlyInterSectionTrivia(
                    _data.AsSpan(current.End, nextStart - current.End)))
            {
                throw new PdfFormatException(
                    "A protected linearized first-page byte range contains bytes that are not " +
                    "indexed first-page objects or inter-object trivia.");
            }
        }

        foreach (ProtectedXrefSectionRange section in _protectedXrefSections)
        {
            if (section.Start > pageRange.Start && section.Start < end)
            {
                throw new PdfFormatException(
                    "A protected linearized first-page byte range overlaps a cross-reference section.");
            }
        }

        ValidateProtectedFirstPageReferenceClosure(info, firstXref, page, pageRange);
    }

    private static bool IsUsableProtectedMediaBox(PdfValue value)
    {
        if (value.AsArray is not { Count: 4 } mediaBox)
            return false;

        Span<double> coordinates = stackalloc double[4];
        for (int i = 0; i < coordinates.Length; i++)
        {
            PdfValue coordinate = mediaBox.Get(i);
            if (!coordinate.IsNumber || !double.IsFinite(coordinate.AsNumber))
                return false;

            coordinates[i] = coordinate.AsNumber;
        }

        return coordinates[0] != coordinates[2] && coordinates[1] != coordinates[3];
    }

    private void ValidateProtectedFirstPageReferenceClosure(
        ProtectedLinearizationInfo info,
        ProtectedClassicXrefJournal firstXref,
        PdfDictionary page,
        (int Start, int End) pageRange)
    {
        var visited = new HashSet<PdfObjectId>();
        int objectLimit = _options.StreamDecodePolicy!.MaxStructuralEntries;
        Visit(PdfValue.Dictionary(page), depth: 0, isRootPageDictionary: true);

        void Visit(PdfValue value, int depth, bool isRootPageDictionary)
        {
            if (depth > 128)
            {
                throw new PdfFormatException(
                    "A protected linearized first-page reference graph is nested too deeply.");
            }

            if (value.IsReference)
            {
                PdfObjectId id = value.AsReference;
                if (!visited.Add(id))
                    return;

                if (visited.Count > objectLimit)
                {
                    throw new PdfFormatException(
                        "A protected linearized first-page reference graph exceeds the " +
                        "structural-entry limit.");
                }

                PdfValue resolved = GetObject(id);
                string? type = resolved.AsDictionary?.GetName(PdfNames.Type)?.Value;
                if (type is "Pages" or "Page" or "DPart")
                    return;

                if (!firstXref.Rows.TryGetValue(id.Number, out ProtectedClassicXrefRow row) ||
                    row.Free || row.Generation != id.Generation ||
                    !_protectedObjectRanges.TryGetValue(id.Number, out var range) ||
                    row.Offset != range.Start ||
                    range.Start < pageRange.Start || range.End > info.FirstPageEnd)
                {
                    throw new PdfFormatException(
                        "A protected linearized first-page reference shall resolve to an exact " +
                        "first-page xref object wholly contained in Part 6.");
                }

                Visit(resolved, depth + 1, isRootPageDictionary: false);
                return;
            }

            if (value.AsArray is { } array)
            {
                foreach (PdfValue item in array.AsSpan())
                    Visit(item, depth + 1, isRootPageDictionary: false);

                return;
            }

            if (value.AsDictionary is not { } dictionary)
                return;

            foreach (KeyValuePair<PdfName, PdfValue> item in dictionary)
            {
                if (isRootPageDictionary && item.Key.Value is "Parent" or "Thumb")
                    continue;

                Visit(item.Value, depth + 1, isRootPageDictionary: false);
            }
        }
    }

    private bool IsProtectedStructuralStart(int position, int declaredLength)
    {
        if (position == declaredLength ||
            position == _protectedLinearizedOriginalStartXrefKeyword)
        {
            return true;
        }

        foreach (var range in _protectedObjectRanges.Values)
        {
            if (range.Start == position)
                return true;
        }

        return _protectedXrefSections.Any(section => section.Start == position);
    }

    private void ValidateNoProtectedInfrastructureReferences(HashSet<PdfObjectId> infrastructure)
    {
        foreach (PdfValue value in _cache.Values)
        {
            if (ContainsProtectedReference(value, infrastructure, depth: 0))
            {
                throw new PdfFormatException(
                    "A protected linearized PDF contains an indirect reference to its " +
                    "linearization dictionary or a hint stream.");
            }
        }

        if (ContainsProtectedReference(
                PdfValue.Dictionary(Trailer),
                infrastructure,
                depth: 0))
        {
            throw new PdfFormatException(
                "A protected linearized PDF trailer references its linearization dictionary " +
                "or a hint stream.");
        }
    }

    private static bool ContainsProtectedReference(
        PdfValue value,
        HashSet<PdfObjectId> targets,
        int depth)
    {
        if (depth > 128)
            throw new PdfFormatException("A protected linearized object graph is nested too deeply.");

        if (value.IsReference)
            return targets.Contains(value.AsReference);

        if (value.AsArray is { } array)
        {
            foreach (PdfValue item in array.AsSpan())
            {
                if (ContainsProtectedReference(item, targets, depth + 1))
                    return true;
            }

            return false;
        }

        if (value.AsDictionary is { } dictionary)
        {
            foreach (KeyValuePair<PdfName, PdfValue> item in dictionary)
            {
                if (ContainsProtectedReference(item.Value, targets, depth + 1))
                    return true;
            }
        }

        return false;
    }

    internal void ValidateProtectedLinearizedPages(IReadOnlyList<global::Inkwright.PdfPage> pages)
    {
        if (_protectedLinearization is not { } info)
            return;

        if (pages.Count != info.PageCount)
        {
            throw new PdfFormatException(
                $"A protected linearized PDF declares N={info.PageCount}, but its current " +
                $"page tree contains {pages.Count} pages.");
        }

        if (info.FirstPageNumber < 0 || info.FirstPageNumber >= pages.Count ||
            pages[info.FirstPageNumber].Id.Number != info.FirstPageObjectNumber)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires O to identify page P in the current " +
                "ordered page tree.");
        }
    }

    private void ValidateProtectedLinearizedMainXref(ProtectedClassicXrefJournal journal)
    {
        ProtectedLinearizationInfo info = _protectedLinearization!;
        if (journal.Subsections.Count != 1 ||
            journal.Subsections[0].First != 0 ||
            journal.Subsections[0].Count <= 0 ||
            journal.Rows.Count != journal.Subsections[0].Count)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires one exact main xref subsection beginning at zero.");
        }

        int subsectionCount = journal.Subsections[0].Count;
        if (journal.Trailer.Count != 1 ||
            !journal.Trailer.TryGetRaw(PdfNames.Size, out PdfValue mainSize) ||
            mainSize.Kind != PdfValueKind.Integer ||
            mainSize.AsInteger != subsectionCount)
        {
            throw new PdfFormatException(
                "A protected linearized main trailer shall contain only Size, equal to its " +
                "subsection count.");
        }

        if (!journal.Rows.TryGetValue(0, out ProtectedClassicXrefRow rowZero) ||
            !rowZero.Free || rowZero.Generation != ushort.MaxValue)
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires a generation-65535 free row for object 0 " +
                "in its main xref table.");
        }

        int expectedWhitespace = rowZero.RowStart - 1;
        if (expectedWhitespace < 0 ||
            info.MainFirstEntryWhitespace != expectedWhitespace ||
            !PdfCharacters.IsWhitespace(_data[expectedWhitespace]))
        {
            throw new PdfFormatException(
                "A protected linearized PDF requires T to identify the whitespace immediately " +
                "before the main xref row for object 0.");
        }

        for (int number = 1; number < subsectionCount; number++)
        {
            if (!journal.Rows.TryGetValue(number, out ProtectedClassicXrefRow row) ||
                row.Free || row.Generation != 0)
            {
                throw new PdfFormatException(
                    "A protected linearized PDF requires consecutive generation-zero in-use " +
                    "rows after object zero in its main xref table.");
            }
        }
    }

    private static long ValidateProtectedTrailerSize(PdfDictionary trailer)
    {
        if (!trailer.TryGetRaw(PdfNames.Size, out PdfValue rawSize) ||
            rawSize.Kind != PdfValueKind.Integer ||
            rawSize.AsInteger is <= 0 or > int.MaxValue)
        {
            throw new PdfFormatException(
                "A protected classic xref trailer requires a direct positive integer Size entry.");
        }

        return rawSize.AsInteger;
    }

    private void ValidateProtectedCombinedSize()
    {
        if (!Trailer.TryGetRaw(PdfNames.Size, out PdfValue rawSize) ||
            rawSize.Kind != PdfValueKind.Integer ||
            rawSize.AsInteger is <= 0 or > int.MaxValue)
        {
            throw new PdfFormatException(
                "A protected cross-reference chain requires a direct positive integer Size entry.");
        }

        int highestObjectNumber = 0;
        foreach (int number in _entries.Keys)
            highestObjectNumber = Math.Max(highestObjectNumber, number);

        if (highestObjectNumber == int.MaxValue || rawSize.AsInteger != highestObjectNumber + 1L)
        {
            throw new PdfFormatException(
                "The protected cross-reference chain's Size entry is not one greater than its " +
                "highest defined object number.");
        }

        if (_entries.Count != rawSize.AsInteger - 1L)
        {
            throw new PdfFormatException(
                "The protected cross-reference chain's Size range does not define every object " +
                "number from 1 through Size minus one.");
        }
    }

    private void ValidateProtectedFreeList()
    {
        int size = (int)Trailer.GetInteger(PdfNames.Size);
        if (_protectedObjectZeroEntry is not { } head)
            throw new PdfFormatException("The protected cross-reference chain has no free-list head.");

        ValidatePointer(0, head);

        var linked = new HashSet<int>();
        int next = (int)head.Offset;
        while (next != 0)
        {
            if (!linked.Add(next))
                throw new PdfFormatException("The protected free-object linked list contains a cycle.");

            if (!_entries.TryGetValue(next, out XrefEntry entry) || entry.Type != XrefEntryType.Free)
            {
                throw new PdfFormatException(
                    $"The protected free-list pointer {next} does not identify a free object.");
            }

            ValidatePointer(next, entry);
            next = (int)entry.Offset;
        }

        foreach (KeyValuePair<int, XrefEntry> item in _entries)
        {
            if (item.Value.Type != XrefEntryType.Free)
                continue;

            ValidatePointer(item.Key, item.Value);
            if (!linked.Contains(item.Key) &&
                (item.Value.Offset != 0 || item.Value.Generation != ushort.MaxValue))
            {
                throw new PdfFormatException(
                    $"Protected free entry {item.Key} is outside the linked list but is not a " +
                    "generation-65535 terminal entry pointing to object 0.");
            }
        }

        void ValidatePointer(int number, XrefEntry entry)
        {
            if (entry.Offset < 0 || entry.Offset >= size)
            {
                throw new PdfFormatException(
                    $"Protected free entry {number} has a free-list pointer outside Size {size}.");
            }
        }
    }

    private void ParseProtectedHistoricalObjects()
    {
        if (_protectedXrefRows.Count == 0)
            return;

        Dictionary<int, List<ProtectedRevisionXrefEntry>> revisionIndex =
            BuildProtectedRevisionIndex();
        var parsedRows = new HashSet<(int Revision, PdfObjectId ObjectId, long Offset)>();
        ProtectedHistoricalObjectParser? parser = null;
        int parserRevision = -1;
        foreach (ProtectedXrefRowRecord row in _protectedXrefRows)
        {
            if (row.Revision != parserRevision)
            {
                // Rows are journalled newest-to-oldest. Once a revision group ends, discard its
                // resolver and cache instead of retaining one full object graph per revision.
                parser = null;
                parserRevision = row.Revision;
            }

            if (row.IsEffective || row.Number <= 0 || row.Entry.Type != XrefEntryType.Normal)
                continue;

            var id = new PdfObjectId(row.Number, row.Entry.Generation);
            if (!parsedRows.Add((row.Revision, id, row.Entry.Offset)))
                continue;

            // A hybrid or repeated historical row may describe the exact live object that the
            // eager protected pass already parsed. Reuse that exact physical range; it cannot hide
            // any bytes that are absent from the live provenance journal.
            if (_entries.TryGetValue(row.Number, out XrefEntry effective) &&
                effective == row.Entry &&
                _protectedObjectRanges.TryGetValue(row.Number, out var effectiveRange) &&
                effectiveRange.Start == row.Entry.Offset)
            {
                continue;
            }

            parser ??= new ProtectedHistoricalObjectParser(this, row.Revision, revisionIndex);

            parser.ParseRecorded(id, row.Entry);
        }
    }

    private Dictionary<int, List<ProtectedRevisionXrefEntry>> BuildProtectedRevisionIndex()
    {
        var index = new Dictionary<int, List<ProtectedRevisionXrefEntry>>();
        foreach (ProtectedXrefRowRecord row in _protectedXrefRows)
        {
            if (row.Number <= 0)
                continue;

            if (!index.TryGetValue(row.Number, out List<ProtectedRevisionXrefEntry>? entries))
            {
                entries = [];
                index.Add(row.Number, entries);
            }

            if (entries.Count > 0)
            {
                int previousRevision = entries[^1].Revision;
                if (previousRevision > row.Revision)
                {
                    throw new PdfFormatException(
                        "Protected xref rows are not ordered from newer to older revisions.");
                }

                if (previousRevision == row.Revision)
                {
                    // Classic rows have priority over their same-revision hybrid XRefStm rows, and
                    // the first section parsed within a linearized original revision likewise wins.
                    continue;
                }
            }

            entries.Add(new ProtectedRevisionXrefEntry(row.Revision, row.Entry));
        }

        return index;
    }

    private static bool TryGetProtectedRevisionEntry(
        IReadOnlyDictionary<int, List<ProtectedRevisionXrefEntry>> index,
        int number,
        int revision,
        out XrefEntry entry)
    {
        entry = default;
        if (!index.TryGetValue(number, out List<ProtectedRevisionXrefEntry>? entries))
            return false;

        int low = 0;
        int high = entries.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (entries[middle].Revision < revision)
                low = middle + 1;
            else
                high = middle;
        }

        if (low >= entries.Count)
            return false;

        entry = entries[low].Entry;
        return true;
    }

    private void AddProtectedHistoricalObjectRange(
        int revision,
        PdfObjectId id,
        int start,
        int end)
    {
        if (start < 0 || end <= start || end > _data.Length)
            throw new PdfFormatException("A protected historical object has an invalid byte range.");

        if (!_protectedHistoricalObjectRangeKeys.Add((id, start, end)))
            return;

        _protectedHistoricalObjectRanges.Add(
            new ProtectedHistoricalObjectRange(revision, id, start, end));
    }

    private void AddProtectedStreamPayloadRange(
        int revision,
        PdfObjectId id,
        int start,
        int end)
    {
        if (!_protectedStreamPayloadRangeKeys.Add((start, end, id.Number)))
            return;

        _protectedParsedStreamPayloadRanges.Add(
            new ProtectedStreamPayloadRange(start, end, id.Number, revision));
    }

    private int FindProtectedEffectiveRevision(PdfObjectId id)
    {
        if (_protectedSectionObjectNumber == id.Number && !_loading.Contains(id.Number))
            return _protectedCurrentXrefRevision;

        foreach (ProtectedXrefRowRecord row in _protectedXrefRows)
        {
            if (row.IsEffective &&
                row.Number == id.Number &&
                row.Entry.Type == XrefEntryType.Normal &&
                row.Entry.Generation == id.Generation)
            {
                return row.Revision;
            }
        }

        // Xref streams are parsed before their own rows. WalkChain sets this field to the owning
        // section's revision, which is the only protected parse that can reach this fallback.
        return _protectedCurrentXrefRevision;
    }

    private sealed class ProtectedHistoricalObjectParser : IPdfResolver, IPdfStreamPayloadReader
    {
        private readonly PdfReader _owner;
        private readonly int _revision;
        private readonly IReadOnlyDictionary<int, List<ProtectedRevisionXrefEntry>> _revisionIndex;
        private readonly Dictionary<(PdfObjectId ObjectId, long Offset), PdfValue> _cache = [];
        private readonly HashSet<(PdfObjectId ObjectId, long Offset)> _loading = [];
        private PdfObjectId _currentObject;

        internal ProtectedHistoricalObjectParser(
            PdfReader owner,
            int revision,
            IReadOnlyDictionary<int, List<ProtectedRevisionXrefEntry>> revisionIndex)
        {
            _owner = owner;
            _revision = revision;
            _revisionIndex = revisionIndex;
        }

        IPdfStreamDecodePolicy? IPdfStreamDecodePolicyProvider.DecodePolicy =>
            _owner._options.StreamDecodePolicy;

        internal PdfValue ParseRecorded(PdfObjectId id, XrefEntry entry)
        {
            if (entry.Type != XrefEntryType.Normal)
                throw new PdfFormatException("Only normal historical xref rows can be parsed from the file.");

            return LoadAt(id, entry);
        }

        public PdfValue Resolve(PdfValue value)
        {
            int depth = 0;
            while (value.IsReference)
            {
                if (++depth > MaxResolveDepth)
                {
                    throw new PdfFormatException(
                        "A protected historical indirect-reference chain exceeds the depth limit.");
                }

                value = LoadSnapshotObject(value.AsReference);
            }

            return value;
        }

        private PdfValue LoadSnapshotObject(PdfObjectId id)
        {
            if (!TryGetProtectedRevisionEntry(
                    _revisionIndex,
                    id.Number,
                    _revision,
                    out XrefEntry entry) ||
                entry.Type != XrefEntryType.Normal ||
                entry.Generation != id.Generation)
            {
                throw new PdfFormatException(
                    $"Protected historical reference {id} is not a generation-matching normal " +
                    "entry in its revision snapshot.");
            }

            return LoadAt(id, entry);
        }

        private PdfValue LoadAt(PdfObjectId expectedId, XrefEntry entry)
        {
            var key = (expectedId, entry.Offset);
            if (_cache.TryGetValue(key, out PdfValue cached))
                return cached;

            if (!_loading.Add(key))
                throw new PdfFormatException("A protected historical indirect-reference chain contains a cycle.");

            try
            {
                if (entry.Offset is < 0 or > int.MaxValue || entry.Offset >= _owner._data.Length)
                {
                    throw new PdfFormatException(
                        $"Protected historical xref offset for object {expectedId.Number} is outside the file.");
                }

                int start = (int)entry.Offset;
                var firstToken = new PdfLexer(_owner._data, start, strict: true);
                if (firstToken.Read() == PdfTokenType.EndOfInput ||
                    firstToken.TokenStart != start ||
                    (start > 0 && PdfCharacters.IsRegular(_owner._data[start - 1])))
                {
                    throw new PdfFormatException(
                        $"A protected historical xref offset for object {expectedId.Number} does " +
                        "not point exactly at its header.");
                }

                var parser = new PdfObjectParser(_owner._data, start, this, this);
                if (!parser.TryReadHeader(out PdfObjectId actualId) || actualId != expectedId)
                {
                    throw new PdfFormatException(
                        $"A protected historical xref row for {expectedId} does not match its object header.");
                }

                PdfObjectId saved = _currentObject;
                _currentObject = actualId;
                PdfValue value;
                try
                {
                    value = parser.ParseValue();
                }
                finally
                {
                    _currentObject = saved;
                }

                var boundary = new PdfLexer(_owner._data, parser.Position, strict: true);
                if (boundary.Read() != PdfTokenType.Keyword || !boundary.KeywordIs("endobj"u8))
                {
                    throw new PdfFormatException(
                        "A protected historical indirect object shall end with endobj.");
                }

                _owner.AddProtectedHistoricalObjectRange(
                    _revision,
                    actualId,
                    start,
                    boundary.Position);
                _cache.Add(key, value);
                return value;
            }
            finally
            {
                _loading.Remove(key);
            }
        }

        ReadOnlyMemory<byte> IPdfStreamPayloadReader.ReadPayload(
            PdfDictionary dictionary,
            int dataStart,
            out int endPosition)
        {
            if (!_currentObject.IsValid)
                throw new PdfFormatException("A protected historical stream has no owning object.");

            if (!dictionary.TryGetRaw(PdfNames.Length, out PdfValue length))
                throw new PdfFormatException("A protected historical stream requires a Length entry.");

            long declared;
            if (length.Kind == PdfValueKind.Integer)
            {
                declared = length.AsInteger;
            }
            else if (length.IsReference)
            {
                if (dictionary.GetName(PdfNames.Type) == PdfNames.XRef)
                {
                    throw new PdfFormatException(
                        "A protected historical cross-reference stream requires a direct Length entry.");
                }

                PdfValue resolved = Resolve(length);
                if (resolved.Kind != PdfValueKind.Integer)
                {
                    throw new PdfFormatException(
                        "A protected historical stream's indirect Length shall resolve to an integer.");
                }

                declared = resolved.AsInteger;
            }
            else
            {
                throw new PdfFormatException(
                    "A protected historical stream's Length shall be an integer or indirect reference.");
            }

            if (declared < 0 ||
                declared > int.MaxValue ||
                declared > _owner._data.Length - (long)dataStart ||
                !TryFindProtectedEndstream(
                    _owner._data,
                    dataStart + (int)declared,
                    out endPosition))
            {
                throw new PdfFormatException(
                    "A protected historical stream's Length does not point exactly to the EOL " +
                    "before endstream.");
            }

            int payloadEnd = checked(dataStart + (int)declared);
            _owner.AddProtectedStreamPayloadRange(
                _revision,
                _currentObject,
                dataStart,
                payloadEnd);

            // Provenance parsing needs byte boundaries only. Historical payloads deliberately stay
            // encrypted; decrypting with the live security state would apply the wrong revision.
            return _owner._memory.Slice(dataStart, (int)declared);
        }
    }

    private void ValidateProtectedRevisionBodyCoverage()
    {
        if (_protectedRevisionBodies.Count == 0)
            return;

        var rangeIndex = new Dictionary<(PdfObjectId ObjectId, long Offset), (int Start, int End)>();
        foreach (KeyValuePair<int, XrefEntry> item in _entries)
        {
            if (item.Value.Type != XrefEntryType.Normal ||
                !_protectedObjectRanges.TryGetValue(item.Key, out var range))
            {
                continue;
            }

            AddRange(
                new PdfObjectId(item.Key, item.Value.Generation),
                item.Value.Offset,
                range.Start,
                range.End);
        }

        foreach (ProtectedHistoricalObjectRange historical in _protectedHistoricalObjectRanges)
        {
            AddRange(
                historical.ObjectId,
                historical.Start,
                historical.Start,
                historical.End);
        }

        var rangesByRevision = new Dictionary<int, HashSet<(int Start, int End)>>();
        foreach (ProtectedXrefRowRecord row in _protectedXrefRows)
        {
            if (row.Number <= 0 || row.Entry.Type != XrefEntryType.Normal)
                continue;

            var id = new PdfObjectId(row.Number, row.Entry.Generation);
            if (!rangeIndex.TryGetValue((id, row.Entry.Offset), out var range))
            {
                throw new PdfFormatException(
                    $"Protected revision {row.Revision} normal row for object {row.Number} has " +
                    "no exact parsed object range.");
            }

            if (!rangesByRevision.TryGetValue(
                    row.Revision,
                    out HashSet<(int Start, int End)>? revisionRanges))
            {
                revisionRanges = [];
                rangesByRevision.Add(row.Revision, revisionRanges);
            }

            revisionRanges.Add(range);
        }

        foreach (ProtectedRevisionBody body in _protectedRevisionBodies)
        {
            List<(int Start, int End)> ranges = rangesByRevision.TryGetValue(
                body.Revision,
                out HashSet<(int Start, int End)>? bodyRanges)
                ? [.. bodyRanges]
                : [];
            ranges.Sort(static (left, right) =>
            {
                int byStart = left.Start.CompareTo(right.Start);
                return byStart != 0 ? byStart : right.End.CompareTo(left.End);
            });

            int rangePosition = 0;
            int position = body.Start;
            while (position < body.PrimaryXrefStart)
            {
                while (rangePosition < ranges.Count && ranges[rangePosition].End <= position)
                    rangePosition++;

                if (rangePosition < ranges.Count &&
                    ranges[rangePosition].Start <= position &&
                    position < ranges[rangePosition].End)
                {
                    position = Math.Min(body.PrimaryXrefStart, ranges[rangePosition].End);
                    continue;
                }

                if (PdfCharacters.IsWhitespace(_data[position]))
                {
                    position++;
                    continue;
                }

                if (_data[position] == (byte)'%')
                {
                    while (position < body.PrimaryXrefStart &&
                           !PdfCharacters.IsEndOfLine(_data[position]))
                    {
                        position++;
                    }

                    continue;
                }

                throw new PdfFormatException(
                    $"Protected revision body {body.Revision} contains unindexed non-trivia " +
                    $"bytes at offset {position}.");
            }
        }

        void AddRange(PdfObjectId id, long offset, int start, int end)
        {
            var key = (id, offset);
            if (rangeIndex.TryGetValue(key, out var existing))
            {
                if (existing.Start != start || existing.End != end)
                {
                    throw new PdfFormatException(
                        $"Protected object {id.Number} has inconsistent revision-body ranges.");
                }

                return;
            }

            rangeIndex.Add(key, (start, end));
        }
    }

    private void ValidateProtectedProvenanceRanges()
    {
        ParseProtectedHistoricalObjects();
        List<(int Start, int End)> comments = CollectProtectedCommentRanges();
        var objects = new List<(PdfObjectId ObjectId, int Start, int End)>();
        foreach (KeyValuePair<int, XrefEntry> item in _entries)
        {
            if (item.Value.Type != XrefEntryType.Normal)
                continue;

            if (!_protectedObjectRanges.TryGetValue(item.Key, out var range) ||
                range.Start < 0 || range.End <= range.Start || range.End > _data.Length)
            {
                throw new PdfFormatException(
                    $"Protected live object {item.Key} has no exact parsed byte range.");
            }

            if (IsInsideRange(range.Start, comments))
            {
                throw new PdfFormatException(
                    $"The protected xref offset for object {item.Key} points inside a comment.");
            }

            objects.Add((new PdfObjectId(item.Key, item.Value.Generation), range.Start, range.End));
        }

        foreach (ProtectedHistoricalObjectRange historical in _protectedHistoricalObjectRanges)
        {
            if (IsInsideRange(historical.Start, comments))
            {
                throw new PdfFormatException(
                    $"The protected historical xref offset for object " +
                    $"{historical.ObjectId.Number} points inside a comment.");
            }

            objects.Add((historical.ObjectId, historical.Start, historical.End));
        }

        objects.Sort(static (left, right) =>
        {
            int byStart = left.Start.CompareTo(right.Start);
            return byStart != 0 ? byStart : right.End.CompareTo(left.End);
        });
        for (int i = 1; i < objects.Count; i++)
        {
            var previous = objects[i - 1];
            var current = objects[i];
            if (current.Start < previous.End)
            {
                if (current.Start == previous.Start && current.End == previous.End)
                    continue;

                throw new PdfFormatException(
                    $"Protected normal-object ranges {previous.ObjectId.Number} and " +
                    $"{current.ObjectId.Number} overlap.");
            }
        }

        foreach (ProtectedStreamPayloadRange payload in _protectedParsedStreamPayloadRanges)
        {
            if (payload.Start < 0 || payload.End < payload.Start || payload.End > _data.Length)
                throw new PdfFormatException("A protected stream payload has an invalid byte range.");

            foreach (var value in objects)
            {
                if (!RangesOverlap(value.Start, value.End, payload.Start, payload.End))
                    continue;

                bool owningStreamObject =
                    value.ObjectId.Number == payload.ObjectNumber &&
                    value.Start <= payload.Start &&
                    payload.End <= value.End;
                if (!owningStreamObject)
                {
                    throw new PdfFormatException(
                        $"Protected object {value.ObjectId.Number} overlaps the raw stream payload of " +
                        $"object {payload.ObjectNumber}.");
                }
            }
        }

        for (int i = 0; i < _protectedParsedStreamPayloadRanges.Count; i++)
        {
            ProtectedStreamPayloadRange left = _protectedParsedStreamPayloadRanges[i];
            for (int j = i + 1; j < _protectedParsedStreamPayloadRanges.Count; j++)
            {
                ProtectedStreamPayloadRange right = _protectedParsedStreamPayloadRanges[j];
                if (RangesOverlap(left.Start, left.End, right.Start, right.End) &&
                    (left.Start != right.Start || left.End != right.End))
                {
                    throw new PdfFormatException(
                        "Protected stream payload ranges overlap without being exact duplicates.");
                }
            }
        }

        for (int i = 0; i < _protectedXrefSections.Count; i++)
        {
            ProtectedXrefSectionRange section = _protectedXrefSections[i];
            if (section.Start < 0 || section.End <= section.Start || section.End > _data.Length)
                throw new PdfFormatException("A protected cross-reference section has an invalid byte range.");
            if (IsInsideRange(section.Start, comments))
            {
                throw new PdfFormatException(
                    $"The protected cross-reference offset {section.Start} points inside a comment.");
            }

            bool hasExactSelfObject =
                section.IsStream &&
                objects.Any(value =>
                    value.ObjectId.Number == section.ObjectNumber &&
                    value.Start == section.Start &&
                    value.End == section.End);

            foreach (ProtectedStreamPayloadRange payload in _protectedParsedStreamPayloadRanges)
            {
                if (!RangesOverlap(section.Start, section.End, payload.Start, payload.End))
                    continue;

                bool ownXrefStreamPayload =
                    hasExactSelfObject &&
                    section.ObjectNumber == payload.ObjectNumber &&
                    section.Start <= payload.Start &&
                    payload.End <= section.End;
                if (!ownXrefStreamPayload)
                {
                    throw new PdfFormatException(
                        $"Protected cross-reference section at {section.Start} overlaps the raw " +
                        $"stream payload of object {payload.ObjectNumber}.");
                }
            }

            foreach (var value in objects)
            {
                if (!RangesOverlap(section.Start, section.End, value.Start, value.End))
                    continue;

                bool ownXrefStreamObject =
                    hasExactSelfObject &&
                    section.ObjectNumber == value.ObjectId.Number &&
                    section.Start == value.Start &&
                    section.End == value.End;
                if (!ownXrefStreamObject)
                {
                    throw new PdfFormatException(
                        $"Protected cross-reference section at {section.Start} overlaps normal " +
                        $"object {value.ObjectId.Number}.");
                }
            }

            for (int j = i + 1; j < _protectedXrefSections.Count; j++)
            {
                ProtectedXrefSectionRange other = _protectedXrefSections[j];
                if (RangesOverlap(section.Start, section.End, other.Start, other.End) &&
                    (section.Start != other.Start || section.End != other.End))
                {
                    throw new PdfFormatException(
                        "Protected cross-reference section ranges overlap each other.");
                }
            }
        }

        ValidateProtectedRevisionBodyCoverage();
    }

    private static bool RangesOverlap(int leftStart, int leftEnd, int rightStart, int rightEnd) =>
        leftStart < rightEnd && rightStart < leftEnd;

    private List<(int Start, int End)> CollectProtectedCommentRanges()
    {
        var payloads = new List<(int Start, int End)>();
        foreach (ProtectedStreamPayloadRange payload in _protectedParsedStreamPayloadRanges)
        {
            if (payload.End > payload.Start)
                payloads.Add((payload.Start, payload.End));
        }

        payloads.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var comments = new List<(int Start, int End)>();
        int payloadIndex = 0;
        int literalDepth = 0;
        bool hexadecimal = false;
        int position = 0;

        while (position < _data.Length)
        {
            while (payloadIndex < payloads.Count && payloads[payloadIndex].End <= position)
                payloadIndex++;

            if (payloadIndex < payloads.Count &&
                position >= payloads[payloadIndex].Start &&
                position < payloads[payloadIndex].End)
            {
                position = payloads[payloadIndex].End;
                literalDepth = 0;
                hexadecimal = false;
                continue;
            }

            byte value = _data[position];
            if (literalDepth > 0)
            {
                if (value == (byte)'\\')
                {
                    position++;
                    if (position < _data.Length && _data[position] == (byte)'\r')
                    {
                        position++;
                        if (position < _data.Length && _data[position] == (byte)'\n')
                            position++;
                    }
                    else if (position < _data.Length)
                    {
                        position++;
                    }

                    continue;
                }

                if (value == (byte)'(')
                    literalDepth++;
                else if (value == (byte)')')
                    literalDepth--;

                position++;
                continue;
            }

            if (hexadecimal)
            {
                if (value == (byte)'>')
                    hexadecimal = false;

                position++;
                continue;
            }

            if (value == (byte)'%')
            {
                int start = position;
                while (position < _data.Length && !PdfCharacters.IsEndOfLine(_data[position]))
                    position++;

                comments.Add((start, position));
                continue;
            }

            if (value == (byte)'(')
            {
                literalDepth = 1;
                position++;
                continue;
            }

            if (value == (byte)'<' &&
                (position + 1 >= _data.Length || _data[position + 1] != (byte)'<'))
            {
                hexadecimal = true;
                position++;
                continue;
            }

            if (value == (byte)'<' && position + 1 < _data.Length && _data[position + 1] == (byte)'<')
                position += 2;
            else
                position++;
        }

        return comments;
    }

    private static bool IsInsideRange(int offset, List<(int Start, int End)> ranges)
    {
        int low = 0;
        int high = ranges.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            var range = ranges[middle];
            if (offset < range.Start)
            {
                high = middle - 1;
            }
            else if (offset >= range.End)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private void ValidateProtectedFinalSectionTail()
    {
        ValidateProtectedSectionTail(
            _protectedFinalSectionEnd,
            _protectedFinalStartXrefKeyword,
            "A protected PDF requires its final startxref to follow the latest " +
            "cross-reference section directly.");
    }

    private void ValidateProtectedSectionTail(int sectionEnd, int startXrefKeyword, string message)
    {
        if (sectionEnd < 0 ||
            startXrefKeyword < sectionEnd ||
            !ContainsOnlyInterSectionTrivia(
                _data.AsSpan(sectionEnd, startXrefKeyword - sectionEnd)))
        {
            throw new PdfFormatException(message);
        }
    }

    private static bool ContainsOnlyInterSectionTrivia(ReadOnlySpan<byte> bytes)
    {
        int position = 0;
        while (position < bytes.Length)
        {
            if (PdfCharacters.IsWhitespace(bytes[position]))
            {
                position++;
                continue;
            }

            if (bytes[position] != (byte)'%' || bytes[position..].StartsWith("%%EOF"u8))
                return false;

            while (position < bytes.Length && !PdfCharacters.IsEndOfLine(bytes[position]))
                position++;
        }

        return true;
    }

    private static bool ContainsOnlyProtectedHintSeparator(ReadOnlySpan<byte> bytes)
    {
        bool containsEndOfLine = false;
        foreach (byte value in bytes)
        {
            if (!PdfCharacters.IsWhitespace(value))
                return false;

            containsEndOfLine |= PdfCharacters.IsEndOfLine(value);
        }

        return containsEndOfLine;
    }

    private static bool TryParseClassicXrefRow(
        ReadOnlySpan<byte> row,
        out long offset,
        out int generation,
        out bool free)
    {
        offset = 0;
        generation = 0;
        free = false;

        if (row.Length != 20 || row[10] != (byte)' ' || row[16] != (byte)' ' ||
            row[17] is not ((byte)'n' or (byte)'f'))
        {
            return false;
        }

        bool validEndOfLine =
            (row[18] == (byte)' ' && row[19] is (byte)'\r' or (byte)'\n') ||
            (row[18] == (byte)'\r' && row[19] == (byte)'\n');
        if (!validEndOfLine ||
            !TryParseUnsigned(row[..10], out offset) ||
            !TryParseUnsigned(row.Slice(11, 5), out long generationValue))
        {
            return false;
        }

        generation = (int)generationValue;
        free = row[17] == (byte)'f';
        return true;
    }

    private static bool TryParseUnsigned(ReadOnlySpan<byte> digits, out long value)
    {
        value = 0;
        if (digits.IsEmpty)
            return false;

        foreach (byte digit in digits)
        {
            if (digit is < (byte)'0' or > (byte)'9' || value > (long.MaxValue - 9) / 10)
                return false;

            value = value * 10 + digit - (byte)'0';
        }

        return true;
    }

    private static bool TryConsumeEndOfLine(ReadOnlySpan<byte> data, ref int position)
    {
        if (position >= data.Length)
            return false;

        if (data[position] == (byte)'\n')
        {
            position++;
            return true;
        }

        if (data[position] != (byte)'\r')
            return false;

        position++;
        if (position < data.Length && data[position] == (byte)'\n')
            position++;

        return true;
    }

    private PdfDictionary? ReadXrefStream(long offset)
    {
        bool strict = _options.StreamDecodePolicy is not null;
        var parser = new PdfObjectParser(_data, (int)offset, this, this);
        if (!parser.TryReadHeader(out PdfObjectId id))
            return null;

        if (strict)
            _protectedSectionObjectNumber = id.Number;

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

        if (strict)
        {
            var boundary = new PdfLexer(_data, parser.Position);
            if (boundary.Read() != PdfTokenType.Keyword || !boundary.KeywordIs("endobj"u8))
            {
                throw new PdfFormatException(
                    "A protected cross-reference stream shall end with endobj.");
            }

            _protectedSectionEnd = boundary.Position;
        }

        PdfDictionary dictionary = stream.Dictionary;
        if (strict)
        {
            RequireDirect(PdfNames.Type);
            RequireDirect(PdfNames.Size);
            RequireDirect(PdfNames.W);
            RequireDirect(PdfNames.Index);
            RequireDirect(PdfNames.Filter);
            RequireDirect(PdfNames.DecodeParms);
            RequireDirect(PdfNames.Length);
        }

        if (strict && dictionary.GetName(PdfNames.Type) != PdfNames.XRef)
            throw new PdfFormatException("A protected cross-reference stream has no XRef Type.");

        if (strict && ContainsFilter(dictionary.GetFirst(PdfNames.Filter, PdfNames.F), PdfNames.Crypt))
        {
            throw new PdfFormatException(
                "A protected cross-reference stream specifies the forbidden Crypt filter.");
        }

        if (strict &&
            (dictionary.Get(PdfNames.Size).Kind != PdfValueKind.Integer ||
             dictionary.GetInteger(PdfNames.Size) is <= 0 or > int.MaxValue))
        {
            throw new PdfFormatException("A protected cross-reference stream has an invalid Size entry.");
        }

        PdfArray? widths = dictionary.GetArray(PdfNames.W);
        if (widths is null || widths.Count < 3)
            return null;
        if (strict &&
            (widths.Count != 3 ||
             widths[0].Kind != PdfValueKind.Integer ||
             widths[1].Kind != PdfValueKind.Integer ||
             widths[2].Kind != PdfValueKind.Integer))
        {
            throw new PdfFormatException(
                "A protected cross-reference stream requires exactly three direct integer W widths.");
        }

        long w0Value = widths.GetInteger(0);
        long w1Value = widths.GetInteger(1);
        long w2Value = widths.GetInteger(2);
        if (strict &&
            (w0Value is < 0 or > 8 || w1Value is < 1 or > 8 || w2Value is < 0 or > 8))
        {
            throw new PdfFormatException("A protected cross-reference stream has an unsafe W field width.");
        }

        int w0 = (int)w0Value;
        int w1 = (int)w1Value;
        int w2 = (int)w2Value;
        if (w0 < 0 || w1 <= 0 || w2 < 0 || w0 + w1 + w2 == 0)
            return null;

        ReadOnlySpan<byte> data = stream.DecodedSpan();
        int rowLength = w0 + w1 + w2;
        if (rowLength == 0)
            return null;

        List<(int Start, int Count)> ranges = [];
        bool hasIndex = dictionary.ContainsKey(PdfNames.Index);
        PdfValue indexValue = dictionary.Get(PdfNames.Index);
        PdfArray? index = indexValue.AsArray;
        if (strict && hasIndex && (indexValue.Kind != PdfValueKind.Array || index is null || index.Count < 2))
            throw new PdfFormatException("A protected cross-reference stream has a malformed Index entry.");

        if (index is { Count: >= 2 })
        {
            if (strict && (index.Count & 1) != 0)
                throw new PdfFormatException("A protected cross-reference stream has an odd-length Index array.");

            long previousEnd = -1;
            for (int i = 0; i + 1 < index.Count; i += 2)
            {
                if (strict &&
                    (index[i].Kind != PdfValueKind.Integer ||
                     index[i + 1].Kind != PdfValueKind.Integer ||
                     index.GetInteger(i) is < 0 or > int.MaxValue ||
                     index.GetInteger(i + 1) is < 0 or > int.MaxValue))
                {
                    throw new PdfFormatException(
                        "A protected cross-reference stream has a non-integer or negative Index range.");
                }

                long start = index.GetInteger(i);
                long count = index.GetInteger(i + 1);
                if (strict &&
                    (start + count > int.MaxValue ||
                     start < previousEnd ||
                     start + count > dictionary.GetInteger(PdfNames.Size)))
                {
                    throw new PdfFormatException(
                        "A protected cross-reference Index range overflows Size, overlaps, or is out of order.");
                }

                ranges.Add(((int)start, (int)count));
                previousEnd = start + count;
            }
        }
        else
        {
            ranges.Add((0, (int)dictionary.GetInteger(PdfNames.Size, data.Length / rowLength)));
        }

        if (strict)
        {
            long expectedRows = 0;
            foreach ((_, int count) in ranges)
                expectedRows += count;

            if (expectedRows > int.MaxValue || expectedRows * rowLength != data.Length)
            {
                throw new PdfFormatException(
                    "A protected cross-reference stream's decoded length does not exactly match Index and W.");
            }

            if (expectedRows > 0)
            {
                // This accounts for free and unknown rows too. Live entries inserted below are
                // therefore not charged a second time.
                _options.StreamDecodePolicy!.ConsumeStructuralEntries((int)expectedRows);
            }
        }

        int position = 0;
        bool hasSelfEntry =
            _entries.TryGetValue(id.Number, out XrefEntry existingSelf) &&
            existingSelf.Type == XrefEntryType.Normal &&
            existingSelf.Offset == offset &&
            existingSelf.Generation == id.Generation;
        foreach ((int start, int count) in ranges)
        {
            for (int i = 0; i < count && position + rowLength <= data.Length; i++, position += rowLength)
            {
                ReadOnlySpan<byte> row = data.Slice(position, rowLength);
                long type = w0 == 0 ? 1 : ReadField(row[..w0]);
                long field2 = ReadField(row.Slice(w0, w1));
                long field3 = ReadField(row.Slice(w0 + w1, w2));
                int number = start + i;

                if (strict && number == 0)
                {
                    if (type == 0 && field3 == ushort.MaxValue)
                    {
                        _protectedObjectZeroSeen = true;
                        _protectedObjectZeroEntry ??= XrefEntry.Free(field2, (int)field3);
                    }
                    else
                        _protectedObjectZeroInvalid = true;
                }

                switch (type)
                {
                    case 0:
                        if (strict &&
                            (field2 is < 0 or > int.MaxValue ||
                             field3 is < 0 or > ushort.MaxValue))
                        {
                            throw new PdfFormatException(
                                "A protected cross-reference stream has an out-of-range " +
                                "free-list pointer or generation.");
                        }

                        XrefEntry freeEntry = XrefEntry.Free(field2, (int)field3);
                        bool freeIsEffective = number > 0 &&
                            TryAddEntry(number, freeEntry, chargePolicy: !strict);
                        JournalProtectedXrefRow(number, freeEntry, freeIsEffective);
                        break;

                    case 1:
                    {
                        if (strict &&
                            (field2 < 0 || field2 >= _data.Length ||
                             field3 is < 0 or > ushort.MaxValue))
                        {
                            throw new PdfFormatException(
                                "A protected cross-reference stream has an out-of-range " +
                                "object offset or generation.");
                        }

                        if (strict && number == id.Number &&
                            field2 == offset && field3 == id.Generation)
                        {
                            hasSelfEntry = true;
                        }

                        XrefEntry normalEntry = XrefEntry.Normal(field2, (int)field3);
                        bool normalIsEffective = number > 0 &&
                            TryAddEntry(number, normalEntry, chargePolicy: !strict);
                        JournalProtectedXrefRow(number, normalEntry, normalIsEffective);
                        break;
                    }

                    case 2:
                    {
                        if (strict &&
                            (field2 is < 1 or > int.MaxValue ||
                             field3 is < 0 or > int.MaxValue))
                        {
                            throw new PdfFormatException(
                                "A protected cross-reference stream has an out-of-range " +
                                "object-stream number or index.");
                        }

                        XrefEntry compressedEntry =
                            XrefEntry.Compressed((int)field2, (int)field3);
                        bool compressedIsEffective = number > 0 &&
                            TryAddEntry(number, compressedEntry, chargePolicy: !strict);
                        JournalProtectedXrefRow(number, compressedEntry, compressedIsEffective);
                        break;
                    }

                    default:
                        // ISO 32000 reserves future entry types and requires readers to treat an
                        // unrecognised row as null. Keep a tombstone so an older Prev section cannot
                        // resurrect it; the row was already charged to the shared ledger.
                        XrefEntry unknownEntry = XrefEntry.Unknown;
                        bool unknownIsEffective = number > 0 &&
                            TryAddEntry(number, unknownEntry, chargePolicy: !strict);
                        JournalProtectedXrefRow(number, unknownEntry, unknownIsEffective);
                        break;
                }
            }
        }

        if (strict && !hasSelfEntry)
        {
            throw new PdfFormatException(
                "A protected cross-reference stream has no valid entry for itself.");
        }

        return dictionary;

        static bool ContainsFilter(PdfValue value, PdfName expected)
        {
            if (value.AsName is { } single)
                return single == expected;

            if (value.AsArray is not { } array)
                return false;

            for (int i = 0; i < array.Count; i++)
            {
                if (array.GetName(i) == expected)
                    return true;
            }

            return false;
        }

        void RequireDirect(PdfName key)
        {
            if (dictionary.TryGetRaw(key, out PdfValue raw) && raw.IsReference)
            {
                throw new PdfFormatException(
                    $"A protected cross-reference stream requires a direct {key.Value} entry.");
            }
        }
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
        bool hasEntry = _entries.TryGetValue(number, out XrefEntry entry);
        bool recoverNullEntry =
            hasEntry &&
            entry.Type is XrefEntryType.Free or XrefEntryType.Unknown &&
            _options.Recover &&
            _options.StreamDecodePolicy is null;
        if (!hasEntry || recoverNullEntry)
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

        if (_options.StreamDecodePolicy is not null && id.Generation != entry.Generation)
        {
            throw new PdfFormatException(
                $"A protected xref entry records generation {entry.Generation}, but object header " +
                $"{id.Number} {id.Generation} obj uses another generation.");
        }

        PdfObjectId saved = _currentObject;
        _currentObject = id;

        try
        {
            PdfValue value = parser.ParseValue();
            if (_options.StreamDecodePolicy is not null)
            {
                var boundary = new PdfLexer(_data, parser.Position);
                if (boundary.Read() != PdfTokenType.Keyword || !boundary.KeywordIs("endobj"u8))
                {
                    throw new PdfFormatException(
                        "A protected indirect object shall end with endobj.");
                }

                RecordProtectedRange(
                    _protectedObjectRanges,
                    number,
                    start,
                    boundary.Position,
                    "indirect object");
            }

            return DecryptStrings(value, id, 0);
        }
        finally
        {
            _currentObject = saved;
        }
    }

    private bool TryLocate(int number, XrefEntry entry, out int start)
    {
        if (_options.StreamDecodePolicy is not null)
        {
            if (entry.Offset >= 0 && entry.Offset < _data.Length)
            {
                var lexer = new PdfLexer(_data, (int)entry.Offset);
                if (lexer.Read() != PdfTokenType.EndOfInput &&
                    lexer.TokenStart == entry.Offset &&
                    (entry.Offset == 0 ||
                     !PdfCharacters.IsRegular(_data[(int)entry.Offset - 1])) &&
                    MatchesHeader(entry.Offset, number, out start))
                {
                    return true;
                }
            }

            throw new PdfFormatException(
                $"A protected xref offset for object {number} does not point exactly at its header.");
        }

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

    private static void RecordProtectedRange(
        Dictionary<int, (int Start, int End)> ranges,
        int number,
        int start,
        int end,
        string description,
        bool allowEmpty = false)
    {
        if (start < 0 || end < start || (!allowEmpty && end == start))
            throw new PdfFormatException($"A protected {description} has an invalid byte range.");

        if (ranges.TryGetValue(number, out var existing))
        {
            if (existing.Start != start || existing.End != end)
            {
                throw new PdfFormatException(
                    $"A protected {description} {number} was parsed from inconsistent byte ranges.");
            }

            return;
        }

        ranges.Add(number, (start, end));
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

        return _options.StreamDecodePolicy is null ? position : -1;
    }

    private PdfValue LoadFromObjectStream(int number, XrefEntry entry)
    {
        bool strict = _options.StreamDecodePolicy is not null;
        ObjectStreamIndex? index = ReadObjectStream((int)entry.Offset);
        if (index is null)
        {
            if (strict)
                throw new PdfFormatException("A protected object stream could not be indexed.");

            return PdfValue.Null;
        }

        int slot = entry.Generation;
        if (slot < 0 || slot >= index.Numbers.Length || index.Numbers[slot] != number)
        {
            if (strict)
            {
                throw new PdfFormatException(
                    "A protected xref object-stream slot does not identify the requested object.");
            }

            slot = System.Array.IndexOf(index.Numbers, number);
            if (slot < 0)
                return PdfValue.Null;
        }

        int start = index.First + index.Offsets[slot];
        int end = slot + 1 < index.Offsets.Length
            ? index.First + index.Offsets[slot + 1]
            : index.Data.Length;
        if (start < 0 || start >= index.Data.Length || end <= start || end > index.Data.Length)
        {
            if (strict)
                throw new PdfFormatException("A protected object stream has an invalid object body range.");

            return PdfValue.Null;
        }

        // Strings inside an object stream were encrypted as part of the container, not on their own.
        ReadOnlySpan<byte> data = strict ? index.Data.Span[start..end] : index.Data.Span;
        if (strict)
        {
            var slotLexer = new PdfLexer(data);
            if (slotLexer.Read() == PdfTokenType.EndOfInput || slotLexer.TokenStart != 0)
            {
                throw new PdfFormatException(
                    "A protected object-stream slot shall start exactly at its declared offset.");
            }
        }

        var parser = new PdfObjectParser(data, strict ? 0 : start, this);
        PdfValue value = parser.ParseValue();
        if (strict && parser.ReadToken() != PdfTokenType.EndOfInput)
        {
            throw new PdfFormatException(
                "A protected object-stream slot contains more than one object value.");
        }

        if (strict && value.IsReference)
        {
            throw new PdfFormatException(
                "A protected object-stream slot cannot consist of an indirect reference.");
        }

        return value;
    }

    private ObjectStreamIndex? ReadObjectStream(int number)
    {
        if (_options.StreamDecodePolicy is not null &&
            (!_entries.TryGetValue(number, out XrefEntry containerEntry) ||
             containerEntry.Type != XrefEntryType.Normal ||
             containerEntry.Generation != 0))
        {
            throw new PdfFormatException(
                "A protected object stream shall be an uncompressed object with generation 0.");
        }

        if (_objectStreams.TryGetValue(number, out ObjectStreamIndex? cached))
            return cached;

        _objectStreams[number] = null;

        if (GetObject(number).AsStream is not { } stream)
        {
            if (_options.StreamDecodePolicy is not null)
            {
                throw new PdfFormatException(
                    "A protected object-stream reference does not point to a stream object.");
            }

            Diagnostics.Add(PdfWarningCode.ObjectStreamUnreadable,
                "An object stream could not be read.", new PdfObjectId(number, 0));
            return null;
        }

        long declaredCount = stream.Dictionary.GetInteger(PdfNames.N);
        long declaredFirst = stream.Dictionary.GetInteger(PdfNames.First);
        if (declaredCount is <= 0 or > int.MaxValue || declaredFirst is < 0 or > int.MaxValue)
        {
            string message = "An object stream has an invalid N or First entry.";
            if (_options.StreamDecodePolicy is not null)
                throw new PdfFormatException(message);

            Diagnostics.Add(PdfWarningCode.ObjectStreamUnreadable, message, new PdfObjectId(number, 0));
            return null;
        }

        if (_options.StreamDecodePolicy is not null &&
            stream.Dictionary.GetName(PdfNames.Type) != PdfNames.ObjStm)
        {
            throw new PdfFormatException("A protected object-stream reference does not point to an ObjStm stream.");
        }

        int count = (int)declaredCount;
        int first = (int)declaredFirst;
        ReadOnlyMemory<byte> data = stream.Decode();

        if (count <= 0 || first < 0 || first > data.Length)
        {
            if (_options.StreamDecodePolicy is not null)
                throw new PdfFormatException("A protected object stream has First beyond its decoded bytes.");

            return null;
        }

        int structuralLimit = _options.StreamDecodePolicy?.MaxStructuralEntries ?? int.MaxValue;
        if (count > structuralLimit || count > ((long)first + 1) / 4)
        {
            string message =
                $"An object stream declares {count} entries, but its {first}-byte header and the " +
                $"reader limit of {structuralLimit} entries cannot contain them safely.";
            if (_options.StreamDecodePolicy is not null)
                throw new PdfFormatException(message);

            Diagnostics.Add(
                PdfWarningCode.ObjectStreamUnreadable,
                message,
                new PdfObjectId(number, 0));
            return null;
        }

        _options.StreamDecodePolicy?.ConsumeStructuralEntries(count);

        int[] numbers = new int[count];
        int[] offsets = new int[count];
        bool strict = _options.StreamDecodePolicy is not null;
        var lexer = new PdfLexer(strict ? data.Span[..first] : data.Span);
        HashSet<int>? seenNumbers = strict ? [] : null;
        int previousOffset = -1;
        int bodyLength = data.Length - first;

        for (int i = 0; i < count; i++)
        {
            if (lexer.Read() != PdfTokenType.Integer)
            {
                if (strict)
                    throw new PdfFormatException("An object stream's N entry exceeds its header pairs.");

                System.Array.Resize(ref numbers, i);
                System.Array.Resize(ref offsets, i);
                break;
            }

            double numberValue = lexer.NumberValue;
            numbers[i] = (int)numberValue;

            if (lexer.Read() != PdfTokenType.Integer)
            {
                if (strict)
                    throw new PdfFormatException("An object stream header ends between an object number and offset.");

                System.Array.Resize(ref numbers, i);
                System.Array.Resize(ref offsets, i);
                break;
            }

            double offsetValue = lexer.NumberValue;
            offsets[i] = (int)offsetValue;
            if (strict && i == 0 && offsets[i] != 0)
            {
                throw new PdfFormatException(
                    "A protected object stream's first object offset must be zero.");
            }

            if (strict &&
                (numberValue is < 1 or > int.MaxValue ||
                 offsetValue is < 0 or > int.MaxValue ||
                 !seenNumbers!.Add(numbers[i]) ||
                 (i > 0 && offsets[i] <= previousOffset) ||
                 offsets[i] >= bodyLength))
            {
                throw new PdfFormatException(
                    "An object stream header has duplicate/invalid numbers or unsafe object offsets.");
            }

            previousOffset = offsets[i];
        }

        if (strict && lexer.Read() != PdfTokenType.EndOfInput)
            throw new PdfFormatException("An object stream header contains more pairs than its N entry declares.");

        if (strict &&
            (first <= 0 || first >= data.Length ||
             !PdfCharacters.IsWhitespace(data.Span[first - 1]) ||
             PdfCharacters.IsWhitespace(data.Span[first])))
        {
            throw new PdfFormatException(
                "An object stream's First entry shall point immediately after the whitespace " +
                "that separates its header from its first object.");
        }

        var index = new ObjectStreamIndex(data, numbers, offsets, first);
        _objectStreams[number] = index;
        return index;
    }

    private bool TryAddEntry(int number, XrefEntry entry, bool chargePolicy = true)
    {
        if (number <= 0 || _entries.ContainsKey(number))
            return false;

        if (chargePolicy)
            _options.StreamDecodePolicy?.ConsumeStructuralEntries(1);

        _entries[number] = entry;
        return true;
    }

    private void JournalProtectedXrefRow(int number, XrefEntry entry, bool isEffective)
    {
        if (_options.StreamDecodePolicy is null)
            return;

        _protectedXrefRows.Add(
            new ProtectedXrefRowRecord(
                _protectedCurrentXrefRevision,
                _protectedXrefRows.Count,
                number,
                entry,
                isEffective));
    }

    ReadOnlyMemory<byte> IPdfStreamPayloadReader.ReadPayload(
        PdfDictionary dictionary, int dataStart, out int endPosition)
    {
        ReadOnlySpan<byte> span = _data;
        bool strict = _options.StreamDecodePolicy is not null;
        long declared = strict
            ? ReadProtectedDeclaredLength(dictionary)
            : ReadDeclaredLength(dictionary);
        int length = -1;

        if (strict)
        {
            if (declared > int.MaxValue ||
                declared > span.Length - (long)dataStart ||
                !TryFindProtectedEndstream(span, dataStart + (int)declared, out endPosition))
            {
                throw new PdfFormatException(
                    "A protected stream's Length does not point exactly to the EOL before endstream.");
            }

            length = (int)declared;
            if (_currentObject.Number > 0)
            {
                int payloadEnd = checked(dataStart + length);
                AddProtectedStreamPayloadRange(
                    FindProtectedEffectiveRevision(_currentObject),
                    _currentObject,
                    dataStart,
                    payloadEnd);

                // WalkChain parses xref streams before the effective object map exists. Only a
                // GetObject/LoadFromFile parse represents the live object whose range callers ask
                // for; historical xref-stream payloads remain in the all-ranges journal above.
                if (_loading.Contains(_currentObject.Number))
                {
                    RecordProtectedRange(
                        _protectedStreamPayloadRanges,
                        _currentObject.Number,
                        dataStart,
                        payloadEnd,
                        "stream payload",
                        allowEmpty: true);
                }
            }

            ReadOnlyMemory<byte> protectedPayload = _memory.Slice(dataStart, length);
            if (_security is null || !ShouldDecrypt(dictionary))
                return protectedPayload;

            return _security.DecryptStream(_currentObject, protectedPayload.Span, dictionary);
        }

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

    private long ReadProtectedDeclaredLength(PdfDictionary dictionary)
    {
        if (!dictionary.TryGetRaw(PdfNames.Length, out PdfValue length))
            throw new PdfFormatException("A protected stream requires a Length entry.");

        long declared;
        if (length.Kind == PdfValueKind.Integer)
        {
            declared = length.AsInteger;
        }
        else if (length.IsReference)
        {
            if (dictionary.GetName(PdfNames.Type) == PdfNames.XRef)
            {
                throw new PdfFormatException(
                    "A protected cross-reference stream requires a direct Length entry.");
            }

            PdfObjectId lengthId = length.AsReference;
            if (dictionary.GetName(PdfNames.Type) == PdfNames.ObjStm &&
                _entries.TryGetValue(lengthId.Number, out XrefEntry entry) &&
                entry.Type == XrefEntryType.Compressed)
            {
                throw new PdfFormatException(
                    "A protected object stream's Length object cannot be compressed.");
            }

            PdfValue resolved = Resolve(length);
            if (resolved.Kind != PdfValueKind.Integer)
            {
                throw new PdfFormatException(
                    "A protected stream's indirect Length shall resolve to an integer.");
            }

            declared = resolved.AsInteger;
        }
        else
        {
            throw new PdfFormatException("A protected stream's Length shall be an integer or indirect reference.");
        }

        if (declared < 0)
            throw new PdfFormatException("A protected stream's Length shall be nonnegative.");

        return declared;
    }

    private static bool TryFindProtectedEndstream(
        ReadOnlySpan<byte> span,
        int payloadEnd,
        out int endPosition)
    {
        endPosition = payloadEnd;
        if (payloadEnd < 0 || payloadEnd >= span.Length)
            return false;

        int markerStart;
        if (span[payloadEnd] == (byte)'\n')
        {
            markerStart = payloadEnd + 1;
        }
        else if (span[payloadEnd] == (byte)'\r')
        {
            markerStart = payloadEnd + 1;
            if (markerStart < span.Length && span[markerStart] == (byte)'\n')
                markerStart++;
        }
        else
        {
            return false;
        }

        const int KeywordLength = 9;
        if (markerStart > span.Length - KeywordLength ||
            !span[markerStart..].StartsWith("endstream"u8))
        {
            return false;
        }

        endPosition = markerStart + KeywordLength;
        return endPosition == span.Length || !PdfCharacters.IsRegular(span[endPosition]);
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
