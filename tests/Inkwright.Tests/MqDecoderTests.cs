using Inkwright.Images.Jbig2;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// The arithmetic decoder of ITU-T T.88 Annex E, checked against the test sequence the standard
/// prints in Annex H.2. Agreement with it does not prove a completely correct implementation, as
/// the standard itself says, but disagreement proves an incorrect one.
/// </summary>
public class MqDecoderTests
{
    /// <summary>The 256 decisions of H.2, packed into 32 bytes.</summary>
    private const string Decisions =
        "00 02 00 51 00 00 00 C0 03 52 87 2A AA AA AA AA " +
        "82 C0 20 00 FC D7 9E F6 BF 7F ED 90 4F 46 A3 BF";

    /// <summary>The 30 bytes those decisions encode to.</summary>
    private const string Encoded =
        "84 C7 3B FC E1 A1 43 04 02 20 00 00 41 0D BB 86 " +
        "F4 31 7F FF 88 FF 37 47 1A DB 6A DF FF AC";

    [Fact]
    public void ReproducesTheTestSequenceOfAnnexH()
    {
        byte[] coded = Hex(Encoded);
        byte[] expected = Hex(Decisions);

        // The whole test uses a single context, starting at index zero with zero more probable.
        MqContext[] contexts = MqDecoder.CreateContexts(1);
        var decoder = new MqDecoder(coded);

        for (int i = 0; i < expected.Length * 8; i++)
        {
            int bit = decoder.Decode(contexts, 0);
            int want = (expected[i >> 3] >> (7 - (i & 7))) & 1;

            Assert.Equal(want, bit);
        }
    }

    [Fact]
    public void KeepsGoingPastTheEndOfTheData()
    {
        // Every byte past the end reads as 0xFF, which is what makes a truncated stream stop
        // rather than run off the buffer.
        var decoder = new MqDecoder(new byte[] { 0x84 });
        MqContext[] contexts = MqDecoder.CreateContexts(1);

        for (int i = 0; i < 1000; i++)
            _ = decoder.Decode(contexts, 0);
    }

    [Fact]
    public void StartsEveryContextAtTheFirstEstimate()
    {
        MqContext[] contexts = MqDecoder.CreateContexts(1 << 16);

        Assert.Equal(1 << 16, contexts.Length);
        Assert.All(contexts, context =>
        {
            Assert.Equal(0, context.Index);
            Assert.Equal(0, context.Mps);
        });
    }

    private static byte[] Hex(string text)
    {
        string clean = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        byte[] bytes = new byte[clean.Length / 2];

        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);

        return bytes;
    }
}
