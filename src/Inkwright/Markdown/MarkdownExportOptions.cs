namespace Inkwright.Markdown;

/// <summary>The Markdown dialect an export is allowed to use.</summary>
public enum MarkdownFlavor : byte
{
    /// <summary>CommonMark plus GitHub Flavored Markdown extensions.</summary>
    GitHub = 0,

    /// <summary>CommonMark only; unsupported structures use generated HTML when possible.</summary>
    CommonMark,
}

/// <summary>How the exporter chooses the semantic source for the Markdown document.</summary>
public enum MarkdownStructureMode : byte
{
    /// <summary>Use Tagged PDF structure when present, otherwise fall back to extracted page text.</summary>
    PreferTagged = 0,

    /// <summary>Require Tagged PDF structure and reject an untagged document.</summary>
    RequireTagged,

    /// <summary>Ignore Tagged PDF structure and export extracted page text.</summary>
    PlainText,

    /// <summary>
    /// Use Tagged PDF structure when present; for an untagged document, infer columns, headings,
    /// lists and tables from positioned glyphs.
    /// </summary>
    InferLayout,
}

/// <summary>Controls how a PDF document is projected into Markdown.</summary>
public sealed record MarkdownExportOptions
{
    internal static readonly MarkdownExportOptions Default = new();

    /// <summary>The Markdown dialect to emit.</summary>
    public MarkdownFlavor Flavor { get; init; } = MarkdownFlavor.GitHub;

    /// <summary>The semantic source used for block structure and reading order.</summary>
    public MarkdownStructureMode StructureMode { get; init; } = MarkdownStructureMode.PreferTagged;

    /// <summary>Whether referenced raster images are returned as sidecar files.</summary>
    public bool IncludeImages { get; init; } = true;

    /// <summary>Whether content hidden by the document's default optional-content configuration is included.</summary>
    public bool IncludeHiddenLayers { get; init; }

    /// <summary>
    /// Relative directory the generated image links point into. Rooted paths and parent traversal
    /// segments are rejected.
    /// </summary>
    public string MediaDirectoryName { get; init; } = "media";

    /// <summary>Maximum number of source pages accepted by one export.</summary>
    public int MaximumPages { get; init; } = 10_000;

    /// <summary>Maximum number of structure elements accepted by one tagged export.</summary>
    public int MaximumStructureElements { get; init; } = 1_000_000;

    /// <summary>Maximum number of UTF-16 characters in the generated Markdown.</summary>
    public int MaximumOutputCharacters { get; init; } = 64 * 1024 * 1024;

    /// <summary>Maximum number of decoded sidecar images retained by one export.</summary>
    public int MaximumImages { get; init; } = 10_000;

    /// <summary>Maximum total encoded bytes retained for sidecar images.</summary>
    public long MaximumTotalImageBytes { get; init; } = 512L * 1024 * 1024;
}
