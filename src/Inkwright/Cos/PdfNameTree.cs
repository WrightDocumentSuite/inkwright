namespace Inkwright.Cos;

/// <summary>
/// Reads the name trees of ISO 32000-1 §7.9.6: the same shape as a number tree, but keyed by
/// strings and holding its entries in a <c>Names</c> array. Named destinations, embedded files and
/// the JavaScript a document carries all live in one.
/// </summary>
public static class PdfNameTree
{
    /// <summary>How deep a tree may nest before it is taken to be looping.</summary>
    private const int MaxDepth = 32;

    /// <summary>Walks every entry of a tree in the order it is stored.</summary>
    /// <param name="root">The tree root, or <see langword="null"/> for an absent tree.</param>
    public static IEnumerable<(string Key, PdfValue Value)> Enumerate(PdfDictionary? root)
    {
        if (root is null)
            yield break;

        foreach ((string key, PdfValue value) in Walk(root, 0))
            yield return (key, value);
    }

    /// <summary>Finds the value stored under a name.</summary>
    /// <param name="root">The tree root.</param>
    /// <param name="key">The name to look up.</param>
    public static PdfValue Find(PdfDictionary? root, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach ((string candidate, PdfValue value) in Enumerate(root))
        {
            if (string.Equals(candidate, key, StringComparison.Ordinal))
                return value;
        }

        return PdfValue.Null;
    }

    /// <summary>Builds a single-leaf tree, sorting the entries as the specification requires.</summary>
    /// <param name="entries">The entries to store.</param>
    public static PdfDictionary Build(IEnumerable<(string Key, PdfValue Value)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var names = new PdfArray();
        foreach ((string key, PdfValue value) in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            names.Add(PdfValue.TextString(key));
            names.Add(value);
        }

        var tree = new PdfDictionary(1);
        tree.Set(PdfNames.Names, PdfValue.Array(names));
        return tree;
    }

    private static IEnumerable<(string Key, PdfValue Value)> Walk(PdfDictionary node, int depth)
    {
        if (depth > MaxDepth)
            yield break;

        if (node.GetArray(PdfNames.Names) is { } names)
        {
            for (int i = 0; i + 1 < names.Count; i += 2)
            {
                if (names.Get(i).AsTextString is { } key)
                    yield return (key, names.Get(i + 1));
            }
        }

        if (node.GetArray(PdfNames.Kids) is not { } kids)
            yield break;

        for (int i = 0; i < kids.Count; i++)
        {
            if (kids.GetDictionary(i) is not { } kid)
                continue;

            foreach ((string key, PdfValue value) in Walk(kid, depth + 1))
                yield return (key, value);
        }
    }
}
