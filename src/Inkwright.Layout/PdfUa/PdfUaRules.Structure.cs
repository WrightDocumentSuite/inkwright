using Inkwright.Cos;
using Inkwright.Tagging;

namespace Inkwright.Layout;

/// <summary>
/// The rules ISO 14289 states about the shape of the structure tree: headings, tables, lists,
/// figures, formulae and notes.
/// </summary>
internal static partial class PdfUaRules
{
    /// <summary>
    /// The structure types ISO 32000 defines. A role map may lead to one of these but must not
    /// redefine one, and a tag outside this set has to be mapped.
    /// </summary>
    private static readonly HashSet<string> StandardTypes = new(StringComparer.Ordinal)
    {
        "Document", "DocumentFragment", "Part", "Div", "Aside", "Sect", "Art", "BlockQuote", "Caption",
        "NonStruct", "TOC", "TOCI", "Index", "Private", "Title", "Sub", "P", "H", "Note", "Reference",
        "BibEntry", "Code", "Span", "Link", "Annot", "Form", "Ruby", "RB", "RT", "RP", "Warichu", "WT",
        "WP", "L", "LI", "Lbl", "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot", "Figure",
        "Formula", "Artifact", "Em", "Strong", "Quote", "FENote", "H1", "H2", "H3", "H4", "H5", "H6",
    };

    /// <summary>
    /// Checks the heading sequence (part 1 clause 7.4). A numbered heading structure has to start
    /// at the first level and descend one step at a time, because that sequence is how a screen
    /// reader builds the outline a user navigates by.
    /// </summary>
    internal static void CheckHeadings(PdfUaContext context)
    {
        string clause = context.Clause("7.4.2", "8.2.5.2");

        List<(PdfStructureNode Node, int Level)> numbered = [];
        bool unnumbered = false;

        foreach (PdfStructureNode node in context.AllNodes())
        {
            if (node.Tag == "H")
            {
                unnumbered = true;
                continue;
            }

            if (TryReadHeadingLevel(node.Tag, out int level))
                numbered.Add((node, level));
        }

        if (numbered.Count == 0 && !unnumbered)
            return;

        // A document is either strongly structured, using the generic heading, or weakly
        // structured, using numbered ones. Doing both leaves the outline ambiguous.
        if (unnumbered && numbered.Count > 0)
        {
            context.Violation(
                context.Clause("7.4.4", "8.2.5.2"),
                "The document mixes the generic heading with numbered headings.");
        }

        if (numbered.Count == 0)
        {
            CheckSingleGenericHeadingPerNode(context);
            return;
        }

        if (numbered[0].Level != 1)
            context.Violation(clause, $"The first heading is H{numbered[0].Level} rather than H1.");

        for (int i = 1; i < numbered.Count; i++)
        {
            int previous = numbered[i - 1].Level;
            int current = numbered[i].Level;

            // Going back up any number of levels is fine; going down may only be by one.
            if (current > previous + 1)
            {
                context.Violation(
                    clause,
                    $"The heading sequence skips from H{previous} to H{current}.");
            }
        }

        CheckSingleGenericHeadingPerNode(context);
    }

    private static void CheckSingleGenericHeadingPerNode(PdfUaContext context)
    {
        string clause = context.Clause("7.4.4", "8.2.5.2");

        foreach (PdfStructureNode node in context.AllNodes())
        {
            int headings = node.Children.Count(child => child.Tag == "H");

            if (headings > 1)
            {
                context.Violation(
                    clause,
                    $"A {node.Tag} element has {headings} generic heading children, where at most one is allowed.");
            }
        }
    }

    /// <summary>
    /// Reads the level out of a numbered heading tag. The standard allows levels past six, written
    /// as the letter followed by digits with nothing between them and no other numbering system.
    /// </summary>
    private static bool TryReadHeadingLevel(string tag, out int level)
    {
        level = 0;

        if (tag.Length < 2 || tag[0] != 'H')
            return false;

        foreach (char digit in tag.AsSpan(1))
        {
            if (!char.IsAsciiDigit(digit))
                return false;
        }

        return int.TryParse(tag.AsSpan(1), out level) && level > 0;
    }

    /// <summary>
    /// Checks that figures carry something a reader who cannot see them can use (part 1 clause 7.3).
    /// </summary>
    internal static void CheckFigures(PdfUaContext context)
    {
        string clause = context.Clause("7.3", "8.5");

        foreach (PdfStructureNode node in context.AllNodes())
        {
            if (node.Tag != "Figure")
                continue;

            if (string.IsNullOrWhiteSpace(node.AlternateText) && string.IsNullOrWhiteSpace(node.ActualText))
                context.Violation(clause, "A figure has neither alternative text nor actual text.");
        }
    }

