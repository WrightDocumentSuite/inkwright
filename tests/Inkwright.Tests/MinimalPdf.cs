using System.Globalization;
using System.Text;

namespace Inkwright.Tests;

/// <summary>
/// Hand-written files with exact byte layouts, used where a test needs to damage a specific part of
/// the syntax. The cross-reference table is computed from the body so edits stay consistent.
/// </summary>
public static class MinimalPdf
{
    /// <summary>The body of a one-page document, without cross-reference data.</summary>
    public const string Source = """
        %PDF-1.7
        1 0 obj
        << /Type /Catalog /Pages 2 0 R >>
        endobj
        2 0 obj
        << /Type /Pages /Kids [3 0 R] /Count 1 >>
        endobj
        3 0 obj
        << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>
        endobj
        4 0 obj
        << /Length 44 >>
        stream
        BT /F1 24 Tf 72 700 Td (Hello, world) Tj ET
        endstream
        endobj
        5 0 obj
        << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
        endobj

        """;

    /// <summary>Builds the default one-page document.</summary>
    public static byte[] OnePage() => Build(Source);

    /// <summary>Builds a file from a body, appending a cross-reference table that matches it.</summary>
    /// <param name="source">The body, starting with the header and ending after the last object.</param>
    public static byte[] Build(string source)
    {
        string body = source.ReplaceLineEndings("\n");
        byte[] bodyBytes = Encoding.ASCII.GetBytes(body);

        var offsets = new List<int>();
        for (int number = 1; ; number++)
        {
            string marker = $"\n{number} 0 obj";
            int index = body.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                break;

            offsets.Add(index + 1);
        }

        var xref = new StringBuilder();
        xref.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n");
        xref.Append("0000000000 65535 f \n");
        foreach (int offset in offsets)
            xref.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");

        xref.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n");
        xref.Append(CultureInfo.InvariantCulture, $"{bodyBytes.Length}\n%%EOF\n");

        byte[] xrefBytes = Encoding.ASCII.GetBytes(xref.ToString());
        byte[] file = new byte[bodyBytes.Length + xrefBytes.Length];
        bodyBytes.CopyTo(file, 0);
        xrefBytes.CopyTo(file, bodyBytes.Length);
        return file;
    }
}
