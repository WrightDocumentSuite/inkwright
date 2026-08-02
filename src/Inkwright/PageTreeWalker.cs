using Inkwright.Cos;
using Inkwright.Diagnostics;

namespace Inkwright;

/// <summary>
/// Flattens the page tree of §7.7.3 into the ordered list the model works with, pushing inherited
/// attributes down onto each page so that a page dictionary is self-contained from then on.
/// </summary>
internal static class PageTreeWalker
{
    private static readonly PdfName[] Inheritable =
        [PdfNames.Resources, PdfNames.MediaBox, PdfNames.CropBox, PdfNames.Rotate];

    private const int MaxPages = 1_000_000;

    internal static List<PdfPage> Collect(PdfDocument document, PdfDictionary catalog, LoadDiagnostics diagnostics)
    {
        List<PdfPage> pages = [];
        HashSet<int> visited = [];

        PdfValue root = catalog.TryGetRaw(PdfNames.Pages, out PdfValue value) ? value : PdfValue.Null;
        _ = Walk(document, root, [], pages, visited, 0, diagnostics, PdfObjectId.None);

        if (pages.Count == 0 && document.Reader?.Recover == true)
        {
            List<PdfPage> scanned = ScanForPages(document);
            if (scanned.Count > 0)
            {
                diagnostics.Add(PdfWarningCode.PageTreeRecovered,
                    $"The page tree yielded no pages; {scanned.Count} were found by scanning.");
                return scanned;
            }
        }

        return pages;
    }

    private static int Walk(
        PdfDocument document,
        PdfValue node,
        PdfDictionary? inherited,
        List<PdfPage> pages,
        HashSet<int> visited,
        int depth,
        LoadDiagnostics diagnostics,
        PdfObjectId expectedParent)
    {
        bool strict = document.Reader?.Recover == false;
        if (depth > 64)
        {
            if (strict)
                throw new PdfFormatException("The page tree exceeds the protected depth limit.");

            return 0;
        }

        if (pages.Count >= MaxPages)
        {
            if (strict)
                throw new PdfFormatException($"The page tree exceeds the protected limit of {MaxPages} pages.");

            return 0;
        }

        if (depth > 0 && !node.IsReference && strict)
            throw new PdfFormatException("A page tree Kids entry must be an indirect reference.");

        PdfObjectId id = node.IsReference ? node.AsReference : PdfObjectId.None;
        if (id.IsValid && !visited.Add(id.Number))
        {
            if (strict)
                throw new PdfFormatException("The page tree contains a cycle or repeated node.");

            diagnostics.Add(PdfWarningCode.PageTreeCycle, "The page tree revisits a node.", id);
            return 0;
        }

        if (document.Resolve(node).AsDictionary is not { } dictionary)
        {
            if (strict)
                throw new PdfFormatException("A page tree entry does not resolve to a dictionary.");

            return 0;
        }

        PdfName? type = dictionary.GetName(PdfNames.Type);
        PdfArray? kids = dictionary.GetArray(PdfNames.Kids);

        if (strict)
        {
            if (type != PdfNames.Page && type != PdfNames.Pages)
                throw new PdfFormatException("Every page tree node must have Type Page or Pages.");

            if (depth == 0)
            {
                if (type != PdfNames.Pages)
                    throw new PdfFormatException("The page tree root must have Type Pages.");

                if (dictionary.ContainsKey(PdfNames.Parent))
                    throw new PdfFormatException("The page tree root must not have a Parent entry.");
            }
            else if (!dictionary.TryGetRaw(PdfNames.Parent, out PdfValue parent) ||
                     !parent.IsReference || parent.AsReference != expectedParent)
            {
                throw new PdfFormatException(
                    "A page tree node's Parent entry must reference its actual parent.");
            }
        }

        if (type == PdfNames.Page || (!strict && kids is null && type != PdfNames.Pages))
        {
            ApplyInherited(dictionary, inherited);

            if (!id.IsValid)
                id = document.AddObject(PdfValue.Dictionary(dictionary));

            pages.Add(new PdfPage(document, id, dictionary));
            return 1;
        }

        if (kids is null)
        {
            if (strict)
                throw new PdfFormatException("A Pages node has no usable Kids array.");

            return 0;
        }

        int declaredCount = 0;
        if (strict)
        {
            PdfValue count = dictionary.Get(PdfNames.Count);
            if (count.Kind != PdfValueKind.Integer || count.AsInteger is < 0 or > MaxPages)
                throw new PdfFormatException("A Pages node has no valid Count entry.");

            declaredCount = (int)count.AsInteger;
        }

        PdfDictionary? merged = Merge(inherited, dictionary);
        int descendants = 0;
        for (int i = 0; i < kids.Count; i++)
            descendants += Walk(document, kids[i], merged, pages, visited, depth + 1, diagnostics, id);

        if (strict && descendants != declaredCount)
        {
            throw new PdfFormatException(
                $"A Pages node declares Count {declaredCount}, but has {descendants} descendant pages.");
        }

        return descendants;
    }

    private static PdfDictionary? Merge(PdfDictionary? inherited, PdfDictionary node)
    {
        PdfDictionary? merged = null;

        foreach (PdfName key in Inheritable)
        {
            if (!node.TryGetRaw(key, out PdfValue value))
                continue;

            merged ??= inherited?.Clone() ?? new PdfDictionary(Inheritable.Length);
            merged.Set(key, value);
        }

        return merged ?? inherited;
    }

    private static void ApplyInherited(PdfDictionary page, PdfDictionary? inherited)
    {
        if (inherited is null)
            return;

        foreach (PdfName key in Inheritable)
        {
            if (!page.ContainsKey(key) && inherited.TryGetRaw(key, out PdfValue value))
                page.Set(key, value);
        }
    }

    private static List<PdfPage> ScanForPages(PdfDocument document)
    {
        List<PdfPage> pages = [];
        IO.PdfReader reader = document.Reader!;

        foreach (int number in reader.LiveObjectNumbers.Order())
        {
            if (reader.GetObject(number).AsDictionary is not { } dictionary)
                continue;

            if (dictionary.GetName(PdfNames.Type) != PdfNames.Page)
                continue;

            pages.Add(new PdfPage(document, new PdfObjectId(number, reader.GetGeneration(number)), dictionary));
        }

        return pages;
    }
}
