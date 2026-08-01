using Inkwright.Cos;

namespace Inkwright.Merging;

/// <summary>
/// Brings the interactive form across. §12.7.3.2 requires every fully qualified field name in a
/// document to be unique, and two forms being merged are almost certain to both call something
/// <c>Name</c>; the incoming field is renamed and its widgets follow it, so filling one form no
/// longer silently fills the other.
/// </summary>
internal sealed class FormImporter(MergeContext context)
{
    /// <summary>How deep a field tree may nest before it is taken to be looping.</summary>
    private const int MaxDepth = 32;

    /// <summary>Copies the fields whose widgets landed on an imported page.</summary>
    internal void Merge()
    {
        if (context.Source.Catalog.GetDictionary(PdfNames.AcroForm) is not { } source)
            return;

        if (source.GetArray(PdfNames.Fields) is not { Count: > 0 } incoming)
            return;

        PdfDictionary form = Target();
        PdfArray fields = form.GetArray(PdfNames.Fields) ?? Create(form);
        var taken = new HashSet<string>(Names(fields, string.Empty, 0), StringComparer.Ordinal);

        for (int i = 0; i < incoming.Count; i++)
        {
            if (incoming.Get(i) is not { AsDictionary: { } field })
                continue;

            string name = field.GetTextString(PdfNames.T) ?? string.Empty;
            PdfValue copied = context.Importer.Import(incoming[i]);

            if (copied.IsReference && context.Target.GetObject(copied.AsReference).AsDictionary is { } copy)
            {
                copy.Remove(PdfNames.Parent);

                string unique = context.UniqueName(name, taken);
                taken.Add(unique);

                if (unique != name && unique.Length > 0)
                    copy.Set(PdfNames.T, PdfValue.TextString(unique));
            }

            fields.Add(copied);
        }

        Adopt(form, source);
        context.Target.MarkChanged(context.Target.CatalogId);
    }

    /// <summary>
    /// Takes over the settings a form carries beside its fields: the resources its appearance
    /// strings name, the default appearance, the quadding, and the order calculations run in. What
    /// the target already states wins, because it was there first.
    /// </summary>
    private void Adopt(PdfDictionary form, PdfDictionary source)
    {
        if (form.GetDictionary(PdfNames.DR) is { } resources && source.GetDictionary(PdfNames.DR) is { } incoming)
        {
            foreach (KeyValuePair<PdfName, PdfValue> category in incoming)
                MergeCategory(resources, category.Key, incoming.GetDictionary(category.Key));
        }
        else if (source[PdfNames.DR] is { IsNull: false } only)
            form.Set(PdfNames.DR, context.Importer.Import(only));

        foreach (PdfName key in (PdfName[])[PdfNames.DA, PdfNames.Q, PdfName.Get("NeedAppearances")])
        {
            if (form[key].IsNull && !source[key].IsNull)
                form.Set(key, context.Importer.Import(source[key]));
        }

        if (source.GetArray(PdfName.Get("CO")) is not { Count: > 0 } order)
            return;

        PdfArray target = form.GetArray(PdfName.Get("CO")) ?? CreateOrder(form);
        for (int i = 0; i < order.Count; i++)
            target.Add(context.Importer.Import(order[i]));
    }

    /// <summary>Folds one resource category, such as the fonts, into the target's own.</summary>
    private void MergeCategory(PdfDictionary resources, PdfName category, PdfDictionary? incoming)
    {
        if (incoming is null)
            return;

        PdfDictionary? existing = resources.GetDictionary(category);
        if (existing is null)
        {
            resources.Set(category, context.Importer.Import(PdfValue.Dictionary(incoming)));
            return;
        }

        // A name the target already uses keeps whatever it already meant; anything else would
        // repaint the target's own fields in the source's fonts.
        foreach (KeyValuePair<PdfName, PdfValue> entry in incoming)
        {
            if (existing[entry.Key].IsNull)
                existing.Set(entry.Key, context.Importer.Import(entry.Value));
        }
    }

    /// <summary>The fully qualified names a field tree already uses.</summary>
    private static IEnumerable<string> Names(PdfArray fields, string prefix, int depth)
    {
        if (depth > MaxDepth)
            yield break;

        for (int i = 0; i < fields.Count; i++)
        {
            if (fields.GetDictionary(i) is not { } field)
                continue;

            string partial = field.GetTextString(PdfNames.T) ?? string.Empty;
            string full = partial.Length == 0 ? prefix : prefix.Length == 0 ? partial : $"{prefix}.{partial}";

            if (full.Length > 0)
                yield return full;

            if (field.GetArray(PdfNames.Kids) is { } kids)
            {
                foreach (string name in Names(kids, full, depth + 1))
                    yield return name;
            }
        }
    }

    private PdfDictionary Target()
    {
        if (context.Target.Catalog.GetDictionary(PdfNames.AcroForm) is { } existing)
            return existing;

        var form = new PdfDictionary(4);
        context.Target.Attach(PdfValue.Dictionary(form));
        context.Target.Catalog.Set(PdfNames.AcroForm, PdfValue.Dictionary(form));
        return form;
    }

    private PdfArray Create(PdfDictionary form)
    {
        var fields = new PdfArray();
        context.Target.Attach(PdfValue.Array(fields));
        form.Set(PdfNames.Fields, PdfValue.Array(fields));
        return fields;
    }

    private PdfArray CreateOrder(PdfDictionary form)
    {
        var order = new PdfArray();
        context.Target.Attach(PdfValue.Array(order));
        form.Set(PdfName.Get("CO"), PdfValue.Array(order));
        return order;
    }
}
