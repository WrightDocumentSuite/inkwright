using System.Buffers.Binary;
using System.Security.Cryptography;
using Inkwright.Images;
using Inkwright.Images.Jbig2;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// The generic region decoder of ITU-T T.88. The strongest check available is the family of
/// <c>bitmap-*.pdf</c> files in the corpus: they encode one and the same picture through every
/// variation of the format — custom adaptive pixels, typical prediction, fax coding, each
/// composition operator, striped and unknown-size regions — so a decoder that is right about all
/// of them returns the same image every time.
/// </summary>
public class Jbig2Tests
{
    [Fact]
    public void DecodesEveryEncodingOfTheSamePictureIdentically()
    {
        IReadOnlyList<string> files = Corpus.Matching("bitmap-", int.MaxValue);
        Assert.SkipUnless(files.Count >= 96, "The complete corpus of JBIG2 variants is not on this machine.");
        Assert.Equal(96, files.Count);

        byte[]? first = null;
        string? firstName = null;
        int decodedImages = 0;
        int compared = 0;
        HashSet<string> unsupported = new(StringComparer.OrdinalIgnoreCase);

        foreach (string path in files)
        {
            using PdfDocument document = PdfDocument.Load(File.ReadAllBytes(path), Corpus.ReadOptions);

            foreach (PdfPageImage image in document.Pages.SelectMany(page => page.Images))
            {
                if (image.Codec != "JBIG2Decode")
                    continue;

                PdfImageData decoded;
                try
                {
                    decoded = image.Decode();
                }
                catch (NotSupportedException)
                {
                    unsupported.Add(Path.GetFileName(path));
                    continue;
                }

                decodedImages++;
                if (first is null)
                {
                    first = decoded.Pixels;
                    firstName = Path.GetFileName(path);
                    continue;
                }

                compared++;
                Assert.True(
                    first.AsSpan().SequenceEqual(decoded.Pixels),
                    $"{Path.GetFileName(path)} decoded differently from {firstName}.");
            }
        }

        Assert.Equal(96, decodedImages);
        Assert.Equal(95, compared);

        Assert.True(
            unsupported.Count == 0,
            $"Unsupported JBIG2 variants: [{string.Join(", ", unsupported)}].");
    }

    [Fact]
    public void ReusedSymbolCodingContextMatchesTheBitmapConformancePicture()
    {
        string? baselinePath = Corpus.Matching("bitmap-symbol.pdf", 1).FirstOrDefault();
        string? reusedPath = Corpus.Matching("bitmap-symbol-context-reuse.pdf", 1).FirstOrDefault();
        Assert.SkipUnless(
            baselinePath is not null && reusedPath is not null,
            "The JBIG2 symbol-context-reuse fixture is not on this machine.");

        PdfImageData baseline = DecodeJbig2Image(baselinePath);
        PdfImageData reused = DecodeJbig2Image(reusedPath);

        Assert.Equal(baseline.Width, reused.Width);
        Assert.Equal(baseline.Height, reused.Height);
        Assert.True(
            baseline.Pixels.AsSpan().SequenceEqual(reused.Pixels),
            "The retained symbol coding context changed the conformance bitmap.");
    }

    [Theory]
    [InlineData("bitmap-symbol-symbolrefine-textrefine.pdf")]
    [InlineData("bitmap-symbol-symhuffrefine-textrefine.pdf")]
    [InlineData("bitmap-symbol-symhuffrefineseveral.pdf")]
    public void RefinementAggregateVariantsMatchTheBitmapConformancePicture(string fileName)
    {
        string? baselinePath = Corpus.Matching("bitmap-symbol.pdf", 1).FirstOrDefault();
        string? variantPath = Corpus.Matching(fileName, 1).FirstOrDefault();
        Assert.SkipUnless(
            baselinePath is not null && variantPath is not null,
            "The JBIG2 refinement-aggregate fixtures are not on this machine.");

        PdfImageData baseline = DecodeJbig2Image(baselinePath);
        PdfImageData variant = DecodeJbig2Image(variantPath);

        Assert.Equal(baseline.Width, variant.Width);
        Assert.Equal(baseline.Height, variant.Height);
        Assert.True(
            baseline.Pixels.AsSpan().SequenceEqual(variant.Pixels),
            $"{fileName} decoded differently from bitmap-symbol.pdf.");
    }

