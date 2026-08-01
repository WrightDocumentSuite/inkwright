namespace Inkwright.Images.Jbig2;

/// <summary>Which corner of a symbol its recorded position refers to (ITU-T T.88 §6.4.5).</summary>
internal enum Jbig2ReferenceCorner
{
    BottomLeft = 0,
    TopLeft = 1,
    BottomRight = 2,
    TopRight = 3,
}

/// <summary>Everything a text region decoding procedure needs to know before it starts.</summary>
internal sealed class Jbig2TextRegionParameters
{
    /// <summary>The width of the region being built.</summary>
    internal required int Width { get; init; }

    /// <summary>The height of the region being built.</summary>
    internal required int Height { get; init; }

    /// <summary>How many symbol instances the region places.</summary>
    internal required int Instances { get; init; }

    /// <summary>How many rows of the region one strip covers.</summary>
    internal required int Strips { get; init; }

    /// <summary>The base-two logarithm of <see cref="Strips"/>, which the coding uses directly.</summary>
    internal required int LogStrips { get; init; }

    /// <summary>The symbols available to place, in identifier order.</summary>
    internal required IReadOnlyList<Jbig2Bitmap> Symbols { get; init; }

    /// <summary>What the region is filled with before any symbol is drawn.</summary>
    internal required int DefaultPixel { get; init; }

    /// <summary>How a symbol is combined with what is already in the region.</summary>
    internal required Jbig2CombinationOperator Combination { get; init; }

    /// <summary>Whether the coordinate axes are swapped, which is how vertical text is coded.</summary>
    internal required bool Transposed { get; init; }

    /// <summary>Which corner of a symbol its recorded position refers to.</summary>
    internal required Jbig2ReferenceCorner ReferenceCorner { get; init; }

    /// <summary>A constant added to every gap, so that the commonest gap codes as zero.</summary>
    internal required int SOffset { get; init; }

    /// <summary>Whether instances may refine the symbol they name rather than copy it.</summary>
    internal required bool Refine { get; init; }

    /// <summary>Which refinement template applies when an instance refines.</summary>
    internal required int RefinementTemplate { get; init; }

    /// <summary>The adaptive pixel positions for refinement.</summary>
    internal required (int X, int Y)[] RefinementAt { get; init; }
}

/// <summary>
/// The text region decoding procedure of ITU-T T.88 §6.4. A text region is a list of placements:
/// which symbol, and where. Positions are coded as gaps from the previous instance rather than as
/// absolute coordinates, and instances are grouped into horizontal strips, so a line of text costs
/// little more than one small number per letter.
/// </summary>
internal static class Jbig2TextRegion
{
    /// <summary>How many instances one region may place before it is taken to be malformed.</summary>
    private const int MaxInstances = 10_000_000;

    /// <summary>How many contexts a refinement decoder addresses.</summary>
    internal const int RefinementContextCount = 1 << 13;

    /// <summary>
    /// How many bits a symbol identifier takes for a given number of symbols: the ceiling of the
    /// base-two logarithm (§7.4.3.1.1 and §6.5.8.2.3).
    /// </summary>
    /// <param name="symbols">How many symbols are addressable.</param>
    /// <remarks>
    /// There is no floor of one bit here, though there is one in the Huffman variant. A dictionary
    /// holding a single symbol spends nothing at all naming it, and reading one bit anyway throws
    /// the rest of the region out of step.
    /// </remarks>
    internal static int CodeLength(int symbols)
    {
        int length = 0;
        while ((1 << length) < symbols)
            length++;

        return length;
    }

    /// <summary>Decodes a text region (§6.4.5).</summary>
    /// <param name="parameters">The region's settings.</param>
    /// <param name="reader">Where the numbers come from, arithmetic or Huffman.</param>
    /// <param name="refinement">The context storage for refinement decisions.</param>
    internal static Jbig2Bitmap Decode(
        Jbig2TextRegionParameters parameters,
        Jbig2ValueReader reader,
        MqContext[] refinement)
    {
        var region = new Jbig2Bitmap(parameters.Width, parameters.Height);

        if (parameters.DefaultPixel != 0)
            region.Fill(1);

        if (parameters.Instances is < 0 or > MaxInstances)
            throw new NotSupportedException($"A JBIG2 text region claims {parameters.Instances} instances.");

        // The first strip may sit above the region, so the starting coordinate is negated.
        int stripT = -reader.DeltaT() * parameters.Strips;
        int firstS = 0;
        int placed = 0;

        while (placed < parameters.Instances)
        {
            int deltaT = reader.DeltaT();
            if (Jbig2IntDecoder.IsOutOfBand(deltaT))
                break;

            stripT += deltaT * parameters.Strips;

            int currentS = 0;
            bool first = true;

            while (placed < parameters.Instances)
            {
                if (first)
                {
                    firstS += reader.FirstS();
                    currentS = firstS;
                    first = false;
                }
                else
                {
                    int gap = reader.DeltaS();

                    // Out of band ends the strip rather than naming a gap.
                    if (Jbig2IntDecoder.IsOutOfBand(gap))
                        break;

                    currentS += gap + parameters.SOffset;
                }

                int t = stripT + reader.CurrentT(parameters.LogStrips);
                int id = reader.SymbolId();

                Jbig2Bitmap symbol = Instance(parameters, reader, refinement, id);

                Place(region, parameters, symbol, ref currentS, t);
                placed++;
            }
        }

        return region;
    }

