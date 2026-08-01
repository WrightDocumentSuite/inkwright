namespace Inkwright.Images.Jbig2;

/// <summary>
/// One of the arithmetic integer decoding procedures of ITU-T T.88 Annex A. Each kind of number a
/// symbol dictionary or text region reads — a height difference, a coordinate, an export flag —
/// gets its own procedure, and each procedure keeps its own probability estimates, so a decoder
/// learns the shape of heights separately from the shape of coordinates.
/// </summary>
/// <remarks>
/// The value is coded as a decision tree (Table A.1): a sign bit, then a run of bits that says how
/// wide the magnitude field is, then the magnitude. Small numbers therefore cost few bits, which is
/// what most of these values are.
/// </remarks>
internal sealed class Jbig2IntDecoder
{
    /// <summary>
    /// A context is nine bits: a leading one and the eight most recently decoded bits, so 512
    /// estimates cover every state the tree can reach.
    /// </summary>
    private const int ContextCount = 512;

    /// <summary>What the standard writes as OOB, the out-of-band value that ends a list.</summary>
    internal const int OutOfBand = int.MinValue;

    private readonly MqContext[] _contexts = MqDecoder.CreateContexts(ContextCount);

    /// <summary>Decodes one value (Figure A.1).</summary>
    /// <param name="decoder">The arithmetic decoder to read decisions from.</param>
    /// <returns>The value, or <see cref="OutOfBand"/> when the coder signalled out of band.</returns>
    internal int Decode(MqDecoder decoder)
    {
        int prev = 1;

        int Bit()
        {
            int d = decoder.Decode(_contexts, prev);

            // PREV holds a leading one and the eight most recent bits; once it is full the leading
            // one is kept in place and the oldest bit falls off the bottom of the window.
            prev = prev < 256 ? (prev << 1) | d : ((((prev << 1) | d) & 511) | 256);
            return d;
        }

        int Bits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
                value = (value << 1) | Bit();

            return value;
        }

        int sign = Bit();

        // Each zero decision stops the widening; each one moves to the next-wider magnitude field.
        int magnitude =
            Bit() == 0 ? Bits(2)
            : Bit() == 0 ? Bits(4) + 4
            : Bit() == 0 ? Bits(6) + 20
            : Bit() == 0 ? Bits(8) + 84
            : Bit() == 0 ? Bits(12) + 340
            : Bits(32) + 4436;

        if (sign == 0)
            return magnitude;

        // A negative zero is not a number: the standard spends that spare code on out of band.
        return magnitude > 0 ? -magnitude : OutOfBand;
    }

    /// <summary>Whether a decoded value was the out-of-band marker rather than a number.</summary>
    /// <param name="value">The value to test.</param>
    internal static bool IsOutOfBand(int value) => value == OutOfBand;
}

/// <summary>
/// The symbol identifier decoder of ITU-T T.88 Annex A.3. It differs from every other integer
/// procedure: the width is fixed at <c>SBSYMCODELEN</c> bits, and the context is every bit decoded
/// so far rather than a sliding window of eight.
/// </summary>
internal sealed class Jbig2IdDecoder
{
    private readonly MqContext[] _contexts;
    private readonly int _codeLength;

    /// <summary>Creates a decoder for symbol identifiers of a given width.</summary>
    /// <param name="codeLength">How many bits a symbol identifier takes.</param>
    internal Jbig2IdDecoder(int codeLength)
    {
        _codeLength = Math.Clamp(codeLength, 0, 24);
        _contexts = MqDecoder.CreateContexts(1 << (_codeLength + 1));
    }

    /// <summary>Decodes one symbol identifier.</summary>
    /// <param name="decoder">The arithmetic decoder to read decisions from.</param>
    internal int Decode(MqDecoder decoder)
    {
        int prev = 1;

        for (int i = 0; i < _codeLength; i++)
            prev = (prev << 1) | decoder.Decode(_contexts, prev);

        // Drop the leading one that was tracking how many bits had been read.
        return prev - (1 << _codeLength);
    }
}
