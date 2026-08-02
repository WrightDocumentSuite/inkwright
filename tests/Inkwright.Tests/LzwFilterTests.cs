using Inkwright.Cos;
using Inkwright.Filters;
using Xunit;

namespace Inkwright.Tests;

public class LzwFilterTests
{
    [Fact]
    public void OutOfRangeCodeStopsBeforeItCanReadAPooledDictionaryEntry()
    {
        byte[] malformed = PackNineBitCodes(256, 65, 300, 259, 257);

        Assert.Equal("A"u8.ToArray(), LzwFilter.Decode(malformed));

        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfName.Get("LZWDecode")),
        };
        var stream = new PdfStream(dictionary, malformed);
        PdfBoundedDecodeResult bounded = stream.DecodeBounded(1_024);

        Assert.Equal(PdfBoundedDecodeStatus.InvalidData, bounded.Status);
        Assert.Equal("A"u8.ToArray(), bounded.Data.ToArray());
    }

    [Fact]
    public void StrictPathAcceptsZeroPaddingAfterEodInTheFinalByte()
    {
        byte[] encoded = PackNineBitCodes(256, 65, 257);

        PdfBoundedDecodeResult bounded = DecodeBounded(encoded);

        Assert.Equal(PdfBoundedDecodeStatus.Complete, bounded.Status);
        Assert.Equal("A"u8.ToArray(), bounded.Data.ToArray());
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("padding")]
    public void StrictPathRejectsDataAfterEod(string mutation)
    {
        byte[] encoded = PackNineBitCodes(256, 65, 257);
        if (mutation == "byte")
            encoded = [.. encoded, 0x00];
        else
            encoded[^1] |= 0x01;

        Assert.Equal("A"u8.ToArray(), LzwFilter.Decode(encoded));
        Assert.Equal(PdfBoundedDecodeStatus.InvalidData, DecodeBounded(encoded).Status);
    }

    [Fact]
    public void StrictPathRequiresTheInitialClearCode()
    {
        byte[] malformed = PackNineBitCodes(65, 257);

        Assert.Equal("A"u8.ToArray(), LzwFilter.Decode(malformed));

        PdfBoundedDecodeResult bounded = DecodeBounded(malformed);

        Assert.Equal(PdfBoundedDecodeStatus.InvalidData, bounded.Status);
        Assert.Empty(bounded.Data.ToArray());
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ValidPayloadRespectsCapBoundaries(int delta, bool limited)
    {
        byte[] encoded = PackNineBitCodes(256, 65, 66, 67, 257);
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfName.Get("LZWDecode")),
        };

        PdfBoundedDecodeResult bounded = new PdfStream(dictionary, encoded).DecodeBounded(3 + delta);

        Assert.Equal(
            limited ? PdfBoundedDecodeStatus.LimitExceeded : PdfBoundedDecodeStatus.Complete,
            bounded.Status);
        if (!limited)
            Assert.Equal("ABC"u8.ToArray(), bounded.Data.ToArray());
    }

    private static PdfBoundedDecodeResult DecodeBounded(byte[] encoded)
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfName.Get("LZWDecode")),
        };
        return new PdfStream(dictionary, encoded).DecodeBounded(1_024);
    }

    private static byte[] PackNineBitCodes(params ReadOnlySpan<int> codes)
    {
        var bytes = new List<byte>();
        uint buffer = 0;
        int bits = 0;

        foreach (int code in codes)
        {
            buffer = (buffer << 9) | (uint)code;
            bits += 9;
            while (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= bits == 0 ? 0u : (1u << bits) - 1;
            }
        }

        if (bits > 0)
            bytes.Add((byte)(buffer << (8 - bits)));

        return bytes.ToArray();
    }
}
