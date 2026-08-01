using Inkwright.Cos;
using Inkwright.IO;

namespace Inkwright;

/// <summary>
/// One bookmark in the document outline (ISO 32000-1 §12.3.3). It reads and writes its dictionary
/// directly; the title, colour, style and open/closed state are all live, and children can be added
/// to any depth, each change rewriting the sibling chain and re-counting the tree the specification
/// asks for.
/// </summary>
public sealed class PdfOutlineItem
{
    private readonly PdfOutline _outline;
    private readonly List<PdfOutlineItem> _children = [];

    internal PdfOutlineItem(PdfOutline outline, PdfObjectId id, PdfDictionary dictionary)
    {
        _outline = outline;
        Id = id;
        Dictionary = dictionary;
    }

    /// <summary>The identity of the bookmark object.</summary>
    public PdfObjectId Id { get; }

    /// <summary>The bookmark dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The bookmarks nested under this one, in reading order.</summary>
    public IReadOnlyList<PdfOutlineItem> Children => _children;

    /// <summary>The text shown in the navigation pane.</summary>
    public string Title
    {
        get => Dictionary.GetTextString(PdfNames.Title) ?? string.Empty;
        set
        {
            Dictionary.Set(PdfNames.Title, PdfValue.TextString(value));
            _outline.Document.MarkChanged(Id);
        }
    }

    /// <summary>
    /// Whether the bookmark shows its children expanded. It is written through the sign of the
    /// item's count (§12.3.3): non-negative is open, negative is closed. Setting it re-counts the
    /// tree so the ancestors' totals stay right.
    /// </summary>
    public bool IsOpen
    {
        get => Dictionary.GetInteger(PdfNames.Count) >= 0;
        set
        {
            _isOpen = value;
            _outline.Reflow();
        }
    }

    private bool _isOpen = true;

    /// <summary>Points the bookmark at a destination, replacing whatever it led to before.</summary>
    /// <param name="destination">The place to go to.</param>
    public void SetDestination(PdfDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // A destination and a go-to action are two ways to say the same thing; writing the array
        // drops any action, so the two never disagree.
        Dictionary.Remove(PdfNames.A);
        Dictionary.Remove(PdfName.Get("SD")); // clean up the non-standard form written by older Inkwright builds

        PdfArray array = destination.ToArray();
        _outline.Document.Attach(PdfValue.Array(array));
        Dictionary.Set(PdfNames.Dest, PdfValue.Array(array));
        if (destination.IsStructureDestination)
            _outline.Document.Version = PdfVersion.Max(_outline.Document.Version, PdfVersion.Pdf20);
        _outline.Document.MarkChanged(Id);
    }

    /// <summary>
    /// Points the bookmark at a structure element rather than a page (§12.3.2.3, PDF 2.0). Structure
    /// destinations use the ordinary <c>Dest</c> entry; their first array item identifies a structure
    /// element instead of a page.
    /// </summary>
    /// <param name="destination">A destination built for a structure element.</param>
    public void SetStructureDestination(PdfDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.IsStructureDestination)
            throw new ArgumentException("The destination does not point at a structure element.", nameof(destination));

