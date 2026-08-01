using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    /// <summary>Clause 6.1.11 and 6.1.13 of part 1; clauses 6.8 to 6.11 of parts 2 and 3.</summary>
    internal static void CheckAttachments(PdfAContext context)
    {
        CheckEmbeddedFiles(context);
        CheckOptionalContent(context);
        CheckPresentations(context);
        CheckRequirements(context);
    }

    private static void CheckEmbeddedFiles(PdfAContext context)
    {
        PdfArray? names = EmbeddedFiles(context);

        // Annex A.2: PDF/A-4f is the level for documents built around an attachment, so a file that
        // claims it and carries none has claimed the wrong level rather than merely wasted one.
        if (names is null or { Count: 0 } && context.Conformance == PdfAConformance.A4F)
        {
            context.Report(
                "A.2",
                "The document claims PDF/A-4f but embeds no file. That level exists for documents " +
                "that carry one; a document without an attachment is plain PDF/A-4.");
        }

        if (names is null)
            return;

        if (context.Part == 1)
        {
            context.Report(
                "6.1.11",
                "The document carries embedded files. Part 1 forbids them outright; a document that " +
                "needs an attachment needs PDF/A-3.");

            return;
        }

        for (int i = 1; i < names.Count; i += 2)
        {
            if (names.GetDictionary(i) is { } specification)
                CheckFileSpecification(context, specification);
        }
    }

    /// <summary>
    /// Part 2 lets a document carry attachments only if they are themselves PDF/A. Whether a PDF is
    /// conformant cannot be judged from here, but a file that is not a PDF at all certainly is not —
    /// which is the usual mistake: an invoice XML attached to a document claiming part 2.
    /// </summary>
    private static void CheckFileSpecification(PdfAContext context, PdfDictionary specification)
    {
        string name = specification.GetTextString(PdfName.Get("UF")) ??
                      specification.GetTextString(PdfName.Get("F")) ?? "an attachment";

        if (!specification.ContainsKey(PdfName.Get("F")) || !specification.ContainsKey(PdfName.Get("UF")))
            context.Report("6.8", $"The file specification for '{name}' needs both the F and UF entries.");

        string? mime = specification.GetDictionary(PdfName.Get("EF"))?
            .GetStream(PdfName.Get("F"))?.Dictionary.GetName(PdfNames.Subtype)?.Value;

        // Part 4 asks of every attachment what part 3 asks: a relationship that says why it is
        // there, alongside both names. Which files are admitted then depends on the level.
        if (context.Part is 3 or 4)
            CheckAssociation(context, specification, name, mime);

        if (context.Part == 3 || context.Conformance.AllowsAnyEmbeddedFile)
            return;

        if (mime is not null && !mime.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            string advice = context.Part == 4
                ? "PDF/A-4 only carries files that are themselves PDF/A. Claim PDF/A-4f instead."
                : "PDF/A-2 only carries files that are themselves PDF/A. Claim PDF/A-3 instead.";

            context.Report(context.Part == 4 ? "6.9" : "6.8", $"The attachment '{name}' is {mime}, and {advice}");
        }
    }

    /// <summary>
    /// Part 3 exists so that a document can carry a file of any kind, and it pays for that by
    /// requiring each one to say what it is and how it relates to the document. PDF 2.0 Application
    /// Note 002 spells the same requirements out for associated files generally.
    /// </summary>
    private static void CheckAssociation(
        PdfAContext context, PdfDictionary specification, string name, string? mime)
    {
        if (string.IsNullOrEmpty(mime))
        {
            context.Report(
                "6.8",
                $"The embedded file '{name}' names no MIME type in the Subtype entry of its stream.");
        }

        string? relationship = specification.GetName(PdfNames.AFRelationship)?.Value;

        if (relationship is null)
        {
            context.Report("6.8", $"The file specification for '{name}' states no AFRelationship.");
            return;
        }

        // A value outside the defined set is allowed, but it is worth saying so, because the usual
        // cause is a misspelling of one that is defined.
        if (!DefinedRelationships.Contains(relationship))
        {
            context.Report(
                "6.8",
                $"The attachment '{name}' declares the relationship {relationship}, which is not one " +
                "the standard defines; a reader will treat it as unspecified.");
        }
    }

    /// <summary>The relationships PDF 2.0 defines, listed in Application Note 002.</summary>
    private static readonly HashSet<string> DefinedRelationships = new(StringComparer.Ordinal)
    {
        "Source", "Data", "Alternative", "Supplement", "EncryptedPayload", "FormData", "Schema", "Unspecified",
    };

    /// <summary>
    /// Clause 6.1.13 of part 1 forbids optional content outright. Parts 2 and 3 allow it as a way of
    /// keeping several variants in one file, but insist that each variant be named and that nothing
    /// switch itself on and off behind the reader's back.
    /// </summary>
    private static void CheckOptionalContent(PdfAContext context)
    {
        if (context.Document.Catalog.GetDictionary(PdfName.Get("OCProperties")) is not { } properties)
            return;

        if (context.Part == 1)
        {
            context.Report("6.1.13", "The catalogue carries OCProperties; part 1 does not permit optional content.");
            return;
        }

        List<PdfDictionary> configurations = [];
        if (properties.GetDictionary(PdfName.Get("D")) is { } standard)
            configurations.Add(standard);

        if (properties.GetArray(PdfName.Get("Configs")) is { } configs)
        {
            for (int i = 0; i < configs.Count; i++)
            {
                if (configs.GetDictionary(i) is { } configuration)
                    configurations.Add(configuration);
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        int groups = properties.GetArray(PdfName.Get("OCGs"))?.Count ?? 0;

        foreach (PdfDictionary configuration in configurations)
            CheckConfiguration(context, configuration, names, groups);
    }

    private static void CheckConfiguration(
        PdfAContext context, PdfDictionary configuration, HashSet<string> names, int groups)
    {
        // Parts 2 and 3 state optional content in 6.9; part 4 moved the same rules to 6.10.
        string clause = context.Part == 4 ? "6.10" : "6.9";

        if (configuration.GetTextString(PdfName.Get("Name")) is not { Length: > 0 } name)
        {
            context.Report(clause, "An optional content configuration has no Name, so its variant cannot be identified.");
        }
        else if (!names.Add(name))
        {
            context.Report(clause, $"Two optional content configurations are both named '{name}'.");
        }

        if (configuration.ContainsKey(PdfName.Get("AS")))
            context.Report(clause, "An optional content configuration carries AS, which lets content switch itself on and off.");

        if (configuration.GetArray(PdfName.Get("Order")) is { } order && groups > 0 && Count(order) < groups)
            context.Report(clause, "The Order array does not list every optional content group in the file.");
    }

    private static int Count(PdfArray order)
    {
        int total = 0;

        for (int i = 0; i < order.Count; i++)
            total += order.GetArray(i) is { } nested ? Count(nested) : 1;

        return total;
    }

    /// <summary>Clause 6.10: a file that presents itself differently on screen and on paper.</summary>
    private static void CheckPresentations(PdfAContext context)
    {
        if (context.Part == 1)
            return;

        if (context.Document.Catalog.GetDictionary(PdfName.Get("Names"))?
                .ContainsKey(PdfName.Get("AlternatePresentations")) == true)
        {
            context.Report("6.10", "The name dictionary carries AlternatePresentations, which PDF/A forbids.");
        }

        for (int i = 0; i < context.Document.Pages.Count; i++)
        {
            if (context.Document.Pages[i].Dictionary.ContainsKey(PdfName.Get("PresSteps")))
                context.Report("6.10", $"Page {i + 1} carries PresSteps, which turns the page into a slide show.");
        }
    }

    /// <summary>Clause 6.11: a conforming reader meets every requirement, so none may be demanded.</summary>
    private static void CheckRequirements(PdfAContext context)
    {
        if (context.Part > 1 && context.Document.Catalog.ContainsKey(PdfName.Get("Requirements")))
            context.Report("6.11", "The catalogue carries Requirements, which asks the reader for features PDF/A does not know.");
    }

    private static PdfArray? EmbeddedFiles(PdfAContext context) =>
        context.Document.Catalog.GetDictionary(PdfName.Get("Names"))?
            .GetDictionary(PdfName.Get("EmbeddedFiles"))?
            .GetArray(PdfName.Get("Names"));
}
