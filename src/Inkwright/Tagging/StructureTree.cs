using Inkwright.Cos;

namespace Inkwright.Tagging;

/// <summary>
/// One node of the logical structure tree (ISO 32000-1 §14.7). A node either holds other nodes or
/// points at the marked-content sequences on a page that make up its content.
/// </summary>
public sealed class StructureElement
{
    private readonly List<StructureElement> _children = [];
    private readonly List<(PdfObjectId Page, int Mcid)> _content = [];

    internal StructureElement(string tag, StructureElement? parent)
    {
        Tag = tag;
        Parent = parent;
    }

    /// <summary>The structure type, such as <c>P</c>, <c>H1</c> or <c>TD</c>.</summary>
    public string Tag { get; }

    /// <summary>The node this one hangs from, or <see langword="null"/> for the root.</summary>
    public StructureElement? Parent { get; }

    /// <summary>Text that replaces the content for a reader that cannot see it.</summary>
    public string? AlternateText { get; set; }

    /// <summary>The text the content actually spells, when the glyphs do not say it plainly.</summary>
    public string? ActualText { get; set; }

    /// <summary>The language of this subtree, as a BCP 47 tag.</summary>
    public string? Language { get; set; }

    /// <summary>The nodes below this one.</summary>
    public IReadOnlyList<StructureElement> Children => _children;

    internal IReadOnlyList<(PdfObjectId Page, int Mcid)> Content => _content;

    internal PdfObjectId Id { get; set; }

    /// <summary>Adds a child node.</summary>
    /// <param name="tag">The structure type of the child.</param>
    public StructureElement Add(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);

        var child = new StructureElement(tag, this);
        _children.Add(child);
        return child;
    }

    /// <summary>Attaches a marked-content sequence on a page to this node.</summary>
    /// <param name="page">The page the sequence lives on.</param>
    /// <param name="mcid">The identifier the <c>BDC</c> operator gave it.</param>
    public void AddContent(PdfObjectId page, int mcid) => _content.Add((page, mcid));
}

/// <summary>
/// Builds the structure tree, the parent tree and the page links that make a document tagged. The
/// numbers that tie the three together are bookkeeping, so the builder owns them: callers name tags
/// and hand over marked-content identifiers, and everything else follows.
/// </summary>
public sealed class StructureTreeBuilder
{
    private readonly PdfDocument _document;
    private readonly List<StructureElement> _roots = [];
    private readonly Dictionary<PdfObjectId, int> _pageKeys = [];
    private readonly Dictionary<PdfObjectId, int> _nextMcid = [];

    /// <summary>Starts a structure tree for a document.</summary>
    /// <param name="document">The document to tag.</param>
    public StructureTreeBuilder(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
    }

    /// <summary>The document's language, written into the catalog.</summary>
    public string? Language { get; set; }

    /// <summary>Adds a top-level node.</summary>
    /// <param name="tag">The structure type, usually <c>Document</c> or a section type.</param>
    public StructureElement AddRoot(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);

