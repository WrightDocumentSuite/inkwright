using Inkwright.Cos;

namespace Inkwright.Tagging;

/// <summary>A piece of a page's content that a structure element claims.</summary>
/// <param name="Page">The page the content sits on.</param>
/// <param name="MarkedContentId">The identifier the <c>BDC</c> operator on that page gave it.</param>
/// <param name="Stream">
/// The non-page content stream containing the sequence, from an MCR dictionary's <c>Stm</c>
/// entry, or no object for an ordinary page-content sequence.
/// </param>
/// <param name="StreamOwner">
/// The object owning <paramref name="Stream"/>'s resources, from <c>StmOwn</c>, when declared.
/// </param>
public readonly record struct PdfContentReference(
    PdfObjectId Page,
    int MarkedContentId,
    PdfObjectId Stream = default,
    PdfObjectId StreamOwner = default);

/// <summary>An entire PDF object claimed by a structure element through an <c>OBJR</c> kid.</summary>
/// <param name="Page">The page on which the object is rendered, when known.</param>
/// <param name="Object">The indirect object claimed by the structure element.</param>
public readonly record struct PdfObjectReference(PdfObjectId Page, PdfObjectId Object);

/// <summary>One item from a structure element's <c>K</c> entry, in its original logical order.</summary>
public abstract record PdfStructureKid;

/// <summary>A nested structure element.</summary>
/// <param name="Element">The nested element.</param>
public sealed record PdfStructureElementKid(PdfStructureNode Element) : PdfStructureKid;

/// <summary>A marked-content sequence claimed by a structure element.</summary>
/// <param name="Reference">The page and marked-content identifier.</param>
public sealed record PdfStructureContentKid(PdfContentReference Reference) : PdfStructureKid;

/// <summary>An annotation, XObject, or other complete PDF object claimed by a structure element.</summary>
/// <param name="Reference">The page and indirect object identifier.</param>
public sealed record PdfStructureObjectKid(PdfObjectReference Reference) : PdfStructureKid;

/// <summary>
/// One node of a document's logical structure as it was read back. The tree says what the content
/// of a page means and, just as usefully, what order it is meant to be read in — which is the only
/// reliable way to get two columns or a table back out in the order a person would read them.
/// </summary>
public sealed class PdfStructureNode
{
    private readonly List<PdfStructureNode> _children = [];
    private readonly List<PdfContentReference> _content = [];
    private readonly List<PdfObjectReference> _objects = [];
    private readonly List<PdfStructureKid> _kids = [];

    internal PdfStructureNode(
        string tag,
        string resolvedTag,
        string? namespaceName,
        string? resolvedNamespaceName,
        PdfDictionary dictionary,
        PdfDictionary treeRoot,
        PdfStructureNode? parent,
        PdfObjectId id)
    {
        Tag = tag;
        ResolvedTag = resolvedTag;
        NamespaceName = namespaceName;
        ResolvedNamespaceName = resolvedNamespaceName;
        Dictionary = dictionary;
        TreeRoot = treeRoot;
        Parent = parent;
        Id = id;
    }

    /// <summary>The structure type, such as <c>P</c>, <c>H1</c> or <c>TD</c>.</summary>
    public string Tag { get; }

    /// <summary>
    /// The structure type after applying the document's <c>RoleMap</c> or the element namespace's
    /// <c>RoleMapNS</c>. This equals <see cref="Tag"/> when no mapping applies.
    /// </summary>
    public string ResolvedTag { get; }

    /// <summary>
    /// The explicitly declared namespace URI, or <see langword="null"/> for the default PDF 1.7
    /// standard structure namespace.
    /// </summary>
    public string? NamespaceName { get; }

    /// <summary>
    /// The namespace after applying <c>RoleMapNS</c>, or <see langword="null"/> for the default
    /// PDF 1.7 standard structure namespace.
    /// </summary>
    public string? ResolvedNamespaceName { get; }

    /// <summary>The element dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The structure-tree root that owns role and attribute class maps.</summary>
    internal PdfDictionary TreeRoot { get; }

