using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    /// <summary>
    /// Annex B of parts 2 and 3: what a digital signature in an archival file may look like.
    /// Part 4 replaced these file-shape rules with PAdES profile requirements in clause 6.5; this
    /// validator does not pretend that a dictionary-only inspection proves profile conformance.
    /// </summary>
    internal static void CheckSignatures(PdfAContext context)
    {
        if (context.Part is 1 or 4)
            return;

        foreach (PdfDictionary dictionary in context.Dictionaries)
        {
            if (dictionary.GetName(PdfNames.Type)?.Value == "Sig" || dictionary.ContainsKey(PdfNames.ByteRange))
                CheckSignature(context, dictionary);
        }
    }

    /// <summary>
    /// The digest has to cover the whole file save for the signature itself. A byte range that stops
    /// short leaves room for a page nobody signed, which is the classic way a signed PDF lies.
    /// </summary>
    private static void CheckSignature(PdfAContext context, PdfDictionary signature)
    {
        if (!signature.TryGetRaw(PdfNames.ByteRange, out PdfValue rawRange) ||
            rawRange.AsArray is not { Count: 4 } range ||
            !HasOnlyDirectIntegers(range))
        {
            context.Report(
                "B.1",
                "The signature needs a direct four-integer ByteRange, so what it covers is unambiguous.");
            return;
        }

        if (!signature.TryGetRaw(PdfNames.Contents, out PdfValue contents) ||
            contents.Kind != PdfValueKind.String ||
            !contents.IsHexString)
        {
            context.Report(
                "B.1",
                "The PDF Signature must be a direct hexadecimal string in the signature dictionary's Contents entry.");
            return;
        }

        foreach (KeyValuePair<PdfName, PdfValue> entry in signature)
        {
            if (!entry.Value.IsReference)
                continue;

            context.Report(
                "B.1",
                $"The signature dictionary's {entry.Key} value is indirect; every value in a byte-range signature must be direct.");
            break;
        }

        if (!context.HasFile)
            return;

        long start = range[0].AsInteger;
        long firstLength = range[1].AsInteger;
        long secondStart = range[2].AsInteger;
        long secondLength = range[3].AsInteger;
        long total = context.FileBytes.Length;

        if (start != 0)
            context.Report("B.1", "The signature's byte range does not start at the beginning of the file.");

        if (start < 0 || firstLength < 0 || secondStart < 0 || secondLength < 0)
        {
            context.Report("B.1", "The signature's byte range contains a negative offset or length.");
            return;
        }

        if (start > long.MaxValue - firstLength || secondStart > long.MaxValue - secondLength)
        {
            context.Report("B.1", "The signature's byte range exceeds the numeric range a PDF offset can represent.");
            return;
        }

        long firstEnd = start + firstLength;
        long coveredEnd = secondStart + secondLength;
        if (firstEnd > total || secondStart > total || coveredEnd > total)
        {
            context.Report(
                "B.1",
                "The signature's byte range has an offset or end outside the file.");
            return;
        }

        if (secondStart <= firstEnd)
        {
            context.Report("B.1", "The two halves of the byte range do not leave room for the signature itself.");
            return;
        }

        if (!GapIsContents(context.FileBytes.Span, firstEnd, secondStart, contents.AsStringBytes))
        {
            context.Report(
                "B.1",
                "The only bytes excluded by ByteRange must be exactly the hexadecimal Contents entry.");
        }

        if (coveredEnd < total && !EndsAtCompleteRevision(context.FileBytes.Span, coveredEnd))
        {
            context.Report(
                "B.1",
                $"The signature's byte range ends at byte {coveredEnd}, which is not the end of a complete PDF revision.");
        }
    }

    private static bool HasOnlyDirectIntegers(PdfArray range)
    {
        foreach (PdfValue value in range.AsSpan())
        {
            if (value.Kind != PdfValueKind.Integer)
                return false;
        }

        return true;
    }

    /// <summary>
    /// ISO 32000 requires the two ranges to meet the delimiters of Contents exactly. Comparing the
    /// decoded bytes as well as the delimiters prevents a different, equally-sized string from
    /// being used as a convenient unsigned hole.
    /// </summary>
    private static bool GapIsContents(
        ReadOnlySpan<byte> file,
        long firstEnd,
        long secondStart,
        ReadOnlySpan<byte> expectedContents)
    {
        if (firstEnd > int.MaxValue || secondStart > int.MaxValue)
            return false;

        int start = (int)firstEnd;
        int end = (int)secondStart;
        ReadOnlySpan<byte> gap = file[start..end];
        if (gap.Length < 2 || gap[0] != (byte)'<' || gap[^1] != (byte)'>')
            return false;

        if (!HasContentsKeyImmediatelyBefore(file, start))
            return false;

        return TryDecodeHexString(gap[1..^1], out byte[] decoded) &&
               decoded.AsSpan().SequenceEqual(expectedContents);
    }

    private static bool HasContentsKeyImmediatelyBefore(ReadOnlySpan<byte> file, int valueStart)
    {
        int searchEnd = valueStart;
        while (searchEnd > 0)
        {
            int relativeSlash = file[..searchEnd].LastIndexOf((byte)'/');
            if (relativeSlash < 0)
                return false;

            int tokenEnd = relativeSlash + 1;
            while (tokenEnd < valueStart && !IsPdfDelimiter(file[tokenEnd]))
                tokenEnd++;

            if (NameTokenIsContents(file[(relativeSlash + 1)..tokenEnd]) &&
                ContainsOnlySeparators(file[tokenEnd..valueStart]))
            {
                return true;
            }

            searchEnd = relativeSlash;
        }

        return false;
    }

    private static bool NameTokenIsContents(ReadOnlySpan<byte> token)
    {
        ReadOnlySpan<byte> expected = "Contents"u8;
        int output = 0;
        for (int at = 0; at < token.Length; at++)
        {
            byte value = token[at];
            if (value == (byte)'#')
            {
                if (at + 2 >= token.Length ||
                    HexValue(token[at + 1]) is not { } high ||
                    HexValue(token[at + 2]) is not { } low)
                {
                    return false;
                }

                value = (byte)((high << 4) | low);
                at += 2;
            }

            if (output >= expected.Length || expected[output++] != value)
                return false;
        }

        return output == expected.Length;
    }

    private static int? HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        _ => null,
    };

    private static bool IsPdfDelimiter(byte value) =>
        IsPdfWhitespace(value) || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or
            (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool ContainsOnlySeparators(ReadOnlySpan<byte> bytes)
    {
        int at = 0;
        while (at < bytes.Length)
        {
            if (IsPdfWhitespace(bytes[at]))
            {
                at++;
                continue;
            }

            if (bytes[at] != (byte)'%')
                return false;

            while (at < bytes.Length && bytes[at] is not (byte)'\r' and not (byte)'\n')
                at++;

            // Without an EOL the Contents token itself would still be inside the comment.
            if (at == bytes.Length)
                return false;
        }

        return true;
    }

    private static bool TryDecodeHexString(ReadOnlySpan<byte> body, out byte[] decoded)
    {
        int digits = 0;
        foreach (byte value in body)
        {
            if (!IsPdfWhitespace(value))
                digits++;
        }

        decoded = new byte[(digits + 1) / 2];
        int nibble = -1;
        int written = 0;
        foreach (byte value in body)
        {
            if (IsPdfWhitespace(value))
                continue;

            int digit = value switch
            {
                >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
                >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
                >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
                _ => -1,
            };

            if (digit < 0)
                return false;

            if (nibble < 0)
                nibble = digit;
            else
            {
                decoded[written++] = (byte)((nibble << 4) | digit);
                nibble = -1;
            }
        }

        if (nibble >= 0)
            decoded[written] = (byte)(nibble << 4);

        return true;
    }

    private static bool IsPdfWhitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    /// <summary>
    /// A later incremental update does not invalidate the range that covered the whole earlier
    /// revision. Such a revision ends at its own EOF marker, optionally followed by one EOL.
    /// </summary>
    private static bool EndsAtCompleteRevision(ReadOnlySpan<byte> file, long coveredEnd)
    {
        if (coveredEnd <= 0 || coveredEnd > file.Length)
            return false;

        ReadOnlySpan<byte> revision = file[..(int)coveredEnd];
        if (revision.EndsWith("\r\n"u8))
            revision = revision[..^2];
        else if (!revision.IsEmpty && revision[^1] is (byte)'\r' or (byte)'\n')
            revision = revision[..^1];

        return revision.EndsWith("%%EOF"u8);
    }
}