        SetDestination(destination);
    }

    /// <summary>
    /// Sets the colour of the bookmark's text (PDF 1.4). The three components run from zero to one.
    /// </summary>
    /// <param name="color">The colour to draw the title in.</param>
    public void SetColor(PdfColor color)
    {
        (float red, float green, float blue) = color.ToRgb();
        Dictionary.Set(PdfName.Get("C"), PdfValue.Array(PdfArray.OfNumbers(red, green, blue)));
        _outline.Document.MarkChanged(Id);
    }

    /// <summary>
    /// Sets whether the title is drawn italic, bold, both or neither (PDF 1.4, the <c>F</c> flags:
    /// bit 1 italic, bit 2 bold).
    /// </summary>
    /// <param name="italic">Whether to slant the title.</param>
    /// <param name="bold">Whether to embolden the title.</param>
    public void SetStyle(bool italic, bool bold)
    {
        int flags = (italic ? 1 : 0) | (bold ? 2 : 0);
        if (flags == 0)
            Dictionary.Remove(PdfName.Get("F"));
        else
            Dictionary.Set(PdfName.Get("F"), PdfValue.Integer(flags));

        _outline.Document.MarkChanged(Id);
    }

    /// <summary>Adds a bookmark nested under this one.</summary>
    /// <param name="title">The text shown in the navigation pane.</param>
    /// <param name="destination">Where the bookmark leads, or nothing for a heading with no target.</param>
    public PdfOutlineItem Add(string title, PdfDestination? destination = null)
    {
        ArgumentNullException.ThrowIfNull(title);

        PdfOutlineItem child = Create(_outline, title, destination, Id);
        _children.Add(child);
        _outline.Reflow();
        return child;
    }

    /// <summary>Removes a child bookmark and everything beneath it.</summary>
    /// <param name="child">The bookmark to remove.</param>
    /// <returns>Whether the bookmark was a child of this one.</returns>
    public bool Remove(PdfOutlineItem child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!_children.Remove(child))
            return false;

        _outline.Reflow();
        return true;
    }

    /// <summary>Makes one item and wires its dictionary to a parent, ready to be chained in.</summary>
    internal static PdfOutlineItem Create(PdfOutline outline, string title, PdfDestination? destination, PdfObjectId parentId)
    {
        PdfDocument document = outline.Document;

        var dictionary = new PdfDictionary(7);
        dictionary.Set(PdfNames.Title, PdfValue.TextString(title));
        dictionary.Set(PdfNames.Parent, PdfValue.Reference(parentId));

        if (destination is not null)
        {
            PdfArray array = destination.ToArray();
            document.Attach(PdfValue.Array(array));
            dictionary.Set(PdfNames.Dest, PdfValue.Array(array));
            if (destination.IsStructureDestination)
                document.Version = PdfVersion.Max(document.Version, PdfVersion.Pdf20);
        }

        document.Attach(PdfValue.Dictionary(dictionary));
        PdfObjectId id = document.AddObject(PdfValue.Dictionary(dictionary));
        return new PdfOutlineItem(outline, id, dictionary);
    }

    /// <summary>Reads the children out of the file into the model.</summary>
    internal void CollectChildren(HashSet<int> visited, int depth)
    {
        if (depth > 64)
            return;

        _isOpen = Dictionary.GetInteger(PdfNames.Count) >= 0;

        PdfValue reference = Dictionary[PdfNames.First];

        for (int guard = 0; reference.IsReference && guard < 100_000; guard++)
        {
            PdfObjectId id = reference.AsReference;
            if (!visited.Add(id.Number) || _outline.Document.GetObject(id).AsDictionary is not { } dictionary)
                break;

            var child = new PdfOutlineItem(_outline, id, dictionary);
            child.CollectChildren(visited, depth + 1);
            _children.Add(child);
            reference = dictionary[PdfNames.Next];
        }
    }

    /// <summary>
    /// Recomputes this item's links and count and those of everything beneath it, returning how
    /// many rows its subtree shows when it is open. A closed item still writes its count so a
    /// reader knows how many rows to reveal, but hides them from the total above it.
    /// </summary>
    internal int ReflowSubtree()
    {
        int visible = 0;
        foreach (PdfOutlineItem child in _children)
            visible += 1 + child.ReflowSubtree();

        Chain(_outline.Document, Dictionary, Id, _children);
        Dictionary.Set(PdfNames.Count, PdfValue.Integer(_isOpen ? visible : -visible));
        _outline.Document.MarkChanged(Id);

        return _isOpen ? visible : 0;
    }

    /// <summary>Writes the doubly linked child chain of §7.7.2 and the parent's first and last links.</summary>
    internal static void Chain(PdfDocument document, PdfDictionary parent, PdfObjectId parentId, List<PdfOutlineItem> children)
    {
        if (children.Count == 0)
        {
            parent.Remove(PdfNames.First);
            parent.Remove(PdfNames.Last);
            return;
        }

        for (int i = 0; i < children.Count; i++)
        {
            PdfDictionary item = children[i].Dictionary;
            item.Set(PdfNames.Parent, PdfValue.Reference(parentId));

            if (i > 0)
                item.Set(PdfNames.Prev, PdfValue.Reference(children[i - 1].Id));
            else
                item.Remove(PdfNames.Prev);

            if (i < children.Count - 1)
                item.Set(PdfNames.Next, PdfValue.Reference(children[i + 1].Id));
            else
                item.Remove(PdfNames.Next);

            document.MarkChanged(children[i].Id);
        }

        parent.Set(PdfNames.First, PdfValue.Reference(children[0].Id));
        parent.Set(PdfNames.Last, PdfValue.Reference(children[^1].Id));
    }
}
