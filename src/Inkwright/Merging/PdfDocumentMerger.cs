using Inkwright.Cos;

namespace Inkwright.Merging;

/// <summary>
/// Carries across everything that belongs to a document rather than to any one page. Copying pages
/// is the easy half of a merge: the bookmarks that point at them, the names other files link them
/// by, the form fields they carry widgets for and the structure tree that makes them readable all
/// live in the catalog, and a merge that forgets them produces a file that looks right and behaves
/// like a stack of pictures.
/// </summary>
/// <remarks>
/// The list of what has to move follows <c>src/core/editor/pdf_editor.js</c> of pdf.js, which
/// Mozilla licenses under Apache 2.0.
/// </remarks>
internal sealed class PdfDocumentMerger(PdfDocument target)
{
    private readonly PdfDocument _target = target;

    /// <summary>
    /// Moves the document-level structures of one source across, given the pages that were imported
    /// from it and where they landed.
    /// </summary>
    /// <param name="source">The document being merged in.</param>
    /// <param name="importer">The importer that copied its pages, holding the object map.</param>
    /// <param name="pages">The pages that were imported, paired with the source pages they came from.</param>
    internal void Merge(PdfDocument source, PdfObjectImporter importer, IReadOnlyList<(PdfPage From, PdfPage To)> pages)
    {
        if (pages.Count == 0)
            return;

        var context = new MergeContext(source, _target, importer, pages);

        // The outline is copied after the destinations, so that a bookmark naming one that had to
        // be renamed is written pointing straight at its page instead.
        new DestinationImporter(context).Merge();
        new OutlineImporter(context).Merge();
        new FormImporter(context).Merge();
        new StructTreeImporter(context).Merge();

        MergePageLabels(context);
        MergeEmbeddedFiles(context);
        MergeOptionalContent(context);
    }

    /// <summary>
    /// Shifts the source's labelling ranges to where its pages ended up. A document with no labels
    /// of its own still needs one range written for it, or the previous document's roman numerals
    /// would run on into pages that never asked for them.
    /// </summary>
    private void MergePageLabels(MergeContext context)
    {
        IReadOnlyList<PageLabelRange> incoming = context.Source.PageLabels.Ranges;
        if (incoming.Count == 0 && !_target.PageLabels.IsDefined)
            return;

        int offset = _target.Pages.IndexOf(context.Pages[0].To);
        if (offset < 0)
            return;

        List<PageLabelRange> ranges = [.. _target.PageLabels.Ranges.Where(range => range.StartIndex < offset)];

        if (incoming.Count == 0)
            ranges.Add(new PageLabelRange(offset, PageLabelStyle.Decimal));
        else
        {
            foreach (PageLabelRange range in incoming)
            {
                if (range.StartIndex < context.Pages.Count)
                    ranges.Add(range with { StartIndex = range.StartIndex + offset });
            }
        }

        _target.PageLabels.Set(ranges);
    }

    /// <summary>Brings the attached files across, renaming any whose name is already taken.</summary>
    private void MergeEmbeddedFiles(MergeContext context)
    {
        PdfDictionary? tree = context.Source.Catalog
            .GetDictionary(PdfNames.Names)?
            .GetDictionary(PdfNames.EmbeddedFiles);

        foreach ((string name, PdfValue spec) in PdfNameTree.Enumerate(tree))
        {
            string unique = context.UniqueName(name, _target.Attachments.Names);
            PdfValue copied = context.Importer.Import(spec);

            if (copied.AsDictionary is { } dictionary && unique != name)
            {
                dictionary.Set(PdfNames.F, PdfValue.TextString(unique));
                dictionary.Set(PdfNames.UF, PdfValue.TextString(unique));
            }

            _target.Attachments.Relist(unique, copied);
        }
    }

    /// <summary>
    /// Brings the layers across. Without this the groups an imported page names are declared
    /// nowhere, and a viewer shows content the source document was deliberately hiding.
    /// </summary>
    private void MergeOptionalContent(MergeContext context)
    {
        if (context.Source.Catalog.GetDictionary(PdfNames.OCProperties) is not { } incoming)
            return;

        PdfDictionary properties = Existing() ?? Create();
        PdfArray groups = properties.GetArray(PdfNames.OCGs) ?? Attach(properties, PdfNames.OCGs);
        PdfDictionary configuration = properties.GetDictionary(PdfNames.D) ?? CreateConfiguration(properties);
        PdfArray off = configuration.GetArray(PdfNames.OFF) ?? Attach(configuration, PdfNames.OFF);

        foreach (PdfValue group in Raw(incoming.GetArray(PdfNames.OCGs)))
            groups.Add(context.Importer.Import(group));

        foreach (PdfValue group in Raw(incoming.GetDictionary(PdfNames.D)?.GetArray(PdfNames.OFF)))
            off.Add(context.Importer.Import(group));

        if (off.Count == 0)
            configuration.Remove(PdfNames.OFF);

        _target.MarkChanged(_target.CatalogId);

        PdfDictionary? Existing() => _target.Catalog.GetDictionary(PdfNames.OCProperties);

        PdfDictionary Create()
        {
            var created = new PdfDictionary(2);
            _target.Attach(PdfValue.Dictionary(created));
            _target.Catalog.Set(PdfNames.OCProperties, PdfValue.Dictionary(created));
            return created;
        }

        PdfDictionary CreateConfiguration(PdfDictionary parent)
        {
            var created = new PdfDictionary(2);
            _target.Attach(PdfValue.Dictionary(created));
            parent.Set(PdfNames.D, PdfValue.Dictionary(created));
            return created;
        }

        PdfArray Attach(PdfDictionary parent, PdfName key)
        {
            var array = new PdfArray();
            _target.Attach(PdfValue.Array(array));
            parent.Set(key, PdfValue.Array(array));
            return array;
        }
    }

    /// <summary>Walks an array without resolving, so that references stay references.</summary>
    private static IEnumerable<PdfValue> Raw(PdfArray? array)
    {
        if (array is null)
            yield break;

        for (int i = 0; i < array.Count; i++)
            yield return array[i];
    }
}
