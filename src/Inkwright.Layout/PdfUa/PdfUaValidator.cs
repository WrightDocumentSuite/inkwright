namespace Inkwright.Layout;

/// <summary>
/// Runs every accessibility rule the library knows over a document. The groups follow the clauses
/// of ISO 14289 rather than the structure of the code, so a reader with the text open can find the
/// check that matches the paragraph in front of them.
/// </summary>
internal static class PdfUaValidator
{
    internal static IReadOnlyList<PdfUaProblem> Validate(PdfDocument document, PdfUaConformance conformance)
    {
        var context = new PdfUaContext(document, conformance);

        PdfUaRules.CheckIdentification(context);
        PdfUaRules.CheckSecurity(context);

        // While a document is still encrypted its strings are ciphertext, and a rule that reads a
        // title or a description over ciphertext reports nonsense.
        if (document.WasEncrypted)
            return context.Problems;

        PdfUaRules.CheckTagging(context);
        PdfUaRules.CheckRoleMap(context);
        PdfUaRules.CheckMetadata(context);
        PdfUaRules.CheckLanguage(context);

        PdfUaRules.CheckHeadings(context);
        PdfUaRules.CheckFigures(context);
        PdfUaRules.CheckFormulae(context);
        PdfUaRules.CheckTables(context);
        PdfUaRules.CheckLists(context);
        PdfUaRules.CheckNotes(context);

        PdfUaRules.CheckAnnotations(context);
        PdfUaRules.CheckForms(context);
        PdfUaRules.CheckEmbeddedFiles(context);
        PdfUaRules.CheckXObjects(context);
        PdfUaRules.CheckOptionalContent(context);
        PdfUaRules.CheckNavigation(context);

        if (conformance.Part == 2)
        {
            PdfUaRules.CheckNamespaces(context);
            PdfUaRules.CheckTextStrings(context);

            // Structure destinations arrived with PDF 2.0, so only part 2 can require them.
            PdfUaRules.CheckDestinations(context);
        }

        return context.Problems;
    }
}
