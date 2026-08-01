using System.Buffers.Binary;

namespace Inkwright.Images.Jbig2;

/// <summary>
/// The pattern dictionary decoding procedure of ITU-T T.88 §6.7. A pattern dictionary holds the dot
/// shapes a halftone region is built from, one per grey level. They are coded as a single wide
/// bitmap and cut into equal columns, which costs less than coding each separately because
/// neighbouring grey levels look almost the same.
/// </summary>
internal static class Jbig2PatternDictionary
{
    /// <summary>The most patterns one dictionary may define.</summary>
    private const int MaxPatterns = 4096;

    /// <summary>Decodes a pattern dictionary segment (§7.4.4).</summary>
    /// <param name="data">The segment's data part.</param>
    /// <returns>The patterns, indexed by grey level.</returns>
    /// <exception cref="NotSupportedException">The dictionary is malformed or too large.</exception>
    internal static Jbig2Bitmap[] Decode(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;

        if (span.Length < 7)
            throw new NotSupportedException("A JBIG2 pattern dictionary segment is truncated.");

        byte flags = span[0];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;

        int patternWidth = span[1];
        int patternHeight = span[2];
        int greyMax = (int)BinaryPrimitives.ReadUInt32BigEndian(span[3..]);

        if (patternWidth <= 0 || patternHeight <= 0 || greyMax < 0 || greyMax >= MaxPatterns)
            throw new NotSupportedException($"A JBIG2 pattern dictionary claims {greyMax + 1} patterns.");

        int count = greyMax + 1;
        int collectiveWidth = count * patternWidth;

        ReadOnlyMemory<byte> body = data[7..];

        // The first adaptive pixel sits a whole pattern to the left, so each pattern predicts from
        // the one before it rather than from its own neighbouring columns.
        Span<(int X, int Y)> at = [(-patternWidth, 0), (-3, -1), (2, -2), (-2, -2)];

        Jbig2Bitmap collective = mmr
            ? Jbig2GenericRegion.DecodeMmr(body.Span, collectiveWidth, patternHeight)
            : Jbig2GenericRegion.Decode(
                new MqDecoder(body),
                MqDecoder.CreateContexts(1 << 16),
                collectiveWidth,
                patternHeight,
                template,
                at,
                typicalPrediction: false);

        var patterns = new Jbig2Bitmap[count];
        for (int i = 0; i < count; i++)
            patterns[i] = collective.Crop(i * patternWidth, 0, patternWidth, patternHeight);

        return patterns;
    }
}

/// <summary>
/// The halftone region decoding procedure of ITU-T T.88 §6.6. A halftone region codes a photograph
/// as a coarse grid of grey levels and stamps the matching dot pattern at each grid position. The
/// grey levels themselves are coded as bit planes in Gray code (Annex C), so a gradual change in
/// tone flips one plane rather than all of them.
/// </summary>
internal static class Jbig2HalftoneRegion
{
    /// <summary>The most grid positions one region may hold.</summary>
    private const long MaxGridCells = 32L * 1024 * 1024;

