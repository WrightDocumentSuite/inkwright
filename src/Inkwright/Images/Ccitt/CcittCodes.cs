namespace Inkwright.Images.Ccitt;

/// <summary>What a two-dimensional code word says to do next (ITU-T T.4 Table 4).</summary>
internal enum CcittMode
{
    /// <summary>The run passes under the changing element above; no colour change yet.</summary>
    Pass,

    /// <summary>Two run lengths follow, coded the one-dimensional way.</summary>
    Horizontal,

    /// <summary>The change happens near the one above, offset by minus three to plus three.</summary>
    Vertical,

    /// <summary>An extension escape, which this decoder does not follow.</summary>
    Extension,

    /// <summary>An end-of-line pattern, which resets to the start of the next row.</summary>
    EndOfLine,
}

/// <summary>
/// The code tables of ITU-T T.4 and T.6. They are written out as the standard prints them, one
/// binary string per run length, because a table transcribed from a specification is only worth
/// anything if it can be read back against the page it came from.
/// </summary>
/// <remarks>
/// Terminating codes are Table 2/T.4, make-up codes Tables 3a and 3b/T.4, and the two-dimensional
/// modes Table 4/T.4 (identical to Table 1/T.6). Every code here was checked against the reference
/// implementation published with ITU-T T.88.
/// </remarks>
internal static class CcittCodes
{
    /// <summary>The longest code word in any table, which bounds how far the decoder reads ahead.</summary>
    internal const int MaxCodeLength = 14;

    /// <summary>The end-of-line pattern: eleven zeros and a one, optionally preceded by fill bits.</summary>
    internal const int EndOfLineLength = 12;

    /// <summary>Runs of white, 0 to 63 (Table 2/T.4).</summary>
    private const string WhiteTerminating = """
        00110101 000111 0111 1000 1011 1100 1110 1111
        10011 10100 00111 01000 001000 000011 110100 110101
        101010 101011 0100111 0001100 0001000 0010111 0000011 0000100
        0101000 0101011 0010011 0100100 0011000 00000010 00000011 00011010
        00011011 00010010 00010011 00010100 00010101 00010110 00010111 00101000
        00101001 00101010 00101011 00101100 00101101 00000100 00000101 00001010
        00001011 01010010 01010011 01010100 01010101 00100100 00100101 01011000
        01011001 01011010 01011011 01001010 01001011 00110010 00110011 00110100
        """;

    /// <summary>Runs of black, 0 to 63 (Table 2/T.4).</summary>
    private const string BlackTerminating = """
        0000110111 010 11 10 011 0011 0010 00011
        000101 000100 0000100 0000101 0000111 00000100 00000111 000011000
        0000010111 0000011000 0000001000 00001100111 00001101000 00001101100 00000110111 00000101000
        00000010111 00000011000 000011001010 000011001011 000011001100 000011001101 000001101000 000001101001
        000001101010 000001101011 000011010010 000011010011 000011010100 000011010101 000011010110 000011010111
        000001101100 000001101101 000011011010 000011011011 000001010100 000001010101 000001010110 000001010111
        000001100100 000001100101 000001010010 000001010011 000000100100 000000110111 000000111000 000000100111
        000000101000 000001011000 000001011001 000000101011 000000101100 000001011010 000001100110 000001100111
        """;

    /// <summary>Runs of white in multiples of 64, from 64 to 1728 (Table 3a/T.4).</summary>
    private const string WhiteMakeUp = """
        11011 10010 010111 0110111 00110110 00110111 01100100 01100101
        01101000 01100111 011001100 011001101 011010010 011010011 011010100 011010101
        011010110 011010111 011011000 011011001 011011010 011011011 010011000 010011001
        010011010 011000 010011011
        """;

    /// <summary>Runs of black in multiples of 64, from 64 to 1728 (Table 3a/T.4).</summary>
    private const string BlackMakeUp = """
        0000001111 000011001000 000011001001 000001011011 000000110011 000000110100 000000110101 0000001101100
        0000001101101 0000001001010 0000001001011 0000001001100 0000001001101 0000001110010 0000001110011 0000001110100
        0000001110101 0000001110110 0000001110111 0000001010010 0000001010011 0000001010100 0000001010101 0000001011010
        0000001011011 0000001100100 0000001100101
        """;

