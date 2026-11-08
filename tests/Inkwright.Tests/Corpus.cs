namespace Inkwright.Tests;

/// <summary>
/// Locates the thousands of real PDF files that ship with the frameworks sitting next to this
/// repository. They are the honest test set: hand-written fixtures only prove that the library
/// agrees with itself.
/// </summary>
public static class Corpus
{
    private const string CorpusEnvironmentVariable = "INKWRIGHT_CORPUS";
    private const string RequireReferenceEnvironmentVariable = "INKWRIGHT_REQUIRE_REFERENCE_CORPUS";
    private const int ReferenceFileCount = 7_807;
    private const long ReferenceByteCount = 918_216_253;
    private const string ReferenceTreeSha256 =
        "9564a7a968078c1565b82a199952384f9d2dee83be93bf43735445860261824f";
    private static readonly string[] ReferenceRootNames =
    [
        "PdfPig-0.1.15",
        "itext-dotnet-9.7.0",
        "pdf.js-6.1.200",
        "XMP-Toolkit-SDK-main",
    ];
    private static readonly Lazy<CorpusFile[]> AllCorpusFiles = new(
        Discover,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string[]> AllFiles = new(
        () => AllCorpusFiles.Value.Select(file => file.PhysicalPath).ToArray(),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<long> AllBytes = new(
        () => AllCorpusFiles.Value.Sum(file => new FileInfo(file.PhysicalPath).Length),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<CorpusIdentityResult> Identity = new(
        () => CorpusIdentity.Compute(AllCorpusFiles.Value),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly string? ConfiguredPath =
        Environment.GetEnvironmentVariable(CorpusEnvironmentVariable);
    private static readonly string? ConfiguredRoot = FindConfiguredRoot(ConfiguredPath);

    /// <summary>The root that holds the reference frameworks and their resources.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>
    /// How to open a file nobody chose: a sweep over thousands of documents meets locked ones, and a
    /// scanner wants to look at the structure and move on rather than stop for a password it has no
    /// way of knowing. The compatibility sweep deliberately disables request limits: its fixtures
    /// include thousands of incremental revisions and intentionally impossible xref declarations,
    /// and its job is to measure legacy recovery rather than recommend a production trust policy.
    /// </summary>
    public static IO.PdfReadOptions ReadOptions { get; } = new()
    {
        OpenLockedDocuments = true,
        ResourceLimits = null,
    };

    /// <summary>
    /// How to open the representative production-policy sample. Unlike <see cref="ReadOptions"/>,
    /// this keeps the default document-local resource ledger enabled.
    /// </summary>
    public static IO.PdfReadOptions BoundedReadOptions { get; } = new()
    {
        OpenLockedDocuments = true,
    };

    /// <summary>Whether the corpus is present on this machine.</summary>
    public static bool IsAvailable => AllFiles.Value.Length > 0;

    /// <summary>
    /// Whether the discovered files are the exact published reference snapshot. Fixed metric
    /// assertions are meaningful only for this identity, not merely for another 7,807-file tree.
    /// </summary>
    public static bool IsReferenceInventory =>
        AllCorpusFiles.Value.Length == ReferenceFileCount &&
        AllBytes.Value == ReferenceByteCount &&
        string.Equals(Identity.Value.Sha256, ReferenceTreeSha256, StringComparison.Ordinal);

    /// <summary>
    /// Skips the calling test when the corpus is not on this machine. The files are not part of the
    /// repository — they come from reference frameworks unpacked beside it — so a clone has none of
    /// them, and a test suite that failed for everyone but its author would be worth nothing.
    /// </summary>
    public static void Require()
    {
        string unavailableReason = !string.IsNullOrWhiteSpace(ConfiguredPath)
            ? $"{CorpusEnvironmentVariable} does not name an existing readable directory: " +
              $"'{ConfiguredPath}'. Point it at the exact corpus root."
            : "No PDF corpus was found. These checks read the thousands of real files that ship " +
              "with PdfPig, iText, pdf.js, and the XMP Toolkit; unpack those beside the " +
              "repository or set " +
              $"{CorpusEnvironmentVariable} to the exact corpus root.";
        Xunit.Assert.SkipUnless(
            IsAvailable,
            unavailableReason);

        if (Environment.GetEnvironmentVariable(RequireReferenceEnvironmentVariable) == "1")
        {
            Xunit.Assert.True(
                IsReferenceInventory,
                $"{RequireReferenceEnvironmentVariable}=1 requires the published reference inventory " +
                $"with tree SHA-256 {ReferenceTreeSha256}; discovered {Identity.Value.FileCount:N0} " +
                $"files, {Identity.Value.TotalBytes:N0} bytes, and tree SHA-256 " +
                $"{Identity.Value.Sha256}. The published inventory contains " +
                $"{ReferenceFileCount:N0} files and {ReferenceByteCount:N0} bytes.");
        }
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
        if (!string.IsNullOrWhiteSpace(ConfiguredPath))
            return ConfiguredRoot ?? string.Empty;

        string? directory = AppContext.BaseDirectory;

        while (directory is not null)
        {
            if (ReferenceRootNames.Any(name => Directory.Exists(Path.Combine(directory, name))))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        return string.Empty;
    }

    private static string? FindConfiguredRoot(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        try
        {
            string fullPath = Path.GetFullPath(configured);
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static CorpusFile[] Discover()
    {
        if (string.IsNullOrEmpty(Root) || !Directory.Exists(Root))
            return [];

        var files = new List<CorpusFile>(8000);
        string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Root));
        foreach (string name in ReferenceRootNames)
        {
            string directory = string.Equals(rootName, name, StringComparison.OrdinalIgnoreCase)
                ? Root
                : Path.Combine(Root, name);
            if (!Directory.Exists(directory))
                continue;

            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                    continue;

                string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                string logicalPath = (name + '/' + relative).Normalize(System.Text.NormalizationForm.FormC);
                files.Add(new CorpusFile(logicalPath, path));
            }
        }

        files.Sort((left, right) =>
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.PhysicalPath, right.PhysicalPath);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.PhysicalPath, right.PhysicalPath);
        });
        return [.. files];
    }
}
