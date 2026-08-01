namespace Inkwright.Images.Jbig2;

/// <summary>
/// The symbol identifier codes a Huffman text region carries (ITU-T T.88 §7.4.3.1.7). The region
/// needs a code per symbol, and rather than spend a fixed width on each it sends the code lengths,
/// run-length coded and themselves Huffman coded — the same trick deflate uses, and for the same
/// reason: in a page of text most symbols are rare and a few are everywhere.
/// </summary>
internal static class Jbig2SymbolCodes
{
    /// <summary>How many run codes the table defines (Table 32).</summary>
    private const int RunCodeCount = 35;

    /// <summary>Reads the symbol identifier table.</summary>
    /// <param name="bits">The bit reader, positioned at the start of the table.</param>
    /// <param name="symbols">How many symbols need codes.</param>
    internal static Jbig2HuffmanTable Read(Jbig2BitReader bits, int symbols)
    {
        // Each run code's own length is a plain four-bit field.
        var runLengths = new int[RunCodeCount];
        for (int i = 0; i < RunCodeCount; i++)
            runLengths[i] = (int)bits.ReadBits(4);

        Jbig2HuffmanTable runCodes = Jbig2HuffmanTable.FromLengths(runLengths);

        var lengths = new int[Math.Max(symbols, 1)];
        int index = 0;
        int previous = 0;

        while (index < symbols)
        {
            int code = runCodes.Decode(bits);

            switch (code)
            {
                case < 32:
                    lengths[index++] = code;
                    previous = code;
                    break;

                // Repeat what came before, three to six times.
                case 32:
                    Repeat(lengths, ref index, symbols, previous, (int)bits.ReadBits(2) + 3);
                    break;

                // A run of unused symbols, three to ten of them.
                case 33:
                    Repeat(lengths, ref index, symbols, 0, (int)bits.ReadBits(3) + 3);
                    break;

                // A longer run of unused symbols, eleven to a hundred and thirty-eight.
                case 34:
                    Repeat(lengths, ref index, symbols, 0, (int)bits.ReadBits(7) + 11);
                    break;

                default:
                    throw new NotSupportedException($"A JBIG2 symbol code table used run code {code}.");
            }
        }

        // The region's own data starts on a byte boundary after the table.
        bits.AlignToByte();

        return Jbig2HuffmanTable.FromLengths(lengths[..Math.Max(symbols, 1)]);
    }

    private static void Repeat(int[] lengths, ref int index, int symbols, int value, int count)
    {
        for (int i = 0; i < count && index < symbols; i++)
            lengths[index++] = value;
    }
}
