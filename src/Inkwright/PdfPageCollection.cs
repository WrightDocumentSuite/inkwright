using System.Collections;
using Inkwright.Cos;

namespace Inkwright;

/// <summary>
/// The pages of a document in display order. The collection is the single source of truth: whatever
/// shape the source file's page tree had, saving writes the pages in this order.
/// </summary>
public sealed class PdfPageCollection : IReadOnlyList<PdfPage>
{
    private readonly PdfDocument _document;
    private readonly List<PdfPage> _pages;

    internal PdfPageCollection(PdfDocument document, List<PdfPage> pages)
    {
        _document = document;
        _pages = pages;
    }

    /// <inheritdoc />
    public int Count => _pages.Count;

    /// <inheritdoc />
    public PdfPage this[int index] => _pages[index];

    /// <summary>Appends a blank page.</summary>
    /// <param name="size">The page size; A4 when omitted.</param>
    public PdfPage Add(PageSize? size = null) => Insert(_pages.Count, size);

    /// <summary>Inserts a blank page.</summary>
    /// <param name="index">Where to insert it.</param>
    /// <param name="size">The page size; A4 when omitted.</param>
    public PdfPage Insert(int index, PageSize? size = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _pages.Count);

        PageSize actual = size ?? PageSize.A4;
        var dictionary = new PdfDictionary { Resolver = _document };
        dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.Page));
        dictionary.Set(PdfNames.MediaBox, PdfValue.Array(actual.ToRectangle().ToArray()));
        dictionary.Set(PdfNames.Resources, PdfValue.Dictionary(new PdfDictionary { Resolver = _document }));

        PdfObjectId id = _document.AddObject(PdfValue.Dictionary(dictionary));
        var page = new PdfPage(_document, id, dictionary);
        _pages.Insert(index, page);
        return page;
    }

    /// <summary>Removes a page.</summary>
    /// <param name="index">The page to remove.</param>
    public void RemoveAt(int index) => _pages.RemoveAt(index);

    /// <summary>Removes a page.</summary>
    /// <param name="page">The page to remove.</param>
    /// <returns>Whether the page belonged to this document.</returns>
    public bool Remove(PdfPage page) => _pages.Remove(page);

    /// <summary>Moves a page to a different position.</summary>
    /// <param name="from">The page's current index.</param>
    /// <param name="to">The index it should end up at.</param>
    public void Move(int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(from, _pages.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(to);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(to, _pages.Count);

        PdfPage page = _pages[from];
        _pages.RemoveAt(from);
        _pages.Insert(to, page);
    }

    /// <summary>Reverses the page order.</summary>
    public void Reverse() => _pages.Reverse();

    /// <summary>The position of a page, or -1 when it is not in this document.</summary>
    /// <param name="page">The page to look for.</param>
    public int IndexOf(PdfPage page) => _pages.IndexOf(page);

    /// <summary>
    /// Copies a page from another document, bringing its resources, annotations and everything they
    /// reach along with it. Objects shared by several imported pages are copied once.
    /// </summary>
    /// <param name="page">The page to copy.</param>
    /// <param name="index">Where to insert the copy; appended when omitted.</param>
    public PdfPage Import(PdfPage page, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var importer = new PdfObjectImporter(page.Document, _document);
        PdfPage imported = importer.ImportPage(page);
        _pages.Insert(index ?? _pages.Count, imported);
        return imported;
    }

    /// <summary>Copies every page of another document, appending them in order.</summary>
    /// <param name="source">The document to copy from.</param>
    public IReadOnlyList<PdfPage> ImportAll(PdfDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ImportMany(source, source.Pages);
    }

    /// <summary>
    /// Copies several pages of another document, appending them in order and bringing across
    /// everything that belongs to the document rather than to any one page: the bookmarks that lead
    /// to those pages, the names they are linked by, their form fields, structure and labels.
    /// </summary>
    /// <param name="source">The document to copy from.</param>
    /// <param name="pages">The pages to copy; they must belong to <paramref name="source"/>.</param>
    public IReadOnlyList<PdfPage> ImportMany(PdfDocument source, IEnumerable<PdfPage> pages)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pages);

        List<PdfPage> wanted = [.. pages];
        var importer = new PdfObjectImporter(source, _document);

        // Every page is spoken for before the first one is copied, so links between them survive.
        importer.Reserve(wanted);

        List<(PdfPage From, PdfPage To)> moved = new(wanted.Count);
        List<PdfPage> imported = new(wanted.Count);

        foreach (PdfPage page in wanted)
        {
            PdfPage copy = importer.ImportPage(page);
            _pages.Add(copy);
            imported.Add(copy);
            moved.Add((page, copy));
        }

        new Merging.PdfDocumentMerger(_document).Merge(source, importer, moved);
        return imported;
    }

    /// <inheritdoc />
    public IEnumerator<PdfPage> GetEnumerator() => _pages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal IReadOnlyList<PdfPage> Items => _pages;

    /// <summary>Replaces the model-only page order while building an isolated document snapshot.</summary>
    internal void ReplaceSnapshot(List<PdfPage> pages)
    {
        _pages.Clear();
        _pages.AddRange(pages);
    }
}
