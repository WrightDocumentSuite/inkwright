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
        Walk(document, root, [], pages, visited, 0, diagnostics);

        if (pages.Count == 0 && document.Reader is not null)
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

    private static void Walk(
        PdfDocument document,
        PdfValue node,
        PdfDictionary? inherited,
        List<PdfPage> pages,
        HashSet<int> visited,
        int depth,
        LoadDiagnostics diagnostics)
    {
        if (depth > 64 || pages.Count >= MaxPages)
            return;

        PdfObjectId id = node.IsReference ? node.AsReference : PdfObjectId.None;
        if (id.IsValid && !visited.Add(id.Number))
        {
            diagnostics.Add(PdfWarningCode.PageTreeCycle, "The page tree revisits a node.", id);
            return;
        }

        if (document.Resolve(node).AsDictionary is not { } dictionary)
            return;

        PdfName? type = dictionary.GetName(PdfNames.Type);
        PdfArray? kids = dictionary.GetArray(PdfNames.Kids);

        if (type == PdfNames.Page || (kids is null && type != PdfNames.Pages))
        {
            ApplyInherited(dictionary, inherited);

            if (!id.IsValid)
                id = document.AddObject(PdfValue.Dictionary(dictionary));

            pages.Add(new PdfPage(document, id, dictionary));
            return;
        }

        if (kids is null)
            return;

        PdfDictionary? merged = Merge(inherited, dictionary);
        for (int i = 0; i < kids.Count; i++)
            Walk(document, kids[i], merged, pages, visited, depth + 1, diagnostics);
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
