namespace Inkwright.Text;

/// <summary>
/// How closely a search has to match what is printed. The defaults are what a reader expects from a
/// search box rather than what a byte comparison would give: accents, ligatures and words broken
/// across lines all stop getting in the way.
/// </summary>
public sealed record TextSearchOptions
{
    /// <summary>The settings a search uses when the caller names none.</summary>
    public static TextSearchOptions Default { get; } = new();

    /// <summary>Settings that match the printed characters exactly, bar letter case.</summary>
    public static TextSearchOptions Exact { get; } = new()
    {
        IgnoreDiacritics = false,
        ExpandLigatures = false,
        JoinHyphenatedLines = false,
    };

    /// <summary>How to compare the text; case-insensitive by default.</summary>
    public StringComparison Comparison { get; init; } = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Whether a letter matches regardless of the accents on it, so that <c>resume</c> finds
    /// <c>résumé</c>. A phrase that carries accents of its own is always matched with them, on the
    /// grounds that someone who typed them meant them.
    /// </summary>
    public bool IgnoreDiacritics { get; init; } = true;

    /// <summary>
    /// Whether typographic ligatures are read as the letters they stand for, so that <c>find</c>
    /// matches a printed <c>ﬁnd</c>.
    /// </summary>
    public bool ExpandLigatures { get; init; } = true;

    /// <summary>
    /// Whether a word split across two lines by a hyphen counts as one word, so that
    /// <c>hyphenated</c> matches <c>hyphen-</c> at the end of a line and <c>ated</c> at the start of
    /// the next.
    /// </summary>
    public bool JoinHyphenatedLines { get; init; } = true;

    /// <summary>Whether the phrase has to stand as a whole word rather than inside a longer one.</summary>
    public bool WholeWords { get; init; }
}