        var element = new StructureElement(tag, null);
        _roots.Add(element);
        return element;
    }

    /// <summary>Hands out the next marked-content identifier for a page.</summary>
    /// <param name="page">The page the sequence will be written on.</param>
    public int NextMarkedContentId(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        int next = _nextMcid.GetValueOrDefault(page.Id);
        _nextMcid[page.Id] = next + 1;
        return next;
    }

    /// <summary>Writes the tree into the document and links the catalog and pages to it.</summary>
    public void Build()
    {
        if (_roots.Count == 0)
            return;

        PdfObjectId rootId = _document.AddObject(PdfValue.Null);
        var root = new PdfDictionary(4);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));

        var kids = new PdfArray(_roots.Count);
        var byPage = new Dictionary<PdfObjectId, List<(int Mcid, PdfObjectId Element)>>();

        foreach (StructureElement element in _roots)
            kids.Add(PdfValue.Reference(Write(element, rootId, byPage)));

        root.Set(PdfNames.K, PdfValue.Array(kids));
        root.Set(PdfNames.ParentTree, PdfValue.Reference(BuildParentTree(byPage)));
        root.Set(PdfNames.ParentTreeNextKey, PdfValue.Integer(_pageKeys.Count));
        _document.SetObject(rootId, PdfValue.Dictionary(root));

        _document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        _document.InvalidateStructure();

        var markInfo = new PdfDictionary(1);
        markInfo.Set(PdfNames.Marked, PdfValue.True);
        _document.Catalog.Set(PdfNames.MarkInfo, PdfValue.Dictionary(markInfo));

        if (!string.IsNullOrEmpty(Language))
            _document.Catalog.Set(PdfNames.Lang, PdfValue.TextString(Language));

        _document.MarkChanged(_document.CatalogId);
    }

    private PdfObjectId Write(
        StructureElement element,
        PdfObjectId parentId,
        Dictionary<PdfObjectId, List<(int Mcid, PdfObjectId Element)>> byPage)
    {
        PdfObjectId id = _document.AddObject(PdfValue.Null);
        element.Id = id;

        var dictionary = new PdfDictionary(8);
        dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        dictionary.Set(PdfNames.S, PdfValue.Name(PdfName.Get(element.Tag)));
        dictionary.Set(PdfNames.P, PdfValue.Reference(parentId));

        if (!string.IsNullOrEmpty(element.AlternateText))
            dictionary.Set(PdfNames.Alt, PdfValue.TextString(element.AlternateText));

        if (!string.IsNullOrEmpty(element.ActualText))
            dictionary.Set(PdfNames.ActualText, PdfValue.TextString(element.ActualText));

        if (!string.IsNullOrEmpty(element.Language))
            dictionary.Set(PdfNames.Lang, PdfValue.TextString(element.Language));

        var kids = new PdfArray();
        PdfObjectId page = default;

        foreach ((PdfObjectId contentPage, int mcid) in element.Content)
        {
            page = contentPage;
            kids.Add(PdfValue.Integer(mcid));

            if (!byPage.TryGetValue(contentPage, out List<(int, PdfObjectId)>? entries))
                byPage[contentPage] = entries = [];

            entries.Add((mcid, id));
        }

        if (page.IsValid)
            dictionary.Set(PdfNames.Pg, PdfValue.Reference(page));

        foreach (StructureElement child in element.Children)
            kids.Add(PdfValue.Reference(Write(child, id, byPage)));

        if (kids.Count == 1)
            dictionary.Set(PdfNames.K, kids[0]);
        else if (kids.Count > 1)
            dictionary.Set(PdfNames.K, PdfValue.Array(kids));

        _document.SetObject(id, PdfValue.Dictionary(dictionary));
        return id;
    }

    private PdfObjectId BuildParentTree(Dictionary<PdfObjectId, List<(int Mcid, PdfObjectId Element)>> byPage)
    {
        var numbers = new PdfArray(byPage.Count * 2);
        int key = 0;

        foreach (PdfPage page in _document.Pages)
        {
            if (!byPage.TryGetValue(page.Id, out List<(int Mcid, PdfObjectId Element)>? entries))
                continue;

            entries.Sort((a, b) => a.Mcid.CompareTo(b.Mcid));

            var elements = new PdfArray(entries.Count);
            foreach ((int _, PdfObjectId element) in entries)
                elements.Add(PdfValue.Reference(element));

            PdfObjectId listId = _document.AddObject(PdfValue.Array(elements));

            numbers.Add(PdfValue.Integer(key));
            numbers.Add(PdfValue.Reference(listId));

            page.Dictionary.Set(PdfNames.StructParents, PdfValue.Integer(key));
            page.MarkChanged();
            _pageKeys[page.Id] = key;
            key++;
        }

        var tree = new PdfDictionary(1);
        tree.Set(PdfNames.Nums, PdfValue.Array(numbers));
        return _document.AddObject(PdfValue.Dictionary(tree));
    }
}
