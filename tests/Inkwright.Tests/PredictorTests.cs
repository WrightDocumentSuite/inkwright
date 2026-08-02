using Inkwright.Filters;
using Xunit;

namespace Inkwright.Tests;

public class PredictorTests
{
    [Theory]
    [InlineData(1, 0x5D, 0x69)]
    [InlineData(2, 0x15, 0x1B)]
    [InlineData(4, 0x11, 0x12)]
    public void TiffPredictorRestoresPackedSamples(
        int bitsPerComponent,
        byte encoded,
        byte expected)
    {
        int columns = 8 / bitsPerComponent;

        Assert.Equal(
            new[] { expected },
            Predictor.Undo([encoded], 2, colors: 1, bitsPerComponent, columns));
    }

    [Fact]
    public void TiffPredictorRestoresEightBitSamplesWithMoreThanThirtyTwoColors()
    {
        byte[] encoded = Enumerable.Repeat((byte)10, 66).ToArray();
        byte[] expected = Enumerable.Repeat((byte)10, 33)
            .Concat(Enumerable.Repeat((byte)20, 33))
            .ToArray();

        Assert.Equal(
            expected,
            Predictor.Undo(encoded, 2, colors: 33, bitsPerComponent: 8, columns: 2));
    }

    [Fact]
    public void TiffPredictorRestoresBigEndianSixteenBitSamples()
    {
        byte[] encoded = [0x01, 0x02, 0x01, 0x02, 0x01, 0x02];
        byte[] expected = [0x01, 0x02, 0x02, 0x04, 0x03, 0x06];

        Assert.Equal(
            expected,
            Predictor.Undo(encoded, 2, colors: 1, bitsPerComponent: 16, columns: 3));
    }

    [Fact]
    public void PngSubPredictorRoundsBytesPerPixelUpForPackedColors()
    {
        byte[] encoded = [1, 0x12, 0x34, 0x44];
        byte[] expected = [0x12, 0x34, 0x56];

        Assert.Equal(
            expected,
            Predictor.Undo(encoded, 12, colors: 3, bitsPerComponent: 4, columns: 2));
    }

    [Fact]
    public void StrictPngPredictorBudgetsTheOutputWithoutCountingRowTagsTwice()
    {
        byte[] decoded = Predictor.Undo(
            new byte[] { 1, 0x12, 0x34, 0x44 },
            12,
            colors: 3,
            bitsPerComponent: 4,
            columns: 2,
            maxDecodedBytes: 3,
            out bool limitExceeded,
            out bool invalidData);

        Assert.False(limitExceeded);
        Assert.False(invalidData);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, decoded);
    }

    [Theory]
    [InlineData(new byte[] { 5, 1, 2 })]
    [InlineData(new byte[] { 0, 1 })]
    public void StrictPngPredictorRejectsUnknownTagsAndTruncatedRows(byte[] encoded)
    {
        _ = Predictor.Undo(
            encoded,
            12,
            colors: 1,
            bitsPerComponent: 8,
            columns: 2,
            maxDecodedBytes: 16,
            out bool limitExceeded,
            out bool invalidData);

        Assert.False(limitExceeded);
        Assert.True(invalidData);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void StrictPngPredictorRespectsCapBoundaries(int delta, bool limited)
    {
        byte[] encoded = [0, 10, 20, 30];

        byte[] decoded = Predictor.Undo(
            encoded,
            12,
            colors: 1,
            bitsPerComponent: 8,
            columns: 3,
            maxDecodedBytes: 3 + delta,
            out bool limitExceeded,
            out bool invalidData);

        Assert.Equal(limited, limitExceeded);
        Assert.False(invalidData);
        if (!limited)
            Assert.Equal(new byte[] { 10, 20, 30 }, decoded);
    }
}
