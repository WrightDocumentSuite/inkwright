using Inkwright.Images.Ccitt;

namespace Inkwright.Images.Jbig2;

/// <summary>
/// The generic region decoding procedure of ITU-T T.88 §6.2. Each pixel is predicted from a small
/// neighbourhood of already-decoded pixels — the template — and the arithmetic coder is asked only
/// for the difference between the prediction and the truth. On a scanned page most neighbourhoods
/// have been seen thousands of times already, so most pixels cost a tiny fraction of a bit.
/// </summary>
internal static class Jbig2GenericRegion
{
    /// <summary>
    /// The context reused for the typical-prediction bit, one per template (Figures 8 to 11). It is
    /// a context that no ordinary neighbourhood produces, so the two never collide.
    /// </summary>
    private static readonly int[] TypicalPredictionContext = [0x9B25, 0x0795, 0x00E5, 0x0195];

    /// <summary>
    /// The template pixels, in the reading order the standard's own example uses: rows from top to
    /// bottom, and within each row left to right. Entries of <see langword="null"/> mark where an
    /// adaptive pixel goes, since the caller may have moved it.
    /// </summary>
    private static readonly (int X, int Y, int Adaptive)[][] Templates =
    [
        // Template 0, sixteen pixels over three rows, with four adaptive pixels.
        [
            (-2, -2, 4), (-1, -2, 0), (0, -2, 0), (1, -2, 0), (2, -2, 3),
            (-3, -1, 2), (-2, -1, 0), (-1, -1, 0), (0, -1, 0), (1, -1, 0), (2, -1, 0), (3, -1, 1),
            (-4, 0, 0), (-3, 0, 0), (-2, 0, 0), (-1, 0, 0),
        ],

        // Template 1, thirteen pixels, one adaptive.
        [
            (-1, -2, 0), (0, -2, 0), (1, -2, 0), (2, -2, 0),
            (-2, -1, 0), (-1, -1, 0), (0, -1, 0), (1, -1, 0), (2, -1, 0), (3, -1, 1),
            (-3, 0, 0), (-2, 0, 0), (-1, 0, 0),
        ],

        // Template 2, ten pixels over three rows, one adaptive.
        [
            (-1, -2, 0), (0, -2, 0), (1, -2, 0),
            (-2, -1, 0), (-1, -1, 0), (0, -1, 0), (1, -1, 0), (2, -1, 1),
            (-2, 0, 0), (-1, 0, 0),
        ],

        // Template 3, ten pixels over two rows, one adaptive.
        [
            (-3, -1, 0), (-2, -1, 0), (-1, -1, 0), (0, -1, 0), (1, -1, 0), (2, -1, 1),
            (-4, 0, 0), (-3, 0, 0), (-2, 0, 0), (-1, 0, 0),
        ],
    ];

    /// <summary>Decodes a generic region coded with the arithmetic coder (§6.2.5).</summary>
    /// <param name="decoder">The arithmetic decoder, positioned at the region's data.</param>
    /// <param name="contexts">The context storage, shared across regions that say to reuse it.</param>
    /// <param name="width">The region width.</param>
    /// <param name="height">The region height.</param>
    /// <param name="template">Which template to use, 0 to 3.</param>
    /// <param name="at">The adaptive pixel positions, four pairs.</param>
    /// <param name="typicalPrediction">Whether rows identical to the one above are coded as such.</param>
    internal static Jbig2Bitmap Decode(
        MqDecoder decoder,
        MqContext[] contexts,
        int width,
        int height,
        int template,
        ReadOnlySpan<(int X, int Y)> at,
        bool typicalPrediction) =>
        Decode(decoder, contexts, width, height, template, at, typicalPrediction, skip: null);

    /// <summary>Decodes a generic region, leaving out the pixels a skip bitmap marks (§6.2.5.6).</summary>
    /// <param name="decoder">The arithmetic decoder, positioned at the region's data.</param>
    /// <param name="contexts">The context storage, shared across regions that say to reuse it.</param>
    /// <param name="width">The region width.</param>
    /// <param name="height">The region height.</param>
    /// <param name="template">Which template to use, 0 to 3.</param>
    /// <param name="at">The adaptive pixel positions, four pairs.</param>
    /// <param name="typicalPrediction">Whether rows identical to the one above are coded as such.</param>
    /// <param name="skip">
    /// Pixels set here are known to be zero and are not coded at all. A halftone region uses this
    /// for grid positions whose pattern would fall entirely outside the region.
    /// </param>
    internal static Jbig2Bitmap Decode(
        MqDecoder decoder,
        MqContext[] contexts,
        int width,
        int height,
        int template,
        ReadOnlySpan<(int X, int Y)> at,
        bool typicalPrediction,
        Jbig2Bitmap? skip)
    {
        var bitmap = new Jbig2Bitmap(width, height);
        (int X, int Y, int Adaptive)[] pattern = Templates[Math.Clamp(template, 0, 3)];

        // Resolving the adaptive positions once turns the inner loop into plain offsets.
        var offsets = new (int X, int Y)[pattern.Length];
        for (int i = 0; i < pattern.Length; i++)
        {
            (int x, int y, int adaptive) = pattern[i];
            offsets[i] = adaptive == 0 || adaptive > at.Length ? (x, y) : at[adaptive - 1];
        }

        int typicalContext = TypicalPredictionContext[Math.Clamp(template, 0, 3)];
        bool typical = false;

        for (int y = 0; y < height; y++)
        {
            if (typicalPrediction)
            {
                // A single bit per row says whether this row repeats the one above it, which is
                // what makes the white margins of a scan almost free.
                typical ^= decoder.Decode(contexts, typicalContext) == 1;

                if (typical)
                {
                    bitmap.CopyRowFromAbove(y);
                    continue;
                }
            }

            for (int x = 0; x < width; x++)
            {
                if (skip is not null && skip[x, y] != 0)
                {
                    bitmap[x, y] = 0;
                    continue;
                }

                int context = 0;
                foreach ((int dx, int dy) in offsets)
                    context = (context << 1) | bitmap[x + dx, y + dy];

                bitmap[x, y] = decoder.Decode(contexts, context);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// Decodes a generic region coded the fax way (§6.2.6). It is the two-dimensional coding of
    /// ITU-T T.6, with a set bit meaning black and no end-of-block pattern.
    /// </summary>
    /// <param name="data">The coded bytes.</param>
    /// <param name="width">The region width.</param>
    /// <param name="height">The region height.</param>
    internal static Jbig2Bitmap DecodeMmr(ReadOnlySpan<byte> data, int width, int height) =>
        DecodeMmr(data, width, height, out _);

    /// <summary>Decodes a fax-coded region and reports how many bytes it took.</summary>
    /// <param name="data">The coded bytes.</param>
    /// <param name="width">The region width.</param>
    /// <param name="height">The region height.</param>
    /// <param name="consumed">Receives the bytes read, counting the end-of-block pattern.</param>
    internal static Jbig2Bitmap DecodeMmr(ReadOnlySpan<byte> data, int width, int height, out int consumed)
    {
        var options = new CcittOptions(
            Columns: width,
            Rows: height,
            K: -1,
            EndOfLine: false,
            ByteAligned: false,
            BlackIs1: true,
            DamagedRowsBeforeError: 0);

        byte[] rows = CcittDecoder.Decode(data, options, out consumed);
        var bitmap = new Jbig2Bitmap(width, height);

        int stride = (width + 7) / 8;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * stride) + (x >> 3);
                if (index < rows.Length)
                    bitmap[x, y] = (rows[index] >> (7 - (x & 7))) & 1;
            }
        }

        return bitmap;
    }
}
