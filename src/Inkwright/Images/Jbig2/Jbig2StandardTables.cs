namespace Inkwright.Images.Jbig2;

/// <summary>
/// The fifteen standard Huffman tables of ITU-T T.88 Annex B.5. A file may use these without
/// transmitting them, and most do: they were chosen to match the distributions that actually occur
/// in scanned text, so a custom table rarely earns its own size.
/// </summary>
/// <remarks>
/// Each line is written as the standard prints it: the prefix code in binary, the width of the
/// offset field, and the value the offset counts from. A line marked <c>lower</c> counts downwards,
/// and <c>oob</c> carries no value at all.
/// </remarks>
internal static class Jbig2StandardTables
{
    /// <summary>Table B.1, used for sizes and counts that are never negative.</summary>
    private const string TableA = """
        0     4  0
        10    8  16
        110   16 272
        111   32 65808
        """;

    /// <summary>Table B.2, which can also signal the end of a list.</summary>
    private const string TableB = """
        0      0  0
        10     0  1
        110    0  2
        1110   3  3
        11110  6  11
        111110 32 75
        111111 0  0 oob
        """;

    /// <summary>Table B.3, table B's range extended downwards.</summary>
    private const string TableC = """
        11111110 8  -256
        0        0  0
        10       0  1
        110      0  2
        1110     3  3
        11110    6  11
        11111111 32 -257 lower
        1111110  32 75
        111110   0  0 oob
        """;

    /// <summary>Table B.4, used where the smallest value is one rather than zero.</summary>
    private const string TableD = """
        0     0  1
        10    0  2
        110   0  3
        1110  3  4
        11110 6  12
        11111 32 76
        """;

    /// <summary>Table B.5, table D's range extended downwards.</summary>
    private const string TableE = """
        1111110 8  -255
        0       0  1
        10      0  2
        110     0  3
        1110    3  4
        11110   6  12
        1111111 32 -256 lower
        111110  32 76
        """;

    /// <summary>Table B.6, for the first coordinate of a strip, which spreads widely either way.</summary>
    private const string TableF = """
        11100  10 -2048
        1000   9  -1024
        1001   8  -512
        1010   7  -256
        11101  6  -128
        11110  5  -64
        1011   5  -32
        00     7  0
        010    7  128
        011    8  256
        1100   9  512
        1101   10 1024
        111110 32 -2049 lower
        111111 32 2048
        """;

    /// <summary>Table B.7, table F's alternative weighting.</summary>
    private const string TableG = """
        1000  9  -1024
        000   8  -512
        1001  7  -256
        11010 6  -128
        11011 5  -64
        1010  5  -32
        1011  5  0
        11100 5  32
        11101 6  64
        1100  7  128
        001   8  256
        010   9  512
        011   10 1024
        11110 32 -1025 lower
        11111 32 2048
        """;

    /// <summary>Table B.8, for the gap between symbols, which is usually tiny.</summary>
    private const string TableH = """
        11111100  3  -15
        111111100 1  -7
        11111101  1  -5
        111111101 0  -3
        1111100   0  -2
        1010      0  -1
        00        1  0
        11010     0  2
        111010    0  3
        100       4  4
        111011    1  20
        1011      4  22
        1100      5  38
        11011     6  70
        11100     7  134
        111100    7  262
        1111101   8  390
        111101    10 646
        111111110 32 -16 lower
        111111111 32 1670
        01        0  0 oob
        """;

    /// <summary>Table B.9, table H at twice the granularity.</summary>
    private const string TableI = """
        11111100  4  -31
        111111100 2  -15
        11111101  2  -11
        111111101 1  -7
        1111100   1  -5
        1010      1  -3
        010       1  -1
        011       1  1
        11010     1  3
        111010    1  5
        100       5  7
        111011    2  39
        1011      5  43
        1100      6  75
        11011     7  139
        11100     8  267
        111100    8  523
        1111101   9  779
        111101    11 1291
        111111110 32 -32 lower
        111111111 32 3339
        00        0  0 oob
        """;

    /// <summary>Table B.10, for gaps in a widely spaced region.</summary>
    private const string TableJ = """
        1111010  4  -21
        11111100 0  -5
        1111011  0  -4
        11000    0  -3
        00       2  -2
        11001    0  2
        110110   0  3
        1111100  0  4
        11111101 0  5
        01       6  6
        11010    5  70
        110111   5  102
        111000   6  134
        111001   7  198
        111010   8  326
        111011   9  582
        111100   10 1094
        1111101  11 2118
        11111110 32 -22 lower
        11111111 32 4166
        10       0  0 oob
        """;

