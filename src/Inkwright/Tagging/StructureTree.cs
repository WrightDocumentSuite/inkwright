using Inkwright.Annotations;
using Inkwright.Cos;

namespace Inkwright.Tagging;

/// <summary>
/// One node of the logical structure tree (ISO 32000-1 §14.7). A node either holds other nodes or
/// points at the marked-content sequences on a page that make up its content.
/// </summary>
public sealed class StructureElement
{
    private readonly StructureTreeBuilder _builder;
    private readonly List<StructureElement> _children = [];
    private readonly List<(PdfObjectId Page, int Mcid)> _content = [];
    private readonly List<PdfAnnotation> _objects = [];
    private readonly List<Kid> _kids = [];

    internal StructureElement(string tag, StructureElement? parent, StructureTreeBuilder builder)
    {
        Tag = tag;
        Parent = parent;
        _builder = builder;
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

    internal IReadOnlyList<PdfAnnotation> Objects => _objects;

    internal IReadOnlyList<Kid> Kids => _kids;

    internal PdfObjectId Id { get; set; }

    /// <summary>Adds a child node at the current position in this element's reading order.</summary>
    /// <param name="tag">The structure type of the child.</param>
    public StructureElement Add(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        _builder.EnsureMutable();

        var child = new StructureElement(tag, this, _builder);
        _children.Add(child);
        _kids.Add(new ChildKid(child));
        return child;
    }

    /// <summary>
    /// Attaches a marked-content sequence on a page at the current position in this element's
    /// reading order.
    /// </summary>
    /// <param name="page">The page the sequence lives on.</param>
    /// <param name="mcid">The identifier the <c>BDC</c> operator gave it.</param>
    public void AddContent(PdfObjectId page, int mcid)
    {
        _builder.EnsureMutable();
        ArgumentOutOfRangeException.ThrowIfNegative(mcid);
        _content.Add((page, mcid));
        _kids.Add(new ContentKid(page, mcid));
    }

    /// <summary>
    /// Represents an annotation as a whole-object content item of this element. Building the tree
    /// writes the element's <c>OBJR</c>, the annotation's <c>StructParent</c>, and the matching
    /// <c>ParentTree</c> entry as one operation.
    /// </summary>
    /// <param name="annotation">The indirect annotation to place at this point in reading order.</param>
    public void AddAnnotation(PdfAnnotation annotation)
    {
        _builder.RegisterAnnotation(annotation);
        _objects.Add(annotation);
        _kids.Add(new AnnotationKid(annotation));
    }

    internal abstract record Kid;

    internal sealed record ChildKid(StructureElement Element) : Kid;

    internal sealed record ContentKid(PdfObjectId Page, int Mcid) : Kid;

    internal sealed record AnnotationKid(PdfAnnotation Annotation) : Kid;
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
    private readonly List<ExistingAnnotation> _existingAnnotations = [];
    private readonly HashSet<PdfObjectId> _annotationIds = [];
    private readonly Dictionary<PdfObjectId, int> _nextMcid = [];
    private HashSet<PdfObjectId>? _structuredObjectIds;
    private bool _built;

    private sealed record ExistingAnnotation(
        PdfStructureNode Element,
        PdfAnnotation Annotation,
        ExistingKidPlacement Placement);

    private sealed record BuildPlan(IReadOnlyDictionary<PdfObjectId, int> PageKeys);

    private enum ExistingKidPlacementKind
    {
        Append,
        Index,
        Before,
        After,
    }

    private readonly record struct ExistingKidPlacement(
        ExistingKidPlacementKind Kind,
        int Index,
        PdfStructureKid? Anchor)
    {
        internal static ExistingKidPlacement Append() =>
            new(ExistingKidPlacementKind.Append, 0, null);

        internal static ExistingKidPlacement At(int index) =>
            new(ExistingKidPlacementKind.Index, index, null);

        internal static ExistingKidPlacement Before(PdfStructureKid anchor) =>
            new(ExistingKidPlacementKind.Before, 0, anchor);

        internal static ExistingKidPlacement After(PdfStructureKid anchor) =>
            new(ExistingKidPlacementKind.After, 0, anchor);
    }

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
        EnsureMutable();

        var element = new StructureElement(tag, null, this);
        _roots.Add(element);
        return element;
    }

