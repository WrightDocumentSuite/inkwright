using System.Text;

namespace Inkwright.Text;

/// <summary>One stretch of text that reads in a single direction.</summary>
/// <param name="Text">The characters, in logical order.</param>
/// <param name="IsRightToLeft">Whether the stretch reads right to left.</param>
public readonly record struct DirectionalRun(string Text, bool IsRightToLeft)
{
    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
/// The bidirectional algorithm in the direction a producer needs it: text in logical order — the
/// order it is read aloud — turned into the order its glyphs are painted.
/// </summary>
/// <remarks>
/// <para>
/// This is the mirror image of <see cref="Bidi"/>, which resolves glyphs already painted on a
/// page back into logical order. A producer starts from the other end: it holds the text as the
/// author wrote it and has to decide where each piece goes.
/// </para>
/// <para>
/// Enough of Unicode Annex #9 for the documents a word processor and a spreadsheet produce:
/// Hebrew and Arabic among Latin text and numbers, with brackets mirrored. The explicit
/// embedding controls and isolates are left out — files that carry them are vanishingly rare,
/// and a wrong guess about them is worse than the plain resolution.
/// </para>
/// </remarks>
public static class BidiLayout
{
    /// <summary>What a character contributes to direction, reduced to what layout needs.</summary>
    private enum Class : byte
    {
        /// <summary>A strong left-to-right character.</summary>
        Left,

        /// <summary>A strong right-to-left character.</summary>
        Right,

        /// <summary>A digit, which reads left-to-right even inside right-to-left text.</summary>
        Number,

        /// <summary>Everything that takes its direction from its neighbours.</summary>
        Neutral,
    }

    /// <summary>Whether a code point is strongly right-to-left.</summary>
    /// <param name="rune">The code point to classify.</param>
    public static bool IsRightToLeft(int rune) => Of(rune) == Class.Right;

    /// <summary>Whether any character of the text is strongly right-to-left.</summary>
    /// <param name="text">The text to scan.</param>
    public static bool HasRightToLeft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (char c in text)
        {
            if (Of(c) == Class.Right)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Cuts text into runs of one direction, neutrals going with the run they sit inside: between
    /// two right-to-left runs they are right-to-left; at a boundary they take the base direction.
    /// </summary>
    /// <param name="text">The text, in logical order.</param>
    /// <param name="baseRightToLeft">Whether the surrounding text reads right-to-left.</param>
    public static IEnumerable<DirectionalRun> Split(string text, bool baseRightToLeft)
    {
        ArgumentNullException.ThrowIfNull(text);

        bool[] rtl = new bool[text.Length];
        Class previous = baseRightToLeft ? Class.Right : Class.Left;
        int pendingFrom = -1;

        for (int i = 0; i < text.Length; i++)
        {
            Class of = Of(text[i]);

            if (of is Class.Left or Class.Number)
            {
                Settle(rtl, pendingFrom, i, previous, Class.Left, baseRightToLeft);
                pendingFrom = -1;
                previous = Class.Left;
            }
            else if (of == Class.Right)
            {
                Settle(rtl, pendingFrom, i, previous, Class.Right, baseRightToLeft);
                pendingFrom = -1;
                previous = Class.Right;
                rtl[i] = true;
            }
            else if (pendingFrom < 0)
            {
                pendingFrom = i;
            }
        }

        Settle(rtl, pendingFrom, text.Length, previous, baseRightToLeft ? Class.Right : Class.Left, baseRightToLeft);

        int start = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || rtl[i] != rtl[start])
            {
                yield return new DirectionalRun(text[start..i], rtl[start]);
                start = i;
            }
        }
    }

    /// <summary>
    /// Rearranges a stretch of runs into the order they are painted in, following rule L2: from
    /// the deepest embedding level down, every run at or above it is reversed.
    /// </summary>
    /// <typeparam name="T">Whatever the caller carries its runs in.</typeparam>
    /// <param name="runs">The runs, in logical order; rearranged in place.</param>
    /// <param name="from">The first run to rearrange.</param>
    /// <param name="to">The run just past the last one to rearrange.</param>
    /// <param name="isRightToLeft">Whether a run reads right-to-left.</param>
    /// <param name="baseRightToLeft">Whether the surrounding text reads right-to-left.</param>
    public static void ReorderRuns<T>(
        IList<T> runs, int from, int to, Func<T, bool> isRightToLeft, bool baseRightToLeft)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(isRightToLeft);

        if (to - from <= 1)
            return;

        // The levels UAX #9 resolves for plain content: a left-to-right base puts right-to-left
        // runs at 1; a right-to-left base puts left-to-right runs and numbers at 2.
        Span<byte> levels = to - from <= 128 ? stackalloc byte[to - from] : new byte[to - from];
        for (int i = from; i < to; i++)
            levels[i - from] = isRightToLeft(runs[i]) ? (byte)1 : baseRightToLeft ? (byte)2 : (byte)0;

        for (byte level = 2; level >= 1; level--)
        {
            int start = -1;
            for (int i = 0; i <= levels.Length; i++)
            {
                if (i < levels.Length && levels[i] >= level)
                {
                    if (start < 0)
                        start = i;
                }
                else if (start >= 0)
                {
                    Reverse(runs, from + start, from + i);
                    start = -1;
                }
            }
        }
    }

    /// <summary>
    /// Turns one right-to-left run into the order it is painted: last read, first drawn — with
    /// paired brackets swapped for their mirrors, so a parenthesis still opens.
    /// </summary>
    /// <param name="text">The run, in logical order.</param>
    public static string ToVisual(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var visual = new StringBuilder(text.Length);

        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (char.IsLowSurrogate(c) && i > 0)
            {
                visual.Append(text[i - 1]).Append(c);
                i--;
                continue;
            }

            visual.Append(Mirrored(c));
        }

        return visual.ToString();
    }

    /// <summary>
    /// The whole pipeline for one stretch of text drawn in one appearance: Arabic joined into its
    /// letterforms, the runs put in painting order, each right-to-left run reversed and mirrored.
    /// A caller whose text carries several appearances has to keep its own runs and use
    /// <see cref="Split"/> and <see cref="ReorderRuns"/> instead.
    /// </summary>
    /// <param name="text">The text, in logical order.</param>
    /// <param name="baseRightToLeft">Whether the surrounding text reads right-to-left.</param>
    public static string ToVisualLine(string text, bool baseRightToLeft)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!baseRightToLeft && !HasRightToLeft(text))
            return text;

        List<DirectionalRun> runs = [.. Split(text, baseRightToLeft)];
        for (int i = 0; i < runs.Count; i++)
        {
            if (!runs[i].IsRightToLeft)
                continue;

            string shaped = ArabicShaper.NeedsShaping(runs[i].Text)
                ? ArabicShaper.Shape(runs[i].Text)
                : runs[i].Text;

            runs[i] = runs[i] with { Text = ToVisual(shaped) };
        }

        ReorderRuns(runs, 0, runs.Count, static run => run.IsRightToLeft, baseRightToLeft);

        var visual = new StringBuilder(text.Length);
        foreach (DirectionalRun run in runs)
            visual.Append(run.Text);

        return visual.ToString();
    }

    /// <summary>The direction class of one code point.</summary>
    private static Class Of(int rune)
    {
        if (rune is >= '0' and <= '9')
            return Class.Number;

        // Hebrew, Arabic, Syriac, Thaana, and the presentation forms shaping produces.
        if (rune is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF))
            return rune is >= 0x0660 and <= 0x0669 ? Class.Number : Class.Right;

        if (rune <= 0xFFFF && char.IsLetter((char)rune))
            return Class.Left;

        return rune > 0xFFFF ? Class.Left : Class.Neutral;
    }

    /// <summary>Gives a stretch of neutrals the side of its surroundings, or the base direction.</summary>
    private static void Settle(bool[] rtl, int from, int to, Class before, Class after, bool baseRightToLeft)
    {
        if (from < 0)
            return;

        bool side = before == after ? before == Class.Right : baseRightToLeft;
        for (int i = from; i < to; i++)
            rtl[i] = side;
    }

    private static void Reverse<T>(IList<T> runs, int from, int to)
    {
        for (int a = from, b = to - 1; a < b; a++, b--)
            (runs[a], runs[b]) = (runs[b], runs[a]);
    }

    private static char Mirrored(char c) => c switch
    {
        '(' => ')',
        ')' => '(',
        '[' => ']',
        ']' => '[',
        '{' => '}',
        '}' => '{',
        '<' => '>',
        '>' => '<',
        '«' => '»',
        '»' => '«',
        _ => c,
    };
}
