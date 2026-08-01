using System.Text;

namespace Inkwright.Text;

/// <summary>
/// Joins Arabic letters: each takes its isolated, final, initial or medial form depending on its
/// neighbours, and lam before alef becomes the lam-alef ligature.
/// </summary>
/// <remarks>
/// The forms used are the Unicode presentation forms (U+FE70 and after), which the fonts that
/// carry Arabic carry too. That trades the font's own substitution table for a fixed one — the
/// classical shaping, not a stylistic one — and needs nothing from the font but its character
/// map. Shaping happens before measuring, in logical order; the bidi pass then draws the shaped
/// text right to left like any other.
/// </remarks>
public static class ArabicShaper
{
    /// <summary>The forms of one letter: isolated and final always, initial and medial when it joins on.</summary>
    private readonly record struct Forms(char Isolated, bool Dual);

    /// <summary>
    /// The presentation forms of the Arabic block, each row four code points — isolated, final,
    /// initial, medial — or two for the letters nothing joins to on the left.
    /// </summary>
    private static readonly Dictionary<char, Forms> Table = new()
    {
        ['\u0621'] = new Forms('\uFE80', Dual: false),
        ['\u0622'] = new Forms('\uFE81', Dual: false),
        ['\u0623'] = new Forms('\uFE83', Dual: false),
        ['\u0624'] = new Forms('\uFE85', Dual: false),
        ['\u0625'] = new Forms('\uFE87', Dual: false),
        ['\u0626'] = new Forms('\uFE89', Dual: true),
        ['\u0627'] = new Forms('\uFE8D', Dual: false),
        ['\u0628'] = new Forms('\uFE8F', Dual: true),
        ['\u0629'] = new Forms('\uFE93', Dual: false),
        ['\u062A'] = new Forms('\uFE95', Dual: true),
        ['\u062B'] = new Forms('\uFE99', Dual: true),
        ['\u062C'] = new Forms('\uFE9D', Dual: true),
        ['\u062D'] = new Forms('\uFEA1', Dual: true),
        ['\u062E'] = new Forms('\uFEA5', Dual: true),
        ['\u062F'] = new Forms('\uFEA9', Dual: false),
        ['\u0630'] = new Forms('\uFEAB', Dual: false),
        ['\u0631'] = new Forms('\uFEAD', Dual: false),
        ['\u0632'] = new Forms('\uFEAF', Dual: false),
        ['\u0633'] = new Forms('\uFEB1', Dual: true),
        ['\u0634'] = new Forms('\uFEB5', Dual: true),
        ['\u0635'] = new Forms('\uFEB9', Dual: true),
        ['\u0636'] = new Forms('\uFEBD', Dual: true),
        ['\u0637'] = new Forms('\uFEC1', Dual: true),
        ['\u0638'] = new Forms('\uFEC5', Dual: true),
        ['\u0639'] = new Forms('\uFEC9', Dual: true),
        ['\u063A'] = new Forms('\uFECD', Dual: true),
        ['\u0641'] = new Forms('\uFED1', Dual: true),
        ['\u0642'] = new Forms('\uFED5', Dual: true),
        ['\u0643'] = new Forms('\uFED9', Dual: true),
        ['\u0644'] = new Forms('\uFEDD', Dual: true),
        ['\u0645'] = new Forms('\uFEE1', Dual: true),
        ['\u0646'] = new Forms('\uFEE5', Dual: true),
        ['\u0647'] = new Forms('\uFEE9', Dual: true),
        ['\u0648'] = new Forms('\uFEED', Dual: false),
        ['\u0649'] = new Forms('\uFEEF', Dual: false),
        ['\u064A'] = new Forms('\uFEF1', Dual: true),
    };

    /// <summary>The lam-alef ligatures: lam followed by each kind of alef, isolated and final.</summary>
    private static char? LamAlef(char alef) => alef switch
    {
        '\u0622' => '\uFEF5',
        '\u0623' => '\uFEF7',
        '\u0625' => '\uFEF9',
        '\u0627' => '\uFEFB',
        _ => null,
    };

    /// <summary>Marks that sit on a letter without interrupting the joining around it.</summary>
    private static bool IsTransparent(char c) => c is >= '\u064B' and <= '\u065F' or '\u0670';

    /// <summary>Whether the text has anything this shaper would change.</summary>
    /// <param name="text">The text to scan.</param>
    public static bool NeedsShaping(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (char c in text)
        {
            if (Table.ContainsKey(c))
                return true;
        }

        return false;
    }

    /// <summary>Shapes a stretch of text, in logical order.</summary>
    /// <param name="text">The text to shape.</param>
    public static string Shape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var shaped = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!Table.TryGetValue(c, out Forms forms))
            {
                shaped.Append(c);
                continue;
            }

            bool joinsBefore = JoinsOnLeftOf(text, i);

            // Lam before alef fuses into one letter, whose only variation is whether something
            // joins on before it.
            if (c == '\u0644' && Next(text, i) is { } next && LamAlef(text[next]) is { } ligature)
            {
                shaped.Append((char)(ligature + (joinsBefore ? 1 : 0)));
                i = next;
                continue;
            }

            bool joinsAfter = forms.Dual && Next(text, i) is { } after && Table.ContainsKey(text[after]);

            // The four forms sit side by side: isolated, final, initial, medial.
            int offset = (joinsBefore ? 1 : 0) + (joinsAfter ? 2 : 0);
            shaped.Append((char)(forms.Isolated + offset));
        }

        return shaped.ToString();
    }

    /// <summary>Whether the letter before this one reaches out to join it.</summary>
    private static bool JoinsOnLeftOf(string text, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            char c = text[i];
            if (IsTransparent(c))
                continue;

            return Table.TryGetValue(c, out Forms forms) && forms.Dual;
        }

        return false;
    }

    /// <summary>The next letter that joining sees, stepping over the marks that ride along.</summary>
    private static int? Next(string text, int index)
    {
        for (int i = index + 1; i < text.Length; i++)
        {
            if (!IsTransparent(text[i]))
                return Table.ContainsKey(text[i]) ? i : null;
        }

        return null;
    }
}