    /// <summary>
    /// Appends an annotation beneath an indirect structure element already present in the
    /// document. A semantic child is created when the selected element cannot own that annotation
    /// type directly. This is the editing counterpart to
    /// <see cref="StructureElement.AddAnnotation"/> for a tree already built by layout.
    /// </summary>
    /// <param name="element">The existing element beneath which the annotation belongs.</param>
    /// <param name="annotation">The indirect annotation to represent.</param>
    public void AddAnnotation(PdfStructureNode element, PdfAnnotation annotation)
        => QueueAnnotation(element, annotation, ExistingKidPlacement.Append());

    /// <summary>
    /// Inserts an annotation at a zero-based position in an indirect structure element already
    /// present in the document. The position addresses the element's serialized <c>K</c> sequence;
    /// zero inserts first and <c>element.Kids.Count</c> appends for a well-formed element.
    /// </summary>
    /// <param name="element">The existing element beside which the annotation belongs.</param>
    /// <param name="annotation">The indirect annotation to represent.</param>
    /// <param name="index">The zero-based position in the existing <c>K</c> sequence.</param>
    public void AddAnnotation(PdfStructureNode element, PdfAnnotation annotation, int index)
        => QueueAnnotation(element, annotation, ExistingKidPlacement.At(index));

    /// <summary>
    /// Inserts an annotation immediately before an existing item in a structure element's reading
    /// order.
    /// </summary>
    /// <param name="element">The existing element beside which the annotation belongs.</param>
    /// <param name="annotation">The indirect annotation to represent.</param>
    /// <param name="anchor">An item obtained from the selected element's <c>Kids</c> list.</param>
    public void AddAnnotationBefore(
        PdfStructureNode element,
        PdfAnnotation annotation,
        PdfStructureKid anchor)
        => QueueAnnotation(element, annotation, ExistingKidPlacement.Before(anchor));

    /// <summary>
    /// Inserts an annotation immediately after an existing item in a structure element's reading
    /// order.
    /// </summary>
    /// <param name="element">The existing element beside which the annotation belongs.</param>
    /// <param name="annotation">The indirect annotation to represent.</param>
    /// <param name="anchor">An item obtained from the selected element's <c>Kids</c> list.</param>
    public void AddAnnotationAfter(
        PdfStructureNode element,
        PdfAnnotation annotation,
        PdfStructureKid anchor)
        => QueueAnnotation(element, annotation, ExistingKidPlacement.After(anchor));

    private void QueueAnnotation(
        PdfStructureNode element,
        PdfAnnotation annotation,
        ExistingKidPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(element);
        EnsureMutable();

        if (!element.Id.IsValid ||
            !ReferenceEquals(_document.GetObject(element.Id).AsDictionary, element.Dictionary))
        {
            throw new ArgumentException(
                "The structure element must be an indirect element from this document.",
                nameof(element));
        }

        ValidatePlacement(element, placement);
        RegisterAnnotation(annotation);
        _existingAnnotations.Add(new ExistingAnnotation(element, annotation, placement));
    }

    /// <summary>Hands out the next marked-content identifier for a page.</summary>
    /// <param name="page">The page the sequence will be written on.</param>
    public int NextMarkedContentId(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        EnsureMutable();

        if (!ReferenceEquals(page.Document, _document))
            throw new ArgumentException("The page belongs to another document.", nameof(page));

        int next = _nextMcid.TryGetValue(page.Id, out int remembered)
            ? remembered
            : ExistingMarkedContentCount(page);

        if (next == int.MaxValue)
            throw new InvalidOperationException($"Page {page.Id} has no representable next marked-content identifier.");

        _nextMcid[page.Id] = next + 1;
        return next;
    }

