using System.Globalization;
using Inkwright.Cos;

namespace Inkwright.Text;

/// <summary>
/// One glyph as it was painted: the text it stands for, where it landed and how big it was. Working
/// from letters rather than from a flat string is what makes columns, tables and reading order
/// recoverable later.
/// </summary>
/// <param name="Text">The characters the glyph stands for, usually one.</param>
/// <param name="Origin">The glyph origin on the baseline, in page space.</param>
/// <param name="Width">The advance width in page space.</param>
/// <param name="Height">The nominal height, taken from the font size after transformation.</param>
/// <param name="FontSize">The effective font size in points.</param>
/// <param name="FontName">The base font name the glyph came from.</param>
/// <param name="Angle">The baseline direction in degrees, zero for ordinary horizontal text.</param>
public readonly record struct PdfLetter(
    string Text,
    PdfPoint Origin,
    double Width,
    double Height,
    double FontSize,
    string FontName,
    double Angle)
{
    /// <summary>
    /// The marked-content sequence the glyph was painted inside, or <see langword="null"/> when it
    /// was painted outside any. This is what ties a glyph to the structure tree, and so to the
    /// order the page is meant to be read in.
    /// </summary>
    public int? MarkedContentId { get; init; }

    /// <summary>
    /// The Form XObject or other non-page content stream in which the MCID was declared. No object
    /// means the MCID belongs to the page content stream.
    /// </summary>
    internal PdfObjectId MarkedContentStream { get; init; }

    /// <summary>Whether this item is an ISO 32000 <c>ActualText</c> replacement rather than a glyph.</summary>
    internal bool IsActualText { get; init; }

    /// <summary>The box the glyph occupies, from the baseline up.</summary>
    public PdfRectangle Bounds => PdfRectangle.FromSize(Origin.X, Origin.Y, Width, Height);

    /// <summary>The point the next glyph on the same baseline would start at.</summary>
    public PdfPoint End => new(Origin.X + Width, Origin.Y);

    /// <summary>Whether the glyph is whitespace.</summary>
    public bool IsWhiteSpace => Text.Length == 0 || Text.All(char.IsWhiteSpace);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"'{Text}' at {Origin} size {FontSize:0.##}");
}

/// <summary>A run of letters that belong together, in reading order.</summary>
/// <param name="Text">The text of the run.</param>
/// <param name="Bounds">The box the run occupies.</param>
/// <param name="Letters">The letters that make it up.</param>
public readonly record struct PdfWord(string Text, PdfRectangle Bounds, IReadOnlyList<PdfLetter> Letters)
{
    /// <inheritdoc />
    public override string ToString() => Text;
}
