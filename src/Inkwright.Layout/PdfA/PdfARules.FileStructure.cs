using Inkwright.Content;
using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>
/// The archival rules, grouped the way clause 6 of ISO 19005 groups them. Each check names the
/// clause it comes from, and where the parts disagree the difference is stated in the code rather
/// than smoothed over.
/// </summary>
internal static partial class PdfARules
{
    /// <summary>Clause 6.1: the shape of the file itself.</summary>
    internal static void CheckFileStructure(PdfAContext context)
    {
        CheckEncryption(context);
        CheckHeader(context);
        CheckTrailer(context);
        CheckStreamDictionaries(context);
        CheckPermissions(context);
        CheckLimits(context);
        CheckInlineImages(context);
    }

    private static void CheckEncryption(PdfAContext context)
    {
        if (!context.Document.WasEncrypted && !context.Document.Trailer.ContainsKey(PdfName.Get("Encrypt")))
            return;

        context.Report(
            "6.1.3",
            "The document is encrypted. Every part of PDF/A forbids the Encrypt keyword outright, " +
            "because a file nobody can open in fifty years is not preserved.");
    }

    /// <summary>
    /// The header has to start at byte zero and be followed by a comment of high bytes, which is how
    /// transfer tools tell the file is binary and must not be mangled.
    /// </summary>
    private static void CheckHeader(PdfAContext context)
    {
        if (!context.HasFile)
            return;

        // Part 4 is written against PDF 2.0 and says so in the header: 6.1.2 requires "%PDF-2.n"
        // where the parts before it require "%PDF-1.n". The two are mutually exclusive, which is
        // why no single file can claim both PDF/A-4 and any earlier part.
        bool partFour = context.Part == 4;
        ReadOnlySpan<byte> expected = partFour ? "%PDF-2."u8 : "%PDF-1."u8;

        ReadOnlySpan<byte> bytes = context.FileBytes.Span;
        if (bytes.Length < 9 || !bytes.StartsWith(expected))
        {
            context.Report(
                "6.1.2",
                $"The file does not begin with a %PDF-{(partFour ? '2' : '1')}.n header at byte zero.");
            return;
        }

        if (partFour && bytes[7] is < (byte)'0' or > (byte)'9')
            context.Report("6.1.2", $"The header declares version 2.{(char)bytes[7]}, which is not a decimal digit.");
        else if (!partFour && context.Part > 1 && bytes[7] is < (byte)'0' or > (byte)'7')
            context.Report("6.1.2", $"The header declares version 1.{(char)bytes[7]}, and PDF/A allows 1.0 to 1.7.");

        int line = bytes.IndexOfAny((byte)'\r', (byte)'\n');
        if (line < 0)
        {
            context.Report("6.1.2", "The header line has no end-of-line marker.");
            return;
        }

        int comment = line;
        while (comment < bytes.Length && bytes[comment] is (byte)'\r' or (byte)'\n')
            comment++;

        if (comment >= bytes.Length || bytes[comment] != (byte)'%' || !HighBytes(bytes[(comment + 1)..]))
        {
            context.Report(
                "6.1.2",
                "The header is not followed by a comment of at least four bytes above 127, so tools " +
                "that sniff file types would treat the file as text and could corrupt it.");
        }

        static bool HighBytes(ReadOnlySpan<byte> tail)
        {
            for (int i = 0; i < 4; i++)
            {
                if (i >= tail.Length || tail[i] <= 127)
                    return false;
            }

            return true;
        }
    }

    private static void CheckTrailer(PdfAContext context)
    {
        if (context.Document.Trailer.GetArray(PdfName.Get("ID")) is not { Count: 2 })
            context.Report("6.1.3", "The trailer has no two-part ID, so the file cannot be identified across copies.");

        if (context.Part == 4)
        {
            CheckInformationDictionaryIsAbsent(context);
            CheckCatalogVersion(context);
        }

        if (!context.HasFile)
            return;

        ReadOnlySpan<byte> bytes = context.FileBytes.Span;
        int marker = bytes.LastIndexOf("%%EOF"u8);
        if (marker < 0)
        {
            context.Report("6.1.3", "The file has no %%EOF marker.");
            return;
        }

        ReadOnlySpan<byte> tail = bytes[(marker + 5)..];
        bool clean = tail.Length == 0 ||
                     tail.SequenceEqual("\n"u8) || tail.SequenceEqual("\r"u8) || tail.SequenceEqual("\r\n"u8);

        if (!clean)
            context.Report("6.1.3", $"{tail.Length} bytes follow the last %%EOF; only one end-of-line marker may.");
    }

