using Inkwright.Images;
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
        IReadOnlyList<string> files = Corpus.Matching("bitmap-");
        Assert.SkipUnless(files.Count >= 5, "The corpus of JBIG2 variants is not on this machine.");

        byte[]? first = null;
        string? firstName = null;
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

        Assert.True(compared >= 4, $"Only {compared} variants were compared.");

        string[] knownUnsupported =
        [
            "bitmap-symbol-context-reuse.pdf",
            "bitmap-symbol-symbolrefine-textrefine.pdf",
            "bitmap-symbol-symhuffrefine-textrefine.pdf",
            "bitmap-symbol-symhuffrefineseveral.pdf",
        ];
        HashSet<string> presentKnown = files.Select(Path.GetFileName)
            .Where(name => name is not null && knownUnsupported.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(
            unsupported.SetEquals(presentKnown),
            $"Unsupported JBIG2 variants changed. Expected [{string.Join(", ", presentKnown)}], " +
            $"got [{string.Join(", ", unsupported)}].");
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
}
