namespace Inkwright.Text;

/// <summary>A run of text with the direction the bidirectional algorithm resolved for it.</summary>
/// <param name="Text">The text in logical order.</param>
/// <param name="IsRightToLeft">Whether the run reads right to left overall.</param>
public readonly record struct BidiText(string Text, bool IsRightToLeft)
{
    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
/// Puts a line of extracted text back into logical order. A PDF paints glyphs in the order they sit
/// on the page, so Arabic and Hebrew come out of a content stream backwards: the last letter of a
/// word is painted leftmost and read first. This runs the Unicode bidirectional algorithm over such
/// a line and returns the order the text was written in, which is the order it must be stored,
/// searched and copied in.
/// </summary>
/// <remarks>
/// A subset of Unicode Annex #9, ported from <c>src/core/bidi.js</c> of pdf.js, which Mozilla
/// licenses under Apache 2.0. Explicit embedding controls (X1-X10), combining-mark reordering (L3)
/// and glyph mirroring (L4) are left out: a PDF has already resolved all three by the time its
/// glyphs are painted.
/// </remarks>
public static class Bidi
{
    /// <summary>
    /// The bidirectional class of every code point below <c>U+0100</c>, one letter each. Latin-1 is
    /// the only block dense enough to be worth a table; everything above it is decided by range.
    /// </summary>
    private const string LatinClasses =
        "bbbbbbbbbSBSWBbbbbbbbbbbbbbbBBBSWOOtttOOOOOscsccEEEEEEEEEEcOOOOO" +
        "OLLLLLLLLLLLLLLLLLLLLLLLLLLOOOOOOLLLLLLLLLLLLLLLLLLLLLLLLLLOOOOb" +
        "bbbbbBbbbbbbbbbbbbbbbbbbbbbbbbbbcOttttOOOOLOObOOttEEOLOOOELOOOOO" +
        "LLLLLLLLLLLLLLLLLLLLLLLOLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLOLLLLLLLL";

    /// <summary>
    /// The classes of the Arabic block, <c>U+0600</c> to <c>U+06FF</c>. The question mark stands for
    /// <c>U+061D</c>, which the standard does not assign; it holds the place so the rest line up.
    /// </summary>
    private const string ArabicClasses =
        "NNNNNNOOAttAcAOOMMMMMMMMMMMAA?AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAMMMMMMMMMMMMMMMMMMMMMNNNNNNNNNNtNNAAAMAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAMMMMMMMNOMMMMMMAAMMOMMMMAAEEEEEEEEEEAAAAAA";

    /// <summary>
    /// The share of right-to-left characters above which a line is taken to be right-to-left
    /// overall. Short lines are treated as right-to-left as soon as they contain any, because a
    /// two-word Hebrew phrase with a comma would otherwise fall the wrong way.
    /// </summary>
    private const double RightToLeftShare = 0.3;

    private const int ShortLine = 4;

    /// <summary>
    /// Resolves a line of text that was read off a page in the order its glyphs were painted.
    /// </summary>
    /// <param name="text">The line, in the order the content stream showed it.</param>
    /// <returns>The line in logical order, and which way it reads.</returns>
    public static BidiText Resolve(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
            return new BidiText(text, false);

        Span<char> types = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        int directional = Classify(text, types);

        if (directional == 0)
            return new BidiText(text, false);

        bool rightToLeft = directional / (double)text.Length >= RightToLeftShare || text.Length <= ShortLine;
        int baseLevel = rightToLeft ? 1 : 0;

        Span<byte> levels = text.Length <= 512 ? stackalloc byte[text.Length] : new byte[text.Length];
        levels.Fill((byte)baseLevel);

        char boundary = baseLevel == 1 ? 'R' : 'L';
        ResolveWeak(types, boundary);
        ResolveNeutral(types, boundary);
        AssignLevels(types, levels);

        char[] characters = text.ToCharArray();
        Reorder(characters, levels);

        return new BidiText(new string(characters), rightToLeft);
    }

    /// <summary>Tags every character with its bidirectional class, counting the right-to-left ones.</summary>
    private static int Classify(ReadOnlySpan<char> text, Span<char> types)
    {
        int directional = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char type = ClassOf(text[i]);
            types[i] = type;

            if (type is 'R' or 'A' or 'N')
                directional++;
        }

        return directional;
    }

    private static char ClassOf(char c) => c switch
    {
        <= '\u00FF' => LatinClasses[c],
        >= '\u0590' and <= '\u05F4' => 'R',
        >= '\u0600' and <= '\u06FF' => ArabicClasses[c & 0xFF],
        >= '\u0700' and <= '\u08AC' => 'A',
        >= '\uFB50' and <= '\uFDFF' => 'A',
        >= '\uFE70' and <= '\uFEFF' => 'A',
        _ => 'L',
    };

    /// <summary>Rules W1 to W7: resolve marks, numbers, separators and terminators.</summary>
    private static void ResolveWeak(Span<char> types, char boundary)
    {
        // W1: a non-spacing mark takes the class of whatever it sits on.
        char last = boundary;
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] == 'M')
                types[i] = last;
            else
                last = types[i];
        }

        // W2: a European number after Arabic letters is an Arabic number.
        last = boundary;
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] == 'E')
            {
                if (last == 'A')
                    types[i] = 'N';
            }
            else if (types[i] is 'R' or 'L' or 'A')
                last = types[i];
        }

        // W3: Arabic letters are plain right-to-left from here on.
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] == 'A')
                types[i] = 'R';
        }

        // W4: a lone separator between two numbers joins them.
        for (int i = 1; i < types.Length - 1; i++)
        {
            if (types[i] == 's' && types[i - 1] == 'E' && types[i + 1] == 'E')
                types[i] = 'E';

            if (types[i] == 'c' && types[i - 1] is 'E' or 'N' && types[i + 1] == types[i - 1])
                types[i] = types[i - 1];
        }

        // W5: terminators touching a European number become part of it.
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] != 'E')
                continue;

            for (int j = i - 1; j >= 0 && types[j] == 't'; j--)
                types[j] = 'E';

            for (int j = i + 1; j < types.Length && types[j] == 't'; j++)
                types[j] = 'E';
        }

        // W6: every separator and terminator left over is neutral.
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] is 'W' or 's' or 't' or 'c')
                types[i] = 'O';
        }

        // W7: a European number in left-to-right context is left-to-right.
        last = boundary;
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] == 'E')
            {
                if (last == 'L')
                    types[i] = 'L';
            }
            else if (types[i] is 'R' or 'L')
                last = types[i];
        }
    }

    /// <summary>Rules N1 and N2: give the neutrals a direction.</summary>
    private static void ResolveNeutral(Span<char> types, char boundary)
    {
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] != 'O')
                continue;

            int end = i + 1;
            while (end < types.Length && types[end] == 'O')
                end++;

            // N1: a run of neutrals between text of one direction joins it. Numbers count as
            // right-to-left here, which is what keeps "1 2 3" together inside an Arabic sentence.
            char before = Strong(types, i - 1, -1, boundary);
            char after = Strong(types, end, 1, boundary);

            if (before == after)
                types[i..end].Fill(before);

            i = end - 1;
        }

        // N2: whatever is still neutral takes the direction of the line.
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] == 'O')
                types[i] = boundary;
        }
    }

    private static char Strong(ReadOnlySpan<char> types, int from, int step, char boundary)
    {
        for (int j = from; j >= 0 && j < types.Length; j += step)
        {
            if (types[j] == 'L')
                return 'L';

            if (types[j] is 'R' or 'E' or 'N')
                return 'R';
        }

        return boundary;
    }

    /// <summary>Rules I1 and I2: raise the embedding level of anything running against the line.</summary>
    private static void AssignLevels(ReadOnlySpan<char> types, Span<byte> levels)
    {
        for (int i = 0; i < types.Length; i++)
        {
            if ((levels[i] & 1) == 0)
            {
                if (types[i] == 'R')
                    levels[i] += 1;
                else if (types[i] is 'N' or 'E')
                    levels[i] += 2;
            }
            else if (types[i] is 'L' or 'N' or 'E')
                levels[i] += 1;
        }
    }

    /// <summary>
    /// Rule L2: from the deepest level down to the shallowest odd one, reverse every run at or
    /// above it. Nested reversals are what put a Latin quotation inside an Arabic sentence, or a
    /// number inside that quotation, back the right way round.
    /// </summary>
    private static void Reorder(Span<char> characters, ReadOnlySpan<byte> levels)
    {
        int highest = 0;
        int lowestOdd = int.MaxValue;

        foreach (byte level in levels)
        {
            highest = Math.Max(highest, level);
            if ((level & 1) != 0)
                lowestOdd = Math.Min(lowestOdd, level);
        }

        for (int level = highest; level >= lowestOdd; level--)
        {
            int start = -1;

            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] >= level)
                {
                    if (start < 0)
                        start = i;

                    continue;
                }

                if (start >= 0)
                {
                    characters[start..i].Reverse();
                    start = -1;
                }
            }

            if (start >= 0)
                characters[start..].Reverse();
        }
    }
}
