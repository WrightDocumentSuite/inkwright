namespace Inkwright.Fonts.Cff;

/// <summary>
/// Just enough of the Type 2 charstring language (Adobe Technical Note #5177) to find out which
/// other glyphs a glyph needs. Almost none do, but an accented letter may be built from a base
/// letter and an accent through the four-argument form of <c>endchar</c>, and dropping either of
/// those would leave a glyph that draws nothing.
/// </summary>
internal static class Type2Charstring
{
    /// <summary>
    /// The codes of the standard encoding, which the composite form of <c>endchar</c> names its
    /// two components by. Only the accented range matters, so the table is the standard encoding
    /// restricted to the codes that can appear there.
    /// </summary>
    private const int MaxStandardCode = 255;

    /// <summary>
    /// Reports the glyphs a charstring builds itself from. The composite form is
    /// <c>adx ady bchar achar endchar</c>: the last two operands are standard encoding codes of the
    /// base glyph and the accent.
    /// </summary>
    /// <param name="charstring">The glyph's program.</param>
    /// <param name="baseCode">Receives the standard code of the base glyph, or minus one.</param>
    /// <param name="accentCode">Receives the standard code of the accent, or minus one.</param>
    /// <returns>Whether the charstring is a composite.</returns>
    internal static bool TryReadComposite(ReadOnlySpan<byte> charstring, out int baseCode, out int accentCode)
    {
        baseCode = -1;
        accentCode = -1;

        Span<double> stack = stackalloc double[48];
        int count = 0;

        for (int i = 0; i < charstring.Length;)
        {
            int b = charstring[i];

            if (b >= 32 || b == 28)
            {
                if (!TryReadOperand(charstring, ref i, out double value))
                    return false;

                if (count < stack.Length)
                    stack[count++] = value;

                continue;
            }

            i++;

            switch (b)
            {
                case 14:
                    {
                        // Four operands, or five when a width is given first.
                        if (count is 4 or 5)
                        {
                            baseCode = (int)stack[count - 2];
                            accentCode = (int)stack[count - 1];

                            return baseCode is >= 0 and <= MaxStandardCode &&
                                   accentCode is >= 0 and <= MaxStandardCode;
                        }

                        return false;
                    }

                case 12:
                    {
                        if (i < charstring.Length)
                            i++;

                        count = 0;
                        break;
                    }

                case 19 or 20:
                    {
                        // A hint mask carries one bit per stem, which follows the operator inline.
                        i += (count / 2) + 1;
                        count = 0;
                        break;
                    }

                case 10 or 29:
                    // A call into a subroutine: the composite form never hides behind one in
                    // practice, and following it would need the subroutine index.
                    return false;

                default:
                    count = 0;
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks a charstring and records every subroutine it calls, following those calls in turn.
    /// Subroutines hold the shapes several glyphs share — the stem of a letter, a common curve —
    /// so a glyph that survives subsetting drags its subroutines along with it.
    /// </summary>
    /// <param name="charstring">The program to walk.</param>
    /// <param name="local">The local subroutines it may call.</param>
    /// <param name="global">The global subroutines it may call.</param>
    /// <param name="font">The whole font program, which the indices point into.</param>
    /// <param name="usedLocal">Receives the indices of the local subroutines reached.</param>
    /// <param name="usedGlobal">Receives the indices of the global subroutines reached.</param>
    /// <param name="depth">How deep the walk already is, which bounds runaway recursion.</param>
    internal static void CollectSubroutines(
        ReadOnlySpan<byte> charstring,
        CffIndex local,
        CffIndex global,
        ReadOnlyMemory<byte> font,
        HashSet<int> usedLocal,
        HashSet<int> usedGlobal,
        int depth = 0)
    {
        if (depth > 10)
            return;

        Span<double> stack = stackalloc double[48];
        int count = 0;

        for (int i = 0; i < charstring.Length;)
        {
            int b = charstring[i];

            if (b >= 32 || b == 28)
            {
                if (!TryReadOperand(charstring, ref i, out double value))
                    return;

                if (count < stack.Length)
                    stack[count++] = value;

                continue;
            }

            i++;

            switch (b)
            {
                case 10 or 29:
                    {
                        // The index is biased so that the commonest subroutines get short encodings.
                        bool isLocal = b == 10;
                        CffIndex index = isLocal ? local : global;
                        HashSet<int> used = isLocal ? usedLocal : usedGlobal;

                        if (count > 0)
                        {
                            int number = (int)stack[--count] + Bias(index.Count);

                            if (number >= 0 && number < index.Count && used.Add(number))
                            {
                                CollectSubroutines(
                                    index.Item(font.Span, number), local, global, font, usedLocal, usedGlobal, depth + 1);
                            }
                        }

                        break;
                    }

                case 11:
                    return;

                case 14:
                    return;

                case 12:
                    {
                        if (i < charstring.Length)
                            i++;

                        count = 0;
                        break;
                    }

                case 19 or 20:
                    {
                        i += (count / 2) + 1;
                        count = 0;
                        break;
                    }

                default:
                    count = 0;
                    break;
            }
        }
    }

    /// <summary>
    /// The offset added to a subroutine number (#5177 §4.7). Larger indexes get a larger bias so
    /// that the numbers around zero, which encode most cheaply, land in the middle of the index.
    /// </summary>
    /// <param name="count">How many subroutines the index holds.</param>
    internal static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    private static bool TryReadOperand(ReadOnlySpan<byte> data, ref int i, out double value)
    {
        value = 0;
        int b = data[i];

        switch (b)
        {
            case >= 32 and <= 246:
                value = b - 139;
                i++;
                return true;

            case >= 247 and <= 250 when i + 1 < data.Length:
                value = ((b - 247) * 256) + data[i + 1] + 108;
                i += 2;
                return true;

            case >= 251 and <= 254 when i + 1 < data.Length:
                value = (-(b - 251) * 256) - data[i + 1] - 108;
                i += 2;
                return true;

            case 28 when i + 2 < data.Length:
                value = (short)((data[i + 1] << 8) | data[i + 2]);
                i += 3;
                return true;

            case 255 when i + 4 < data.Length:
                {
                    int fixedPoint = (data[i + 1] << 24) | (data[i + 2] << 16) | (data[i + 3] << 8) | data[i + 4];
                    value = fixedPoint / 65536.0;
                    i += 5;
                    return true;
                }

            default:
                return false;
        }
    }
}
