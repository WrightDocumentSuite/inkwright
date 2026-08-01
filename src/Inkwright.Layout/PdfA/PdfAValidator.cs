namespace Inkwright.Layout;

/// <summary>
/// Runs every archival rule the library knows over a document. The groups follow the structure of
/// clause 6 of the standard rather than the structure of the code, so a reader with the text open
/// can find the check that matches the paragraph in front of them.
/// </summary>
internal static class PdfAValidator
{
    internal static IReadOnlyList<PdfAProblem> Validate(PdfDocument document, PdfAConformance conformance)
    {
        var context = new PdfAContext(document, conformance);

        PdfARules.CheckFileStructure(context);

        // Encryption alone sinks the claim. When the file was opened locked the strings and
        // streams are still ciphertext, and rules read over ciphertext report gibberish; when it
        // was decrypted, everything else it says is moot until the file is saved without the
        // cipher anyway.
        if (document.WasEncrypted)
            return context.Problems;

        PdfARules.CheckSyntax(context);
        PdfARules.CheckGraphics(context);
        PdfARules.CheckFonts(context);
        PdfARules.CheckInteractive(context);
        PdfARules.CheckMetadata(context);
        PdfARules.CheckLogicalStructure(context);
        PdfARules.CheckAttachments(context);
        PdfARules.CheckSignatures(context);

        return context.Problems;
    }
}