    /// <summary>
    /// The identity of the element object, when it was read as an indirect one. A structure
    /// destination (§12.3.2.3) needs it to point at the element rather than at a page.
    /// </summary>
    public PdfObjectId Id { get; }

    /// <summary>The node this one hangs from, or <see langword="null"/> at the root.</summary>
    public PdfStructureNode? Parent { get; }

    /// <summary>The nodes below this one, in reading order.</summary>
    public IReadOnlyList<PdfStructureNode> Children => _children;

    /// <summary>The marked-content sequences this node claims, in reading order.</summary>
    public IReadOnlyList<PdfContentReference> Content => _content;

    /// <summary>The complete PDF objects this node claims, in reading order.</summary>
    public IReadOnlyList<PdfObjectReference> Objects => _objects;

    /// <summary>
    /// Every item in this element's <c>K</c> entry in its original order. Unlike
    /// <see cref="Children"/> and <see cref="Content"/>, this preserves interleaving between child
    /// elements, marked content, and object references.
    /// </summary>
    public IReadOnlyList<PdfStructureKid> Kids => _kids;

    /// <summary>Text that stands in for the content for a reader that cannot see it.</summary>
    public string? AlternateText => Dictionary.GetTextString(PdfName.Get("Alt"));

    /// <summary>The text the content actually spells, when the glyphs do not spell it plainly.</summary>
    public string? ActualText => Dictionary.GetTextString(PdfName.Get("ActualText"));

    /// <summary>The expansion of an abbreviation the content holds.</summary>
    public string? Expansion => Dictionary.GetTextString(PdfName.Get("E"));

    /// <summary>The language of this subtree, as a BCP 47 tag.</summary>
    public string? Language => Dictionary.GetTextString(PdfName.Get("Lang"));

    /// <summary>The title a reader would see in a structure browser.</summary>
    public string? Title => Dictionary.GetTextString(PdfNames.Title);

    /// <summary>This node and everything beneath it, parents before children.</summary>
    public IEnumerable<PdfStructureNode> Descendants()
    {
        yield return this;

        foreach (PdfStructureNode child in _children)
        {
            foreach (PdfStructureNode node in child.Descendants())
                yield return node;
        }
    }

    internal void AddChild(PdfStructureNode child)
    {
        _children.Add(child);
        _kids.Add(new PdfStructureElementKid(child));
    }

    internal void AddContent(PdfContentReference reference)
    {
        _content.Add(reference);
        _kids.Add(new PdfStructureContentKid(reference));
    }

    internal void AddObject(PdfObjectReference reference)
    {
        _objects.Add(reference);
        _kids.Add(new PdfStructureObjectKid(reference));
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Tag} ({_children.Count} children, {_content.Count} sequences, {_objects.Count} objects)";
}

/// <summary>
/// Reads the logical structure tree of ISO 32000-1 §14.7 back out of a document. The builder in
/// <see cref="StructureTreeBuilder"/> writes one; this reads whatever is there, including trees
/// written by other producers.
/// </summary>
public static class StructureReader
{
    /// <summary>How deep the tree may nest before it is taken to be looping.</summary>
    private const int MaxDepth = 64;

