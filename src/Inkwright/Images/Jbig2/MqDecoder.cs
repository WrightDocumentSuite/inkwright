namespace Inkwright.Images.Jbig2;

/// <summary>
/// One adaptive probability context: which row of the estimation table it currently sits on, and
/// which symbol it currently believes is the more probable one.
/// </summary>
internal struct MqContext
{
    internal byte Index;
    internal byte Mps;
}

/// <summary>
/// The MQ arithmetic decoder of ITU-T T.88 Annex E. Arithmetic coding does not spend a whole bit on
/// each decision: it narrows an interval in proportion to how likely each outcome was, so a long
/// run of predictable pixels costs a fraction of a bit apiece. What makes it adaptive is that each
/// context carries its own probability estimate, revised every time the interval is renormalised.
/// </summary>
internal sealed class MqDecoder
{
    /// <summary>
    /// The probability estimation table (Table E.1). Each row gives the sub-interval assigned to
    /// the less probable symbol, the rows to move to after renormalising on a more or less probable
    /// symbol, and whether the sense of the more probable symbol flips.
    /// </summary>
    private static readonly (ushort Qe, byte Nmps, byte Nlps, byte Switch)[] States =
    [
        (0x5601, 1, 1, 1), (0x3401, 2, 6, 0), (0x1801, 3, 9, 0), (0x0AC1, 4, 12, 0),
        (0x0521, 5, 29, 0), (0x0221, 38, 33, 0), (0x5601, 7, 6, 1), (0x5401, 8, 14, 0),
        (0x4801, 9, 14, 0), (0x3801, 10, 14, 0), (0x3001, 11, 17, 0), (0x2401, 12, 18, 0),
        (0x1C01, 13, 20, 0), (0x1601, 29, 21, 0), (0x5601, 15, 14, 1), (0x5401, 16, 14, 0),
        (0x5101, 17, 15, 0), (0x4801, 18, 16, 0), (0x3801, 19, 17, 0), (0x3401, 20, 18, 0),
        (0x3001, 21, 19, 0), (0x2801, 22, 19, 0), (0x2401, 23, 20, 0), (0x2201, 24, 21, 0),
        (0x1C01, 25, 22, 0), (0x1801, 26, 23, 0), (0x1601, 27, 24, 0), (0x1401, 28, 25, 0),
        (0x1201, 29, 26, 0), (0x1101, 30, 27, 0), (0x0AC1, 31, 28, 0), (0x09C1, 32, 29, 0),
        (0x08A1, 33, 30, 0), (0x0521, 34, 31, 0), (0x0441, 35, 32, 0), (0x02A1, 36, 33, 0),
        (0x0221, 37, 34, 0), (0x0141, 38, 35, 0), (0x0111, 39, 36, 0), (0x0085, 40, 37, 0),
        (0x0049, 41, 38, 0), (0x0025, 42, 39, 0), (0x0015, 43, 40, 0), (0x0009, 44, 41, 0),
        (0x0005, 45, 42, 0), (0x0001, 45, 43, 0), (0x5601, 46, 46, 0),
    ];

    private readonly ReadOnlyMemory<byte> _data;
    private int _position;
    private uint _c;
    private uint _a;
    private int _ct;

    /// <summary>Starts a decoder over a stretch of coded data (INITDEC, Figure E.20).</summary>
    /// <param name="data">The arithmetically coded bytes.</param>
    internal MqDecoder(ReadOnlyMemory<byte> data)
    {
        _data = data;
        _position = 0;

        _c = (uint)Byte(0) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>Decodes one decision against a context (DECODE, Figure E.15).</summary>
    /// <param name="contexts">The context storage the caller keeps for this kind of decision.</param>
    /// <param name="cx">Which context in that storage applies.</param>
    /// <returns>The decoded bit.</returns>
    internal int Decode(MqContext[] contexts, int cx)
    {
        ref MqContext context = ref contexts[cx];
        (ushort qe, byte nmps, byte nlps, byte flip) = States[context.Index];

        _a -= qe;
        int d;

        if (((_c >> 16) & 0xFFFF) < qe)
        {
            // The code string fell in the sub-interval of the less probable symbol. Which symbol
            // that actually is depends on whether the two intervals ended up inverted in size.
            if (_a < qe)
            {
                _a = qe;
                d = context.Mps;
                context.Index = nmps;
            }
            else
            {
                _a = qe;
                d = 1 - context.Mps;

                if (flip == 1)
                    context.Mps = (byte)(1 - context.Mps);

                context.Index = nlps;
            }
        }
        else
        {
            _c -= (uint)qe << 16;

            if ((_a & 0x8000) != 0)
                return context.Mps;

            if (_a < qe)
            {
                d = 1 - context.Mps;

                if (flip == 1)
                    context.Mps = (byte)(1 - context.Mps);

                context.Index = nlps;
            }
            else
            {
                d = context.Mps;
                context.Index = nmps;
            }
        }

        Renormalise();
        return d;
    }

    /// <summary>RENORMD, Figure E.18: shift until the interval is back in range.</summary>
    private void Renormalise()
    {
        do
        {
            if (_ct == 0)
                ByteIn();

            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);

        _c &= 0xFFFFFFFF;
    }

    /// <summary>
    /// BYTEIN, Figure E.19: pull the next byte into the code register. A <c>0xFF</c> byte is
    /// special, because the encoder never emits a value above <c>0x8F</c> after one; anything
    /// higher marks the end of the coded data, and the decoder feeds itself ones from then on.
    /// </summary>
    private void ByteIn()
    {
        if (Byte(_position) == 0xFF)
        {
            if (Byte(_position + 1) > 0x8F)
            {
                _c += 0xFF00;
                _ct = 8;
            }
            else
            {
                _position++;
                _c += (uint)Byte(_position) << 9;
                _ct = 7;
            }
        }
        else
        {
            _position++;
            _c += (uint)Byte(_position) << 8;
            _ct = 8;
        }
    }

    /// <summary>Past the end of the data every byte reads as <c>0xFF</c>, which stops the decoder.</summary>
    private int Byte(int index) => index < _data.Length ? _data.Span[index] : 0xFF;

    /// <summary>Creates a fresh block of contexts, all starting at the first row with zero as likely.</summary>
    /// <param name="count">How many contexts the caller addresses.</param>
    internal static MqContext[] CreateContexts(int count) => new MqContext[count];
}
