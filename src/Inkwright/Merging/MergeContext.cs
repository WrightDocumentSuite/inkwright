using Inkwright.Cos;

namespace Inkwright.Merging;

/// <summary>
/// What every part of a merge needs to know: which documents are involved, the map of copied
/// objects, and which source pages became which target pages.
/// </summary>
internal sealed class MergeContext
{
    private readonly Dictionary<int, PdfPage> _pageByNumber = [];

    internal MergeContext(
        PdfDocument source,
        PdfDocument target,
        PdfObjectImporter importer,
        IReadOnlyList<(PdfPage From, PdfPage To)> pages)
    {
        Source = source;
        Target = target;
        Importer = importer;
        Pages = pages;

        foreach ((PdfPage from, PdfPage to) in pages)
        {
            if (from.Id.IsValid)
                _pageByNumber[from.Id.Number] = to;
        }
    }

    /// <summary>The document being merged in.</summary>
    internal PdfDocument Source { get; }

    /// <summary>The document being merged into.</summary>
    internal PdfDocument Target { get; }

    /// <summary>The importer that copied the pages, holding the map from source objects to target ones.</summary>
    internal PdfObjectImporter Importer { get; }

    /// <summary>The pages that came across, in order.</summary>
    internal IReadOnlyList<(PdfPage From, PdfPage To)> Pages { get; }

    /// <summary>Where a source page ended up, or <see langword="null"/> when it was left behind.</summary>
    /// <param name="page">A reference to, or dictionary of, a page in the source document.</param>
    internal PdfPage? Landed(PdfValue page) =>
        page.IsReference ? _pageByNumber.GetValueOrDefault(page.AsReference.Number) : null;

    /// <summary>
    /// Rewrites a destination so it points at the copy of the page it named. A destination is an
    /// array whose first element is the page; everything after it says where on the page to go, and
    /// is carried over untouched.
    /// </summary>
    /// <param name="destination">The destination array from the source document.</param>
    /// <returns>The rewritten array, or <see langword="null"/> when its page was left behind.</returns>
    internal PdfArray? Retarget(PdfArray? destination)
    {
        if (destination is not { Count: > 0 })
            return null;

        if (Landed(destination[0]) is not { } page)
            return null;

        var copy = new PdfArray(destination.Count);
        copy.Add(PdfValue.Reference(page.Id));

        for (int i = 1; i < destination.Count; i++)
            copy.Add(Importer.Import(destination[i]));

        return copy;
    }

    /// <summary>Finds a name that nothing already uses, by adding a number to it.</summary>
    /// <param name="name">The name the source used.</param>
    /// <param name="taken">The names already in the target.</param>
    internal string UniqueName(string name, IEnumerable<string> taken)
    {
        _ = this;

        var used = taken as ISet<string> ?? new HashSet<string>(taken, StringComparer.Ordinal);
        if (!used.Contains(name))
            return name;

        for (int suffix = 2; suffix < 10_000; suffix++)
        {
            string candidate = $"{name} ({suffix})";
            if (!used.Contains(candidate))
                return candidate;
        }

        return $"{name} ({Guid.NewGuid():N})";
    }
}