    /// <summary>Writes the tree into the document and links the catalog and pages to it.</summary>
    public void Build()
    {
        EnsureMutable();
        BuildPlan plan = PreflightBuild();
        _built = true;

        if (_roots.Count == 0 && _existingAnnotations.Count == 0)
            return;

        (PdfDictionary root, PdfObjectId rootId) = Root();
        SortedDictionary<int, PdfValue> parentEntries = ReadParentTree(root);
        int nextKey = NextParentKey(root, parentEntries);

        var addedRoots = new PdfArray(_roots.Count);
        var byPage = new Dictionary<PdfObjectId, List<(int Mcid, PdfObjectId Element)>>();
        var annotations = new List<(PdfAnnotation Annotation, PdfObjectId Element)>();

        foreach (StructureElement element in _roots)
            addedRoots.Add(PdfValue.Reference(Write(element, rootId, byPage, annotations)));

        AppendKids(root, addedRoots);

        foreach (ExistingAnnotation pending in _existingAnnotations)
        {
            PdfObjectId owner = AttachToExistingTree(pending);
            annotations.Add((pending.Annotation, owner));
        }

        AddPageEntries(byPage, parentEntries, plan.PageKeys, ref nextKey);
        AddAnnotationEntries(annotations, parentEntries, ref nextKey);

        if (parentEntries.Count > 0)
        {
            PdfDictionary parentTree = PdfNumberTree.Build(parentEntries.Select(entry => (entry.Key, entry.Value)));
            PdfObjectId parentTreeId = _document.AddObject(PdfValue.Dictionary(parentTree));
            root.Set(PdfNames.ParentTree, PdfValue.Reference(parentTreeId));
            root.Set(PdfNames.ParentTreeNextKey, PdfValue.Integer(nextKey));
        }

        _document.MarkChanged(rootId);
        _document.InvalidateStructure();

        PdfDictionary markInfo = Copy(_document.Catalog.GetDictionary(PdfNames.MarkInfo));
        markInfo.Set(PdfNames.Marked, PdfValue.True);
        _document.Catalog.Set(PdfNames.MarkInfo, PdfValue.Dictionary(markInfo));

        if (!string.IsNullOrEmpty(Language))
            _document.Catalog.Set(PdfNames.Lang, PdfValue.TextString(Language));

        _document.MarkChanged(_document.CatalogId);
    }

    private PdfObjectId Write(
        StructureElement element,
        PdfObjectId parentId,
        Dictionary<PdfObjectId, List<(int Mcid, PdfObjectId Element)>> byPage,
        List<(PdfAnnotation Annotation, PdfObjectId Element)> annotations)
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
        PdfObjectId page = FirstContentPage(element);

        foreach (StructureElement.Kid kid in element.Kids)
        {
            switch (kid)
            {
                case StructureElement.ContentKid content:
                    kids.Add(ContentReference(content, page));

                    if (!byPage.TryGetValue(content.Page, out List<(int, PdfObjectId)>? entries))
                        byPage[content.Page] = entries = [];

                    entries.Add((content.Mcid, id));
                    break;

                case StructureElement.AnnotationKid annotation:
                    kids.Add(PdfValue.Dictionary(ObjectReference(annotation.Annotation)));
                    annotations.Add((annotation.Annotation, id));
                    break;

                case StructureElement.ChildKid child:
                    kids.Add(PdfValue.Reference(Write(child.Element, id, byPage, annotations)));
                    break;
            }
        }

        if (page.IsValid)
            dictionary.Set(PdfNames.Pg, PdfValue.Reference(page));

        if (kids.Count == 1)
            dictionary.Set(PdfNames.K, kids[0]);
        else if (kids.Count > 1)
            dictionary.Set(PdfNames.K, PdfValue.Array(kids));

