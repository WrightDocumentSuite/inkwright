using Inkwright.Cos;
using Inkwright.Images;
using Inkwright.Images.Ccitt;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// The fax codings of ITU-T T.4 and T.6. The code tables were transcribed from the printed tables
/// of the recommendation, so the tests check runs whose codes can be read straight off the page.
/// </summary>
public class CcittTests
{
    [Fact]
    public void DecodesRunsCodedTheOneDimensionalWay()
    {
        // White 4 is 1011 and black 4 is 011 (Table 2/T.4). Eight pixels: four white, four black.
        byte[] data = Bits("1011 011");

        byte[] rows = CcittDecoder.Decode(data, Options(columns: 8, rows: 1, k: 0));

        // The decoder sets a bit where the pixel is black.
        Assert.Equal([0b0000_1111], rows);
    }

    [Fact]
    public void ReadsRunsLongerThanSixtyThreeAsMakeUpPlusTerminator()
    {
        // White 64 is 11011 (make-up) and white 0 is 00110101, so 64 white pixels then 64 black:
        // black 64 make-up is 0000001111 and black 0 is 0000110111.
        byte[] data = Bits("11011 00110101 0000001111 0000110111");

        byte[] rows = CcittDecoder.Decode(data, Options(columns: 128, rows: 1, k: 0));

        Assert.Equal(16, rows.Length);
        Assert.All(rows[..8], b => Assert.Equal(0x00, b));
        Assert.All(rows[8..], b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void FollowsTheRowAboveInTwoDimensionalMode()
    {
        // First row one-dimensionally: 4 white, 4 black. Then group 4 vertical zero twice, which
        // says both changes sit exactly under the ones above, so the row repeats.
        byte[] data = Bits("1011 011" + "1 1");

        byte[] rows = CcittDecoder.Decode(data, Options(columns: 8, rows: 2, k: 0));
        Assert.Equal(2, rows.Length);

        // Coded purely two-dimensionally, the first row is measured against imaginary white above.
        byte[] group4 = CcittDecoder.Decode(
            Bits("001 1011 011" + "1 1"), Options(columns: 8, rows: 2, k: -1));

        Assert.Equal([0b0000_1111, 0b0000_1111], group4);
    }

    [Fact]
    public void ShiftsAChangeWhenVerticalModeSaysSo()
    {
        // Horizontal mode sets up 4 white and 4 black, then VR(1) moves the change one to the
        // right and V(0) leaves the end where it is.
        byte[] data = Bits("001 1011 011" + "011 1");

        byte[] rows = CcittDecoder.Decode(data, Options(columns: 8, rows: 2, k: -1));

        Assert.Equal(0b0000_1111, rows[0]);
        Assert.Equal(0b0000_0111, rows[1]);
    }

    [Fact]
    public void InvertsUnlessTheParametersSayBlackIsOne()
    {
        byte[] data = Bits("1011 011");
        PdfDictionary parms = Parms(columns: 8, rows: 1, k: 0);

        // Without BlackIs1 the filter reports the PDF convention, where zero is black.
        byte[] normal = CcittFaxDecoder.Decode(data, CcittFaxDecoder.Read(parms, 8, 1));
        Assert.Equal([0b1111_0000], normal);

        parms.Set(PdfName.Get("BlackIs1"), PdfValue.Boolean(true));
        byte[] inverted = CcittFaxDecoder.Decode(data, CcittFaxDecoder.Read(parms, 8, 1));
        Assert.Equal([0b0000_1111], inverted);
    }

    [Fact]
    public void ReadsTheParametersOfTableEleven()
    {
        var parms = new PdfDictionary(8);
        parms.Set(PdfName.Get("K"), PdfValue.Integer(-1));
        parms.Set(PdfName.Get("Columns"), PdfValue.Integer(1728));
        parms.Set(PdfName.Get("Rows"), PdfValue.Integer(2200));
        parms.Set(PdfName.Get("EncodedByteAlign"), PdfValue.Boolean(true));
        parms.Set(PdfName.Get("BlackIs1"), PdfValue.Boolean(true));
        parms.Set(PdfName.Get("EndOfLine"), PdfValue.Boolean(true));
        parms.Set(PdfName.Get("DamagedRowsBeforeError"), PdfValue.Integer(3));

        CcittOptions options = CcittFaxDecoder.Read(parms, 100, 100);

        Assert.Equal(-1, options.K);
        Assert.Equal(1728, options.Columns);
        Assert.Equal(2200, options.Rows);
        Assert.True(options.ByteAligned);
        Assert.True(options.BlackIs1);
        Assert.True(options.EndOfLine);
        Assert.Equal(3, options.DamagedRowsBeforeError);

        // The defaults of Table 11 apply when the dictionary is silent.
        CcittOptions bare = CcittFaxDecoder.Read(new PdfDictionary(0), 640, 480);
        Assert.Equal(0, bare.K);
        Assert.Equal(1728, bare.Columns);
        Assert.Equal(480, bare.Rows);
        Assert.False(bare.ByteAligned);
    }

    [Fact]
    public void StepsOverEndOfLinePatterns()
    {
        // Each row is preceded by the end-of-line pattern, which is eleven zeros and a one.
        byte[] data = Bits("000000000001 1011 011 000000000001 1011 011");

        byte[] rows = CcittDecoder.Decode(data, Options(columns: 8, rows: 2, k: 0));

        Assert.Equal([0b0000_1111, 0b0000_1111], rows);
    }

    [Fact]
    public void GivesUpOnRubbishRatherThanSpinning()
    {
        // A run of ones is not a valid code in either table, so no row can be read.
        byte[] rubbish = [0xFF, 0xFF, 0xFF, 0xFF];

        byte[] rows = CcittDecoder.Decode(rubbish, Options(columns: 64, rows: 4, k: -1));

        // The result is the expected size, filled with whatever could be salvaged: nothing.
        Assert.Equal(8 * 4, rows.Length);
    }

    private static CcittOptions Options(int columns, int rows, int k) =>
        new(columns, rows, k, EndOfLine: false, ByteAligned: false, BlackIs1: true, DamagedRowsBeforeError: 0);

    private static PdfDictionary Parms(int columns, int rows, int k)
    {
        var parms = new PdfDictionary(4);
        parms.Set(PdfName.Get("K"), PdfValue.Integer(k));
        parms.Set(PdfName.Get("Columns"), PdfValue.Integer(columns));
        parms.Set(PdfName.Get("Rows"), PdfValue.Integer(rows));
        return parms;
    }

    /// <summary>Packs a string of ones and zeros into bytes, most significant bit first.</summary>
    private static byte[] Bits(string bits)
    {
        string clean = bits.Replace(" ", string.Empty, StringComparison.Ordinal);
        byte[] data = new byte[(clean.Length + 7) / 8];

        for (int i = 0; i < clean.Length; i++)
        {
            if (clean[i] == '1')
                data[i >> 3] |= (byte)(0x80 >> (i & 7));
        }

        return data;
    }
}
