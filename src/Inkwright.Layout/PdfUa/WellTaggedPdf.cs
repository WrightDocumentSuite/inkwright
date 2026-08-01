using Inkwright.Metadata;

namespace Inkwright.Layout;

/// <summary>
/// The conformance levels of Well-Tagged PDF 1.0. WTPDF is not an ISO standard but a PDF
/// Association specification, and it declares itself through the PDF Declarations mechanism rather
/// than through an identification schema of its own.
/// </summary>
public enum WellTaggedLevel
{
    /// <summary>Content can be extracted, reflowed and reused reliably.</summary>
    Reuse,

    /// <summary>
    /// The stronger level, which subsumes the requirements of PDF/UA-2. A file meeting it may also
    /// carry the accessibility identification schema.
    /// </summary>
    Accessibility,
}

/// <summary>
/// Declaring conformance with Well-Tagged PDF 1.0.
/// </summary>
/// <remarks>
/// As with every declaration, writing one is an assertion by the author rather than a proof. The
/// accessibility level is defined to meet everything ISO 14289-2 requires, so a file claiming it
/// should also pass <see cref="PdfUaProfile.Validate"/> against PDF/UA-2 — and most of what that
/// standard asks for is a judgement no program can make.
/// </remarks>
public static class WellTaggedPdf
{
    /// <summary>The identifier of the conformance level for reuse.</summary>
    public const string ReuseIdentifier = "http://pdfa.org/declarations/wtpdf/#reuse1.0";

    /// <summary>The identifier of the conformance level for accessibility.</summary>
    public const string AccessibilityIdentifier = "http://pdfa.org/declarations/wtpdf/#accessibility1.0";

    /// <summary>The identifier a level is declared by.</summary>
    /// <param name="level">The level.</param>
    public static string IdentifierOf(WellTaggedLevel level) =>
        level == WellTaggedLevel.Accessibility ? AccessibilityIdentifier : ReuseIdentifier;

    /// <summary>Whether a document already claims a level.</summary>
    /// <param name="document">The document to inspect.</param>
    /// <param name="level">The level to look for.</param>
    public static bool Declares(PdfDocument document, WellTaggedLevel level)
    {
        ArgumentNullException.ThrowIfNull(document);

        string identifier = IdentifierOf(level);

        foreach (PdfDeclaration declaration in PdfDeclarations.Read(document))
        {
            if (string.Equals(declaration.ConformsTo, identifier, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Adds a claim of conformance to a document's metadata.</summary>
    /// <param name="document">The document to mark.</param>
    /// <param name="level">The level being claimed.</param>
    /// <param name="claimedBy">Who is making the claim, when they wish to be named.</param>
    public static void Declare(PdfDocument document, WellTaggedLevel level, string? claimedBy = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var declaration = new PdfDeclaration(IdentifierOf(level))
        {
            ClaimedBy = claimedBy,
            ClaimedOn = claimedBy is null ? null : DateTimeOffset.Now,
        };

        // Existing claims are kept: a document may conform to several things at once, and the
        // specification expects exactly that.
        var claims = new List<PdfDeclaration>(PdfDeclarations.Read(document));
        claims.RemoveAll(existing =>
            string.Equals(existing.ConformsTo, declaration.ConformsTo, StringComparison.Ordinal));

        claims.Add(declaration);

        var metadata = new XmpMetadata();
        metadata.CopyFrom(document.Info);

        // The accessibility level is defined to meet ISO 14289-2, so a file claiming it may say so
        // through the identification schema as well.
        if (level == WellTaggedLevel.Accessibility)
        {
            metadata.PdfUaPart = 2;
            metadata.PdfUaRevision = 2024;
        }

        metadata.AddSchema(PdfDeclarations.Build(claims));
        metadata.ApplyTo(document);
    }
}
