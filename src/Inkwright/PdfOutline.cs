using System.Collections;
using Inkwright.Cos;

namespace Inkwright;

/// <summary>
/// The document outline — the tree of bookmarks a reader shows in its navigation pane (ISO 32000-1
/// §12.3.3). The façade reads and writes the outline dictionaries directly, so a bookmark added
/// here is a bookmark in the file, and the doubly linked list the specification asks for is kept
/// consistent as items are added and removed, however deep in the tree the change is.
/// </summary>
public sealed class PdfOutline : IReadOnlyList<PdfOutlineItem>
{
    private readonly PdfDocument _document;
    private readonly PdfDictionary _root;
    private readonly PdfObjectId _rootId;
    private readonly List<PdfOutlineItem> _items = [];

    private PdfOutline(PdfDocument document, PdfDictionary root, PdfObjectId rootId)
    {
        _document = document;
        _root = root;
        _rootId = rootId;
    }

    /// <summary>The top-level bookmarks, in reading order.</summary>
    public IReadOnlyList<PdfOutlineItem> Items => _items;

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public PdfOutlineItem this[int index] => _items[index];

    internal PdfDocument Document => _document;

    internal PdfObjectId RootId => _rootId;

    internal List<PdfOutlineItem> Roots => _items;

    /// <summary>Opens the outline of a document, or nothing when it has none.</summary>
    /// <param name="document">The document to read.</param>
    public static PdfOutline? Open(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Catalog.GetDictionary(PdfNames.Outlines) is not { } root)
            return null;

        PdfObjectId rootId = document.Catalog.TryGetRaw(PdfNames.Outlines, out PdfValue reference) && reference.IsReference
            ? reference.AsReference
            : PdfObjectId.None;

        var outline = new PdfOutline(document, root, rootId);
        outline.Collect();
        return outline;
    }

    /// <summary>Opens the outline of a document, creating an empty one when there is none.</summary>
    /// <param name="document">The document to read.</param>
    public static PdfOutline OpenOrCreate(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Open(document) is { } existing)
            return existing;

        var root = new PdfDictionary(3);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.Outlines));
        root.Set(PdfNames.Count, PdfValue.Integer(0));
        document.Attach(PdfValue.Dictionary(root));

        PdfObjectId id = document.AddObject(PdfValue.Dictionary(root));
        document.Catalog.Set(PdfNames.Outlines, PdfValue.Reference(id));
        document.MarkChanged(document.CatalogId);

        var outline = new PdfOutline(document, root, id);
        outline.Collect();
        return outline;
    }

    /// <summary>Adds a top-level bookmark.</summary>
    /// <param name="title">The text shown in the navigation pane.</param>
    /// <param name="destination">Where the bookmark leads, or nothing for a heading with no target.</param>
    public PdfOutlineItem Add(string title, PdfDestination? destination = null)
    {
        ArgumentNullException.ThrowIfNull(title);

        PdfOutlineItem item = PdfOutlineItem.Create(this, title, destination, _rootId);
        _items.Add(item);
        Reflow();
        return item;
    }

    /// <summary>Removes a top-level bookmark and everything beneath it.</summary>
    /// <param name="item">The bookmark to remove.</param>
    /// <returns>Whether the bookmark was a top-level one of this outline.</returns>
    public bool Remove(PdfOutlineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_items.Remove(item))
            return false;

        Reflow();
        return true;
    }

    /// <inheritdoc />
    public IEnumerator<PdfOutlineItem> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Rewrites the whole tree's links and counts from the root down. Doing it from the top every
    /// time is cheap for an outline and keeps a count correct no matter how deep the change was:
    /// a section added three levels down still bumps the totals of every ancestor above it.
    /// </summary>
    internal void Reflow()
    {
        int visible = 0;
        foreach (PdfOutlineItem item in _items)
            visible += 1 + item.ReflowSubtree();

        PdfOutlineItem.Chain(_document, _root, _rootId, _items);
        _root.Set(PdfNames.Count, PdfValue.Integer(visible));
        _document.MarkChanged(_rootId);
    }

    private void Collect()
    {
        HashSet<int> visited = [];
        PdfValue reference = _root[PdfNames.First];

        for (int guard = 0; reference.IsReference && guard < 100_000; guard++)
        {
            PdfObjectId id = reference.AsReference;
            if (!visited.Add(id.Number) || _document.GetObject(id).AsDictionary is not { } dictionary)
                break;

            var item = new PdfOutlineItem(this, id, dictionary);
            item.CollectChildren(visited, 0);
            _items.Add(item);
            reference = dictionary[PdfNames.Next];
        }
    }
}
