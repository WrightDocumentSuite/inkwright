using System.Text;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Images;
using Xunit;

namespace Inkwright.Tests;

public class ContentTests
{
    [Fact]
    public void ParsesOperatorsWithoutAllocatingPerOperand()
    {
        ContentOperations operations = ContentOperations.Parse(
            "q 1 0 0 1 10 20 cm 0.5 0 0 RG 2 w 0 0 m 100 100 l S Q"u8);

        Assert.Equal(8, operations.Count);
        Assert.Equal(PdfOperator.SaveState, operations.GetOperator(0));
        Assert.Equal(PdfOperator.ConcatMatrix, operations.GetOperator(1));
        Assert.Equal(6, operations.GetOperands(1).Length);
        Assert.Equal(20, operations.GetNumber(1, 5));
        Assert.Equal(PdfOperator.SetStrokeRgb, operations.GetOperator(2));
        Assert.Equal(PdfOperator.Stroke, operations.GetOperator(6));
        Assert.Equal(PdfOperator.RestoreState, operations.GetOperator(7));
    }

    [Fact]
    public void KeepsUnknownOperatorsWalkable()
    {
        ContentOperations operations = ContentOperations.Parse("1 2 zz 3 4 m"u8);

        Assert.Equal(2, operations.Count);
        Assert.Equal(PdfOperator.Unknown, operations.GetOperator(0));
        Assert.Equal("zz", operations.GetUnknownName(0));
        Assert.Equal(PdfOperator.MoveTo, operations.GetOperator(1));
    }

    [Fact]
    public void ReadsTextShowingOperators()
    {
        ContentOperations operations = ContentOperations.Parse(
            "BT /F1 12 Tf 72 700 Td [(Wor) -25 (ld)] TJ ET"u8);

        Assert.Equal(PdfOperator.SetFont, operations.GetOperator(1));
        Assert.Equal("F1", operations.GetOperands(1)[0].AsName!.Value);
        Assert.Equal(PdfOperator.ShowTextAdjusted, operations.GetOperator(3));

        PdfArray parts = operations.GetOperands(3)[0].AsArray!;
        Assert.Equal(3, parts.Count);
        Assert.Equal("Wor", Encoding.ASCII.GetString(parts[0].AsStringBytes));
        Assert.Equal(-25, parts[1].AsNumber);
    }

    [Fact]
    public void ReadsInlineImages()
    {
        byte[] content = [.. "q BI /W 2 /H 2 /CS /G /BPC 8 ID "u8.ToArray(), 1, 2, 3, 4, .. " EI Q"u8.ToArray()];

        ContentOperations operations = ContentOperations.Parse(content);

        Assert.Equal(3, operations.Count);
        Assert.Equal(PdfOperator.InlineImage, operations.GetOperator(1));

        PdfStream image = operations.GetOperands(1)[0].AsStream!;
        Assert.Equal(2, image.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.Equal("DeviceGray", image.Dictionary.GetName(PdfName.Get("ColorSpace"))!.Value);
        Assert.Equal(4, image.RawLength);
    }

    /// <summary>
    /// ISO 32000-2 Tables 91 and 92 deliberately reuse <c>/I</c>: as a dictionary key it means
    /// Interpolate, while as a colour-space value it means Indexed. Expanding values with the key
    /// table silently turned this palette into an unknown colour space.
    /// </summary>
    [Fact]
    public void ExpandsInlineImageNamesAccordingToTheirContext()
    {
        byte[] content =
        [
            .. "q BI /W 2 /H 1 /BPC 1 /CS [/I /RGB 1 <FF00000000FF>] ID "u8.ToArray(),
            0b0100_0000,
            .. " EI Q"u8.ToArray(),
        ];

        ContentOperations operations = ContentOperations.Parse(content);
        PdfStream stream = operations.GetOperands(1)[0].AsStream!;
        PdfArray colourSpace = stream.Dictionary.GetArray(PdfNames.ColorSpace)!;

        Assert.Equal("Indexed", colourSpace.GetName(0)!.Value);
        Assert.Equal("DeviceRGB", colourSpace.GetName(1)!.Value);

        PdfImageData image = PdfImageReader.Decode(stream);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, image.Pixels[4..8]);
    }

    /// <summary>
    /// The declared length of §8.9.7 is what settles where compressed data ends. Scanning for a
    /// lone <c>EI</c> is guesswork, and this payload is built to make the guess wrong: the encoded
    /// bytes carry a space, <c>EI</c> and another space in the middle of a literal run.
    /// </summary>
    [Fact]
    public void TrustsTheDeclaredLengthOverAnEndMarkerInsideCompressedData()
    {
        // RunLengthDecode: a leading 7 means "the next eight bytes are literal", 0x80 ends it.
        byte[] data = [7, 0x00, 0x20, 0x45, 0x49, 0x20, 0x00, 0x01, 0x02, 0x80];
        byte[] content =
        [
            .. "q BI /W 2 /H 2 /CS /G /BPC 8 /F /RL /L 10 ID "u8.ToArray(),
            .. data,
            .. " EI Q"u8.ToArray(),
        ];

        ContentOperations operations = ContentOperations.Parse(content);

        Assert.Equal(3, operations.Count);
        PdfStream image = operations.GetOperands(1)[0].AsStream!;
        Assert.Equal(data.Length, image.RawLength);
        Assert.Equal(PdfOperator.RestoreState, operations.GetOperator(2));
    }