    /// <summary>
    /// The bitmap for one instance: the named symbol, or a refinement of it when the region says so
    /// (§6.4.11).
    /// </summary>
    private static Jbig2Bitmap Instance(
        Jbig2TextRegionParameters parameters,
        Jbig2ValueReader reader,
        MqContext[] refinement,
        int id)
    {
        Jbig2Bitmap symbol = id >= 0 && id < parameters.Symbols.Count
            ? parameters.Symbols[id]
            : new Jbig2Bitmap(1, 1);

        if (!parameters.Refine || reader.RefinementFlag() == 0)
            return symbol;

        int deltaWidth = reader.RefinementDeltaWidth();
        int deltaHeight = reader.RefinementDeltaHeight();
        int offsetX = reader.RefinementX();
        int offsetY = reader.RefinementY();

        int width = symbol.Width + deltaWidth;
        int height = symbol.Height + deltaHeight;

        if (width <= 0 || height <= 0 || (long)width * height > 16L * 1024 * 1024)
            return symbol;

        // The Huffman variant states how long the refinement runs and restarts on a byte boundary,
        // because the arithmetic coder that follows cannot share a byte with the bit reader.
        int size = 0;
        Jbig2BitReader? bits = null;
        MqDecoder decoder;

        if (reader is Jbig2HuffmanReader huffman)
        {
            size = reader.RefinementSize();
            bits = huffman.Bits;
            bits.AlignToByte();
            decoder = new MqDecoder(bits.Remaining);
        }
        else
        {
            decoder = reader.Arithmetic
                ?? throw new NotSupportedException("A JBIG2 refinement needs the arithmetic coder.");
        }

        // The halves round towards negative infinity, which is what the standard's division means
        // for the negative deltas that a shrinking refinement produces.
        Jbig2Bitmap refined = Jbig2RefinementRegion.Decode(
            decoder,
            refinement,
            width,
            height,
            parameters.RefinementTemplate,
            symbol,
            Floor2(deltaWidth) + offsetX,
            Floor2(deltaHeight) + offsetY,
            parameters.RefinementAt,
            typicalPrediction: false);

        bits?.SeekByte(bits.BytePosition + size);
        return refined;
    }

    private static int Floor2(int value) => value >> 1;

    /// <summary>
    /// Draws one instance and advances the running coordinate (§6.4.5 steps vi to xi). The gap
    /// between instances is what gets coded, so the width of the symbol has to be added on either
    /// before or after placing it, depending on which corner the position refers to.
    /// </summary>
    private static void Place(
        Jbig2Bitmap region,
        Jbig2TextRegionParameters parameters,
        Jbig2Bitmap symbol,
        ref int currentS,
        int t)
    {
        int width = symbol.Width;
        int height = symbol.Height;
        Jbig2ReferenceCorner corner = parameters.ReferenceCorner;

        if (!parameters.Transposed)
        {
            if (corner is Jbig2ReferenceCorner.TopRight or Jbig2ReferenceCorner.BottomRight)
                currentS += width - 1;
        }
        else if (corner is Jbig2ReferenceCorner.BottomLeft or Jbig2ReferenceCorner.BottomRight)
            currentS += height - 1;

        int s = currentS;

        // The position names one corner; the drawing needs the top left one.
        (int x, int y) = parameters.Transposed
            ? (t, s)
            : (s, t);

        if (!parameters.Transposed)
        {
            if (corner is Jbig2ReferenceCorner.TopRight or Jbig2ReferenceCorner.BottomRight)
                x -= width - 1;

            if (corner is Jbig2ReferenceCorner.BottomLeft or Jbig2ReferenceCorner.BottomRight)
                y -= height - 1;
        }
        else
        {
            if (corner is Jbig2ReferenceCorner.TopRight or Jbig2ReferenceCorner.BottomRight)
                x -= width - 1;

            if (corner is Jbig2ReferenceCorner.BottomLeft or Jbig2ReferenceCorner.BottomRight)
                y -= height - 1;
        }

        region.Combine(symbol, x, y, parameters.Combination);

        if (!parameters.Transposed)
        {
            if (corner is Jbig2ReferenceCorner.TopLeft or Jbig2ReferenceCorner.BottomLeft)
                currentS += width - 1;
        }
        else if (corner is Jbig2ReferenceCorner.TopLeft or Jbig2ReferenceCorner.TopRight)
            currentS += height - 1;
    }
}