    /// <summary>Reads a document's structure tree.</summary>
    /// <param name="document">The document to read.</param>
    /// <returns>The top-level nodes, or an empty list when the document is not tagged.</returns>
    public static IReadOnlyList<PdfStructureNode> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Catalog.GetDictionary(PdfNames.StructTreeRoot) is not { } root)
            return [];

        List<PdfStructureNode> nodes = [];
        var visited = new HashSet<PdfObjectId>();

        Collect(root[PdfNames.K], root, root, null, nodes, document, visited, 0);
        return nodes;
    }

    /// <summary>
    /// The order a page's marked-content sequences are meant to be read in, taken from the tree. A
    /// sequence the tree never mentions does not appear.
    /// </summary>
    /// <param name="roots">The structure tree, as <see cref="Read"/> returned it.</param>
    /// <param name="page">The page to order.</param>
    public static IReadOnlyList<int> ReadingOrder(IReadOnlyList<PdfStructureNode> roots, PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(page);

        List<int> order = [];
        var seen = new HashSet<int>();

        foreach (PdfContentReference reference in ReadingReferences(roots, page))
        {
            if (seen.Add(reference.MarkedContentId))
                order.Add(reference.MarkedContentId);
        }

        return order;
    }

    /// <summary>Reading order retaining the stream that qualifies each MCID.</summary>
    internal static IReadOnlyList<PdfContentReference> ReadingReferences(
        IReadOnlyList<PdfStructureNode> roots,
        PdfPage page)
    {
        List<PdfContentReference> order = [];
        var seen = new HashSet<(int Stream, int Mcid)>();

        foreach (PdfStructureNode root in roots)
            CollectReadingReferences(root, page.Id, order, seen);

        return order;
    }

    private static void CollectReadingReferences(
        PdfStructureNode node,
        PdfObjectId page,
        List<PdfContentReference> order,
        HashSet<(int Stream, int Mcid)> seen)
    {
        foreach (PdfStructureKid kid in node.Kids)
        {
            switch (kid)
            {
                case PdfStructureContentKid content
                    when content.Reference.Page.Number == page.Number &&
                         seen.Add((content.Reference.Stream.Number, content.Reference.MarkedContentId)):
                    order.Add(content.Reference);
                    break;

                case PdfStructureElementKid element:
                    CollectReadingReferences(element.Element, page, order, seen);
                    break;
            }
        }
    }

    /// <summary>
    /// Reads a <c>K</c> entry, which the specification lets be almost anything: an element, an
    /// array of them, a bare integer naming a marked-content sequence, or a reference dictionary
    /// that names one on a page other than the element's own.
    /// </summary>
    private static void Collect(
        PdfValue value,
        PdfDictionary owner,
        PdfDictionary treeRoot,
        PdfStructureNode? parent,
        List<PdfStructureNode> roots,
        PdfDocument document,
        HashSet<PdfObjectId> visited,
        int depth)
    {
        if (depth > MaxDepth || value.IsNull)
            return;

        if (value.IsReference && !visited.Add(value.AsReference))
            return;

        PdfValue resolved = value.IsReference ? document.GetObject(value.AsReference) : value;

        if (resolved.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
                Collect(array[i], owner, treeRoot, parent, roots, document, visited, depth + 1);

            return;
        }

        if (resolved.Kind == PdfValueKind.Integer)
        {
            if (resolved.AsInteger is >= 0 and <= int.MaxValue)
                parent?.AddContent(new PdfContentReference(PageOf(owner, parent), (int)resolved.AsInteger));
            return;
        }

        if (resolved.AsDictionary is not { } dictionary)
            return;

        // A marked-content reference names a sequence, possibly on another page. An object
        // reference claims an annotation, form, or another complete indirect object.
        PdfName? type = dictionary.GetName(PdfNames.Type);

        if (type?.Value == "MCR")
        {
            PdfObjectId page = dictionary[PdfNames.Pg].IsReference
                ? dictionary[PdfNames.Pg].AsReference
                : PageOf(owner, parent);

            PdfValue markedContentId = dictionary.Get(PdfNames.MCID);
            if (markedContentId.Kind == PdfValueKind.Integer &&
                markedContentId.AsInteger is >= 0 and <= int.MaxValue)
            {
                PdfValue stream = dictionary[PdfName.Get("Stm")];
                PdfValue streamOwner = dictionary[PdfName.Get("StmOwn")];
                parent?.AddContent(new PdfContentReference(
                    page,
                    (int)markedContentId.AsInteger,
                    stream.IsReference ? stream.AsReference : PdfObjectId.None,
                    streamOwner.IsReference ? streamOwner.AsReference : PdfObjectId.None));
            }
            return;
        }

        if (type?.Value == "OBJR")
        {
            PdfObjectId page = dictionary[PdfNames.Pg].IsReference
                ? dictionary[PdfNames.Pg].AsReference
                : PageOf(owner, parent);
            PdfValue target = dictionary[PdfName.Get("Obj")];

            if (target.IsReference)
                parent?.AddObject(new PdfObjectReference(page, target.AsReference));

            return;
        }

        if (dictionary.GetName(PdfNames.S) is not { } tag)
            return;

        PdfObjectId id = value.IsReference ? value.AsReference : PdfObjectId.None;
        string? namespaceName = dictionary.GetDictionary(PdfName.Get("NS"))?.GetTextString(PdfName.Get("NS"));
        (string resolvedTag, string? resolvedNamespaceName) =
            ResolveRole(tag.Value, dictionary, treeRoot, document);
        var node = new PdfStructureNode(
            tag.Value,
            resolvedTag,
            namespaceName,
            resolvedNamespaceName,
            dictionary,
            treeRoot,
            parent,
            id);

        if (parent is null)
            roots.Add(node);
        else
            parent.AddChild(node);

        Collect(dictionary[PdfNames.K], dictionary, treeRoot, node, roots, document, visited, depth + 1);
    }

    /// <summary>
    /// Resolves legacy and namespace-aware role maps transitively. Cycles and malformed targets
    /// stop at the last usable role instead of making structure reading fail.
    /// </summary>
    private static (string Tag, string? NamespaceName) ResolveRole(
        string tag,
        PdfDictionary element,
        PdfDictionary treeRoot,
        PdfDocument document)
    {
        PdfDictionary? structureNamespace = element.GetDictionary(PdfName.Get("NS"));
        string? namespaceName = structureNamespace?.GetTextString(PdfName.Get("NS"));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int depth = 0; depth < MaxDepth; depth++)
        {
            string cycleKey = $"{namespaceName}\0{tag}";
            if (!seen.Add(cycleKey))
                break;

            if (structureNamespace is null)
            {
                PdfDictionary? roleMap = treeRoot.GetDictionary(PdfNames.RoleMap);
                if (roleMap?[PdfName.Get(tag)].AsName is not { } mapped)
                    break;

                tag = mapped.Value;
                continue;
            }

            if (IsStandardNamespace(namespaceName))
                break;

            PdfDictionary? namespaceRoleMap = structureNamespace.GetDictionary(PdfName.Get("RoleMapNS"));
            PdfValue target = namespaceRoleMap?[PdfName.Get(tag)] ?? PdfValue.Null;

            if (target.AsName is { } defaultRole)
            {
                tag = defaultRole.Value;
                structureNamespace = null;
                namespaceName = null;
                break;
            }

            if (target.AsArray is not { Count: >= 2 } pair || pair[0].AsName is not { } targetRole)
                break;

            PdfValue targetNamespace = pair[1];
            if (targetNamespace.IsReference)
                targetNamespace = document.GetObject(targetNamespace.AsReference);

            if (targetNamespace.AsDictionary is not { } nextNamespace)
                break;

            tag = targetRole.Value;
            structureNamespace = nextNamespace;
            namespaceName = structureNamespace.GetTextString(PdfName.Get("NS"));
        }

        return (tag, namespaceName);
    }

    private static bool IsStandardNamespace(string? namespaceName) =>
        namespaceName is "http://iso.org/pdf/ssn" or "http://iso.org/pdf2/ssn";

    /// <summary>
    /// The page a bare marked-content identifier belongs to: the one the element names, or failing
    /// that the one an ancestor named, since <c>Pg</c> is inherited down the tree.
    /// </summary>
    private static PdfObjectId PageOf(PdfDictionary owner, PdfStructureNode? node)
    {
        if (owner[PdfNames.Pg].IsReference)
            return owner[PdfNames.Pg].AsReference;

        for (PdfStructureNode? current = node; current is not null; current = current.Parent)
        {
            if (current.Dictionary[PdfNames.Pg].IsReference)
                return current.Dictionary[PdfNames.Pg].AsReference;
        }

        return default;
    }
}
