using Inkwright.Images.Jbig2;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// The Huffman machinery of ITU-T T.88 Annex B, checked against the worked example the standard
/// prints in B.4 and against the standard tables of B.5.
/// </summary>
public class Jbig2HuffmanTests
{
    [Fact]
    public void ReadsTheCustomTableTheStandardWorksThrough()
    {
        // The example encoding of B.4, which the standard says reproduces table B.1.
        byte[] encoded =
            [0x42, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x10, 0x49, 0x23, 0x81, 0x80];

        Jbig2HuffmanTable table = Jbig2HuffmanTable.ReadCustom(encoded);

        // Prefix 0 then four bits covers 0 to 15.
        Assert.Equal(0, Decode(table, "0 0000"));
        Assert.Equal(9, Decode(table, "0 1001"));
        Assert.Equal(15, Decode(table, "0 1111"));

        // Prefix 10 then eight bits covers 16 to 271.
        Assert.Equal(16, Decode(table, "10 00000000"));
        Assert.Equal(271, Decode(table, "10 11111111"));

        // Prefix 110 then sixteen bits covers 272 upwards.
        Assert.Equal(272, Decode(table, "110 0000000000000000"));

        // Prefix 111 then thirty-two bits covers the open-ended top.
        Assert.Equal(65808, Decode(table, "111 00000000000000000000000000000000"));
    }

    [Fact]
    public void AgreesWithTheStandardTableItReproduces()
    {
        byte[] encoded =
            [0x42, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x10, 0x49, 0x23, 0x81, 0x80];

        Jbig2HuffmanTable custom = Jbig2HuffmanTable.ReadCustom(encoded);
        Jbig2HuffmanTable standard = Jbig2StandardTables.Get(1);

        // The transcribed table B.1 and the table decoded from the example must agree everywhere.
        foreach (string bits in new[]
                 {
                     "0 0000", "0 0111", "0 1111", "10 00000000", "10 10000000",
                     "110 0000000000000001", "111 00000000000000000000000000000001",
                 })
        {
            Assert.Equal(Decode(custom, bits), Decode(standard, bits));
        }
    }

    [Fact]
    public void SignalsOutOfBandWhereTheTableAllowsIt()
    {
        // Table B.2 ends a list with the prefix 111111.
        Assert.Equal(Jbig2IntDecoder.OutOfBand, Decode(Jbig2StandardTables.Get(2), "111111"));

        // Its first four lines are single values with no offset field at all.
        Assert.Equal(0, Decode(Jbig2StandardTables.Get(2), "0"));
        Assert.Equal(1, Decode(Jbig2StandardTables.Get(2), "10"));
        Assert.Equal(2, Decode(Jbig2StandardTables.Get(2), "110"));
        Assert.Equal(5, Decode(Jbig2StandardTables.Get(2), "1110 010"));
    }

    [Fact]
    public void CountsDownwardsOnTheOpenEndedLowRange()
    {
        // Table B.3's lower range line: the offset is subtracted from minus two hundred and fifty-seven.
        Assert.Equal(-257, Decode(Jbig2StandardTables.Get(3), "11111111 " + new string('0', 32)));
        Assert.Equal(-258, Decode(Jbig2StandardTables.Get(3), "11111111 " + new string('0', 31) + "1"));

        // And its ordinary negative line counts upwards from minus two hundred and fifty-six.
        Assert.Equal(-256, Decode(Jbig2StandardTables.Get(3), "11111110 00000000"));
        Assert.Equal(-1, Decode(Jbig2StandardTables.Get(3), "11111110 11111111"));
    }

    [Fact]
    public void EveryStandardTableIsPrefixFree()
    {
        // A table where one code is a prefix of another cannot be decoded unambiguously, so a
        // transcription slip almost always shows up as a collision.
        for (int number = 1; number <= 15; number++)
        {
            Jbig2HuffmanTable table = Jbig2StandardTables.Get(number);
            var seen = new List<(int Length, int Code)>();

            foreach ((int length, int code) in Jbig2StandardTables.Codes(number))
            {
                foreach ((int otherLength, int otherCode) in seen)
                {
                    int shorter = Math.Min(length, otherLength);
                    bool clash = (code >> (length - shorter)) == (otherCode >> (otherLength - shorter));

                    Assert.False(clash, $"Table {number} has codes that collide at {length} and {otherLength} bits.");
                }

                seen.Add((length, code));
            }

            Assert.NotEmpty(seen);
        }
    }

    [Fact]
    public void AssignsCanonicalCodesFromLengthsAlone()
    {
        // Lengths 2,1,3,3 give the canonical assignment 10, 0, 110, 111.
        Jbig2HuffmanTable table = Jbig2HuffmanTable.FromLengths([2, 1, 3, 3]);

        Assert.Equal(1, Decode(table, "0"));
        Assert.Equal(0, Decode(table, "10"));
        Assert.Equal(2, Decode(table, "110"));
        Assert.Equal(3, Decode(table, "111"));
    }

    private static int Decode(Jbig2HuffmanTable table, string bits)
    {
        string clean = bits.Replace(" ", string.Empty, StringComparison.Ordinal);
        byte[] data = new byte[(clean.Length + 7) / 8];

        for (int i = 0; i < clean.Length; i++)
        {
            if (clean[i] == '1')
                data[i >> 3] |= (byte)(0x80 >> (i & 7));
        }

        return table.Decode(new Jbig2BitReader(data));
    }
}
