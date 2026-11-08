namespace Inkwright;

/// <summary>
/// Controls how physical content redaction removes underlying page objects and draws the
/// replacement overlay (ISO 32000-1 §12.5.6.23).
/// </summary>
public sealed class PdfRedactionOptions
{
    /// <summary>The default redaction settings: opaque black fill, images and annotations removed.</summary>
    public static PdfRedactionOptions Default { get; } = new();

    /// <summary>
    /// The colour painted over each redacted rectangle after the underlying content has been
    /// removed, or <see langword="null"/> to remove the content without painting an opaque box.
    /// </summary>
    public PdfColor? FillColor { get; init; } = PdfColor.Black;

    /// <summary>
    /// Optional replacement label drawn inside each redacted rectangle (such as <c>REDACTED</c>).
    /// </summary>
    public string? OverlayText { get; init; }

    /// <summary>The colour used when drawing <see cref="OverlayText"/>.</summary>
    public PdfColor OverlayTextColor { get; init; } = PdfColor.White;

    /// <summary>
    /// The font size in points used for <see cref="OverlayText"/>, or <see langword="null"/> to
    /// scale the label automatically to fit each redacted rectangle.
    /// </summary>
    public double? OverlayFontSize { get; init; }

    /// <summary>
    /// Whether image XObjects and inline images intersecting a redaction rectangle are stripped
    /// from the content stream.
    /// </summary>
    public bool RemoveImages { get; init; } = true;

    /// <summary>
    /// Whether interactive annotations intersecting a redaction rectangle are removed from the page.
    /// </summary>
    public bool RemoveAnnotations { get; init; } = true;
}

/// <summary>Summarises what a physical redaction pass removed from one or more pages.</summary>
/// <param name="RegionsApplied">How many redaction rectangles were applied.</param>
/// <param name="GlyphsRemoved">How many painted character codes were stripped from content streams.</param>
/// <param name="ImagesRemoved">How many image invocations were stripped from content streams.</param>
/// <param name="AnnotationsRemoved">How many annotations were removed from the page.</param>
public readonly record struct PdfRedactionResult(
    int RegionsApplied,
    int GlyphsRemoved,
    int ImagesRemoved,
    int AnnotationsRemoved)
{
    /// <summary>Whether the redaction pass modified any content or annotations.</summary>
    public bool HasChanges =>
        RegionsApplied > 0 || GlyphsRemoved > 0 || ImagesRemoved > 0 || AnnotationsRemoved > 0;

    /// <summary>Combines the counts of two redaction results.</summary>
    /// <param name="other">The result to add to this one.</param>
    public PdfRedactionResult Combine(PdfRedactionResult other) => new(
        RegionsApplied + other.RegionsApplied,
        GlyphsRemoved + other.GlyphsRemoved,
        ImagesRemoved + other.ImagesRemoved,
        AnnotationsRemoved + other.AnnotationsRemoved);
}