    /// <summary>
    /// Note 2 of §8.9.7: with ASCIIHexDecode or ASCII85Decode as the last filter, any amount of
    /// white space may follow the <c>ID</c> operator before the data proper.
    /// </summary>
    [Fact]
    public void SkipsLeadingWhiteSpaceOfAnAscii85ImageBeforeCountingTheDeclaredLength()
    {
        byte[] content = [.. "q BI /W 2 /H 2 /CS /G /BPC 8 /F /A85 /L 3 ID \n\n z~> EI Q"u8.ToArray()];

        ContentOperations operations = ContentOperations.Parse(content);

        PdfStream image = operations.GetOperands(1)[0].AsStream!;
        Assert.Equal(3, image.RawLength);
        Assert.Equal("z~>", Encoding.ASCII.GetString(image.RawData.Span));
    }

    /// <summary>
    /// A length that does not land on the end marker is a broken file, not an instruction. The
    /// reader falls back to scanning rather than cutting the image where the number says.
    /// </summary>
    [Fact]
    public void FallsBackToScanningWhenTheDeclaredLengthDoesNotReachTheEndMarker()
    {
        byte[] content = [.. "q BI /W 2 /H 2 /CS /G /BPC 8 /F /A85 /L 999 ID z~> EI Q"u8.ToArray()];

        ContentOperations operations = ContentOperations.Parse(content);

        PdfStream image = operations.GetOperands(1)[0].AsStream!;
        Assert.Equal(3, image.RawLength);
    }

    [Fact]
    public void CanvasDrawsAndRoundTrips()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.Save()
                  .FillColor(PdfColor.Parse("#2278D4"))
                  .Rectangle(new PdfRectangle(40, 40, 240, 140))
                  .Fill()
                  .StrokeColor(PdfColor.Black)
                  .LineWidth(2)
                  .Opacity(0.5)
                  .Ellipse(new PdfRectangle(60, 200, 260, 320))
                  .Stroke()
                  .Restore();
            canvas.Commit();
        }

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        ContentOperations operations = ContentOperations.Parse(reloaded.Pages[0].GetContent());

        Assert.Contains(Enumerable.Range(0, operations.Count), i => operations.GetOperator(i) == PdfOperator.SetFillRgb);
        Assert.Contains(Enumerable.Range(0, operations.Count), i => operations.GetOperator(i) == PdfOperator.SetExtGState);
        Assert.Contains(Enumerable.Range(0, operations.Count), i => operations.GetOperator(i) == PdfOperator.CurveTo);
        Assert.NotNull(reloaded.Pages[0].Resources.GetDictionary(PdfName.Get("ExtGState")));
    }

    [Fact]
    public void EmbedsJpegWithoutRecompressing()
    {
        byte[] jpeg = TestImages.Jpeg(32, 24);

        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfImage image = PdfImage.Load(document, jpeg);

        Assert.Equal(32, image.Width);
        Assert.Equal(24, image.Height);
        Assert.Equal(jpeg.Length, image.Stream.RawLength);

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawImage(image, PdfRectangle.FromSize(50, 50, 200, 150));
            canvas.Commit();
        }

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfDictionary xObjects = reloaded.Pages[0].Resources.GetDictionary(PdfName.Get("XObject"))!;
        PdfStream embedded = xObjects.GetStream(xObjects.Keys[0])!;

        Assert.Equal("DCTDecode", embedded.Dictionary.GetName(PdfName.Get("Filter"))!.Value);
        Assert.True(embedded.RawData.Span.SequenceEqual(jpeg));
    }

    [Fact]
    public void EmbedsPngByReusingItsDeflateStream()
    {
        byte[] png = TestImages.Png(16, 16, withAlpha: false);

        using var document = PdfDocument.Create();
        PdfImage image = PdfImage.Load(document, png);

        Assert.Equal(16, image.Width);
        Assert.Equal("FlateDecode", image.Stream.Dictionary.GetName(PdfName.Get("Filter"))!.Value);

        PdfDictionary parms = image.Stream.Dictionary.GetDictionary(PdfName.Get("DecodeParms"))!;
        Assert.Equal(15, parms.GetInteger(PdfName.Get("Predictor")));

        ReadOnlySpan<byte> decoded = image.Stream.DecodedSpan();
        Assert.Equal(16 * 16 * 3, decoded.Length);
    }

    [Fact]
    public void SplitsPngAlphaIntoASoftMask()
    {
        byte[] png = TestImages.Png(8, 8, withAlpha: true);

        using var document = PdfDocument.Create();
        PdfImage image = PdfImage.Load(document, png);

        PdfStream mask = image.Stream.Dictionary.GetStream(PdfName.Get("SMask"))!;
        Assert.Equal(8, mask.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.Equal("DeviceGray", mask.Dictionary.GetName(PdfName.Get("ColorSpace"))!.Value);
        Assert.Equal(64, mask.Decode().Length);
        Assert.Equal(8 * 8 * 3, image.Stream.Decode().Length);
    }

    [Fact]
    public void BuildsImagesFromRawPixels()
    {
        byte[] pixels = new byte[4 * 4 * 3];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(i * 7);

        using var document = PdfDocument.Create();
        PdfImage image = PdfImage.FromPixels(document, 4, 4, pixels);

        Assert.Equal(4, image.Width);
        Assert.True(image.Stream.DecodedSpan().SequenceEqual(pixels));
    }
}