    /// <summary>
    /// The document information dictionary, which PDF 2.0 deprecated and part 4 all but removes
    /// (6.1.3). It survives only to carry a modification date alongside a <c>PieceInfo</c> entry;
    /// everything a reader should know now lives in the XMP packet.
    /// </summary>
    private static void CheckInformationDictionaryIsAbsent(PdfAContext context)
    {
        // Two ways the trailer in memory disagrees with the file: a document on its way to being
        // written may already have been told to leave the dictionary out, and one read from a file
        // that had none was given an empty one so callers can set a title without checking.
        if (context.Document.SuppressInformationDictionary || !context.Document.HadInformationDictionary)
            return;

        if (!context.Document.Trailer.ContainsKey(PdfNames.Info))
            return;

        bool hasPieceInfo = context.Document.Catalog.ContainsKey(PdfName.Get("PieceInfo"));

        if (!hasPieceInfo)
        {
            context.Report(
                "6.1.3",
                "The trailer carries Info, which part 4 allows only when the catalogue has a PieceInfo entry.");
            return;
        }

        if (context.Resolve(context.Document.Trailer.Get(PdfNames.Info)).AsDictionary is not { } info)
            return;

        foreach (PdfName key in info.Keys)
        {
            if (key.Value != "ModDate")
                context.Report("6.1.3", $"The document information dictionary carries {key.Value}; only ModDate is allowed.");
        }
    }

    /// <summary>
    /// Clause 6.1.12: when the catalogue overrides the header version, it may only override it
    /// within PDF 2.0 — three characters, a 2, a decimal point and a digit.
    /// </summary>
    private static void CheckCatalogVersion(PdfAContext context)
    {
        if (context.Resolve(context.Document.Catalog.Get(PdfNames.Version)).AsName?.Value is not { } version)
            return;

        if (version.Length != 3 || version[0] != '2' || version[1] != '.' || !char.IsAsciiDigit(version[2]))
        {
            context.Report(
                "6.1.12",
                $"The catalogue declares version {version}; part 4 allows exactly three characters of the form 2.n.");
        }
    }

    /// <summary>
    /// A stream that points outside the file, or one packed with a filter the standard withdrew,
    /// makes the document depend on something that will not travel with it.
    /// </summary>
    private static void CheckStreamDictionaries(PdfAContext context)
    {
        foreach (PdfStream stream in context.Streams)
        {
            PdfDictionary dictionary = stream.Dictionary;

            foreach (string key in (string[])["F", "FFilter", "FDecodeParms", "FDecodeParams"])
            {
                if (dictionary.ContainsKey(PdfName.Get(key)))
                {
                    context.Report(
                        context.Clause("6.1.7", "6.1.7.1"),
                        $"A stream dictionary carries {key}, which points at data outside the file.");
                }
            }

            CheckFilters(context, dictionary);
        }
    }

    private static void CheckFilters(PdfAContext context, PdfDictionary dictionary)
    {
        string clause = context.Clause("6.1.10", "6.1.7.2");

        foreach (string filter in FilterNames(dictionary, PdfNames.Filter))
        {
            if (filter is "LZWDecode" or "LZW")
            {
                context.Report(clause, "A stream uses LZWDecode, which no part of PDF/A permits.");
                continue;
            }

            if (filter == "Crypt" && !IsIdentityCrypt(dictionary))
                context.Report(clause, "A stream uses a Crypt filter that is not Identity.");

            if (context.Part > 1 && !Known(filter))
                context.Report(clause, $"A stream uses the filter {filter}, which ISO 32000-1 table 6 does not list.");
        }

        static bool Known(string filter) => filter is
            "ASCIIHexDecode" or "ASCII85Decode" or "LZWDecode" or "FlateDecode" or "RunLengthDecode" or
            "CCITTFaxDecode" or "JBIG2Decode" or "DCTDecode" or "JPXDecode" or "Crypt";
    }

    private static bool IsIdentityCrypt(PdfDictionary dictionary)
    {
        PdfValue parms = dictionary.Get(PdfName.Get("DecodeParms"));
        PdfDictionary? single = parms.AsDictionary;

        if (single is null && parms.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
                single ??= array.GetDictionary(i);
        }

        return single?.GetName(PdfName.Get("Name"))?.Value is null or "Identity";
    }

