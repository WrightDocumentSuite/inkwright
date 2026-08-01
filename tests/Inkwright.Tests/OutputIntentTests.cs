using Inkwright.Cos;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Output intents (ISO 32000-1 §14.11.5) at both the document and, as PDF 2.0 allows, the page
/// level.
/// </summary>
public class OutputIntentTests
{
    [Fact]
    public void AddsADocumentLevelOutputIntent()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);
        document.AddOutputIntent(PdfOutputIntent.SRgb());

        byte[] file = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(file);

        PdfArray intents = reopened.Catalog.GetArray(PdfNames.OutputIntents)!;
        PdfDictionary intent = reopened.Resolve(intents.Get(0)).AsDictionary!;

        Assert.Equal("OutputIntent", intent.GetName(PdfName.Get("Type"))!.Value);
        Assert.Equal("GTS_PDFA1", intent.GetName(PdfName.Get("S"))!.Value);

        PdfStream profile = reopened.Resolve(intent.Get(PdfName.Get("DestOutputProfile"))).AsStream!;
        Assert.Equal(3, profile.Dictionary.GetInteger(PdfName.Get("N")));
        Assert.True(profile.DecodedSpan().Length > 0);
    }

    [Fact]
    public void AddsAPageLevelOutputIntent()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        page.AddOutputIntent(PdfOutputIntent.SRgb("GTS_PDFX"));

        byte[] file = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(file);

        PdfArray intents = reopened.Pages[0].Dictionary.GetArray(PdfName.Get("OutputIntents"))!;
        PdfDictionary intent = reopened.Resolve(intents.Get(0)).AsDictionary!;
        Assert.Equal("GTS_PDFX", intent.GetName(PdfName.Get("S"))!.Value);
        Assert.Equal(PdfVersion.Pdf20, reopened.Version);

        // The catalog carries no intent of its own; this one belongs to the page.
        Assert.Null(reopened.Catalog.GetArray(PdfNames.OutputIntents));
    }

    [Fact]
    public void AddsMoreThanOneIntentToAPage()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        page.AddOutputIntent(PdfOutputIntent.SRgb());
        page.AddOutputIntent(PdfOutputIntent.FromIccProfile(Metadata.IccProfile.CreateSRgb(), 3, "GTS_PDFX", "Press"));

        byte[] file = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(file);

        PdfArray intents = reopened.Pages[0].Dictionary.GetArray(PdfName.Get("OutputIntents"))!;
        Assert.Equal(2, intents.Count);
    }
}
