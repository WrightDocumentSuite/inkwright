namespace Inkwright.Images.Jbig2;

/// <summary>
/// The generic refinement region decoding procedure of ITU-T T.88 §6.3. It codes a bitmap as the
/// difference from another one, reading a neighbourhood in both: what has already been decoded of
/// the new bitmap, and the same area of the reference. Where the two agree, the prediction is
/// almost free, so a letter that differs from its dictionary shape by a few pixels costs a few
/// pixels rather than a whole glyph.
/// </summary>
internal static class Jbig2RefinementRegion
{
    /// <summary>
    /// The context reused for the typical-prediction bit, one per template (Figures 14 and 15).
    /// Both figures show every pixel zero but the centre of the reference, so the value follows
    /// from the gathering order used below.
    /// </summary>
    private static readonly int[] TypicalPredictionContext = [0x0010, 0x0008];

    /// <summary>
    /// The template pixels taken from the bitmap being decoded, in reading order (Figures 12
    /// and 13). Template 0 puts an adaptive pixel at the head of the row above.
    /// </summary>
    private static readonly (int X, int Y, int Adaptive)[][] TargetTemplates =
    [
        [(-1, -1, 1), (0, -1, 0), (1, -1, 0), (-1, 0, 0)],
        [(-1, -1, 0), (0, -1, 0), (1, -1, 0), (-1, 0, 0)],
    ];

    /// <summary>
    /// The template pixels taken from the reference bitmap, in reading order. Template 0 reads the
    /// full three-by-three neighbourhood with an adaptive pixel in one corner; template 1 reads a
    /// cross-shaped subset of it.
    /// </summary>
    private static readonly (int X, int Y, int Adaptive)[][] ReferenceTemplates =
    [
        [
            (-1, -1, 2), (0, -1, 0), (1, -1, 0),
            (-1, 0, 0), (0, 0, 0), (1, 0, 0),
            (-1, 1, 0), (0, 1, 0), (1, 1, 0),
        ],
        [
            (0, -1, 0),
            (-1, 0, 0), (0, 0, 0), (1, 0, 0),
            (0, 1, 0), (1, 1, 0),
        ],
    ];

    /// <summary>Decodes a refinement of another bitmap (§6.3.5.6).</summary>
    /// <param name="decoder">The arithmetic decoder.</param>
    /// <param name="contexts">The context storage for refinement decisions.</param>
    /// <param name="width">The width of the refined bitmap.</param>
    /// <param name="height">The height of the refined bitmap.</param>
    /// <param name="template">Which refinement template to use, 0 or 1.</param>
    /// <param name="reference">The bitmap being refined.</param>
    /// <param name="dx">How far the reference is offset horizontally.</param>
    /// <param name="dy">How far the reference is offset vertically.</param>
    /// <param name="at">The adaptive pixel positions: the first in the target, the second in the reference.</param>
    /// <param name="typicalPrediction">Whether rows the reference already agrees on are coded as such.</param>
    internal static Jbig2Bitmap Decode(
        MqDecoder decoder,
        MqContext[] contexts,
        int width,
        int height,
        int template,
        Jbig2Bitmap reference,
        int dx,
        int dy,
        ReadOnlySpan<(int X, int Y)> at,
        bool typicalPrediction)
    {
        var bitmap = new Jbig2Bitmap(width, height);
        int which = Math.Clamp(template, 0, 1);

        (int X, int Y)[] target = Resolve(TargetTemplates[which], at);
        (int X, int Y)[] source = Resolve(ReferenceTemplates[which], at);

        int typicalContext = TypicalPredictionContext[which];
        bool typical = false;

        for (int y = 0; y < height; y++)
        {
            if (typicalPrediction)
                typical ^= decoder.Decode(contexts, typicalContext) == 1;

            for (int x = 0; x < width; x++)
            {
                int refX = x - dx;
                int refY = y - dy;

                // Where the reference is uniform over the whole neighbourhood, the refined pixel is
                // taken to agree with it and costs nothing to code.
                if (typical && TryPredict(reference, refX, refY, out int predicted))
                {
                    bitmap[x, y] = predicted;
                    continue;
                }

                int context = 0;
                foreach ((int ox, int oy) in target)
                    context = (context << 1) | bitmap[x + ox, y + oy];

                foreach ((int ox, int oy) in source)
                    context = (context << 1) | reference[refX + ox, refY + oy];

                bitmap[x, y] = decoder.Decode(contexts, context);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// Whether the three-by-three neighbourhood of the reference is all one value (Figure 16), and
    /// what that value is.
    /// </summary>
    private static bool TryPredict(Jbig2Bitmap reference, int x, int y, out int value)
    {
        value = reference[x - 1, y - 1];

        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                if (reference[x + ox, y + oy] != value)
                    return false;
            }
        }

        return true;
    }

    private static (int X, int Y)[] Resolve(
        (int X, int Y, int Adaptive)[] pattern, ReadOnlySpan<(int X, int Y)> at)
    {
        var offsets = new (int X, int Y)[pattern.Length];

        for (int i = 0; i < pattern.Length; i++)
        {
            (int x, int y, int adaptive) = pattern[i];
            offsets[i] = adaptive == 0 || adaptive > at.Length ? (x, y) : at[adaptive - 1];
        }

        return offsets;
    }
}
