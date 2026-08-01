using Inkwright.Cos;

namespace Inkwright.Merging;

/// <summary>
/// Brings the logical structure across. The tree itself copies easily enough, but the numbers that
/// tie it to the pages do not: every page holds a <c>StructParents</c> key into the document's
/// parent tree (§14.7.4.4), and two documents merged together will both have used the number zero.
/// The incoming keys are shifted past whatever the target already occupies.
/// </summary>
internal sealed class StructTreeImporter(MergeContext context)
{
    /// <summary>How deep the copy may recurse before it is taken to be looping.</summary>
    private const int MaxDepth = 64;

    /// <summary>Copies the source's structure elements under the target's structure tree root.</summary>
    internal void Merge()
    {
        if (context.Source.Catalog.GetDictionary(PdfNames.StructTreeRoot) is not { } source)
            return;

        if (source.GetArray(PdfNames.K) is not { Count: > 0 } && source[PdfNames.K].IsNull)
            return;

        (PdfDictionary root, PdfObjectId rootId) = Root();
        int shift = NextKey(root);

        Renumber(shift);

        PdfArray children = root.GetArray(PdfNames.K) ?? CreateChildren(root);
        PdfValue copied = context.Importer.Import(source[PdfNames.K]);

        foreach (PdfValue child in Flatten(copied))
        {
            Reparent(child, rootId, 0);
            children.Add(child);
        }

        MergeParentTree(root, source, shift);
        MergeRoleMap(root, source);
        context.Target.MarkChanged(rootId);
    }

    /// <summary>
    /// Shifts the <c>StructParents</c> keys of the imported pages and their annotations, so they
    /// index the places the incoming parent tree was moved to rather than the target's own.
    /// </summary>
    private void Renumber(int shift)
    {
        if (shift == 0)
            return;

        foreach ((_, PdfPage page) in context.Pages)
        {
            Shift(page.Dictionary, PdfNames.StructParents, shift);

            if (page.Dictionary.GetArray(PdfNames.Annots) is { } annotations)
            {
                for (int i = 0; i < annotations.Count; i++)
                {
                    if (annotations.GetDictionary(i) is { } annotation)
                        Shift(annotation, PdfNames.StructParent, shift);
                }
            }

            // Form and image XObjects carry a key of their own when they hold marked content.
            if (page.Dictionary.GetDictionary(PdfNames.Resources)?.GetDictionary(PdfNames.XObject) is { } objects)
            {
                foreach (KeyValuePair<PdfName, PdfValue> entry in objects)
                {
                    if (objects.Get(entry.Key).AsStream is { } stream)
                        Shift(stream.Dictionary, PdfNames.StructParent, shift);
                }
            }

            page.MarkChanged();
        }

        static void Shift(PdfDictionary holder, PdfName key, int by)
        {
            if (!holder[key].IsNull && holder.Get(key).IsNumber)
                holder.Set(key, PdfValue.Integer(holder.GetInteger(key) + by));
        }
    }

    /// <summary>Appends the source's parent tree entries at the offset their keys were shifted by.</summary>
    private void MergeParentTree(PdfDictionary root, PdfDictionary source, int shift)
    {
        List<(int Key, PdfValue Value)> entries =
            [.. PdfNumberTree.Enumerate(root.GetDictionary(PdfNames.ParentTree))];

        foreach ((int key, PdfValue value) in PdfNumberTree.Enumerate(source.GetDictionary(PdfNames.ParentTree)))
            entries.Add((key + shift, context.Importer.Import(value)));

        if (entries.Count == 0)
            return;

        entries.Sort((a, b) => a.Key.CompareTo(b.Key));

        PdfDictionary tree = PdfNumberTree.Build(entries);
        context.Target.Attach(PdfValue.Dictionary(tree));
        root.Set(PdfNames.ParentTree, PdfValue.Dictionary(tree));
        root.Set(PdfNames.ParentTreeNextKey, PdfValue.Integer(entries[^1].Key + 1));
    }

    /// <summary>Folds the source's tag translations in, leaving any the target already states.</summary>
    private void MergeRoleMap(PdfDictionary root, PdfDictionary source)
    {
        if (source.GetDictionary(PdfNames.RoleMap) is not { } incoming)
            return;

        PdfDictionary map = root.GetDictionary(PdfNames.RoleMap) ?? CreateRoleMap(root);

        foreach (KeyValuePair<PdfName, PdfValue> entry in incoming)
        {
            if (map[entry.Key].IsNull)
                map.Set(entry.Key, context.Importer.Import(entry.Value));
        }
    }

    /// <summary>The first parent-tree key the target has not spoken for.</summary>
    private static int NextKey(PdfDictionary root)
    {
        int stated = (int)root.GetInteger(PdfNames.ParentTreeNextKey);
        int highest = 0;

        foreach ((int key, _) in PdfNumberTree.Enumerate(root.GetDictionary(PdfNames.ParentTree)))
            highest = Math.Max(highest, key + 1);

        return Math.Max(stated, highest);
    }

    /// <summary>Points a copied element at its new parent, following arrays down to the elements.</summary>
    private void Reparent(PdfValue child, PdfObjectId parent, int depth)
    {
        if (depth > MaxDepth)
            return;

        if (child.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
                Reparent(array.Get(i), parent, depth + 1);

            return;
        }

        if (child.AsDictionary is { } dictionary && !dictionary[PdfNames.S].IsNull)
            dictionary.Set(PdfNames.P, PdfValue.Reference(parent));
    }

    /// <summary>The children of a copied <c>K</c> entry, which is one element or an array of them.</summary>
    private static IEnumerable<PdfValue> Flatten(PdfValue copied)
    {
        if (copied.AsArray is not { } array)
        {
            if (!copied.IsNull)
                yield return copied;

            yield break;
        }

        for (int i = 0; i < array.Count; i++)
            yield return array[i];
    }

    private (PdfDictionary Root, PdfObjectId Id) Root()
    {
        PdfValue existing = context.Target.Catalog[PdfNames.StructTreeRoot];

        if (existing.IsReference && context.Target.GetObject(existing.AsReference).AsDictionary is { } found)
            return (found, existing.AsReference);

        var root = new PdfDictionary(4);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));

        PdfObjectId id = context.Target.AddObject(PdfValue.Dictionary(root));
        context.Target.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(id));
        context.Target.MarkChanged(context.Target.CatalogId);
        return (root, id);
    }

    private PdfArray CreateChildren(PdfDictionary root)
    {
        var children = new PdfArray();
        context.Target.Attach(PdfValue.Array(children));
        root.Set(PdfNames.K, PdfValue.Array(children));
        return children;
    }

    private PdfDictionary CreateRoleMap(PdfDictionary root)
    {
        var map = new PdfDictionary(4);
        context.Target.Attach(PdfValue.Dictionary(map));
        root.Set(PdfNames.RoleMap, PdfValue.Dictionary(map));
        return map;
    }
}