    [Theory]
    [InlineData(
        "hex_0x0006.pdf", 1206, 1154,
        "a32489be0ec5fc79608e95a095e4a5cbcfb907021d3ea3c4a31a63835fc6a147")]
    [InlineData(
        "MOZILLA-3136-0.pdf", 236, 236,
        "7f415d55403e18bc1bf8e3ef0fdc5ddaa9482f63434e9e65afc17d0bbe389e5e")]
    public void PageInformationOnlyCorpusImagesDecodeAsBlankWhite(
        string fileName, int width, int height, string expectedSha256)
    {
        string? path = Corpus.Matching(fileName, 1).FirstOrDefault();
        Assert.SkipUnless(path is not null, "The page-information-only JBIG2 fixture is not on this machine.");

        using PdfDocument document = PdfDocument.Load(File.ReadAllBytes(path), Corpus.ReadOptions);
        List<PdfImageData> decoded = document.Pages
            .SelectMany(page => page.Images)
            .Where(image => image.Codec == "JBIG2Decode")
            .Select(image => image.Decode())
            .ToList();

        PdfImageData blank = Assert.Single(decoded, image =>
            image.Width == width &&
            image.Height == height &&
            Convert.ToHexString(SHA256.HashData(image.Pixels)).Equals(
                expectedSha256, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(width * height * 4, blank.Pixels.Length);
        Assert.True(blank.Pixels.All(value => value == byte.MaxValue), $"{fileName} is not blank white.");
        Assert.All(decoded, image => Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length));
    }

    [Fact]
    public void PageInformationOnlyUsesPdfDimensionsAndDefaultPixel()
    {
        byte[] stream = PageInformationOnly(declaredWidth: 2, declaredHeight: 3, defaultPixel: 0);

        Assert.Equal(new byte[4], Decode(stream, width: 9, height: 2));

        stream[27] |= 0x04;
        Assert.Equal(new byte[] { 0xFF, 0x80, 0xFF, 0x80 }, Decode(stream, width: 9, height: 2));
    }

    [Fact]
    public void RefusesAReusedSymbolContextWithADifferentGenericTemplate()
    {
        Jbig2SymbolDictionaryResult retained = DecodeEmptySymbolDictionary(
            flags: 0x0600, adaptivePixels: [2, -1]); // retained, template 1

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            DecodeEmptySymbolDictionary(
                flags: 0x0900,
                adaptivePixels: [2, -1],
                reusedContext: retained.RetainedContext)); // used, template 2

        Assert.Contains("SDTEMPLATE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAReusedSymbolContextWithDifferentGenericAdaptivePixels()
    {
        Jbig2SymbolDictionaryResult retained = DecodeEmptySymbolDictionary(
            flags: 0x0600, adaptivePixels: [2, -1]); // retained, template 1

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            DecodeEmptySymbolDictionary(
                flags: 0x0500,
                adaptivePixels: [3, -1],
                reusedContext: retained.RetainedContext)); // used, template 1

        Assert.Contains("SDAT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAReusedSymbolContextWithDifferentRefinementAdaptivePixels()
    {
        Jbig2SymbolDictionaryResult retained = DecodeEmptySymbolDictionary(
            flags: 0x0602,
            adaptivePixels: [2, -1, -1, -1, -1, -1]); // retained, refinement aggregation

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            DecodeEmptySymbolDictionary(
                flags: 0x0502,
                adaptivePixels: [2, -1, -1, -1, 0, -1],
                reusedContext: retained.RetainedContext)); // used, different SDRAT

        Assert.Contains("SDRAT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAReusedSymbolContextWithADifferentRefinementTemplate()
    {
        Jbig2SymbolDictionaryResult retained = DecodeEmptySymbolDictionary(
            flags: 0x0602,
            adaptivePixels: [2, -1, -1, -1, -1, -1]); // retained, refinement template 0

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            DecodeEmptySymbolDictionary(
                flags: 0x1502,
                adaptivePixels: [2, -1],
                reusedContext: retained.RetainedContext)); // used, refinement template 1

        Assert.Contains("SDRTEMPLATE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReusedSymbolContextRequiresTheSourceDictionaryToRetainIt()
    {
        Jbig2SymbolDictionaryResult notRetained = DecodeEmptySymbolDictionary(
            flags: 0x0400, adaptivePixels: [2, -1]); // template 1, no retained bit
        Assert.Null(notRetained.RetainedContext);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            DecodeEmptySymbolDictionary(
                flags: 0x0500,
                adaptivePixels: [2, -1],
                reusedContext: notRetained.RetainedContext)); // used

        Assert.Contains("did not retain", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x0101, "used")]
    [InlineData(0x0201, "retained")]
    public void RefusesBitmapContextFlagsForCollectiveHuffmanSymbols(int flags, string flagName)
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            DecodeEmptySymbolDictionary(flags, []));

        Assert.Contains(flagName, error.Message, StringComparison.Ordinal);
        Assert.Contains("must be zero", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTheSegmentKindsItDoesNotKnow()
    {
        // Segment type 13 is not one the standard defines for a PDF-embedded stream.
        byte[] stream = [0, 0, 0, 0, 13, 0x00, 0x01, 0, 0, 0, 0];

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Decode(stream, 16, 16));

        Assert.Contains("type 13", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenARegionSegmentIsCutShort()
    {
        // A refinement region segment header promising eight bytes that cannot hold a region field.
        byte[] stream = [0, 0, 0, 0, 42, 0x00, 0x01, 0, 0, 0, 8, 0, 0, 0, 1, 0, 0, 0, 1];

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Decode(stream, 16, 16));

        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenPageInformationIsCutShort()
    {
        byte[] stream = PageInformationOnly(declaredWidth: 2, declaredHeight: 3, defaultPixel: 0);
        Array.Resize(ref stream, 28);

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Decode(stream, 16, 16));

        Assert.Contains("page information", error.Message, StringComparison.Ordinal);
        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoresAnUnknownOptionalExtension()
    {
        byte[] stream = ExtensionWithPageInformation(0x00000001u);

        Assert.Equal(new byte[4], Decode(stream, width: 9, height: 2));
    }

    [Theory]
    [InlineData(0xA0000001u)] // Necessary and Reserved, but unknown.
    [InlineData(0x80000001u)] // Necessary without Reserved is itself malformed.
    public void RefusesAnUnknownNecessaryExtension(uint extensionType)
    {
        byte[] stream = ExtensionWithPageInformation(extensionType);

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Decode(stream, width: 9, height: 2));

        Assert.Contains("necessary extension", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"0x{extensionType:X8}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenAnExtensionHeaderIsCutShort()
    {
        byte[] stream = ExtensionWithPageInformation(0xA0000001u, dataLength: 3);

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Decode(stream, width: 9, height: 2));

        Assert.Contains("extension header", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("truncated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecodesTheSymbolCodedVariantsOfTheConformancePicture()
    {
        // The conformance family covers every way a text region can place a symbol: each reference
        // corner, transposed and not, each composition operator, a negative gap offset, refinement,
        // and segment numbers wide enough to change how references are stored.
        string[] wanted =
        [
            "bitmap-symbol.pdf", "bitmap-symbol-textbottomleft.pdf", "bitmap-symbol-textbottomright.pdf",
            "bitmap-symbol-texttopright.pdf", "bitmap-symbol-texttranspose.pdf",
            "bitmap-symbol-textcomposite.pdf", "bitmap-symbol-negative-sbdsoffset.pdf",
            "bitmap-symbol-big-segmentid.pdf", "bitmap-symbol-textrefine.pdf",
        ];

        var decoded = new List<(string Name, byte[] Pixels)>();

        foreach (string name in wanted)
        {
            string? path = Corpus.Matching(name, 1).FirstOrDefault();
            if (path is null)
                continue;

            using PdfDocument document = PdfDocument.Load(File.ReadAllBytes(path), Corpus.ReadOptions);

            foreach (PdfPageImage image in document.Pages.SelectMany(page => page.Images))
            {
                if (image.Codec == "JBIG2Decode")
                    decoded.Add((name, image.Decode().Pixels));
            }
        }

        Assert.SkipUnless(decoded.Count >= 5, "The corpus of symbol-coded JBIG2 variants is not on this machine.");

        // Every variant encodes the same picture, so any difference is a decoding fault.
        foreach ((string name, byte[] pixels) in decoded[1..])
        {
            Assert.True(
                decoded[0].Pixels.AsSpan().SequenceEqual(pixels),
                $"{name} decoded differently from {decoded[0].Name}.");
        }
    }

    [Fact]
    public void RefusesAColourPaletteBecauseThePdfStandardForbidsIt()
    {
        // Segment type 54 is a colour palette, which ISO 32000-2 7.4.7 does not allow in PDF.
        byte[] stream = [0, 0, 0, 0, 54, 0x00, 0x01, 0, 0, 0, 0];

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Decode(stream, 16, 16));

        Assert.Contains("colour palette", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenThereIsNoRegionAtAll()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Decode([], 16, 16));

        Assert.Contains("no region to decode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnImageTooLargeToHold()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Decode([], 40000, 40000));

        Assert.Contains("too large", error.Message, StringComparison.Ordinal);
    }

    private static byte[] Decode(byte[] stream, int width, int height) =>
        Inkwright.Images.Jbig2.Jbig2Decoder.Decode(stream, ReadOnlyMemory<byte>.Empty, width, height);

    private static byte[] PageInformationOnly(int declaredWidth, int declaredHeight, int defaultPixel)
    {
        byte[] stream = new byte[30];
        stream[4] = (byte)Jbig2SegmentType.PageInformation;
        stream[6] = 1; // page association
        stream[10] = 19; // segment data length
        BinaryPrimitives.WriteUInt32BigEndian(stream.AsSpan(11), (uint)declaredWidth);
        BinaryPrimitives.WriteUInt32BigEndian(stream.AsSpan(15), (uint)declaredHeight);
        stream[27] = (byte)(defaultPixel << 2);
        return stream;
    }

    private static byte[] ExtensionWithPageInformation(uint extensionType, int dataLength = 4)
    {
        byte[] extension = new byte[11 + dataLength];
        extension[4] = (byte)Jbig2SegmentType.Extension;
        BinaryPrimitives.WriteUInt32BigEndian(extension.AsSpan(7), (uint)dataLength);
        Span<byte> extensionHeader = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(extensionHeader, extensionType);
        extensionHeader[..dataLength].CopyTo(extension.AsSpan(11));

        byte[] page = PageInformationOnly(declaredWidth: 2, declaredHeight: 3, defaultPixel: 0);
        BinaryPrimitives.WriteUInt32BigEndian(page, 1);
        return [.. extension, .. page];
    }

    private static PdfImageData DecodeJbig2Image(string path)
    {
        using PdfDocument document = PdfDocument.Load(File.ReadAllBytes(path), Corpus.ReadOptions);
        PdfPageImage image = Assert.Single(
            document.Pages.SelectMany(page => page.Images),
            candidate => candidate.Codec == "JBIG2Decode");
        return image.Decode();
    }

    private static Jbig2SymbolDictionaryResult DecodeEmptySymbolDictionary(
        int flags,
        sbyte[] adaptivePixels,
        Jbig2SymbolCodingContext? reusedContext = null)
    {
        byte[] data = new byte[10 + adaptivePixels.Length];
        data[0] = (byte)(flags >> 8);
        data[1] = (byte)flags;

        for (int i = 0; i < adaptivePixels.Length; i++)
            data[i + 2] = (byte)adaptivePixels[i];

        return Jbig2SymbolDictionary.Decode(data, [], [], reusedContext);
    }
}