    /// <summary>Table B.11, for the difference between strip positions.</summary>
    private const string TableK = """
        0       0  1
        10      1  2
        1100    0  4
        1101    1  5
        11100   1  7
        11101   2  9
        111100  2  13
        1111010 2  17
        1111011 3  21
        1111100 4  29
        1111101 5  45
        1111110 6  77
        1111111 32 141
        """;

    /// <summary>Table B.12, for refinement sizes.</summary>
    private const string TableL = """
        0        0  1
        10       0  2
        110      1  3
        11100    0  5
        11101    1  6
        111100   1  8
        1111010  0  10
        1111011  1  11
        1111100  2  13
        1111101  3  17
        1111110  4  25
        11111110 5  41
        11111111 32 73
        """;

    /// <summary>Table B.13, another weighting of table K.</summary>
    private const string TableM = """
        0       0  1
        100     0  2
        1100    0  3
        11100   0  4
        1101    1  5
        101     3  7
        111010  1  15
        111011  2  17
        111100  3  21
        111101  4  29
        111110  5  45
        1111110 6  77
        1111111 32 141
        """;

    /// <summary>Table B.14, five values around zero and nothing else.</summary>
    private const string TableN = """
        100 0 -2
        101 0 -1
        0   0 0
        110 0 1
        111 0 2
        """;

    /// <summary>Table B.15, for refinement offsets, which cluster tightly around zero.</summary>
    private const string TableO = """
        1111100 4  -24
        111100  2  -8
        11100   1  -4
        1100    0  -2
        100     0  -1
        0       0  0
        101     0  1
        1101    0  2
        11101   1  3
        111101  2  5
        1111101 4  9
        1111110 32 -25 lower
        1111111 32 25
        """;

    private static readonly Lazy<Jbig2HuffmanTable[]> Tables = new(Build);

    /// <summary>One of the standard tables, numbered as the standard numbers them from one.</summary>
    /// <param name="number">The table number, 1 through 15.</param>
    internal static Jbig2HuffmanTable Get(int number)
    {
        if (number is < 1 or > 15)
            throw new NotSupportedException($"There is no standard JBIG2 Huffman table {number}.");

        return Tables.Value[number - 1];
    }

    /// <summary>
    /// The prefix codes of one standard table, for checking a transcription against the page it
    /// came from.
    /// </summary>
    /// <param name="number">The table number, 1 through 15.</param>
    internal static IEnumerable<(int Length, int Code)> Codes(int number)
    {
        string[] sources =
        [
            TableA, TableB, TableC, TableD, TableE, TableF, TableG, TableH,
            TableI, TableJ, TableK, TableL, TableM, TableN, TableO,
        ];

        foreach (string text in sources[number - 1].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            int code = 0;
            foreach (char bit in parts[0])
                code = (code << 1) | (bit == '1' ? 1 : 0);

            yield return (parts[0].Length, code);
        }
    }

    private static Jbig2HuffmanTable[] Build()
    {
        string[] sources =
        [
            TableA, TableB, TableC, TableD, TableE, TableF, TableG, TableH,
            TableI, TableJ, TableK, TableL, TableM, TableN, TableO,
        ];

        var tables = new Jbig2HuffmanTable[sources.Length];
        for (int i = 0; i < sources.Length; i++)
            tables[i] = Parse(sources[i]);

        return tables;
    }

    /// <summary>
    /// Reads the transcribed form. Because the standard prints the prefix codes themselves, they
    /// are used as written rather than reassigned, which keeps each table checkable against the
    /// page it came from.
    /// </summary>
    private static Jbig2HuffmanTable Parse(string source)
    {
        var lines = new List<Jbig2HuffmanLine>();

        foreach (string text in source.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            string prefix = parts[0];
            int code = 0;
            foreach (char bit in prefix)
                code = (code << 1) | (bit == '1' ? 1 : 0);

            Jbig2HuffmanLineKind kind = parts.Length > 3
                ? parts[3] switch
                {
                    "lower" => Jbig2HuffmanLineKind.Lower,
                    "oob" => Jbig2HuffmanLineKind.OutOfBand,
                    _ => Jbig2HuffmanLineKind.Normal,
                }
                : Jbig2HuffmanLineKind.Normal;

            lines.Add(new Jbig2HuffmanLine(
                prefix.Length,
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                kind)
            {
                Code = code,
            });
        }

        return Jbig2HuffmanTable.FromAssigned(lines);
    }
}
