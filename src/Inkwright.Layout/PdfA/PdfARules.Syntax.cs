using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    /// <summary>
    /// The clauses that are about the bytes rather than the objects: how an indirect object is
    /// spelled, how a stream is delimited, how a hexadecimal string is written. None of this
    /// survives parsing, so it has to be read off the file itself.
    /// </summary>
    internal static void CheckSyntax(PdfAContext context)
    {
        if (!context.HasFile || context.Document.Reader is not { } reader)
            return;

        CheckCrossReferenceLayout(context);

        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (int number in reader.LiveObjectNumbers)
        {
            if (reader.TryGetObjectRange(number, out int start, out int end))
            {
                int payloadStart = -1;
                int payloadEnd = -1;
                if (reader.TryGetStreamPayloadRange(number, out int absoluteStart, out int absoluteEnd) &&
                    absoluteStart >= start &&
                    absoluteEnd <= end)
                {
                    payloadStart = absoluteStart - start;
                    payloadEnd = absoluteEnd - start;
                }

                CheckObject(
                    context,
                    context.FileBytes.Span[start..end],
                    number,
                    start,
                    end,
                    payloadStart,
                    payloadEnd,
                    reported);
            }
            else if (reader.IsInObjectStream(number) &&
                     reader.TryGetCompressedObjectBytes(number, out ReadOnlyMemory<byte> compressed))
            {
                // Object-stream members deliberately have no indirect-object header or endobj.
                // Their lexical strings still remain subject to the even-hex-digit rule, and the
                // offset table gives us the exact per-object boundary needed to inspect them.
                CheckHexStrings(
                    context,
                    compressed.Span,
                    number,
                    payloadStart: -1,
                    payloadEnd: -1,
                    reported);
            }
            else
            {
                string clause = context.Part is 2 or 3 ? "6.1.9" : "6.1.8";
                context.Report(
                    clause,
                    $"Live object {number} has no exact cross-reference header/generation or " +
                    "required endobj boundary.");
            }
        }
    }

    private static void CheckObject(
        PdfAContext context,
        ReadOnlySpan<byte> bytes,
        int number,
        int start,
        int end,
        int payloadStart,
        int payloadEnd,
        HashSet<string> reported)
    {
        CheckObjectHeader(context, bytes, number, start, end, reported);
        CheckStreamDelimiters(context, bytes, number, payloadStart, payloadEnd, reported);
        CheckHexStrings(context, bytes, number, payloadStart, payloadEnd, reported);
    }

    /// <summary>
    /// Clauses 6.1.8 (parts 1 and 4) and 6.1.9 (parts 2 and 3): object spacing and line endings.
    /// </summary>
    private static void CheckObjectHeader(
        PdfAContext context,
        ReadOnlySpan<byte> bytes,
        int number,
        int start,
        int end,
        HashSet<string> reported)
    {
        string clause = context.Part is 2 or 3 ? "6.1.9" : "6.1.8";
        int position = 0;

        while (position < bytes.Length && IsDigit(bytes[position]))
            position++;

        bool spacing = position > 0 && position + 1 < bytes.Length && IsSpace(bytes[position]) && !IsSpace(bytes[position + 1]);
        int generation = position + 1;

        while (generation < bytes.Length && IsDigit(bytes[generation]))
            generation++;

        int keyword = generation < bytes.Length &&
                      IsSpace(bytes[generation]) &&
                      bytes[(generation + 1)..].StartsWith("obj"u8)
            ? generation + 1
            : -1;
        spacing &= generation > position + 1 &&
                   keyword >= 0;

        if (!spacing && reported.Add($"{clause}-header"))
        {
            context.Report(
                clause,
                $"Object {number} is not written as 'number generation obj' with a single white-space " +
                "character between the parts.");
        }

        ReadOnlySpan<byte> file = context.FileBytes.Span;
        if ((start == 0 || !IsEol(file[start - 1])) && reported.Add($"{clause}-object-start-eol"))
        {
            context.Report(
                clause,
                $"The object number of object {number} is not preceded by an end-of-line marker.");
        }

        if (keyword < 0 || keyword + 3 >= bytes.Length)
            return;

        if (!IsEol(bytes[keyword + 3]) && reported.Add($"{clause}-obj-eol"))
            context.Report(clause, $"The obj keyword of object {number} is not followed by an end-of-line marker.");

        int endKeyword = bytes.LastIndexOf("endobj"u8);
        if (endKeyword < 0)
        {
            if (reported.Add($"{clause}-missing-endobj"))
                context.Report(clause, $"Object {number} has no required endobj keyword.");

            return;
        }

        if (endKeyword > 0 && !IsEol(bytes[endKeyword - 1]) && reported.Add($"{clause}-endobj-eol"))
            context.Report(clause, $"The endobj keyword of object {number} is not preceded by an end-of-line marker.");

        if ((end >= file.Length || !IsEol(file[end])) && reported.Add($"{clause}-object-end-eol"))
        {
            context.Report(
                clause,
                $"The endobj keyword of object {number} is not followed by an end-of-line marker.");
        }
    }

    /// <summary>Clause 6.1.7: how the stream payload is fenced off, and that Length agrees with it.</summary>
    private static void CheckStreamDelimiters(
        PdfAContext context,
        ReadOnlySpan<byte> bytes,
        int number,
        int payloadStart,
        int payloadEnd,
        HashSet<string> reported)
    {
        if (payloadStart < 0 || payloadEnd < payloadStart)
            return;

        string clause = context.Clause("6.1.7", "6.1.7.1", "6.1.6.1");
        int keywordEnd = payloadStart;
        while (keywordEnd > 0 && IsWhiteSpace(bytes[keywordEnd - 1]))
            keywordEnd--;

        int keyword = keywordEnd - 6;
        if (keyword < 0 || !bytes[keyword..keywordEnd].SequenceEqual("stream"u8))
            return;

        int after = keyword + 6;
        if (after >= bytes.Length)
            return;

        bool validStart = (bytes[after] == (byte)'\n' && payloadStart == after + 1) ||
                          (bytes[after] == (byte)'\r' &&
                           after + 1 < bytes.Length &&
                           bytes[after + 1] == (byte)'\n' &&
                           payloadStart == after + 2);
        if (!validStart)
        {
            if (reported.Add($"{clause}-start"))
            {
                context.Report(
                    clause,
                    $"The stream keyword of object {number} is followed by neither a line feed nor a " +
                    "carriage return and line feed pair.");
            }

            return;
        }

        int marker = payloadEnd;
        while (marker < bytes.Length && IsWhiteSpace(bytes[marker]))
            marker++;

        if (!bytes[marker..].StartsWith("endstream"u8))
            return;

        if ((marker == payloadEnd || !IsEol(bytes[marker - 1])) && reported.Add($"{clause}-end"))
            context.Report(clause, $"The endstream keyword of object {number} is not preceded by an end-of-line marker.");
    }

    /// <summary>Clause 6.1.6: a hexadecimal string is written with an even number of digits.</summary>
    private static void CheckHexStrings(
        PdfAContext context,
        ReadOnlySpan<byte> bytes,
        int number,
        int payloadStart,
        int payloadEnd,
        HashSet<string> reported)
    {
        int i = 0;

        while (i < bytes.Length)
        {
            if (i == payloadStart && payloadEnd > payloadStart)
            {
                i = payloadEnd;
                continue;
            }

            byte c = bytes[i];

            switch (c)
            {
                case (byte)'%':
                    while (i < bytes.Length && !IsEol(bytes[i]))
                        i++;

                    break;

                case (byte)'(':
                    i = SkipLiteral(bytes, i);
                    break;

                case (byte)'<' when i + 1 < bytes.Length && bytes[i + 1] == (byte)'<':
                    i += 2;
                    break;

                case (byte)'<':
                    i = ReadHex(context, bytes, i, number, reported);
                    break;

                default:
                    i++;
                    break;
            }
        }
    }

    private static int ReadHex(
        PdfAContext context, ReadOnlySpan<byte> bytes, int start, int number, HashSet<string> reported)
    {
        string clause = context.Part == 4 ? "6.1.5" : "6.1.6";
        int digits = 0;
        int i = start + 1;

        while (i < bytes.Length && bytes[i] != (byte)'>')
        {
            if (IsHex(bytes[i]))
                digits++;
            else if (!IsWhiteSpace(bytes[i]))
                return i + 1;

            i++;
        }

        if (digits % 2 != 0 && reported.Add(clause))
        {
            context.Report(
                clause,
                $"A hexadecimal string in object {number} has an odd number of digits, so its last " +
                "byte depends on the reader guessing a trailing zero.");
        }

        return i + 1;
    }

    private static int SkipLiteral(ReadOnlySpan<byte> bytes, int start)
    {
        int depth = 0;
        int i = start;

        while (i < bytes.Length)
        {
            byte c = bytes[i];

            if (c == (byte)'\\')
            {
                i += 2;
                continue;
            }

            if (c == (byte)'(')
                depth++;
            else if (c == (byte)')' && --depth == 0)
                return i + 1;

            i++;
        }

        return i;
    }

    /// <summary>Clause 6.1.4: one end-of-line marker between the xref keyword and the first subsection.</summary>
    private static void CheckCrossReferenceLayout(PdfAContext context)
    {
        ReadOnlySpan<byte> bytes = context.FileBytes.Span;
        foreach (int at in context.Document.Reader!.ProtectedClassicXrefSectionOffsets)
        {
            if (at < 0 || at > bytes.Length - 4 || !bytes[at..].StartsWith("xref"u8))
                continue;

            int next = at + 4;
            if (next < bytes.Length && bytes[next] == (byte)'\r')
            {
                next++;
                if (next < bytes.Length && bytes[next] == (byte)'\n')
                    next++;
            }
            else if (next < bytes.Length && bytes[next] == (byte)'\n')
            {
                next++;
            }
            else
            {
                next = -1;
            }

            if (next >= 0 && next < bytes.Length && IsDigit(bytes[next]))
                continue;

            context.Report(
                "6.1.4",
                "The xref keyword and the first subsection header are not separated by exactly one " +
                "end-of-line marker.");

            return;
        }
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsSpace(byte value) =>
        value is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0x0C or 0;

    private static bool IsEol(byte value) => value is (byte)'\r' or (byte)'\n';

    private static bool IsWhiteSpace(byte value) => IsSpace(value) || value == 0x0C;

    private static bool IsHex(byte value) =>
        IsDigit(value) || value is >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
}
