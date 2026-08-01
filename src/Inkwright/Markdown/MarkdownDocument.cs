using System.Text;

namespace Inkwright.Markdown;

/// <summary>Objective counters describing how completely and safely an export was produced.</summary>
/// <param name="SourcePages">Pages present in the source PDF.</param>
/// <param name="SkippedPages">Pages skipped after a recoverable decode or parse failure.</param>
/// <param name="StructureElements">Structure elements present in the tagged source tree.</param>
/// <param name="MarkedContentReferences">Distinct page/stream/MCID references in that tree.</param>
/// <param name="ResolvedMarkedContentReferences">Distinct references that yielded text.</param>
/// <param name="Images">Sidecar images emitted after content-based deduplication.</param>
/// <param name="FailedImageResources">Distinct image resources that failed or exceeded a limit.</param>
/// <param name="Warnings">Deliberate approximations reported by the export.</param>
/// <param name="OutputCharacters">UTF-16 characters in the resulting Markdown.</param>
public readonly record struct MarkdownExportMetrics(
    int SourcePages,
    int SkippedPages,
    int StructureElements,
    int MarkedContentReferences,
    int ResolvedMarkedContentReferences,
    int Images,
    int FailedImageResources,
    int Warnings,
    int OutputCharacters)
{
    /// <summary>
    /// Fraction of distinct structure-tree marked-content references that yielded text. A document
    /// with no such references has complete coverage by definition.
    /// </summary>
    public double MarkedContentCoverage => MarkedContentReferences == 0
        ? 1
        : (double)ResolvedMarkedContentReferences / MarkedContentReferences;
}

/// <summary>One image file referenced by a Markdown export.</summary>
public sealed class MarkdownImage
{
    /// <summary>File name inside the media directory, for example <c>image1.png</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>The encoded image bytes.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>The MIME type of <see cref="Content"/>.</summary>
    public required string ContentType { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{FileName} ({Content.Length} bytes)";
}

/// <summary>
/// A Markdown document and the sidecar images it references. Rendering performs no I/O;
/// <see cref="SaveAsync"/> is the optional filesystem layer.
/// </summary>
public sealed class MarkdownDocument
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The file name used by <see cref="SaveAsync"/>.</summary>
    public const string DefaultFileName = "document.md";

    internal MarkdownDocument(
        string text,
        IReadOnlyList<MarkdownImage> images,
        string mediaDirectoryName,
        MarkdownExportDiagnostics diagnostics,
        MarkdownExportMetrics metrics)
    {
        Text = text.Replace('\0', '\uFFFD');
        Images = [.. images];
        MediaDirectoryName = mediaDirectoryName;
        Diagnostics = diagnostics;
        Metrics = metrics with { OutputCharacters = Text.Length, Warnings = diagnostics.Count };
    }

    /// <summary>The Markdown text, with LF line endings and exactly one trailing LF.</summary>
    public string Text { get; }

    /// <summary>Images in first-reference order.</summary>
    public IReadOnlyList<MarkdownImage> Images { get; }

    /// <summary>The normalized relative directory used by image links.</summary>
    public string MediaDirectoryName { get; }

    /// <summary>Every deliberate approximation made while rendering.</summary>
    public MarkdownExportDiagnostics Diagnostics { get; }

    /// <summary>Coverage and loss counters for automated correctness monitoring.</summary>
    public MarkdownExportMetrics Metrics { get; }

    /// <summary>
    /// Writes <see cref="DefaultFileName"/> and referenced images into a directory. Owned files
    /// are overwritten; unrelated and stale files are left untouched. Cancellation is not
    /// transactional and may leave files already written.
    /// </summary>
    public async ValueTask SaveAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(root);

        string markdownPath = Path.Combine(root, DefaultFileName);
        MarkdownPath.EnsureNoReparsePoints(root, markdownPath);
        await File.WriteAllTextAsync(
                markdownPath, Text, Utf8WithoutBom, cancellationToken)
            .ConfigureAwait(false);

        if (Images.Count == 0)
            return;

        string media = MarkdownPath.ResolveInside(root, MediaDirectoryName);
        MarkdownPath.EnsureNoReparsePoints(root, media);
        Directory.CreateDirectory(media);
        MarkdownPath.EnsureNoReparsePoints(root, media);
        foreach (MarkdownImage image in Images)
        {
            string path = MarkdownPath.ResolveInside(media, image.FileName);
            MarkdownPath.EnsureNoReparsePoints(media, path);
            await File.WriteAllBytesAsync(path, image.Content, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Text;
}

internal static class MarkdownPath
{
    private static readonly char[] PortableInvalidFileNameChars =
        ['\0', '<', '>', ':', '"', '|', '?', '*'];

    public static string NormalizeMediaDirectoryName(string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string path = value.Replace('\\', '/');

        if (Path.IsPathRooted(value) || path.StartsWith('/') || path.EndsWith('/') ||
            (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            throw new ArgumentException("The Markdown media directory must be a relative path.", nameof(value));
        }

        string[] segments = path.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." ||
                segment.IndexOfAny(PortableInvalidFileNameChars) >= 0 ||
                segment.Any(char.IsControl) || segment.EndsWith(' ') || segment.EndsWith('.') ||
                IsWindowsDeviceName(segment))
            {
                throw new ArgumentException(
                    "The Markdown media directory contains an unsafe path segment.", nameof(value));
            }
        }

        return string.Join('/', segments);
    }

    public static string ResolveInside(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root);
        string platformRelative = relative.Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, platformRelative));
        string prefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, PathComparison))
            throw new ArgumentException("The Markdown export path leaves its destination directory.", nameof(relative));

        return candidate;
    }

    /// <summary>
    /// Rejects existing symbolic links, junctions and other reparse points between a trusted root
    /// and a generated path. Lexical containment alone cannot prevent an existing link from
    /// redirecting a sidecar write outside the destination.
    /// </summary>
    public static void EnsureNoReparsePoints(string root, string candidate)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullCandidate = Path.GetFullPath(candidate);
        string relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
            throw new ArgumentException("The Markdown export path leaves its destination directory.", nameof(candidate));

        string current = fullRoot;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0 || segment == ".")
                continue;

            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"The Markdown export path contains a reparse point: '{current}'.");
            }
        }
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
