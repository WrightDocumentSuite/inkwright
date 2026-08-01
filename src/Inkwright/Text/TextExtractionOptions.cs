namespace Inkwright.Text;

/// <summary>
/// What to do with the glyphs a page paints beyond simply reading them: whether to put
/// right-to-left lines back into logical order, and whether to believe the page's optional content
/// groups about what is actually visible.
/// </summary>
public sealed record TextExtractionOptions
{
    /// <summary>The settings used when a caller asks for none: everything left as the page paints it.</summary>
    public static TextExtractionOptions Default { get; } = new();

    /// <summary>
    /// Whether to run the bidirectional algorithm over each composed line. A PDF paints Arabic and
    /// Hebrew in the order the glyphs sit on the page, so without this the words come out reversed.
    /// </summary>
    public bool ResolveBidirectional { get; init; }

    /// <summary>
    /// Whether to skip content in optional content groups that the document's default configuration
    /// turns off. Hidden layers hold things like watermark alternatives and translations, which are
    /// painted but not meant to be read.
    /// </summary>
    public bool SkipHiddenLayers { get; init; }

    /// <summary>
    /// Whether to return glyphs in the order the document's structure tree declares rather than the
    /// order they were painted. In a tagged document this is what gets two columns, a sidebar or a
    /// table back out in the order a person reads them; in an untagged one it changes nothing.
    /// </summary>
    public bool UseLogicalOrder { get; init; }

    /// <summary>
    /// Whether <c>ActualText</c> on marked-content sequences replaces the glyphs enclosed by that
    /// sequence. Replacement text remains attached to the enclosing MCID.
    /// </summary>
    public bool UseActualText { get; init; }
}
