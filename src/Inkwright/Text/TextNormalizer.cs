using System.Buffers;
using System.Globalization;
using System.Text;

namespace Inkwright.Text;

/// <summary>
/// Flattens the typographic differences that stop printed text from matching typed text: accents,
/// ligatures, curly quotes, and the hyphen a typesetter put in to break a word across two lines.
/// Every character it emits remembers where it came from, so a match can still be pointed at the
/// glyphs on the page.
/// </summary>
/// <remarks>
/// The rules follow <c>web/pdf_find_controller.js</c> of pdf.js, which Mozilla licenses under
/// Apache 2.0.
/// </remarks>
internal static class TextNormalizer
{
    /// <summary>
    /// Marks that carry meaning rather than decoration, and so survive folding. Dropping a kana
    /// voicing mark or a virama would change the word, not merely its accent.
    /// </summary>
    private static readonly SearchValues<char> KeptMarks = SearchValues.Create(
    [
        '\u3099', '\u309A',
        '\u094D', '\u09CD', '\u0A4D', '\u0ACD', '\u0B4D', '\u0BCD', '\u0C4D', '\u0CCD',
        '\u0D3B', '\u0D3C', '\u0D4D', '\u0DCA', '\u0E3A', '\u0EBA', '\u0F84', '\u1039',
        '\u103A', '\u1714', '\u1734', '\u17D2', '\u1A60', '\u1B44', '\u1BAA', '\u1BAB',
        '\u1BF2', '\u1BF3', '\u2D7F', '\uA806', '\uA82C', '\uA8C4', '\uA953', '\uA9C0',
        '\uAAF6', '\uABED', '\u0C56', '\u0F71', '\u0F72', '\u0F7A', '\u0F7B', '\u0F7C',
        '\u0F7D', '\u0F80', '\u0F74',
    ]);

    /// <summary>Whether a phrase carries any combining mark of its own.</summary>
    /// <param name="text">The phrase to inspect.</param>
    public static bool HasMarks(string text)
    {
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (c > '\u007F' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                return true;
        }

        return false;
    }

    /// <summary>Normalises text for searching.</summary>
    /// <param name="text">The text to fold.</param>
    /// <param name="options">Which differences to ignore.</param>
    /// <param name="foldDiacritics">Whether to drop accents; decided per search, not per option.</param>
    /// <returns>The folded text, and for each of its characters the index it came from.</returns>
    public static (string Text, int[] SourceOf) Normalize(string text, TextSearchOptions options, bool foldDiacritics)
    {
        var result = new StringBuilder(text.Length);
        var sources = new List<int>(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // A soft hyphen is an invitation to break a word, never part of it.
            if (c == '\u00AD')
                continue;

            if (options.JoinHyphenatedLines && IsHyphen(c) && BreaksWord(text, i))
            {
                i++;
                continue;
            }

            if (c == '\n')
            {
                Append(result, sources, ' ', i);
                continue;
            }

            if (Replacement(c) is { } replacement)
            {
                foreach (char part in replacement)
                    Append(result, sources, part, i);

                continue;
            }

            if (options.ExpandLigatures && c is >= '\uFB00' and <= '\uFB4F')
            {
                foreach (char part in c.ToString().Normalize(NormalizationForm.FormKC))
                    AppendFolded(result, sources, part, i, foldDiacritics);

                continue;
            }

            AppendFolded(result, sources, c, i, foldDiacritics);
        }

        return (result.ToString(), [.. sources]);
    }

    /// <summary>
    /// Whether a hyphen at this position is the one a typesetter added to split a word over two
    /// lines, which happens when a letter, the hyphen, the line break and another letter follow one
    /// another. A hyphen inside a compound word or a date is left where it is.
    /// </summary>
    private static bool BreaksWord(string text, int index) =>
        index > 0 &&
        index + 2 < text.Length &&
        text[index + 1] == '\n' &&
        char.IsLetter(text[index - 1]) &&
        char.IsLetter(text[index + 2]);

    private static bool IsHyphen(char c) => c is '-' or '\u2010';

    /// <summary>
    /// Characters a typesetter uses in place of the ones on a keyboard. Without these, a search for
    /// an apostrophe never finds a printed quotation mark.
    /// </summary>
    private static string? Replacement(char c) => c switch
    {
        '\u2010' => "-",
        '\u2018' or '\u2019' or '\u201A' or '\u201B' => "'",
        '\u201C' or '\u201D' or '\u201E' or '\u201F' => "\"",
        '\u00BC' => "1/4",
        '\u00BD' => "1/2",
        '\u00BE' => "3/4",
        _ => null,
    };

    private static void AppendFolded(StringBuilder result, List<int> sources, char c, int source, bool fold)
    {
        // ASCII neither decomposes nor carries marks, which is the overwhelming majority of text.
        if (c <= '\u007F')
        {
            Append(result, sources, c, source);
            return;
        }

        foreach (char part in c.ToString().Normalize(NormalizationForm.FormD))
        {
            bool mark = CharUnicodeInfo.GetUnicodeCategory(part) == UnicodeCategory.NonSpacingMark;
            if (fold && mark && !KeptMarks.Contains(part))
                continue;

            Append(result, sources, part, source);
        }
    }

    private static void Append(StringBuilder result, List<int> sources, char c, int source)
    {
        result.Append(c);
        sources.Add(source);
    }
}
