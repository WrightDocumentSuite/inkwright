namespace Inkwright.Cos;

/// <summary>
/// Reads the number trees of ISO 32000-1 §7.9.7, which map integer keys to values through a
/// balanced tree of <c>Kids</c> whose leaves hold sorted <c>Nums</c> arrays. Small documents write
/// one leaf and nothing else, but a large one splits the tree, and a reader that only looks at the
/// root sees nothing at all.
/// </summary>
public static class PdfNumberTree
{
    /// <summary>How deep a tree may nest before it is taken to be looping.</summary>
    private const int MaxDepth = 32;

    /// <summary>Walks every entry of a tree in key order.</summary>
    /// <param name="root">The tree root, or <see langword="null"/> for an absent tree.</param>
    public static IEnumerable<(int Key, PdfValue Value)> Enumerate(PdfDictionary? root)
    {
        if (root is null)
            yield break;

        foreach ((int key, PdfValue value) in Walk(root, 0))
            yield return (key, value);
    }

    /// <summary>
    /// Finds the value covering a key: the one stored at the key itself, or failing that the one at
    /// the greatest key below it. Page labels and similar trees state a value once and let it stand
    /// until the next entry supersedes it.
    /// </summary>
    /// <param name="root">The tree root.</param>
    /// <param name="key">The key to look up.</param>
    public static PdfValue FindCovering(PdfDictionary? root, int key)
    {
        PdfValue covering = PdfValue.Null;

        foreach ((int candidate, PdfValue value) in Enumerate(root))
        {
            if (candidate > key)
                break;

            covering = value;
        }

        return covering;
    }

    /// <summary>Builds a single-leaf tree from entries the caller has already sorted by key.</summary>
    /// <param name="entries">The entries, in key order.</param>
    public static PdfDictionary Build(IEnumerable<(int Key, PdfValue Value)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var numbers = new PdfArray();
        foreach ((int key, PdfValue value) in entries)
        {
            numbers.Add(PdfValue.Integer(key));
            numbers.Add(value);
        }

        var tree = new PdfDictionary(1);
        tree.Set(PdfNames.Nums, PdfValue.Array(numbers));
        return tree;
    }

    private static IEnumerable<(int Key, PdfValue Value)> Walk(PdfDictionary node, int depth)
    {
        if (depth > MaxDepth)
            yield break;

        if (node.GetArray(PdfNames.Nums) is { } numbers)
        {
            for (int i = 0; i + 1 < numbers.Count; i += 2)
                yield return ((int)numbers.GetInteger(i), numbers.Get(i + 1));
        }

        if (node.GetArray(PdfNames.Kids) is not { } kids)
            yield break;

        for (int i = 0; i < kids.Count; i++)
        {
            if (kids.GetDictionary(i) is not { } kid)
                continue;

            foreach ((int key, PdfValue value) in Walk(kid, depth + 1))
                yield return (key, value);
        }
    }
}
