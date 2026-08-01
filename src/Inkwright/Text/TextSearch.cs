using System.Text;

namespace Inkwright.Text;

/// <summary>Where a phrase was found on a page.</summary>
/// <param name="Text">The text that matched.</param>
/// <param name="Areas">The boxes the match covers, one per line it runs across.</param>
/// <param name="Letters">The glyphs that make it up.</param>
public readonly record struct PdfTextMatch(string Text, IReadOnlyList<PdfRectangle> Areas, IReadOnlyList<PdfLetter> Letters)
{
    /// <summary>The box containing the whole match.</summary>
    public PdfRectangle Bounds
    {
        get
        {
            PdfRectangle bounds = default;
            foreach (PdfRectangle area in Areas)
                bounds = bounds.IsEmpty ? area : bounds.Union(area);

            return bounds;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"'{Text}' at {Bounds}";
}

/// <summary>
/// Finds phrases among extracted glyphs and reports where they sit. Searching over letters rather
/// than over a flat string is what lets a match come back as boxes on the page, which is exactly
/// what a highlight needs.
/// </summary>
public static class TextSearch
{
    /// <summary>Finds every occurrence of a phrase.</summary>
    /// <param name="letters">The glyphs to search, in the order they were painted.</param>
    /// <param name="phrase">The text to look for.</param>
    /// <param name="options">How closely to match; the defaults look past accents and ligatures.</param>
    public static IReadOnlyList<PdfTextMatch> Find(
        IReadOnlyList<PdfLetter> letters,
        string phrase,
        TextSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(letters);
        ArgumentException.ThrowIfNullOrEmpty(phrase);

        options ??= TextSearchOptions.Default;

        List<PdfTextMatch> matches = [];
        if (letters.Count == 0)
            return matches;

        (string printed, int[] letterOf) = Flatten(letters);

        bool fold = options.IgnoreDiacritics && !TextNormalizer.HasMarks(phrase);
        (string haystack, int[] sourceOf) = TextNormalizer.Normalize(printed, options, fold);
        (string needle, _) = TextNormalizer.Normalize(phrase, options, fold);

        if (needle.Length == 0)
            return matches;

        int start = 0;
        while (start <= haystack.Length - needle.Length)
        {
            int found = haystack.IndexOf(needle, start, options.Comparison);
            if (found < 0)
                break;

            if (options.WholeWords && !StandsAlone(haystack, found, needle.Length))
            {
                start = found + 1;
                continue;
            }

            // The span in the printed text runs up to whatever the next surviving character came
            // from, which sweeps up any accent or soft hyphen that folding dropped along the way.
            int from = sourceOf[found];
            int last = found + needle.Length;
            int to = last < sourceOf.Length ? sourceOf[last] : printed.Length;

            List<PdfLetter> run = [];
            int previous = -1;
            for (int i = from; i < to; i++)
            {
                if (letterOf[i] >= 0 && letterOf[i] != previous)
                {
                    run.Add(letters[letterOf[i]]);
                    previous = letterOf[i];
                }
            }

            if (run.Count > 0)
                matches.Add(new PdfTextMatch(printed[from..to], GroupIntoLines(run), run));

            start = found + needle.Length;
        }

        return matches;
    }

    /// <summary>Finds every occurrence of a phrase, comparing the printed characters as they are.</summary>
    /// <param name="letters">The glyphs to search, in the order they were painted.</param>
    /// <param name="phrase">The text to look for.</param>
    /// <param name="comparison">How to compare.</param>
    public static IReadOnlyList<PdfTextMatch> Find(
        IReadOnlyList<PdfLetter> letters, string phrase, StringComparison comparison) =>
        Find(letters, phrase, TextSearchOptions.Exact with { Comparison = comparison });

    /// <summary>Whether a match is bounded by something other than more of the same word.</summary>
    private static bool StandsAlone(string text, int start, int length)
    {
        if (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            return false;

        int after = start + length;
        return after >= text.Length || (!char.IsLetterOrDigit(text[after]) && text[after] != '_');
    }

    /// <summary>
    /// Joins glyphs into readable text, putting a space wherever the gap between them is wide enough
    /// to read as a word break. Content streams rarely paint spaces, so without this the words of an
    /// extracted phrase run together.
    /// </summary>
    /// <param name="letters">The glyphs to join, in painting order.</param>
    public static string Compose(IReadOnlyList<PdfLetter> letters)
    {
        ArgumentNullException.ThrowIfNull(letters);
        return Flatten(letters).Text.Replace('\n', ' ');
    }

    /// <summary>The box a run of glyphs occupies, from a little below the baseline to the ascender.</summary>
    /// <param name="letter">The glyph to bound.</param>
    public static PdfRectangle BoundsOf(PdfLetter letter)
    {
        double height = letter.Height > 0 ? letter.Height : letter.FontSize;
        return new PdfRectangle(
            letter.Origin.X,
            letter.Origin.Y - (height * 0.22),
            letter.Origin.X + letter.Width,
            letter.Origin.Y + (height * 0.78));
    }

    private static (string Text, int[] Map) Flatten(IReadOnlyList<PdfLetter> letters)
    {
        var text = new StringBuilder(letters.Count + 16);
        List<int> map = new(letters.Count + 16);

        double previousEnd = double.NaN;
        double previousBaseline = double.NaN;

        for (int i = 0; i < letters.Count; i++)
        {
            PdfLetter letter = letters[i];

            // Text is usually drawn without space glyphs, so a gap wide enough to read as a word
            // break becomes a separator that belongs to no glyph. A move to another baseline is
            // kept apart from a plain gap, because only a real line break can be what a trailing
            // hyphen was breaking a word across.
            bool newLine = !double.IsNaN(previousBaseline) &&
                           Math.Abs(previousBaseline - letter.Origin.Y) > Math.Max(1, letter.FontSize * 0.3);

            bool gap = !double.IsNaN(previousEnd) &&
                       letter.Origin.X - previousEnd > letter.FontSize * 0.18;

            if ((newLine || gap) && text.Length > 0 && text[^1] is not (' ' or '\n'))
            {
                text.Append(newLine ? '\n' : ' ');
                map.Add(-1);
            }

            foreach (char c in letter.Text)
            {
                text.Append(c);
                map.Add(i);
            }

            previousEnd = letter.Origin.X + letter.Width;
            previousBaseline = letter.Origin.Y;
        }

        return (text.ToString(), [.. map]);
    }

    private static List<PdfRectangle> GroupIntoLines(List<PdfLetter> run)
    {
        List<PdfRectangle> areas = [];
        PdfRectangle current = default;
        double baseline = double.NaN;

        foreach (PdfLetter letter in run)
        {
            PdfRectangle bounds = BoundsOf(letter);

            if (double.IsNaN(baseline) || Math.Abs(baseline - letter.Origin.Y) <= Math.Max(1, letter.FontSize * 0.3))
            {
                current = current.IsEmpty ? bounds : current.Union(bounds);
                baseline = double.IsNaN(baseline) ? letter.Origin.Y : baseline;
                continue;
            }

            areas.Add(current);
            current = bounds;
            baseline = letter.Origin.Y;
        }

        if (!current.IsEmpty)
            areas.Add(current);

        return areas;
    }
}