        _document.SetObject(id, PdfValue.Dictionary(dictionary));
        return id;
    }

    private static PdfObjectId FirstContentPage(StructureElement element)
    {
        foreach (StructureElement.Kid kid in element.Kids)
        {
            if (kid is StructureElement.ContentKid content)
                return content.Page;
        }

        return default;
    }

    private static PdfValue ContentReference(
        StructureElement.ContentKid content,
        PdfObjectId defaultPage)
    {
        if (content.Page == defaultPage)
            return PdfValue.Integer(content.Mcid);

        var reference = new PdfDictionary(3);
        reference.Set(PdfNames.Type, PdfValue.Name("MCR"));
        reference.Set(PdfNames.Pg, PdfValue.Reference(content.Page));
        reference.Set(PdfNames.MCID, PdfValue.Integer(content.Mcid));
        return PdfValue.Dictionary(reference);
    }

    private void AddPageEntries(
        Dictionary<PdfObjectId, List<(int Mcid, PdfObjectId Element)>> byPage,
        SortedDictionary<int, PdfValue> parentEntries,
        IReadOnlyDictionary<PdfObjectId, int> pageKeys,
        ref int nextKey)
    {
        foreach (PdfPage page in _document.Pages)
        {
            if (!byPage.TryGetValue(page.Id, out List<(int Mcid, PdfObjectId Element)>? entries))
                continue;

            entries.Sort((a, b) => a.Mcid.CompareTo(b.Mcid));

            if (!pageKeys.TryGetValue(page.Id, out int key))
                throw new InvalidOperationException($"Page {page.Id} was not included in structure-tree preflight.");

            if (key >= nextKey)
                nextKey = checked(key + 1);

            PdfArray elements = ExistingParentArray(parentEntries.GetValueOrDefault(key));

            foreach ((int mcid, PdfObjectId element) in entries)
            {
                while (elements.Count <= mcid)
                    elements.Add(PdfValue.Null);

                PdfValue owner = PdfValue.Reference(element);
                if (!elements[mcid].IsNull && elements[mcid] != owner)
                {
                    throw new InvalidOperationException(
                        $"Marked-content identifier {mcid} on page {page.Id} already has a structure parent.");
                }

                elements[mcid] = owner;
            }

            PdfObjectId listId = _document.AddObject(PdfValue.Array(elements));
            parentEntries[key] = PdfValue.Reference(listId);

            page.Dictionary.Set(PdfNames.StructParents, PdfValue.Integer(key));
            page.MarkChanged();
        }
    }

    private void AddAnnotationEntries(
        List<(PdfAnnotation Annotation, PdfObjectId Element)> annotations,
        SortedDictionary<int, PdfValue> parentEntries,
        ref int nextKey)
    {
        foreach ((PdfAnnotation annotation, PdfObjectId element) in annotations)
        {
            int key = TakeKey(parentEntries, ref nextKey);
            parentEntries.Add(key, PdfValue.Reference(element));

            annotation.Dictionary.Set(PdfNames.StructParent, PdfValue.Integer(key));
            _document.MarkChanged(annotation.Id);

            annotation.Page.Dictionary.Set(PdfName.Get("Tabs"), PdfValue.Name(PdfNames.S));
            annotation.Page.MarkChanged();
        }
    }

    private PdfArray ExistingParentArray(PdfValue value)
    {
        var copy = new PdfArray();
        if (_document.Resolve(value).AsArray is not { } existing)
            return copy;

        for (int i = 0; i < existing.Count; i++)
            copy.Add(existing[i]);

        return copy;
    }

    private int ExistingMarkedContentCount(PdfPage page)
    {
        PdfValue stated = page.Dictionary.Get(PdfNames.StructParents);
        if (stated.Kind != PdfValueKind.Integer || stated.AsInteger is < 0 or > int.MaxValue)
            return 0;

        if (_document.Catalog.GetDictionary(PdfNames.StructTreeRoot) is not { } root)
            return 0;

        SortedDictionary<int, PdfValue> entries = ReadParentTree(root);
        return entries.TryGetValue((int)stated.AsInteger, out PdfValue value) &&
               _document.Resolve(value).AsArray is { } parents
            ? parents.Count
            : 0;
    }

    private SortedDictionary<int, PdfValue> ReadParentTree(PdfDictionary root)
    {
        var entries = new SortedDictionary<int, PdfValue>();
        if (root.TryGetRaw(PdfNames.ParentTree, out PdfValue tree))
            ReadParentTreeNode(tree, entries, new HashSet<PdfObjectId>(), new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), 0);

        return entries;
    }

    private void ReadParentTreeNode(
        PdfValue value,
        SortedDictionary<int, PdfValue> entries,
        HashSet<PdfObjectId> references,
        HashSet<PdfDictionary> dictionaries,
        int depth)
    {
        if (depth > 32 || (value.IsReference && !references.Add(value.AsReference)))
            return;

        if (_document.Resolve(value).AsDictionary is not { } node || !dictionaries.Add(node))
            return;

        if (node.GetArray(PdfNames.Nums) is { } numbers)
        {
            for (int i = 0; i + 1 < numbers.Count; i += 2)
            {
                PdfValue key = numbers.Get(i);
                if (key.Kind != PdfValueKind.Integer || key.AsInteger is < 0 or > int.MaxValue)
                    continue;

                if (!entries.TryAdd((int)key.AsInteger, numbers[i + 1]))
                    throw new InvalidOperationException($"The ParentTree contains duplicate key {key.AsInteger}.");
            }
        }

        if (node.GetArray(PdfNames.Kids) is not { } kids)
            return;

        for (int i = 0; i < kids.Count; i++)
            ReadParentTreeNode(kids[i], entries, references, dictionaries, depth + 1);
    }

    private static int NextParentKey(PdfDictionary root, SortedDictionary<int, PdfValue> entries)
    {
        long stated = root.GetInteger(PdfNames.ParentTreeNextKey);
        if (stated > int.MaxValue)
            throw new InvalidOperationException("ParentTreeNextKey is too large to extend.");

        if (entries.Count > 0 && entries.Keys.Last() == int.MaxValue)
            throw new InvalidOperationException("The ParentTree has no representable next key.");

        int afterHighest = entries.Count == 0 ? 0 : entries.Keys.Last() + 1;
        return Math.Max(stated < 0 ? 0 : (int)stated, afterHighest);
    }

    private static int TakeKey(SortedDictionary<int, PdfValue> entries, ref int nextKey)
    {
        while (entries.ContainsKey(nextKey))
        {
            if (nextKey == int.MaxValue)
                throw new InvalidOperationException("The ParentTree has no representable next key.");

            nextKey++;
        }

        if (nextKey == int.MaxValue)
            throw new InvalidOperationException("The ParentTree has no representable next key.");

        return nextKey++;
    }

    private PdfDictionary ObjectReference(PdfAnnotation annotation)
    {
        var reference = new PdfDictionary(3);
        reference.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("OBJR")));
        reference.Set(PdfNames.Pg, PdfValue.Reference(annotation.Page.Id));
        reference.Set(PdfName.Get("Obj"), PdfValue.Reference(annotation.Id));
        return reference;
    }

    private void AppendKids(PdfDictionary owner, PdfArray additions)
    {
        if (additions.Count == 0)
            return;

        PdfValue existing = owner[PdfNames.K];
        var combined = new PdfArray();

        if (_document.Resolve(existing).AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
                combined.Add(array[i]);
        }
        else if (!existing.IsNull)
        {
            combined.Add(existing);
        }

        for (int i = 0; i < additions.Count; i++)
            combined.Add(additions[i]);

        owner.Set(PdfNames.K, PdfValue.Array(combined));
    }

    private PdfObjectId AttachToExistingTree(ExistingAnnotation pending)
    {
        PdfStructureNode element = pending.Element;
        PdfAnnotation annotation = pending.Annotation;
        PdfObjectId ownerId;
        PdfValue addition;

        if (CanOwnAnnotation(element, annotation))
        {
            ownerId = element.Id;
            addition = PdfValue.Dictionary(ObjectReference(annotation));
        }
        else
        {
            var wrapper = new PdfDictionary(6);
            wrapper.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
            wrapper.Set(PdfNames.S, PdfValue.Name(PdfName.Get(AnnotationTag(annotation))));
            wrapper.Set(PdfNames.P, PdfValue.Reference(element.Id));
            wrapper.Set(PdfNames.Pg, PdfValue.Reference(annotation.Page.Id));
            wrapper.Set(PdfNames.K, PdfValue.Dictionary(ObjectReference(annotation)));

            ownerId = _document.AddObject(PdfValue.Dictionary(wrapper));
            addition = PdfValue.Reference(ownerId);
        }

        InsertKid(element, addition, pending.Placement);
        _document.MarkChanged(element.Id);
        return ownerId;
    }

    private void InsertKid(
        PdfStructureNode element,
        PdfValue addition,
        ExistingKidPlacement placement)
    {
        List<PdfValue> kids = RawKids(element.Dictionary);
        int index = placement.Kind switch
        {
            ExistingKidPlacementKind.Append => kids.Count,
            ExistingKidPlacementKind.Index => placement.Index,
            ExistingKidPlacementKind.Before => FindAnchorIndex(element, placement.Anchor!),
            ExistingKidPlacementKind.After => checked(FindAnchorIndex(element, placement.Anchor!) + 1),
            _ => throw new InvalidOperationException("Unknown structure-kid placement."),
        };

        if ((uint)index > (uint)kids.Count)
            throw new InvalidOperationException("The requested structure-kid position is no longer available.");

        kids.Insert(index, addition);
        if (kids.Count == 1)
        {
            element.Dictionary.Set(PdfNames.K, kids[0]);
            return;
        }

        var combined = new PdfArray(kids.Count);
        foreach (PdfValue kid in kids)
            combined.Add(kid);

        element.Dictionary.Set(PdfNames.K, PdfValue.Array(combined));
    }

    private void ValidatePlacement(PdfStructureNode element, ExistingKidPlacement placement)
    {
        int count = RawKids(element.Dictionary).Count;
        switch (placement.Kind)
        {
            case ExistingKidPlacementKind.Append:
                break;

            case ExistingKidPlacementKind.Index when placement.Index < 0 || placement.Index > count:
                throw new ArgumentOutOfRangeException(
                    nameof(placement),
                    placement.Index,
                    $"The insertion index must be between zero and {count}.");

            case ExistingKidPlacementKind.Before:
            case ExistingKidPlacementKind.After:
                ArgumentNullException.ThrowIfNull(placement.Anchor, "anchor");
                _ = FindAnchorIndex(element, placement.Anchor);
                break;
        }
    }

    private int FindAnchorIndex(PdfStructureNode element, PdfStructureKid anchor)
    {
        bool belongs = false;
        foreach (PdfStructureKid kid in element.Kids)
        {
            if (ReferenceEquals(kid, anchor))
            {
                belongs = true;
                break;
            }
        }

        if (!belongs)
            throw new ArgumentException("The anchor must come from the selected element's Kids list.", nameof(anchor));

        List<PdfValue> rawKids = RawKids(element.Dictionary);
        int parsed = 0;
        for (int raw = 0; raw < rawKids.Count && parsed < element.Kids.Count; raw++)
        {
            PdfStructureKid candidate = element.Kids[parsed];
            if (!MatchesKid(rawKids[raw], candidate, element))
                continue;

            if (ReferenceEquals(candidate, anchor))
                return raw;

            parsed++;
        }

        throw new ArgumentException("The anchor is no longer present in the selected element's K entry.", nameof(anchor));
    }

    private bool MatchesKid(PdfValue raw, PdfStructureKid kid, PdfStructureNode element)
    {
        PdfValue resolved = _document.Resolve(raw);
        switch (kid)
        {
            case PdfStructureElementKid child:
                return (raw.IsReference && raw.AsReference == child.Element.Id) ||
                       ReferenceEquals(resolved.AsDictionary, child.Element.Dictionary);

            case PdfStructureContentKid content when resolved.Kind == PdfValueKind.Integer:
                return resolved.AsInteger == content.Reference.MarkedContentId &&
                       EffectivePage(element) == content.Reference.Page;

            case PdfStructureContentKid content when resolved.AsDictionary is { } mcr &&
                                                     mcr.GetName(PdfNames.Type)?.Value == "MCR":
                return mcr.GetInteger(PdfNames.MCID, -1) == content.Reference.MarkedContentId &&
                       PageOf(mcr, element) == content.Reference.Page;

            case PdfStructureObjectKid objectKid when resolved.AsDictionary is { } objr &&
                                                      objr.GetName(PdfNames.Type)?.Value == "OBJR":
                PdfValue target = objr[PdfName.Get("Obj")];
                return target.IsReference && target.AsReference == objectKid.Reference.Object &&
                       PageOf(objr, element) == objectKid.Reference.Page;

            default:
                return false;
        }
    }

    private static PdfObjectId EffectivePage(PdfStructureNode element)
    {
        for (PdfStructureNode? current = element; current is not null; current = current.Parent)
        {
            if (current.Dictionary[PdfNames.Pg].IsReference)
                return current.Dictionary[PdfNames.Pg].AsReference;
        }

        return PdfObjectId.None;
    }

    private static PdfObjectId PageOf(PdfDictionary reference, PdfStructureNode element) =>
        reference[PdfNames.Pg].IsReference
            ? reference[PdfNames.Pg].AsReference
            : EffectivePage(element);

    private List<PdfValue> RawKids(PdfDictionary owner)
    {
        PdfValue raw = owner[PdfNames.K];
        if (_document.Resolve(raw).AsArray is { } array)
        {
            var kids = new List<PdfValue>(array.Count);
            for (int i = 0; i < array.Count; i++)
                kids.Add(array[i]);

            return kids;
        }

        return raw.IsNull ? [] : [raw];
    }

    private static bool CanOwnAnnotation(PdfStructureNode element, PdfAnnotation annotation)
    {
        string tag = element.ResolvedTag;
        return annotation.Type switch
        {
            PdfAnnotationType.Link => tag is "Link" or "Reference",
            PdfAnnotationType.Widget => tag == "Form",
            _ => tag == "Annot",
        };
    }

    private static string AnnotationTag(PdfAnnotation annotation) => annotation.Type switch
    {
        PdfAnnotationType.Link => "Link",
        PdfAnnotationType.Widget => "Form",
        _ => "Annot",
    };

    private BuildPlan PreflightBuild()
    {
        foreach (ExistingAnnotation pending in _existingAnnotations)
        {
            if (!pending.Element.Id.IsValid ||
                !ReferenceEquals(_document.GetObject(pending.Element.Id).AsDictionary, pending.Element.Dictionary))
            {
                throw new InvalidOperationException(
                    "An existing structure element changed before the structure-tree update was built.");
            }

            ValidatePlacement(pending.Element, pending.Placement);
            ValidateAnnotation(pending.Annotation);
        }

        PdfDictionary? root = _document.Catalog.GetDictionary(PdfNames.StructTreeRoot);
        if (_existingAnnotations.Count > 0 && root is null)
            throw new InvalidOperationException("The existing structure tree no longer has a root dictionary.");

        var contentByPage = new Dictionary<PdfObjectId, HashSet<int>>();
        foreach (StructureElement element in _roots)
            CollectPendingContent(element, contentByPage);

        if (root is null && contentByPage.Count == 0 && _annotationIds.Count == 0)
            return new BuildPlan(new Dictionary<PdfObjectId, int>());

        SortedDictionary<int, PdfValue> parentEntries = root is null
            ? []
            : ReadParentTree(root);
        int nextKey = root is null ? 0 : NextParentKey(root, parentEntries);
        var pagesById = _document.Pages.ToDictionary(page => page.Id);
        var claimedPageKeys = new Dictionary<int, PdfObjectId>();
        var pageKeys = new Dictionary<PdfObjectId, int>();

        foreach (PdfObjectId pageId in contentByPage.Keys)
        {
            if (!pagesById.ContainsKey(pageId))
            {
                throw new InvalidOperationException(
                    $"Structure content names page {pageId}, which is not a page in this document.");
            }
        }

        // Reserve every producer-stated page key before allocating one for an unkeyed page. This
        // makes the plan independent of page/content traversal order and prevents an early unkeyed
        // page from taking a key that a later page already claims.
        foreach (PdfPage page in _document.Pages)
        {
            if (!contentByPage.ContainsKey(page.Id))
                continue;

            PdfValue stated = page.Dictionary.Get(PdfNames.StructParents);
            if (stated.Kind != PdfValueKind.Integer || stated.AsInteger is < 0 or >= int.MaxValue)
                continue;

            int key = (int)stated.AsInteger;
            ReservePageKey(page, key, parentEntries, claimedPageKeys, pageKeys, ref nextKey);
        }

        foreach (PdfPage page in _document.Pages)
        {
            if (!contentByPage.ContainsKey(page.Id) || pageKeys.ContainsKey(page.Id))
                continue;

            int key = TakeKey(parentEntries, ref nextKey);
            parentEntries.Add(key, PdfValue.Array(new PdfArray()));
            claimedPageKeys.Add(key, page.Id);
            pageKeys.Add(page.Id, key);
        }

        foreach ((PdfObjectId pageId, HashSet<int> mcids) in contentByPage)
        {
            PdfPage page = pagesById[pageId];
            int key = pageKeys[pageId];

            PdfValue existingValue = parentEntries.GetValueOrDefault(key);
            PdfArray? existingParents = _document.Resolve(existingValue).AsArray;
            if (!existingValue.IsNull && existingParents is null)
            {
                throw new InvalidOperationException(
                    $"Page {page.Id} shares its StructParents key {key} with a non-page content item.");
            }

            foreach (int mcid in mcids)
            {
                if (existingParents is not null && mcid < existingParents.Count && !existingParents[mcid].IsNull)
                {
                    throw new InvalidOperationException(
                        $"Marked-content identifier {mcid} on page {page.Id} already has a structure parent.");
                }
            }

        }

        for (int i = 0; i < _annotationIds.Count; i++)
        {
            int key = TakeKey(parentEntries, ref nextKey);
            parentEntries.Add(key, PdfValue.Null);
        }

        return new BuildPlan(pageKeys);
    }

    private void ReservePageKey(
        PdfPage page,
        int key,
        SortedDictionary<int, PdfValue> parentEntries,
        Dictionary<int, PdfObjectId> claimedPageKeys,
        Dictionary<PdfObjectId, int> pageKeys,
        ref int nextKey)
    {
        if (claimedPageKeys.TryGetValue(key, out PdfObjectId otherPage) && otherPage != page.Id)
        {
            throw new InvalidOperationException(
                $"Pages {otherPage} and {page.Id} share StructParents key {key}.");
        }

        if (parentEntries.TryGetValue(key, out PdfValue existing) &&
            _document.Resolve(existing).AsArray is null)
        {
            throw new InvalidOperationException(
                $"Page {page.Id} shares its StructParents key {key} with a non-page content item.");
        }

        if (!parentEntries.ContainsKey(key))
            parentEntries.Add(key, PdfValue.Array(new PdfArray()));

        if (key >= nextKey)
            nextKey = checked(key + 1);

        claimedPageKeys[key] = page.Id;
        pageKeys.Add(page.Id, key);
    }

    private static void CollectPendingContent(
        StructureElement element,
        Dictionary<PdfObjectId, HashSet<int>> contentByPage)
    {
        foreach (StructureElement.Kid kid in element.Kids)
        {
            switch (kid)
            {
                case StructureElement.ContentKid content:
                    if (!contentByPage.TryGetValue(content.Page, out HashSet<int>? mcids))
                        contentByPage.Add(content.Page, mcids = []);

                    if (!mcids.Add(content.Mcid))
                    {
                        throw new InvalidOperationException(
                            $"Marked-content identifier {content.Mcid} on page {content.Page} occurs more than once in the pending structure tree.");
                    }

                    break;

                case StructureElement.ChildKid child:
                    CollectPendingContent(child.Element, contentByPage);
                    break;
            }
        }
    }

    private void ValidateAnnotation(PdfAnnotation annotation)
    {
        if (!ReferenceEquals(annotation.Document, _document))
            throw new ArgumentException("The annotation belongs to another document.", nameof(annotation));

        if (annotation.IsDeleted)
            throw new InvalidOperationException("A deleted annotation cannot be added to the structure tree.");

        if (!annotation.Id.IsValid)
        {
            throw new InvalidOperationException(
                "The annotation must be an indirect object before it can be referenced by an OBJR.");
        }

        if (!annotation.Page.Id.IsValid)
            throw new InvalidOperationException("The annotation's page must be an indirect object.");

        if (!annotation.Dictionary.Get(PdfNames.StructParent).IsNull)
            throw new InvalidOperationException("The annotation already has a StructParent entry.");
    }

    private (PdfDictionary Root, PdfObjectId Id) Root()
    {
        PdfValue existing = _document.Catalog[PdfNames.StructTreeRoot];
        if (existing.IsReference && _document.GetObject(existing.AsReference).AsDictionary is { } indirect)
            return (indirect, existing.AsReference);

        if (_document.Resolve(existing).AsDictionary is { } direct)
        {
            PdfObjectId promoted = _document.AddObject(PdfValue.Dictionary(direct));
            _document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(promoted));
            _document.MarkChanged(_document.CatalogId);
            return (direct, promoted);
        }

        var root = new PdfDictionary(5);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        PdfObjectId id = _document.AddObject(PdfValue.Dictionary(root));
        _document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(id));
        _document.MarkChanged(_document.CatalogId);
        return (root, id);
    }

    private static PdfDictionary Copy(PdfDictionary? source)
    {
        var copy = new PdfDictionary(source?.Count ?? 1);
        if (source is not null)
        {
            foreach (KeyValuePair<PdfName, PdfValue> entry in source)
                copy.Set(entry.Key, entry.Value);
        }

        return copy;
    }

    internal void RegisterAnnotation(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        EnsureMutable();
        ValidateAnnotation(annotation);

        _structuredObjectIds ??= [.. _document.Structure
            .SelectMany(root => root.Descendants())
            .SelectMany(element => element.Objects)
            .Where(reference => reference.Object.IsValid)
            .Select(reference => reference.Object)];

        if (_structuredObjectIds.Contains(annotation.Id))
            throw new InvalidOperationException("The annotation is already represented by an OBJR in the structure tree.");

        if (!_annotationIds.Add(annotation.Id))
            throw new InvalidOperationException("The annotation has already been added to this structure tree update.");
    }

    internal void EnsureMutable()
    {
        if (_built)
            throw new InvalidOperationException("The structure tree builder has already built its pending changes.");
    }
}
