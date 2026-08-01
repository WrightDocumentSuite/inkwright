using System.Text;
using System.Xml.Linq;
using Inkwright.Diagnostics;

namespace Inkwright.Annotations;

/// <summary>
/// Sending a round of review out and taking one back in. Reviewers who never see each other's
/// files still have to agree on the comments, and the two formats for that are XFDF — the XML one
/// of ISO 19444-1 — and the older FDF of ISO 32000-1 §12.7.7, which is PDF syntax and can therefore
/// carry the drawings as well.
/// </summary>
public static class PdfReviewExchange
{
    extension(PdfDocument document)
    {
        /// <summary>Writes the document's comments and form values as XFDF.</summary>
        /// <param name="content">Which halves to write; both by default.</param>
        /// <param name="href">The file to name as the payload's source, or <see langword="null"/> for none.</param>
        /// <returns>The XFDF text, ready to be sent or saved.</returns>
        public string ExportXfdf(PdfExchangeContent content = PdfExchangeContent.All, string? href = null)
        {
            ArgumentNullException.ThrowIfNull(document);

            using var buffer = new MemoryStream();
            document.ExportXfdf(buffer, content, href);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>Writes the document's comments and form values as XFDF.</summary>
        /// <param name="output">Where to write; the stream is left open.</param>
        /// <param name="content">Which halves to write; both by default.</param>
        /// <param name="href">The file to name as the payload's source, or <see langword="null"/> for none.</param>
        public void ExportXfdf(
            Stream output, PdfExchangeContent content = PdfExchangeContent.All, string? href = null)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(output);

            var settings = new System.Xml.XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                CloseOutput = false,

                // Line breaks inside a value are content, not formatting: entitizing a carriage
                // return is the only way it survives the far end's XML parser (§5.8.2).
                NewLineHandling = System.Xml.NewLineHandling.Entitize,
            };

            using System.Xml.XmlWriter writer = System.Xml.XmlWriter.Create(output, settings);
            XfdfWriter.Write(document, href, content).Save(writer);
        }

        /// <summary>
        /// Writes just the values of the document's form fields, which is what submitting a filled
        /// form sends back.
        /// </summary>
        /// <param name="href">The file to name as the payload's source, or <see langword="null"/> for none.</param>
        public string ExportFormData(string? href = null) =>
            document.ExportXfdf(PdfExchangeContent.Fields, href);

        /// <summary>Writes the document's comments and form values as an FDF file.</summary>
        /// <param name="content">Which halves to write; both by default.</param>
        /// <returns>The complete FDF file.</returns>
        public byte[] ExportFdf(PdfExchangeContent content = PdfExchangeContent.All)
        {
            ArgumentNullException.ThrowIfNull(document);

            using var buffer = new MemoryStream();
            FdfWriter.Write(document, buffer, content);
            return buffer.ToArray();
        }

        /// <summary>Writes the document's comments and form values as an FDF file.</summary>
        /// <param name="output">Where to write; the stream is left open.</param>
        /// <param name="content">Which halves to write; both by default.</param>
        public void ExportFdf(Stream output, PdfExchangeContent content = PdfExchangeContent.All)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(output);

