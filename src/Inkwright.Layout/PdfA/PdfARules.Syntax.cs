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
            if (reader.TryGetObjectBytes(number, out ReadOnlyMemory<byte> bytes))
                CheckObject(context, bytes.Span, number, reported);
        }
    }

    private static void CheckObject(PdfAContext context, ReadOnlySpan<byte> bytes, int number, HashSet<string> reported)
    {
        CheckObjectHeader(context, bytes, number, reported);
        CheckStreamDelimiters(context, bytes, number, reported);
        CheckHexStrings(context, bytes, number, reported);
    }

    /// <summary>Clause 6.1.9: one white-space character between the parts of an object header.</summary>
    private static void CheckObjectHeader(
        PdfAContext context, ReadOnlySpan<byte> bytes, int number, HashSet<string> reported)
    {
        int keyword = bytes.IndexOf(" obj"u8);
        int position = 0;

        while (position < bytes.Length && IsDigit(bytes[position]))
            position++;

        bool spacing = position > 0 && position + 1 < bytes.Length && IsSpace(bytes[position]) && !IsSpace(bytes[position + 1]);
        int generation = position + 1;

        while (generation < bytes.Length && IsDigit(bytes[generation]))
            generation++;

        spacing &= generation > position + 1 &&
                   generation < bytes.Length &&
                   IsSpace(bytes[generation]) &&
                   bytes[(generation + 1)..].StartsWith("obj"u8);

        if (!spacing && reported.Add("6.1.9-header"))
        {
            context.Report(
                "6.1.9",
                $"Object {number} is not written as 'number generation obj' with a single white-space " +
                "character between the parts.");
        }

        if (keyword < 0 || keyword + 4 >= bytes.Length)
            return;

        if (!IsEol(bytes[keyword + 4]) && reported.Add("6.1.9-obj-eol"))
            context.Report("6.1.9", $"The obj keyword of object {number} is not followed by an end-of-line marker.");

        int end = bytes.LastIndexOf("endobj"u8);
        if (end > 0 && !IsEol(bytes[end - 1]) && reported.Add("6.1.9-endobj-eol"))
            context.Report("6.1.9", $"The endobj keyword of object {number} is not preceded by an end-of-line marker.");
    }

    /// <summary>Clause 6.1.7: how the stream payload is fenced off, and that Length agrees with it.</summary>
    private static void CheckStreamDelimiters(
        PdfAContext context, ReadOnlySpan<byte> bytes, int number, HashSet<string> reported)
    {
        int keyword = FindStreamKeyword(bytes);
        if (keyword < 0)
            return;

        string clause = context.Clause("6.1.7", "6.1.7.1");
        int after = keyword + 6;
        if (after >= bytes.Length)
            return;

        int start;
        if (bytes[after] == (byte)'\n')
        {
            start = after + 1;
        }
        else if (bytes[after] == (byte)'\r' && after + 1 < bytes.Length && bytes[after + 1] == (byte)'\n')
        {
            start = after + 2;
        }
        else
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

        int end = bytes[start..].IndexOf("endstream"u8);
        if (end < 0)
            return;

        end += start;
        if (end > start && !IsEol(bytes[end - 1]) && reported.Add($"{clause}-end"))
            context.Report(clause, $"The endstream keyword of object {number} is not preceded by an end-of-line marker.");
    }

    /// <summary>
    /// Where the stream keyword actually starts. The word turns up inside names as well — a media
    /// type of <c>application/octet-stream</c> is the usual one — so it only counts as the keyword
    /// when a delimiter stands in front of it.
    /// </summary>
    private static int FindStreamKeyword(ReadOnlySpan<byte> bytes)
    {
        int position = 0;

        while (true)
        {
            int found = bytes[position..].IndexOf("stream"u8);
            if (found < 0)
                return -1;

            int at = position + found;
            position = at + 6;

            byte before = at == 0 ? (byte)'\n' : bytes[at - 1];
            if (before is (byte)'>' or (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0)
                return at;
        }
    }

    /// <summary>Clause 6.1.6: a hexadecimal string is written with an even number of digits.</summary>
    private static void CheckHexStrings(
        PdfAContext context, ReadOnlySpan<byte> bytes, int number, HashSet<string> reported)
    {
        int i = 0;

        while (i < bytes.Length)
        {
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

                case (byte)'s' when bytes[i..].StartsWith("stream"u8):
                    int payload = bytes[i..].IndexOf("endstream"u8);
                    i = payload < 0 ? bytes.Length : i + payload;
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

        if (digits % 2 != 0 && reported.Add("6.1.6"))
        {
            context.Report(
                "6.1.6",
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
        int position = 0;

        while (true)
        {
            int found = bytes[position..].IndexOf("xref"u8);
            if (found < 0)
                return;

            int at = position + found;
            position = at + 4;

            // "startxref" ends with the same four letters and is not a table.
            if (at >= 5 && bytes[(at - 5)..at].SequenceEqual("start"u8))
                continue;

            if (at + 5 >= bytes.Length)
                return;

            int next = at + 4;
            int eol = 0;

            while (next < bytes.Length && IsEol(bytes[next]) && eol < 3)
            {
                eol += bytes[next] == (byte)'\r' && next + 1 < bytes.Length && bytes[next + 1] == (byte)'\n' ? 2 : 1;
                next += bytes[next] == (byte)'\r' && next + 1 < bytes.Length && bytes[next + 1] == (byte)'\n' ? 2 : 1;
            }

            if (eol is 1 or 2 && next < bytes.Length && IsDigit(bytes[next]))
                return;

            context.Report(
                "6.1.4",
                "The xref keyword and the first subsection header are not separated by exactly one " +
                "end-of-line marker.");

            return;
        }
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsSpace(byte value) => value is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0;

    private static bool IsEol(byte value) => value is (byte)'\r' or (byte)'\n';

    private static bool IsWhiteSpace(byte value) => IsSpace(value) || value == 0x0C;

    private static bool IsHex(byte value) =>
        IsDigit(value) || value is >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
}