    /// <summary>
    /// Checks that formulae carry alternative text (part 1 clause 7.7). A formula's glyphs rarely
    /// spell what it means, so the text is the only thing that conveys it.
    /// </summary>
    internal static void CheckFormulae(PdfUaContext context)
    {
        string clause = context.Clause("7.7", "8.5");

        foreach (PdfStructureNode node in context.AllNodes())
        {
            if (node.Tag != "Formula")
                continue;

            if (string.IsNullOrWhiteSpace(node.AlternateText) && string.IsNullOrWhiteSpace(node.ActualText))
                context.Violation(clause, "A formula has no alternative text.");
        }
    }

    /// <summary>
    /// Checks tables (part 1 clause 7.5). Header cells are how a reader announces which column a
    /// value belongs to, and without either a scope or an explicit association it cannot.
    /// </summary>
    internal static void CheckTables(PdfUaContext context)
    {
        string clause = context.Clause("7.5", "8.2.5.9");

        foreach (PdfStructureNode table in context.AllNodes())
        {
            if (table.Tag != "Table")
                continue;

            List<PdfStructureNode> headers =
                [.. table.Descendants().Where(node => node.Tag == "TH")];

            if (headers.Count == 0)
            {
                context.Advice(clause, "A table has no header cells.");
                continue;
            }

            foreach (PdfStructureNode header in headers)
            {
                if (HasAttribute(context, header, "Scope") || HasAttribute(context, header, "Headers"))
                    continue;

                context.Violation(
                    clause,
                    "A table header cell states neither a scope nor the headers it belongs to.");
            }
        }
    }

    /// <summary>
    /// Checks lists (part 1 clause 7.6). An ordered list has to say how it is numbered, because the
    /// numbers themselves are usually drawn as ordinary text a reader cannot interpret.
    /// </summary>
    internal static void CheckLists(PdfUaContext context)
    {
        string clause = context.Clause("7.6", "8.2.5.8");

        foreach (PdfStructureNode list in context.AllNodes())
        {
            if (list.Tag != "L")
                continue;

            foreach (PdfStructureNode child in list.Children)
            {
                if (child.Tag is "LI" or "Caption" or "L")
                    continue;

                context.Violation(clause, $"A list has a {child.Tag} child where a list item was expected.");
            }

            // Whether the list is ordered is only visible from its items carrying labels.
            bool labelled = list.Children
                .Where(child => child.Tag == "LI")
                .Any(item => item.Children.Any(part => part.Tag == "Lbl"));

            if (labelled && !HasAttribute(context, list, "ListNumbering"))
                context.Violation(clause, "A list with labelled items does not state how it is numbered.");
        }
    }

    /// <summary>
    /// Checks notes (part 1 clause 7.9). A note is referred to from elsewhere in the document, so
    /// it needs an identifier, and one that is not shared with another note.
    /// </summary>
    internal static void CheckNotes(PdfUaContext context)
    {
        string clause = context.Clause("7.9", "8.2.5.11");
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (PdfStructureNode node in context.AllNodes())
        {
            if (node.Tag is not ("Note" or "FENote"))
                continue;

            string? id = node.Dictionary.GetTextString(PdfName.Get("ID"));

            if (string.IsNullOrEmpty(id))
            {
                context.Violation(clause, "A note has no identifier.");
                continue;
            }

            if (!seen.Add(id))
                context.Violation(clause, $"More than one note carries the identifier {id}.");
        }
    }

    /// <summary>
    /// Whether an element carries an attribute, whether it was written directly, inside an
    /// attribute dictionary, or inside one of an array of them.
    /// </summary>
    private static bool HasAttribute(PdfUaContext context, PdfStructureNode node, string name)
    {
        PdfName key = PdfName.Get(name);

        if (!node.Dictionary.Get(key).IsNull)
            return true;

        PdfValue attributes = context.Resolve(node.Dictionary.Get(PdfName.Get("A")));

        if (attributes.AsDictionary is { } single)
            return !single.Get(key).IsNull;

        if (attributes.AsArray is not { } list)
            return false;

        for (int i = 0; i < list.Count; i++)
        {
            if (context.Resolve(list[i]).AsDictionary is { } entry && !entry.Get(key).IsNull)
                return true;
        }

        return false;
    }
}