            FdfWriter.Write(document, output, content);
        }

        /// <summary>
        /// Writes just the values of the document's form fields as FDF, which is what submitting a
        /// filled form as FDF sends back. Unlike XFDF, the drawings travel with it.
        /// </summary>
        public byte[] ExportFormDataFdf() => document.ExportFdf(PdfExchangeContent.Fields);

        /// <summary>
        /// Writes every comment to a file, choosing the format from the extension: <c>.fdf</c> gets
        /// FDF, anything else gets XFDF.
        /// </summary>
        /// <param name="path">Where to write.</param>
        public void ExportReview(string path)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentException.ThrowIfNullOrEmpty(path);

            using FileStream file = File.Create(path);

            if (Path.GetExtension(path).Equals(".fdf", StringComparison.OrdinalIgnoreCase))
                document.ExportFdf(file);
            else
                document.ExportXfdf(file);
        }

        /// <summary>
        /// Takes in an XFDF payload. A comment the document already carries under the same name is
        /// rewritten in place and anything else is added to the page it names; a form field is
        /// given the value the payload states, and one the document does not carry is reported
        /// rather than created, because XFDF cannot say what kind of field it would be (§5.6.1).
        /// </summary>
        /// <param name="xfdf">The XFDF text.</param>
        /// <exception cref="PdfFormatException">The text is not well-formed XFDF.</exception>
        public PdfReviewImportResult ImportXfdf(string xfdf)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(xfdf);

            List<string> warnings = [];
            XfdfReader.Payload payload = XfdfReader.Read(document, xfdf, warnings);

            int filled = XfdfFields.Apply(document, payload.Fields, warnings);
            PdfReviewImportResult result = ReviewMerger.Merge(document, payload.Annotations, warnings);

            return result with { FieldsFilled = filled };
        }

        /// <summary>Takes in a round of review written as XFDF.</summary>
        /// <param name="input">The stream to read; it is left open.</param>
        /// <exception cref="PdfFormatException">The stream does not hold well-formed XFDF.</exception>
        public PdfReviewImportResult ImportXfdf(Stream input)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(input);

            using var reader = new StreamReader(input, Encoding.UTF8, true, leaveOpen: true);
            return document.ImportXfdf(reader.ReadToEnd());
        }

        /// <summary>
        /// Takes in an FDF payload. Comments merge as a round of review does, and any form field
        /// values are written into the document's own fields by fully qualified name — a field the
        /// document does not carry is reported rather than created (§12.7.8.3.2). Whatever the
        /// sender drew travels with it, so the comments look here as they looked there.
        /// </summary>
        /// <param name="fdf">The complete FDF file.</param>
        /// <exception cref="PdfFormatException">The bytes do not hold a readable FDF.</exception>
        public PdfReviewImportResult ImportFdf(byte[] fdf)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(fdf);

            List<string> warnings = [];
            FdfReader.Payload payload = FdfReader.Read(document, fdf, warnings);

            int filled = XfdfFields.Apply(document, payload.Fields, warnings);
            PdfReviewImportResult result = ReviewMerger.Merge(document, payload.Annotations, warnings);

            return result with { FieldsFilled = filled };
        }

        /// <summary>Takes in a round of review written as FDF.</summary>
        /// <param name="input">The stream to read; it is left open.</param>
        /// <exception cref="PdfFormatException">The stream does not hold a readable FDF.</exception>
        public PdfReviewImportResult ImportFdf(Stream input)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(input);

            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            return document.ImportFdf(buffer.ToArray());
        }

        /// <summary>
        /// Takes in a round of review from a file, working out for itself which of the two formats
        /// it is: the first bytes say <c>%FDF</c> or they do not.
        /// </summary>
        /// <param name="path">The file to read.</param>
        /// <exception cref="PdfFormatException">The file holds neither readable FDF nor XFDF.</exception>
        public PdfReviewImportResult ImportReview(string path)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentException.ThrowIfNullOrEmpty(path);

            byte[] bytes = File.ReadAllBytes(path);

            return LooksLikeFdf(bytes)
                ? document.ImportFdf(bytes)
                : document.ImportXfdf(Encoding.UTF8.GetString(bytes));
        }
    }

    /// <summary>
    /// Whether the bytes are the COS spelling rather than the XML one. FDF is required to open with
    /// its header, and a byte-order mark or leading whitespace in front of it is common enough to
    /// be worth stepping over.
    /// </summary>
    private static bool LooksLikeFdf(ReadOnlySpan<byte> bytes)
    {
        int start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;

        while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
            start++;

        return bytes[start..].StartsWith("%FDF"u8);
    }
}