    /// <summary>
    /// Runs from 1792 to 2560, shared by both colours (Table 3b/T.4). Terminals that handle wider
    /// paper than the standard width need them, and a decoder meets them in any scan wider than
    /// about A4 at fine resolution.
    /// </summary>
    private const string SharedMakeUp = """
        00000001000 00000001100 00000001101 000000010010 000000010011 000000010100 000000010101 000000010110
        000000010111 000000011100 000000011101 000000011110 000000011111
        """;

    private static readonly CcittRunTable White = BuildRuns(WhiteTerminating, WhiteMakeUp);
    private static readonly CcittRunTable Black = BuildRuns(BlackTerminating, BlackMakeUp);
    private static readonly CcittModeTable Modes = BuildModes();

    /// <summary>The run-length codes for one colour.</summary>
    /// <param name="white">Whether to read white runs rather than black.</param>
    internal static CcittRunTable Runs(bool white) => white ? White : Black;

    /// <summary>The two-dimensional mode codes.</summary>
    internal static CcittModeTable ModeCodes => Modes;

    private static CcittRunTable BuildRuns(string terminating, string makeUp)
    {
        var table = new CcittRunTable();

        int run = 0;
        foreach (string code in Split(terminating))
            table.Add(code, run++);

        run = 64;
        foreach (string code in Split(makeUp))
        {
            table.Add(code, run);
            run += 64;
        }

        run = 1792;
        foreach (string code in Split(SharedMakeUp))
        {
            table.Add(code, run);
            run += 64;
        }

        return table;
    }

    private static CcittModeTable BuildModes()
    {
        var table = new CcittModeTable();

        table.Add("0001", CcittMode.Pass, 0);
        table.Add("001", CcittMode.Horizontal, 0);
        table.Add("1", CcittMode.Vertical, 0);
        table.Add("011", CcittMode.Vertical, 1);
        table.Add("000011", CcittMode.Vertical, 2);
        table.Add("0000011", CcittMode.Vertical, 3);
        table.Add("010", CcittMode.Vertical, -1);
        table.Add("000010", CcittMode.Vertical, -2);
        table.Add("0000010", CcittMode.Vertical, -3);

        // The extension escapes are recognised so that a stream using one is refused rather than
        // read as whatever the bits happen to look like.
        table.Add("0000001", CcittMode.Extension, 0);
        table.Add("000000001", CcittMode.Extension, 1);

        table.Add("000000000001", CcittMode.EndOfLine, 0);

        return table;
    }

    private static IEnumerable<string> Split(string block) =>
        block.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A prefix-code lookup. The codes are not all the same length and no code is a prefix of another,
/// so decoding reads one bit at a time and asks after each whether what it holds is a whole word.
/// </summary>
internal sealed class CcittRunTable
{
    private readonly Dictionary<int, int> _runs = [];

    internal void Add(string code, int run) => _runs[Key(code.Length, Parse(code))] = run;

    /// <summary>Looks up a code of a given length.</summary>
    /// <param name="length">How many bits the candidate holds.</param>
    /// <param name="code">The bits, most significant first.</param>
    /// <param name="run">Receives the run length.</param>
    internal bool TryGet(int length, int code, out int run) => _runs.TryGetValue(Key(length, code), out run);

    internal static int Key(int length, int code) => (length << 16) | code;

    internal static int Parse(string code)
    {
        int value = 0;
        foreach (char bit in code)
            value = (value << 1) | (bit == '1' ? 1 : 0);

        return value;
    }
}

/// <summary>The same kind of lookup for the two-dimensional modes, which carry an offset as well.</summary>
internal sealed class CcittModeTable
{
    private readonly Dictionary<int, (CcittMode Mode, int Offset)> _modes = [];

    internal void Add(string code, CcittMode mode, int offset) =>
        _modes[CcittRunTable.Key(code.Length, CcittRunTable.Parse(code))] = (mode, offset);

    /// <summary>Looks up a mode code of a given length.</summary>
    /// <param name="length">How many bits the candidate holds.</param>
    /// <param name="code">The bits, most significant first.</param>
    /// <param name="mode">Receives the mode.</param>
    /// <param name="offset">Receives the vertical offset, which is meaningful only for vertical mode.</param>
    internal bool TryGet(int length, int code, out CcittMode mode, out int offset)
    {
        if (_modes.TryGetValue(CcittRunTable.Key(length, code), out (CcittMode Mode, int Offset) found))
        {
            (mode, offset) = found;
            return true;
        }

        mode = CcittMode.Pass;
        offset = 0;
        return false;
    }
}
