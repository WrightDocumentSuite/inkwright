using Inkwright.Cos;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Tests;

public class PdfReaderTests
{
    [Fact]
    public void ReadsMinimalHandWrittenFile()
    {
        byte[] file = MinimalPdf.OnePage();

        using var reader = new MemoryStream(file);
        PdfReader pdf = PdfReader.Open(reader);

        Assert.Equal(new PdfVersion(1, 7), pdf.Version);
        Assert.NotNull(pdf.Catalog);
        Assert.False(pdf.IsEncrypted);
        Assert.True(pdf.Diagnostics.IsClean, string.Join("; ", pdf.Diagnostics));

        PdfDictionary pages = pdf.Catalog!.GetDictionary(PdfName.Get("Pages"))!;
        Assert.Equal(1, pages.GetInteger(PdfName.Get("Count")));
    }

    [Fact]
    public void RecoversFromDestroyedCrossReferenceTable()
    {
        byte[] file = MinimalPdf.OnePage();
        int index = file.AsSpan().LastIndexOf("startxref"u8);
        // Point startxref at nonsense: the reader must fall back on scanning.
        file[index + 10] = (byte)'9';
        file[index + 11] = (byte)'9';

        PdfReader pdf = PdfReader.Open(file);

        Assert.NotNull(pdf.Catalog);
        Assert.True(pdf.WasRebuilt);
    }

    [Fact]
    public void ResolvesIndirectReferencesAndStreams()
    {
        PdfReader pdf = PdfReader.Open(MinimalPdf.OnePage());
        PdfDictionary page = FirstPage(pdf);

        PdfStream? contents = page.GetStream(PdfName.Get("Contents"));
        Assert.NotNull(contents);
        Assert.Contains("Hello", System.Text.Encoding.ASCII.GetString(contents!.DecodedSpan()), StringComparison.Ordinal);
    }

    [Fact]
    public void ToleratesWrongStreamLength()
    {
        string source = MinimalPdf.Source.Replace("/Length 44", "/Length 4", StringComparison.Ordinal);
        PdfReader pdf = PdfReader.Open(MinimalPdf.Build(source));

        PdfStream contents = FirstPage(pdf).GetStream(PdfName.Get("Contents"))!;
        Assert.Contains("Hello", System.Text.Encoding.ASCII.GetString(contents.DecodedSpan()), StringComparison.Ordinal);
        Assert.Contains(pdf.Diagnostics, w => w.Code == Diagnostics.PdfWarningCode.StreamLengthMismatch);
    }

    internal static PdfDictionary FirstPage(PdfReader pdf)
    {
        PdfDictionary pages = pdf.Catalog!.GetDictionary(PdfName.Get("Pages"))!;
        PdfArray kids = pages.GetArray(PdfName.Get("Kids"))!;
        return kids.GetDictionary(0)!;
    }
}