    private static List<string> FilterNames(PdfDictionary dictionary, PdfName key)
    {
        List<string> names = [];
        PdfValue value = dictionary.Get(key);

        if (value.AsName is { } single)
            names.Add(single.Value);
        else if (value.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array.Get(i).AsName is { } name)
                    names.Add(name.Value);
            }
        }

        return names;
    }

    /// <summary>Clause 6.1.12: the permissions dictionary may hold two keys and no others.</summary>
    private static void CheckPermissions(PdfAContext context)
    {
        if (context.Part == 1)
            return;

        if (context.Document.Catalog.GetDictionary(PdfName.Get("Perms")) is not { } permissions)
            return;

        foreach (PdfName key in permissions.Keys.ToArray())
        {
            if (key.Value is not ("UR3" or "DocMDP"))
                context.Report("6.1.12", $"The permissions dictionary holds {key.Value}; only UR3 and DocMDP may appear.");
        }
    }

    /// <summary>Clause 6.1.13 of parts 2 and 3, Table C.1 of PDF Reference for part 1.</summary>
    private static void CheckLimits(PdfAContext context)
    {
        const int MaxStringBytes = 32767;
        const int MaxNameBytes = 127;
        const int MaxObjects = 8388607;
        const double MaxReal = 3.403e38;
        const double SmallestReal = 1.175e-38;

        string clause = context.Clause("6.1.12", "6.1.13");
        bool string_ = false, name = false, real = false, integer = false;

        foreach (PdfValue value in context.Values)
        {
            switch (value.Kind)
            {
                case PdfValueKind.String when !string_ && value.AsStringBytes.Length > MaxStringBytes:
                    string_ = true;
                    context.Report(clause, $"A string is longer than the {MaxStringBytes} bytes PDF/A allows.");
                    break;

                case PdfValueKind.Name when !name && value.AsName!.Utf8.Length > MaxNameBytes:
                    name = true;
                    context.Report(clause, $"A name is longer than the {MaxNameBytes} bytes PDF/A allows.");
                    break;

                case PdfValueKind.Real when !real && OutOfRange(value.AsNumber):
                    real = true;
                    context.Report(clause, $"A real number lies outside the range PDF/A allows: {value.AsNumber}.");
                    break;

                case PdfValueKind.Integer when !integer && (value.AsInteger > int.MaxValue || value.AsInteger < int.MinValue):
                    integer = true;
                    context.Report(clause, $"The integer {value.AsInteger} does not fit in 32 bits.");
                    break;

                default:
                    break;
            }
        }

        if (context.ObjectNumbers.Count > MaxObjects)
            context.Report(clause, $"The file holds more than {MaxObjects} indirect objects.");

        CheckPageBoxes(context, clause);
        CheckNestingDepth(context, clause);
        CheckColourantCount(context, clause);

        static bool OutOfRange(double number) =>
            Math.Abs(number) > MaxReal || (number != 0 && Math.Abs(number) < SmallestReal);
    }

    /// <summary>Clause 6.1.13: no more than 28 levels of saved graphics state.</summary>
    private static void CheckNestingDepth(PdfAContext context, string clause)
    {
        const int MaxNesting = 28;

        foreach (PdfAContentStream stream in PdfAContent.Enumerate(context))
        {
            int depth = 0;

            for (int i = 0; i < stream.Operations.Count; i++)
            {
                PdfOperator op = stream.Operations.GetOperator(i);

                if (op == PdfOperator.SaveState && ++depth > MaxNesting)
                {
                    context.Report(
                        clause,
                        $"The content of {stream.Description} nests q/Q more than {MaxNesting} levels deep.");

                    return;
                }

                if (op == PdfOperator.RestoreState && depth > 0)
                    depth--;
            }
        }
    }

    /// <summary>Clause 6.1.13: a DeviceN colour space names at most 32 colourants.</summary>
    private static void CheckColourantCount(PdfAContext context, string clause)
    {
        foreach (PdfValue value in context.Values)
        {
            if (value.AsArray is not { Count: >= 2 } array || array.Get(0).AsName?.Value != "DeviceN")
                continue;

            if (array.Get(1).AsArray is { Count: > 32 } colourants)
            {
                context.Report(clause, $"A DeviceN colour space names {colourants.Count} colourants; PDF/A allows 32.");
                return;
            }
        }
    }

    private static void CheckPageBoxes(PdfAContext context, string clause)
    {
        if (context.Part == 1)
            return;

        for (int i = 0; i < context.Document.Pages.Count; i++)
        {
            PdfRectangle box = context.Document.Pages[i].MediaBox;
            double smallest = Math.Min(box.Width, box.Height);
            double largest = Math.Max(box.Width, box.Height);

            if (smallest < 3 || largest > 14400)
            {
                context.Report(
                    clause,
                    $"Page {i + 1} measures {box.Width:0.#} by {box.Height:0.#} units; PDF/A allows 3 to 14 400.");
            }
        }
    }

    /// <summary>Clause 6.1.10 of parts 2 and 3: an inline image may not smuggle in a withdrawn filter.</summary>
    private static void CheckInlineImages(PdfAContext context)
    {
        if (context.Part == 1)
            return;

        foreach (PdfAContentStream stream in PdfAContent.Enumerate(context))
        {
            for (int i = 0; i < stream.Operations.Count; i++)
            {
                if (stream.Operations.GetOperator(i) != PdfOperator.InlineImage)
                    continue;

                ReadOnlySpan<PdfValue> operands = stream.Operations.GetOperands(i);
                if (operands.Length == 0 || operands[0].AsStream is not { } image)
                    continue;

                CheckInlineFilters(context, image.Dictionary, stream.Description);
            }
        }
    }

    private static void CheckInlineFilters(PdfAContext context, PdfDictionary image, string where)
    {
        foreach (string filter in FilterNames(image, PdfName.Get("F")).Concat(FilterNames(image, PdfNames.Filter)))
        {
            if (filter is "LZW" or "LZWDecode" or "Crypt")
                context.Report("6.1.10", $"An inline image on {where} uses the {filter} filter, which PDF/A forbids.");
        }
    }
}
