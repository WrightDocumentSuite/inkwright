using Inkwright.Cos;

namespace Inkwright.Merging;

/// <summary>
/// Brings the bookmarks across. Only the ones that still lead somewhere survive: a merge that takes
/// three pages out of a book must not keep the entry for chapter nine, and an entry whose children
/// all point at pages that were left behind goes with them.
/// </summary>
internal sealed class OutlineImporter(MergeContext context)
{
    /// <summary>How deep a bookmark tree may nest before it is taken to be looping.</summary>
    private const int MaxDepth = 32;

    /// <summary>A bookmark and the object it was written as, which is what its siblings point at.</summary>
    private readonly record struct Node(PdfDictionary Item, PdfObjectId Id);

    /// <summary>Copies the source's bookmarks after the target's own.</summary>
    internal void Merge()
    {
        if (context.Source.Catalog.GetDictionary(PdfNames.Outlines) is not { } source)
            return;

        List<Node> copied = Copy(source, 0);
        if (copied.Count == 0)
            return;

        (PdfDictionary root, PdfObjectId rootId) = Root();
        List<Node> all = [.. Existing(root), .. copied];

        Link(root, rootId, all);
        root.Set(PdfNames.Count, PdfValue.Integer(Visible(all)));
        context.Target.MarkChanged(rootId);
    }

    /// <summary>
    /// Copies the children of a node, keeping those that lead somewhere. A node with no destination
    /// of its own is kept when any of its children are, so that a chapter heading still holds the
    /// sections beneath it.
    /// </summary>
    private List<Node> Copy(PdfDictionary parent, int depth)
    {
        List<Node> kept = [];
        if (depth > MaxDepth)
            return kept;

        PdfDictionary? item = parent.GetDictionary(PdfNames.First);

        for (int guard = 0; item is not null && guard < 100_000; guard++)
        {
            List<Node> children = Copy(item, depth + 1);
            PdfArray? destination = Destination(item);

            if (destination is not null || children.Count > 0)
                kept.Add(Build(item, destination, children));

            item = item.GetDictionary(PdfNames.Next);
        }

        return kept;
    }

    /// <summary>Where a bookmark leads, whether it says so directly or through a jump action.</summary>
    private PdfArray? Destination(PdfDictionary item)
    {
        PdfValue direct = item.Get(PdfNames.Dest);

        if (direct.AsArray is { } array)
            return context.Retarget(array);

        if (direct.AsTextString is { } name)
            return Named(name);

        if (item.GetDictionary(PdfNames.A) is not { } action || action.GetName(PdfNames.S) != PdfNames.GoTo)
            return null;

        PdfValue target = action.Get(PdfName.Get("D"));
        return target.AsArray is { } targetArray ? context.Retarget(targetArray) : Named(target.AsTextString);
    }

    /// <summary>
    /// Resolves a destination named by string against the source document, then retargets it. The
    /// name is resolved rather than carried, because the merge may have renamed it and because a
    /// bookmark that points straight at its page needs no name at all.
    /// </summary>
    private PdfArray? Named(string? name)
    {
        if (name is null)
            return null;

        PdfDictionary? tree = context.Source.Catalog.GetDictionary(PdfNames.Names)?.GetDictionary(PdfNames.Dests);
        PdfValue value = PdfNameTree.Find(tree, name);

        if (value.IsNull && context.Source.Catalog.GetDictionary(PdfNames.Dests) is { } older)
            value = older.Get(PdfName.Get(name));

        return context.Retarget(value.AsArray ?? value.AsDictionary?.GetArray(PdfName.Get("D")));
    }

    /// <summary>Writes one bookmark and the subtree below it.</summary>
    private Node Build(PdfDictionary source, PdfArray? destination, List<Node> children)
    {
        var item = new PdfDictionary(7);

        if (source.GetTextString(PdfNames.Title) is { } title)
            item.Set(PdfNames.Title, PdfValue.TextString(title));

        // The colour and style are decoration the source chose, and copy as they stand; the
        // destination is the one thing that has to be rebuilt against the new page tree.
        foreach (PdfName key in (PdfName[])[PdfName.Get("C"), PdfName.Get("F")])
        {
            if (!source[key].IsNull)
                item.Set(key, context.Importer.Import(source[key]));
        }

        if (destination is not null)
        {
            context.Target.Attach(PdfValue.Array(destination));
            item.Set(PdfNames.Dest, PdfValue.Array(destination));
        }

        PdfObjectId id = context.Target.AddObject(PdfValue.Dictionary(item));
        Link(item, id, children);

        if (children.Count > 0)
            item.Set(PdfNames.Count, PdfValue.Integer(-children.Count));

        return new Node(item, id);
    }

    /// <summary>Chains a node's children into the doubly linked list the specification asks for.</summary>
    private static void Link(PdfDictionary parent, PdfObjectId parentId, List<Node> children)
    {
        if (children.Count == 0)
            return;

        for (int i = 0; i < children.Count; i++)
        {
            children[i].Item.Set(PdfNames.Parent, PdfValue.Reference(parentId));

            if (i > 0)
                children[i].Item.Set(PdfNames.Prev, PdfValue.Reference(children[i - 1].Id));
            else
                children[i].Item.Remove(PdfNames.Prev);

            if (i < children.Count - 1)
                children[i].Item.Set(PdfNames.Next, PdfValue.Reference(children[i + 1].Id));
            else
                children[i].Item.Remove(PdfNames.Next);
        }

        parent.Set(PdfNames.First, PdfValue.Reference(children[0].Id));
        parent.Set(PdfNames.Last, PdfValue.Reference(children[^1].Id));
    }

    /// <summary>
    /// How many entries a reader sees at once: every top-level bookmark, plus the descendants of
    /// the ones that start open. A closed subtree counts itself and nothing beneath it.
    /// </summary>
    private static int Visible(List<Node> nodes)
    {
        int total = 0;
        foreach (Node node in nodes)
            total += 1 + Math.Max(0, (int)node.Item.GetInteger(PdfNames.Count));

        return total;
    }

    /// <summary>The bookmarks the target already had, read with the objects they were written as.</summary>
    private IEnumerable<Node> Existing(PdfDictionary root)
    {
        PdfValue reference = root[PdfNames.First];

        for (int guard = 0; reference.IsReference && guard < 100_000; guard++)
        {
            PdfObjectId id = reference.AsReference;
            if (context.Target.GetObject(id).AsDictionary is not { } item)
                yield break;

            yield return new Node(item, id);
            reference = item[PdfNames.Next];
        }
    }

    private (PdfDictionary Root, PdfObjectId Id) Root()
    {
        PdfValue existing = context.Target.Catalog[PdfNames.Outlines];

        if (existing.IsReference && context.Target.GetObject(existing.AsReference).AsDictionary is { } found)
            return (found, existing.AsReference);

        var root = new PdfDictionary(3);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.Outlines));

        PdfObjectId id = context.Target.AddObject(PdfValue.Dictionary(root));
        context.Target.Catalog.Set(PdfNames.Outlines, PdfValue.Reference(id));
        context.Target.MarkChanged(context.Target.CatalogId);
        return (root, id);
    }
}
