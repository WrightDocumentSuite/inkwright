using Inkwright.Cos;

namespace Inkwright;

/// <summary>
/// Deep-copies object graphs between documents. Every source object is copied at most once, so
/// pages that share a font or an image still share it after the import, and reference cycles are
/// handled by reserving the destination number before the body is copied.
/// </summary>
internal sealed class PdfObjectImporter(PdfDocument source, PdfDocument target)
{
    private const int MaxDepth = 96;

    private readonly Dictionary<int, PdfObjectId> _map = [];
    private readonly HashSet<int> _sourcePages = Number(source);

    /// <summary>
    /// Reserves a place for every page that is about to be imported, so that a link from one of them
    /// to another lands on the copy rather than on nothing.
    /// </summary>
    /// <param name="pages">The pages the caller is going to import.</param>
    internal void Reserve(IEnumerable<PdfPage> pages)
    {
        foreach (PdfPage page in pages)
        {
            if (page.Id.IsValid && !_map.ContainsKey(page.Id.Number))
                _map[page.Id.Number] = target.AddObject(PdfValue.Null);
        }
    }

    /// <summary>Copies a page, cutting the link to its old parent so the source tree is not dragged along.</summary>
    /// <param name="page">The page to copy.</param>
    internal PdfPage ImportPage(PdfPage page)
    {
        bool reserved = _map.TryGetValue(page.Id.Number, out PdfObjectId existing);
        if (reserved && target.GetObject(existing).AsDictionary is { } already)
            return new PdfPage(target, existing, already);

        PdfObjectId id = reserved ? existing : target.AddObject(PdfValue.Null);
        if (page.Id.IsValid)
            _map[page.Id.Number] = id;

        var copy = new PdfDictionary(page.Dictionary.Count);
        foreach (KeyValuePair<PdfName, PdfValue> entry in page.Dictionary)
        {
            if (entry.Key == PdfNames.Parent)
                continue;

            copy.Set(entry.Key, CopyValue(entry.Value, 0));
        }

        copy.Set(PdfNames.Type, PdfValue.Name(PdfNames.Page));
        DropDeadDestinations(copy);
        target.SetObject(id, PdfValue.Dictionary(copy));
        return new PdfPage(target, id, copy);
    }

    private static HashSet<int> Number(PdfDocument document)
    {
        HashSet<int> numbers = new(document.Pages.Count);
        foreach (PdfPage page in document.Pages)
        {
            if (page.Id.IsValid)
                numbers.Add(page.Id.Number);
        }

        return numbers;
    }

    /// <summary>
    /// Removes the jumps that no longer have anywhere to land. A link to a page that was left behind
    /// would otherwise drag that page — and everything it draws — into the new document as an object
    /// nothing can reach.
    /// </summary>
    private static void DropDeadDestinations(PdfDictionary page)
    {
        if (page.GetArray(PdfNames.Annots) is not { } annotations)
            return;

        for (int i = 0; i < annotations.Count; i++)
        {
            if (annotations.GetDictionary(i) is not { } annotation)
                continue;

            if (IsDead(annotation.Get(PdfNames.Dest)))
                annotation.Remove(PdfNames.Dest);

            if (annotation.GetDictionary(PdfNames.A) is { } action && IsDead(action.Get(PdfName.Get("D"))))
                annotation.Remove(PdfNames.A);
        }

        static bool IsDead(PdfValue destination) =>
            destination.AsArray is { Count: > 0 } array && array[0].IsNull;
    }

    /// <summary>Copies an arbitrary value, following references into the source document.</summary>
    /// <param name="value">The value to copy.</param>
    internal PdfValue Import(PdfValue value) => CopyValue(value, 0);

    private PdfValue CopyValue(PdfValue value, int depth)
    {
        if (depth > MaxDepth)
            return PdfValue.Null;

        switch (value.Kind)
        {
            case PdfValueKind.Reference:
            {
                int number = value.AsReference.Number;
                if (_map.TryGetValue(number, out PdfObjectId mapped))
                    return PdfValue.Reference(mapped);

                // A reference to a page nobody asked for — a link destination, usually. Following it
                // would copy that page, its content and its resources into the new document.
                if (_sourcePages.Contains(number))
                    return PdfValue.Null;

                PdfObjectId id = target.AddObject(PdfValue.Null);
                _map[number] = id;
                target.SetObject(id, CopyValue(source.GetObject(number), depth + 1));
                return PdfValue.Reference(id);
            }

            case PdfValueKind.Array:
            {
                PdfArray original = value.AsArray!;
                var copy = new PdfArray(original.Count);
                for (int i = 0; i < original.Count; i++)
                    copy.Add(CopyValue(original[i], depth + 1));

                return PdfValue.Array(copy);
            }

            case PdfValueKind.Dictionary:
                return PdfValue.Dictionary(CopyDictionary(value.AsDictionary!, depth));

            case PdfValueKind.Stream:
            {
                PdfStream original = value.AsStream!;
                var stream = new PdfStream(CopyDictionary(original.Dictionary, depth), original.RawData);
                return PdfValue.Stream(stream);
            }

            default:
                return value;
        }
    }

    private PdfDictionary CopyDictionary(PdfDictionary original, int depth)
    {
        var copy = new PdfDictionary(original.Count);
        foreach (KeyValuePair<PdfName, PdfValue> entry in original)
            copy.Set(entry.Key, CopyValue(entry.Value, depth + 1));

        return copy;
    }
}