    /// <summary>Decodes a halftone region segment (§7.4.5).</summary>
    /// <param name="data">The segment's data part, positioned past the region information field.</param>
    /// <param name="patterns">The patterns the region stamps, indexed by grey level.</param>
    /// <param name="width">The region width.</param>
    /// <param name="height">The region height.</param>
    /// <exception cref="NotSupportedException">The region is malformed or names no patterns.</exception>
    internal static Jbig2Bitmap Decode(
        ReadOnlyMemory<byte> data, IReadOnlyList<Jbig2Bitmap> patterns, int width, int height)
    {
        ReadOnlySpan<byte> span = data.Span;

        // Flags, grid size, grid origin and grid step take twenty-one bytes before the planes.
        if (span.Length < 21)
            throw new NotSupportedException("A JBIG2 halftone region segment is truncated.");

        if (patterns.Count == 0)
            throw new NotSupportedException("A JBIG2 halftone region refers to no pattern dictionary.");

        byte flags = span[0];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool enableSkip = ((flags >> 3) & 1) != 0;
        var combination = (Jbig2CombinationOperator)((flags >> 4) & 7);
        int defaultPixel = (flags >> 7) & 1;

        int gridWidth = (int)BinaryPrimitives.ReadUInt32BigEndian(span[1..]);
        int gridHeight = (int)BinaryPrimitives.ReadUInt32BigEndian(span[5..]);
        int gridX = BinaryPrimitives.ReadInt32BigEndian(span[9..]);
        int gridY = BinaryPrimitives.ReadInt32BigEndian(span[13..]);
        int stepX = BinaryPrimitives.ReadUInt16BigEndian(span[17..]);
        int stepY = BinaryPrimitives.ReadUInt16BigEndian(span[19..]);

        if (gridWidth <= 0 || gridHeight <= 0 || (long)gridWidth * gridHeight > MaxGridCells)
            throw new NotSupportedException($"A JBIG2 halftone grid claims to be {gridWidth} by {gridHeight}.");

        var region = new Jbig2Bitmap(width, height);
        if (defaultPixel != 0)
            region.Fill(1);

        int patternWidth = patterns[0].Width;
        int patternHeight = patterns[0].Height;

        Jbig2Bitmap? skip = enableSkip
            ? BuildSkip(gridWidth, gridHeight, gridX, gridY, stepX, stepY, patternWidth, patternHeight, width, height)
            : null;

        // One bit plane per bit of grey level, and the number of levels is what the dictionary holds.
        int planes = 0;
        while ((1 << planes) < patterns.Count)
            planes++;

        planes = Math.Max(planes, 1);

        int[,] grey = DecodeGrey(data[21..], mmr, template, gridWidth, gridHeight, planes, skip);

        for (int row = 0; row < gridHeight; row++)
        {
            for (int column = 0; column < gridWidth; column++)
            {
                if (skip is not null && skip[column, row] != 0)
                    continue;

                // The grid may be rotated slightly, so a step along one axis moves along both.
                int x = (gridX + (row * stepY) + (column * stepX)) >> 8;
                int y = (gridY + (row * stepX) - (column * stepY)) >> 8;

                int level = Math.Clamp(grey[column, row], 0, patterns.Count - 1);
                region.Combine(patterns[level], x, y, combination);
            }
        }

        return region;
    }

    /// <summary>
    /// Marks the grid positions whose pattern would fall entirely outside the region (§6.6.5.1).
    /// Those grey values are never coded at all, so the decoder has to know to expect nothing.
    /// </summary>
    private static Jbig2Bitmap BuildSkip(
        int gridWidth, int gridHeight, int gridX, int gridY, int stepX, int stepY,
        int patternWidth, int patternHeight, int width, int height)
    {
        var skip = new Jbig2Bitmap(gridWidth, gridHeight);

        for (int row = 0; row < gridHeight; row++)
        {
            for (int column = 0; column < gridWidth; column++)
            {
                int x = (gridX + (row * stepY) + (column * stepX)) >> 8;
                int y = (gridY + (row * stepX) - (column * stepY)) >> 8;

                if (x + patternWidth <= 0 || x >= width || y + patternHeight <= 0 || y >= height)
                    skip[column, row] = 1;
            }
        }

        return skip;
    }

    /// <summary>
    /// Reads the grey levels as Gray-coded bit planes, most significant first (Annex C.5). Each
    /// plane after the first is exclusive-ored with the one above it to undo the Gray coding.
    /// </summary>
    private static int[,] DecodeGrey(
        ReadOnlyMemory<byte> data, bool mmr, int template, int width, int height, int planes, Jbig2Bitmap? skip)
    {
        var decoded = new Jbig2Bitmap[planes];

        Span<(int X, int Y)> at = [(template <= 1 ? 3 : 2, -1), (-3, -1), (2, -2), (-2, -2)];

        if (mmr)
        {
            // Each plane is its own fax-coded stream, and because the length is not stated it must
            // end with the end-of-block pattern; the next plane starts right after it (§6.2.6).
            ReadOnlyMemory<byte> remaining = data;

            for (int i = 0; i < planes; i++)
            {
                decoded[planes - 1 - i] =
                    Jbig2GenericRegion.DecodeMmr(remaining.Span, width, height, out int consumed);

                remaining = remaining[Math.Clamp(consumed, 0, remaining.Length)..];
            }
        }
        else
        {
            var decoder = new MqDecoder(data);
            MqContext[] contexts = MqDecoder.CreateContexts(1 << 16);

            for (int i = 0; i < planes; i++)
            {
                decoded[planes - 1 - i] = Jbig2GenericRegion.Decode(
                    decoder, contexts, width, height, template, at, typicalPrediction: false, skip);
            }
        }

        for (int plane = planes - 2; plane >= 0; plane--)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    decoded[plane][x, y] ^= decoded[plane + 1][x, y];
            }
        }

        var values = new int[width, height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int value = 0;
                for (int plane = 0; plane < planes; plane++)
                    value |= decoded[plane][x, y] << plane;

                values[x, y] = value;
            }
        }

        return values;
    }
}
