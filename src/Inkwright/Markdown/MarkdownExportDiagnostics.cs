using System.Collections;

namespace Inkwright.Markdown;

/// <summary>What kind of deliberate compromise a PDF to Markdown export made.</summary>
public enum MarkdownExportWarningKind : byte
{
    /// <summary>Extracted page text was used because reliable semantic structure was unavailable.</summary>
    UntaggedFallback = 0,

    /// <summary>Content survived but its original structure was approximated.</summary>
    StructureApproximated,

    /// <summary>Content that could not be projected safely was omitted.</summary>
    ContentSkipped,

    /// <summary>Generated HTML was used for a construct Markdown cannot represent.</summary>
    HtmlFallbackUsed,

    /// <summary>A potentially executable hyperlink target was not emitted.</summary>
    UnsafeLinkSkipped,

    /// <summary>An image could not be decoded or associated with a semantic figure.</summary>
    ImageSkipped,
}

/// <summary>One deliberate compromise made by a Markdown export.</summary>
/// <param name="Kind">The kind of compromise.</param>
/// <param name="Message">A human-readable explanation.</param>
/// <param name="Subject">A stable feature or source name involved.</param>
public readonly record struct MarkdownExportWarning(
    MarkdownExportWarningKind Kind,
    string Message,
    string? Subject = null)
{
    /// <inheritdoc />
    public override string ToString() => Subject is null ? Message : $"{Message} ({Subject})";
}

/// <summary>
/// The compromises an export made, in first-occurrence order. Repeated warnings with the same
/// kind and subject are reported once.
/// </summary>
public sealed class MarkdownExportDiagnostics : IReadOnlyList<MarkdownExportWarning>
{
    private readonly List<MarkdownExportWarning> _warnings = [];
    private readonly HashSet<(MarkdownExportWarningKind Kind, string? Subject)> _seen = [];

    /// <inheritdoc />
    public int Count => _warnings.Count;

    /// <inheritdoc />
    public MarkdownExportWarning this[int index] => _warnings[index];

    /// <summary>Whether the export needed no workaround.</summary>
    public bool IsEmpty => _warnings.Count == 0;

    /// <inheritdoc />
    public IEnumerator<MarkdownExportWarning> GetEnumerator() => _warnings.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString() => string.Join(Environment.NewLine, _warnings);

    internal void Add(MarkdownExportWarningKind kind, string message, string? subject = null)
    {
        if (_seen.Add((kind, subject)))
            _warnings.Add(new MarkdownExportWarning(kind, message, subject));
    }
}
