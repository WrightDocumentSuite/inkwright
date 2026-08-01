using Inkwright.Cos;

namespace Inkwright.Merging;

/// <summary>
/// Brings across the names other documents use to link into this one. Two files being merged may
/// well both have a destination called <c>chapter1</c>, so the incoming ones are renamed where they
/// clash and every link that used the old name is pointed at the new one.
/// </summary>
internal sealed class DestinationImporter(MergeContext context)
{
    private readonly Dictionary<string, string> _renamed = new(StringComparer.Ordinal);

    /// <summary>The names the source used, mapped to the names they ended up under.</summary>
    internal IReadOnlyDictionary<string, string> Renamed => _renamed;

    /// <summary>Copies the named destinations whose pages came across, then repoints the links.</summary>
    internal void Merge()
    {
        Copy();
        Repoint();
    }

    private void Copy()
    {
        List<(string Name, PdfArray Destination)> incoming = [];

        foreach ((string name, PdfValue value) in Read(context.Source))
        {
            if (context.Retarget(Resolve(value)) is { } destination)
                incoming.Add((name, destination));
        }

        if (incoming.Count == 0)
            return;

        List<(string Key, PdfValue Value)> entries = [.. Existing()];
        var taken = new HashSet<string>(entries.Select(entry => entry.Key), StringComparer.Ordinal);

        foreach ((string name, PdfArray destination) in incoming)
        {
            string unique = context.UniqueName(name, taken);
            taken.Add(unique);

            if (unique != name)
                _renamed[name] = unique;

            context.Target.Attach(PdfValue.Array(destination));
            entries.Add((unique, PdfValue.Array(destination)));
        }

        Write(entries);
    }

    /// <summary>The name a link should use now, which differs only where the merge had to rename it.</summary>
    /// <param name="name">The name the source document used.</param>
    internal string Resolve(string name) => _renamed.GetValueOrDefault(name, name);

    /// <summary>
    /// Points the links on the imported pages at whatever their destinations ended up being called.
    /// A link that names a destination the merge renamed would otherwise lead nowhere.
    /// </summary>
    private void Repoint()
    {
        if (_renamed.Count == 0)
            return;

        foreach ((_, PdfPage page) in context.Pages)
        {
            if (page.Dictionary.GetArray(PdfNames.Annots) is not { } annotations)
                continue;

            for (int i = 0; i < annotations.Count; i++)
            {
                if (annotations.GetDictionary(i) is not { } annotation)
                    continue;

                Rename(annotation, PdfNames.Dest);

                if (annotation.GetDictionary(PdfNames.A) is { } action)
                    Rename(action, PdfName.Get("D"));
            }
        }

        void Rename(PdfDictionary holder, PdfName key)
        {
            PdfValue value = holder.Get(key);

            // Only a destination named by string can have been renamed; an array names its page
            // directly and was already retargeted when the page was copied.
            if (value.Kind is PdfValueKind.String && value.AsTextString is { } name &&
                _renamed.TryGetValue(name, out string? renamed))
            {
                holder.Set(key, PdfValue.TextString(renamed));
            }
            else if (value.AsName is { } named && _renamed.TryGetValue(named.Value, out string? byName))
            {
                holder.Set(key, PdfValue.TextString(byName));
            }
        }
    }

    /// <summary>
    /// The destinations a document declares. Both places the specification allows are read: the
    /// name tree of §12.3.2.3 and the older dictionary under the catalog's <c>Dests</c> entry.
    /// </summary>
    private static IEnumerable<(string Name, PdfValue Value)> Read(PdfDocument document)
    {
        PdfDictionary? tree = document.Catalog.GetDictionary(PdfNames.Names)?.GetDictionary(PdfNames.Dests);
        foreach ((string name, PdfValue value) in PdfNameTree.Enumerate(tree))
            yield return (name, value);

        if (document.Catalog.GetDictionary(PdfNames.Dests) is not { } dictionary)
            yield break;

        foreach (KeyValuePair<PdfName, PdfValue> entry in dictionary)
            yield return (entry.Key.Value, dictionary.Get(entry.Key));
    }

    /// <summary>
    /// The array a destination entry holds. A destination may be wrapped in a dictionary with a
    /// <c>D</c> entry, which is how one that also carries an action is written.
    /// </summary>
    private static PdfArray? Resolve(PdfValue value) =>
        value.AsArray ?? value.AsDictionary?.GetArray(PdfName.Get("D"));

    private IEnumerable<(string Key, PdfValue Value)> Existing()
    {
        PdfDictionary? tree = context.Target.Catalog.GetDictionary(PdfNames.Names)?.GetDictionary(PdfNames.Dests);
        return PdfNameTree.Enumerate(tree);
    }

    private void Write(List<(string Key, PdfValue Value)> entries)
    {
        PdfDictionary tree = PdfNameTree.Build(entries);
        context.Target.Attach(PdfValue.Dictionary(tree));

        PdfDictionary names = context.Target.Catalog.GetDictionary(PdfNames.Names) ?? CreateNames();
        names.Set(PdfNames.Dests, PdfValue.Dictionary(tree));
        context.Target.MarkChanged(context.Target.CatalogId);

        PdfDictionary CreateNames()
        {
            var created = new PdfDictionary(1);
            context.Target.Attach(PdfValue.Dictionary(created));
            context.Target.Catalog.Set(PdfNames.Names, PdfValue.Dictionary(created));
            return created;
        }
    }
}
