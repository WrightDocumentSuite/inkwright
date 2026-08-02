using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    /// <summary>The structure types ISO 32000-1 defines in 14.8.4; anything else needs a role map.</summary>
    private static readonly string[] _standardStructureTypes =
    [
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index",
        "NonStruct", "Private", "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl",
        "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot", "Span", "Quote", "Note",
        "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu",
        "WT", "WP", "Figure", "Formula", "Form",
    ];

    /// <summary>Clause 6.8 of part 1 and 6.7 of parts 2 and 3: the logical structure of level A.</summary>
    internal static void CheckLogicalStructure(PdfAContext context)
    {
        // Part 4 clause 6.8 is explanatory and contains no conformance requirement. Accessibility
        // requirements belong to PDF/UA; the normative level-A rules only exist in parts 1 to 3.
        if (context.Part == 4)
            return;

        CheckLanguage(context);

        if (!context.Conformance.RequiresStructure)
            return;

        CheckTagged(context);
    }

    /// <summary>
    /// Level A is the claim that the file says what its pages mean, not merely what they look like.
    /// That claim rests on two entries: a structure tree, and a statement that the content is marked
    /// up against it.
    /// </summary>
    private static void CheckTagged(PdfAContext context)
    {
        string clause = context.Clause("6.8.2.2", "6.7.2.2");
        PdfDictionary catalog = context.Document.Catalog;

        if (catalog.GetDictionary(PdfName.Get("MarkInfo"))?.GetBoolean(PdfName.Get("Marked")) != true)
            context.Report(clause, "MarkInfo is missing or its Marked flag is not true, so the file is not tagged.");

        if (catalog.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root)
        {
            context.Report(
                context.Clause("6.8.3.3", "6.7.3.3"),
                "The catalogue has no StructTreeRoot, so nothing describes what the pages mean.");

            return;
        }

        CheckRoleMap(context, root);
    }

    /// <summary>
    /// Clause 6.8.3.4 of part 1, 6.7.3.4 of parts 2 and 3: a structure type of one's own is allowed,
    /// but the role map has to lead it to a standard one, however many hops it takes.
    /// </summary>
    private static void CheckRoleMap(PdfAContext context, PdfDictionary root)
    {
        string clause = context.Clause("6.8.3.4", "6.7.3.4");
        PdfDictionary roles = root.GetDictionary(PdfName.Get("RoleMap")) ?? new PdfDictionary(0);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (PdfDictionary element in context.Dictionaries)
        {
            if (element.GetName(PdfNames.Type)?.Value != "StructElem")
                continue;

            if (element.GetName(PdfName.Get("S"))?.Value is not { } type)
                continue;

            if (Resolves(type, roles, 0) || !reported.Add(type))
                continue;

            context.Report(clause, $"The structure type {type} is not standard and the role map does not lead it to one.");
        }
    }

    private static bool Resolves(string type, PdfDictionary roles, int depth)
    {
        if (_standardStructureTypes.Contains(type, StringComparer.Ordinal))
            return true;

        if (depth > 16 || roles.GetName(PdfName.Get(type)) is not { } mapped)
            return false;

        return Resolves(mapped.Value, roles, depth + 1);
    }

    /// <summary>
    /// Clause 6.8.4 of part 1, 6.7.4 of parts 2 and 3. Naming the language is a recommendation, but
    /// naming it wrongly is not: a value that is not a language identifier says nothing.
    /// </summary>
    private static void CheckLanguage(PdfAContext context)
    {
        string clause = context.Clause("6.8.4", "6.7.4");

        if (context.Document.Catalog.GetTextString(PdfName.Get("Lang")) is { } language && !IsLanguageTag(language))
            context.Report(clause, $"The catalogue names the language '{language}', which is not a language identifier.");

        foreach (PdfDictionary element in context.Dictionaries)
        {
            if (element.GetName(PdfNames.Type)?.Value != "StructElem")
                continue;

            if (element.GetTextString(PdfName.Get("Lang")) is { } tag && !IsLanguageTag(tag))
                context.Report(clause, $"A structure element names the language '{tag}', which is not a language identifier.");
        }
    }

    /// <summary>The shape of a language tag: letters, then optional subtags of letters or digits.</summary>
    private static bool IsLanguageTag(string value)
    {
        if (value.Length == 0)
            return true;

        string[] parts = value.Split('-');
        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsAsciiLetter))
            return false;

        foreach (string part in parts.Skip(1))
        {
            if (part.Length is 0 or > 8 || !part.All(char.IsAsciiLetterOrDigit))
                return false;
        }

        return true;
    }
}
