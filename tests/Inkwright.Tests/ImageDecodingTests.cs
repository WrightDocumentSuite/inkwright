using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Inkwright.Cos;
using Inkwright.Images;
using Xunit;

namespace Inkwright.Tests;

public class ImageDecodingTests
{
    [Fact]
    public void DecodesAnUncompressedGreyImage()
    {
        // A two-by-two image: black, white, mid grey, white.
        byte[] samples = [0x00, 0xFF, 0x80, 0xFF];

        PdfImageData image = PdfImageReader.Decode(Image(samples, 2, 2, "DeviceGray", 8));

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, image.Pixels[..4]);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, image.Pixels[4..8]);
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, image.Pixels[8..12]);
        Assert.False(image.HasTransparency);
    }

    [Fact]
    public void UnpacksSamplesNarrowerThanAByte()
    {
        // Four one-bit pixels in a single byte, the rest of which pads the row.
        PdfImageData image = PdfImageReader.Decode(Image([0b1010_0000], 4, 1, "DeviceGray", 1));

        Assert.Equal(255, image.Pixels[0]);
        Assert.Equal(0, image.Pixels[4]);
        Assert.Equal(255, image.Pixels[8]);
        Assert.Equal(0, image.Pixels[12]);
    }

    [Fact]
    public void PadsEachRowToAByteBoundary()
    {
        // Three one-bit pixels per row, so each row takes a byte of its own.
        PdfImageData image = PdfImageReader.Decode(Image([0b1110_0000, 0b0000_0000], 3, 2, "DeviceGray", 1));

        Assert.Equal(255, image.Pixels[0]);
        Assert.Equal(255, image.Pixels[8]);

        // The second row starts at a fresh byte, so it is black rather than a continuation.
        Assert.Equal(0, image.Pixels[12]);
    }

    [Fact]
    public void InvertsSamplesWhenTheDecodeArraySaysSo()
    {
        PdfStream stream = Image([0x00, 0xFF], 2, 1, "DeviceGray", 8);
        stream.Dictionary.Set(PdfName.Get("Decode"), PdfValue.Array(PdfArray.OfNumbers(1, 0)));

        PdfImageData image = PdfImageReader.Decode(stream);

        Assert.Equal(255, image.Pixels[0]);
        Assert.Equal(0, image.Pixels[4]);
    }

    [Fact]
    public void ReadsAStencilAsAShapeRatherThanAColour()
    {
        PdfStream stream = Image([0b0100_0000], 2, 1, "DeviceGray", 1);
        stream.Dictionary.Remove(PdfName.Get("ColorSpace"));
        stream.Dictionary.Remove(PdfName.Get("BitsPerComponent"));
        stream.Dictionary.Set(PdfName.Get("ImageMask"), PdfValue.Boolean(true));

        PdfImageData image = PdfImageReader.Decode(stream);

        // A stencil paints where its samples are zero and leaves the page showing elsewhere.
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, image.Pixels[..4]);
        Assert.Equal(0, image.Pixels[7]);
        Assert.True(image.HasTransparency);
    }

    [Fact]
    public void TakesAlphaFromASoftMask()
    {
        PdfStream mask = Image([0x00, 0xFF], 2, 1, "DeviceGray", 8);
        PdfStream stream = Image([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], 2, 1, "DeviceRGB", 8);
        stream.Dictionary.Set(PdfName.Get("SMask"), PdfValue.Stream(mask));

        PdfImageData image = PdfImageReader.Decode(stream);

        Assert.Equal(0, image.Pixels[3]);
        Assert.Equal(255, image.Pixels[7]);
    }

    [Fact]
    public void RemovesTheSoftMasksMatteBeforeColourConversion()
    {
        PdfStream mask = Image([0x80], 1, 1, "DeviceGray", 8);
        mask.Dictionary.Set(PdfName.Get("Matte"), PdfValue.Array(PdfArray.OfNumbers(0, 0, 0)));

        // Half-transparent red precomposited against black is stored as half red. Removing Matte
        // must recover full red before the separate mask value becomes output alpha.
        PdfStream stream = Image([0x80, 0x00, 0x00], 1, 1, "DeviceRGB", 8);
        stream.Dictionary.Set(PdfNames.SMask, PdfValue.Stream(mask));

        PdfImageData image = PdfImageReader.Decode(stream);

        Assert.Equal(new byte[] { 255, 0, 0, 128 }, image.Pixels);
    }

    [Fact]
    public void DropsTheColoursAColourKeyMaskNames()
    {
        PdfStream stream = Image([0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF], 2, 1, "DeviceRGB", 8);

        // Everything that is pure red disappears.
        var ranges = new PdfArray(6);
        foreach (double bound in (double[])[255, 255, 0, 0, 0, 0])
            ranges.Add(PdfValue.Integer((long)bound));

        stream.Dictionary.Set(PdfName.Get("Mask"), PdfValue.Array(ranges));

        PdfImageData image = PdfImageReader.Decode(stream);

        Assert.Equal(0, image.Pixels[3]);
        Assert.Equal(255, image.Pixels[7]);
    }

    [Fact]
    public void LooksColoursUpThroughAnIndexedSpace()
    {
        var palette = new PdfArray(4);
        palette.Add(PdfValue.Name(PdfName.Get("Indexed")));
        palette.Add(PdfValue.Name(PdfName.Get("DeviceRGB")));
        palette.Add(PdfValue.Integer(1));
        palette.Add(PdfValue.String(new byte[] { 255, 0, 0, 0, 0, 255 }));

        PdfStream stream = Image([0b0100_0000], 2, 1, "DeviceGray", 1);
        stream.Dictionary.Set(PdfName.Get("ColorSpace"), PdfValue.Array(palette));

        PdfImageData image = PdfImageReader.Decode(stream);

        Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, image.Pixels[4..8]);
    }

    [Fact]
    public void DecodesABaselineJpeg()
    {
        // One block whose only coefficient is a direct current term of 40. The transform spreads
        // that evenly over the block as 40/8, and the level shift puts it at 133.
        PdfStream stream = Image(MinimalJpeg(), 8, 8, "DeviceGray", 8);
        stream.Dictionary.Set(PdfName.Get("Filter"), PdfValue.Name(PdfName.Get("DCTDecode")));

        PdfImageData image = PdfImageReader.Decode(stream);

        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);

        for (int i = 0; i < 64; i++)
            Assert.Equal(133, image.Pixels[i * 4]);
    }

    /// <summary>
    /// ISO 32000-2 Table 13 lets DecodeParms disable the default YCbCr transform when a JPEG has
    /// no Adobe APP14 marker. The same three decoded planes therefore have to produce different
    /// RGB samples for ColorTransform 0 and 1.
    /// </summary>
    [Fact]
    public void HonoursTheDctColorTransformWhenThereIsNoAdobeMarker()
    {
        byte[] jpeg = MinimalJpeg(components: 3);
        PdfStream raw = Image(jpeg, 8, 8, "DeviceRGB", 8);
        raw.Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfNames.DCTDecode));
        raw.Dictionary.Set(PdfNames.DecodeParms, PdfValue.Dictionary(new PdfDictionary
        {
            [PdfName.Get("ColorTransform")] = PdfValue.Integer(0),
        }));

        PdfStream transformed = Image(jpeg, 8, 8, "DeviceRGB", 8);
        transformed.Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfNames.DCTDecode));
        transformed.Dictionary.Set(PdfNames.DecodeParms, PdfValue.Dictionary(new PdfDictionary
        {
            [PdfName.Get("ColorTransform")] = PdfValue.Integer(1),
        }));

        PdfImageData rawImage = PdfImageReader.Decode(raw);
        PdfImageData transformedImage = PdfImageReader.Decode(transformed);

        Assert.Equal(new byte[] { 133, 133, 133, 255 }, rawImage.Pixels[..4]);
        Assert.Equal(new byte[] { 140, 128, 142, 255 }, transformedImage.Pixels[..4]);
    }

    [Fact]
    public void HonoursTheFourComponentDctColorTransformWhenThereIsNoAdobeMarker()
    {
        byte[] jpeg = MinimalJpeg(components: 4);

        JpegImage raw = JpegDecoder.Decode(jpeg, colorTransform: 0);
        JpegImage transformed = JpegDecoder.Decode(jpeg, colorTransform: 1);

        Assert.Equal(new byte[] { 133, 133, 133, 133 }, raw.Samples[..4]);
        Assert.Equal(new byte[] { 140, 128, 142, 133 }, transformed.Samples[..4]);
    }

    /// <summary>
    /// ISO 32000-2 Table 13 gives an Adobe APP14 marker precedence over DecodeParms. Transform 0
    /// identifies RGB or CMYK samples, transform 1 identifies YCbCr, and transform 2 identifies
    /// YCCK. Four-component Adobe samples are inverted after either marker-directed path.
    /// </summary>
    [Theory]
    [InlineData(3, 0, 1, 133, 133, 133, -1)]
    [InlineData(3, 1, 0, 140, 128, 142, -1)]
    [InlineData(4, 0, 1, 122, 122, 122, 122)]
    [InlineData(4, 2, 0, 115, 127, 113, 122)]
    public void AdobeMarkerOverridesThePdfDctColorTransform(
        int components,
        int adobeTransform,
        int pdfTransform,
        int first,
        int second,
        int third,
        int fourth)
    {
        JpegImage image = JpegDecoder.Decode(
            MinimalJpeg(components, adobeTransform), pdfTransform);

        byte[] expected = components == 4
            ? [checked((byte)first), checked((byte)second), checked((byte)third), checked((byte)fourth)]
            : [checked((byte)first), checked((byte)second), checked((byte)third)];
        Assert.Equal(expected, image.Samples[..components]);
    }

    [Theory]
    [InlineData(3, 140, 128, 142, -1)]
    [InlineData(4, 133, 133, 133, 133)]
    public void UsesTheComponentCountDefaultWhenNoDctColorTransformIsSpecified(
        int components,
        int first,
        int second,
        int third,
        int fourth)
    {
        JpegImage image = JpegDecoder.Decode(MinimalJpeg(components));

        byte[] expected = components == 4
            ? [checked((byte)first), checked((byte)second), checked((byte)third), checked((byte)fourth)]
            : [checked((byte)first), checked((byte)second), checked((byte)third)];
        Assert.Equal(expected, image.Samples[..components]);
    }

    [Fact]
    public void RefusesTheCodecsItDoesNotDecode()
    {
        PdfStream stream = Image([1, 2, 3], 8, 8, "DeviceGray", 8);
        stream.Dictionary.Set(PdfName.Get("Filter"), PdfValue.Name(PdfName.Get("JPXDecode")));

        Assert.False(PdfImageReader.CanDecode(stream));

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => PdfImageReader.Decode(stream));
        Assert.Contains("JPXDecode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesAPngThatReadsBackAsTheSameImage()
    {
        PdfImageData image = PdfImageReader.Decode(
            Image([0x00, 0xFF, 0x80, 0x40], 2, 2, "DeviceGray", 8));

        byte[] png = image.ToPng();

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));

        // Colour type 6 is red, green, blue and alpha at eight bits each.
        Assert.Equal(8, png[24]);
        Assert.Equal(6, png[25]);
    }

    [Fact]
    public void FindsTheImagesAPageDraws()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        PdfImage embedded = PdfImage.Load(document, TestImages.Png(4, 4, withAlpha: false));

        var objects = new PdfDictionary(1);
        objects.Set(PdfName.Get("Im0"), embedded.Reference);
        page.Resources.Set(PdfName.Get("XObject"), PdfValue.Dictionary(objects));

        PdfPageImage found = Assert.Single(page.Images);

        Assert.Equal("Im0", found.Name);
        Assert.Equal(4, found.Width);
        Assert.Equal(4, found.Height);
        Assert.True(found.CanDecode);

        PdfImageData pixels = found.Decode();
        Assert.Equal(4 * 4 * 4, pixels.Pixels.Length);
    }

    [Fact]
    public void ReadsBackAnEmbeddedPngThroughTheDocument()
    {
        using var document = PdfDocument.Create();
        PdfImage embedded = PdfImage.Load(document, TestImages.Png(8, 8, withAlpha: false));

        PdfImageData image = embedded.Decode();

        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);

        // The test gradient runs red across and green down, with a fixed blue.
        Assert.Equal(0, image.Pixels[0]);
        Assert.Equal(0x40, image.Pixels[2]);
        Assert.Equal(255, image.Pixels[((7 * 8) + 7) * 4]);
    }

    private static PdfStream Image(byte[] data, int width, int height, string space, int bits)
    {
        var stream = new PdfStream(new PdfDictionary(6), data);
        stream.Dictionary.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("XObject")));
        stream.Dictionary.Set(PdfName.Get("Subtype"), PdfValue.Name(PdfName.Get("Image")));
        stream.Dictionary.Set(PdfName.Get("Width"), PdfValue.Integer(width));
        stream.Dictionary.Set(PdfName.Get("Height"), PdfValue.Integer(height));
        stream.Dictionary.Set(PdfName.Get("ColorSpace"), PdfValue.Name(PdfName.Get(space)));
        stream.Dictionary.Set(PdfName.Get("BitsPerComponent"), PdfValue.Integer(bits));
        return stream;
    }

    /// <summary>
    /// A complete eight-by-eight greyscale JPEG holding one block. The quantization table is all
    /// ones, each Huffman table holds a single one-bit code, and the whole scan is one byte:
    /// the code for a magnitude of six, the six bits of the value forty, and the end-of-block code.
    /// </summary>
    private static byte[] MinimalJpeg(int components = 1, int? adobeTransform = null)
    {
        using var file = new MemoryStream();
        file.Write([0xFF, 0xD8]);

        byte[] quantization = new byte[65];
        for (int i = 1; i < 65; i++)
            quantization[i] = 1;

        Segment(file, 0xDB, quantization);

        if (adobeTransform is not null)
        {
            byte[] adobe =
            [
                .. "Adobe"u8,
                0x00, 0x64, // version 100
                0x00, 0x00, // flags 0
                0x00, 0x00, // flags 1
                checked((byte)adobeTransform.Value),
            ];
            Segment(file, 0xEE, adobe);
        }

        var frame = new List<byte> { 8, 0, 8, 0, 8, checked((byte)components) };
        for (int component = 1; component <= components; component++)
            frame.AddRange([checked((byte)component), 0x11, 0]);
        Segment(file, 0xC0, CollectionsMarshal.AsSpan(frame));

        Segment(file, 0xC4, Table(0x00, value: 6));
        Segment(file, 0xC4, Table(0x10, value: 0));

        var scan = new List<byte> { checked((byte)components) };
        for (int component = 1; component <= components; component++)
            scan.AddRange([checked((byte)component), 0x00]);
        scan.AddRange([0, 63, 0]);
        Segment(file, 0xDA, CollectionsMarshal.AsSpan(scan));

        for (int component = 0; component < components; component++)
            file.WriteByte(0b0101_0000);
        file.Write([0xFF, 0xD9]);

        return file.ToArray();

        static byte[] Table(byte specification, byte value)
        {
            byte[] table = new byte[18];
            table[0] = specification;
            table[1] = 1;
            table[17] = value;
            return table;
        }

        static void Segment(Stream stream, byte marker, ReadOnlySpan<byte> payload)
        {
            stream.Write([0xFF, marker]);
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
            stream.Write(length);
            stream.Write(payload);
        }
    }
}
