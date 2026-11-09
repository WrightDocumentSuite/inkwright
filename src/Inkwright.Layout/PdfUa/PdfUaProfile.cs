using Inkwright.Cos;
using Inkwright.IO;

namespace Inkwright.Layout;

/// <summary>
/// Checking and declaring conformance with PDF/UA, the accessibility profile of PDF standardised as
/// ISO 14289.
/// </summary>
/// <remarks>
/// A great deal of what the standard requires is a judgement no program can make: whether a tag is
/// the semantically right one, whether the reading order matches what a person would follow,
/// whether alternative text actually describes the picture. What is checked here is everything that
/// can be decided from the file itself. A document that passes is not thereby accessible; a document
/// that fails is definitely not.
/// </remarks>
public static class PdfUaProfile
{
    /// <summary>The namespace of the identification schema (clause 5).</summary>
    private const string IdentificationNamespace = "http://www.aiim.org/pdfua/ns/id/";

    /// <summary>
    /// Reports the accessibility rules a document breaks, each named by the clause that states it,
    /// and each marked as a requirement of the standard or as advice.
    /// </summary>
    /// <param name="document">The document to check.</param>
    /// <param name="conformance">The part being claimed.</param>
    public static IReadOnlyList<PdfUaProblem> Validate(PdfDocument document, PdfUaConformance conformance)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PdfUaValidator.Validate(document, conformance);
    }

    /// <summary>Checks a file on disk against a part of the standard.</summary>
    /// <param name="path">The file to check.</param>
    /// <param name="conformance">The part being claimed.</param>
    public static IReadOnlyList<PdfUaProblem> ValidateFile(string path, PdfUaConformance conformance)
    {
        using PdfDocument document = PdfDocument.Load(path, new PdfReadOptions { OpenLockedDocuments = true });
        return Validate(document, conformance);
    }

    /// <summary>The findings that are requirements rather than advice.</summary>
    /// <param name="problems">The findings to filter.</param>
    public static IEnumerable<PdfUaProblem> Violations(IEnumerable<PdfUaProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return problems.Where(problem => problem.Severity == PdfUaSeverity.Violation);
    }

    /// <summary>
    /// Marks a document as claiming conformance, which means writing the identification schema into
    /// the metadata and setting the two catalogue entries the standard requires outright. It does
    /// not make a document conform; run <see cref="Validate"/> to find out whether it does.
    /// </summary>
    /// <param name="document">The document to mark.</param>
    /// <param name="conformance">The part being claimed.</param>
    /// <param name="title">The document title, which the standard requires and readers display.</param>
    public static void Declare(PdfDocument document, PdfUaConformance conformance, string title)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (conformance.Part == 2)
            PreparePartTwo(document);

        // The reader has to be told to show the title rather than the file name (clause 7.1).
        PdfDictionary preferences = Ensure(document, document.Catalog, PdfName.Get("ViewerPreferences"));
        preferences.Set(PdfName.Get("DisplayDocTitle"), PdfValue.Boolean(true));

        // The document has to admit that it is tagged, and not flag its tagging as doubtful.
        PdfDictionary markInfo = Ensure(document, document.Catalog, PdfName.Get("MarkInfo"));
        markInfo.Set(PdfName.Get("Marked"), PdfValue.Boolean(true));
        markInfo.Set(PdfName.Get("Suspects"), PdfValue.Boolean(false));

        document.MarkChanged(document.CatalogId);

        document.Info.Title = title;

        var metadata = new Metadata.XmpMetadata
        {
            PdfUaPart = conformance.Part,
            PdfUaRevision = conformance.Part == 2 ? 2024 : null,
        };
        metadata.CopyFrom(document.Info);
        metadata.Title = title;
        metadata.ApplyTo(document);
    }

    private static void PreparePartTwo(PdfDocument document)
    {
        document.Version = PdfVersion.Max(document.Version, PdfVersion.Pdf20);

        // Document has the same meaning in both standard namespaces. Only supply a missing
        // namespace on that root; an explicit namespace or an arbitrary tree needs review.
        IReadOnlyList<Tagging.PdfStructureNode> roots = Tagging.StructureReader.Read(document);
        if (roots.Count != 1 || roots[0].Tag != "Document" ||
            !roots[0].Dictionary.Get(PdfName.Get("NS")).IsNull)
            return;

        PdfDictionary root = roots[0].TreeRoot;
        var structureNamespace = new PdfDictionary(2);
        structureNamespace.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Namespace")));
        structureNamespace.Set(PdfName.Get("NS"), PdfValue.TextString(PdfUaRules.Pdf20Namespace));
        PdfValue reference = PdfValue.Reference(document.AddObject(PdfValue.Dictionary(structureNamespace)));
        PdfArray namespaces = root.GetArray(PdfName.Get("Namespaces")) ?? new PdfArray();
        namespaces.Add(reference);
        root.Set(PdfName.Get("Namespaces"), PdfValue.Array(namespaces));
        roots[0].Dictionary.Set(PdfName.Get("NS"), reference);
        document.MarkChanged(roots[0].Id);
        PdfValue tree = document.Catalog.Get(PdfName.Get("StructTreeRoot"));
        if (tree.IsReference)
            document.MarkChanged(tree.AsReference);
        document.InvalidateStructure();
    }

    private static PdfDictionary Ensure(PdfDocument document, PdfDictionary parent, PdfName key)
    {
        if (document.Resolve(parent.Get(key)).AsDictionary is { } existing)
            return existing;

        var created = new PdfDictionary(4);
        parent.Set(key, PdfValue.Dictionary(created));
        return created;
    }
}
