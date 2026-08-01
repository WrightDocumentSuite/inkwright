namespace Inkwright.Tests;

/// <summary>
/// Locates the thousands of real PDF files that ship with the frameworks sitting next to this
/// repository. They are the honest test set: hand-written fixtures only prove that the library
/// agrees with itself.
/// </summary>
public static class Corpus
{
    private static readonly Lazy<string[]> AllFiles = new(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The root that holds the reference frameworks and their resources.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>
    /// How to open a file nobody chose: a sweep over thousands of documents meets locked ones, and a
    /// scanner wants to look at the structure and move on rather than stop for a password it has no
    /// way of knowing.
    /// </summary>
    public static IO.PdfReadOptions ReadOptions { get; } = new() { OpenLockedDocuments = true };

    /// <summary>Whether the corpus is present on this machine.</summary>
    public static bool IsAvailable => AllFiles.Value.Length > 0;

    /// <summary>
    /// Skips the calling test when the corpus is not on this machine. The files are not part of the
    /// repository — they come from reference frameworks unpacked beside it — so a clone has none of
    /// them, and a test suite that failed for everyone but its author would be worth nothing.
    /// </summary>
    public static void Require()
    {
        Xunit.Assert.SkipUnless(
            IsAvailable,
            "No PDF corpus was found. These checks read the thousands of real files that ship with " +
            "PdfPig and iText; unpack those beside the repository to run them.");
    }

    /// <summary>Every PDF file found under the root, in a stable order.</summary>
    public static IReadOnlyList<string> Files => AllFiles.Value;

    /// <summary>A deterministic sample of the corpus, spread evenly across the whole set.</summary>
    /// <param name="count">How many files to take.</param>
    public static IReadOnlyList<string> Sample(int count)
    {
        string[] files = AllFiles.Value;
        if (files.Length <= count)
            return files;

        var sample = new List<string>(count);
        double step = (double)files.Length / count;
        for (int i = 0; i < count; i++)
            sample.Add(files[(int)(i * step)]);

        return sample;
    }

    /// <summary>Files whose names hint at a particular feature, for targeted round-trip checks.</summary>
    /// <param name="keyword">A case-insensitive substring of the file name.</param>
    /// <param name="limit">The most files to return.</param>
    public static IReadOnlyList<string> Matching(string keyword, int limit = 50)
    {
        List<string> matches = [];
        foreach (string file in AllFiles.Value)
        {
            if (Path.GetFileName(file).Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(file);
                if (matches.Count >= limit)
                    break;
            }
        }

        return matches;
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory, "PdfPig-0.1.15")) ||
                Directory.Exists(Path.Combine(directory, "itext-dotnet-9.7.0")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        return string.Empty;
    }

    private static string[] Discover()
    {
        if (string.IsNullOrEmpty(Root) || !Directory.Exists(Root))
            return [];

        var files = new List<string>(8000);

        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            // Skip our own output so generated files never leak into the corpus.
            string name = Path.GetFileName(directory);
            if (name is "Inkwright" or "artifacts" or ".git")
                continue;

            files.AddRange(Directory.EnumerateFiles(directory, "*.pdf", SearchOption.AllDirectories));
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. files];
    }
}
