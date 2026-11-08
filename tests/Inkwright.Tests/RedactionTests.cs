using Inkwright.Annotations;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Images;
using Xunit;

namespace Inkwright.Tests;

public sealed class RedactionTests
{
    [Fact]
    public void RedactText_PhysicallyRemovesTargetSubstringAndPreservesSurroundingText()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);

        using (var canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("Account: 4412-9981-SECRET-7721 Status: Active", 72, 700, document.Fonts.Helvetica, 12);
            canvas.Commit();
        }

        PdfRedactionResult result = page.RedactText("SECRET", new PdfRedactionOptions
        {
            FillColor = PdfColor.Black,
            OverlayText = "REDACTED",
        });

        Assert.True(result.HasChanges);
        Assert.Equal(1, result.RegionsApplied);
        Assert.Equal(6, result.GlyphsRemoved);

        byte[] saved = document.ToArray();
        using var reloaded = PdfDocument.Load(saved);
        PdfPage reloadedPage = reloaded.Pages[0];

        string extracted = reloadedPage.ExtractText();
        Assert.DoesNotContain("SECRET", extracted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Account:", extracted, StringComparison.Ordinal);
        Assert.Contains("4412-9981-", extracted, StringComparison.Ordinal);
        Assert.Contains("-7721", extracted, StringComparison.Ordinal);
        Assert.Contains("Status: Active", extracted, StringComparison.Ordinal);
        Assert.Contains("REDACTED", extracted, StringComparison.Ordinal);

        string rawContent = System.Text.Encoding.ASCII.GetString(reloadedPage.GetContent());
        Assert.DoesNotContain("SECRET", rawContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_RemovesImageXObjectAndInlineImageInRegion()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);

        byte[] rgb = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
        PdfImage image = PdfImage.FromPixels(document, 2, 2, rgb);

        using (var canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawImage(image, PdfRectangle.FromSize(100, 500, 80, 80));
            canvas.DrawText("Caption below image", 100, 480, document.Fonts.Helvetica, 11);
            canvas.Commit();
        }

        Assert.Single(page.PlacedImages);
        Assert.Single(page.Images);

        PdfRedactionResult result = page.Redact(PdfRectangle.FromSize(95, 495, 90, 90));

        Assert.Equal(1, result.ImagesRemoved);
        Assert.Empty(page.PlacedImages);
        Assert.Empty(page.Images);
        Assert.Contains("Caption below image", page.ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyRedactions_ExecutesRedactAnnotationsAndRemovesThem()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);

        using (var canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("Visible header", 72, 720, document.Fonts.Helvetica, 12);
            canvas.DrawText("Classified token: ALPHA-99", 72, 680, document.Fonts.Helvetica, 12);
            canvas.Commit();
        }

        Text.PdfTextMatch match = Assert.Single(page.FindText("ALPHA-99"));
        page.Annotations.AddRedaction(match.Bounds, overlayText: "[REMOVED]", fillColor: PdfColor.Gray(0.1f));
        Assert.Contains(page.Annotations, a => a.Type == PdfAnnotationType.Redact);

        PdfRedactionResult result = document.ApplyRedactions();

        Assert.Equal(1, result.RegionsApplied);
        Assert.True(result.GlyphsRemoved >= 8);
        Assert.True(result.AnnotationsRemoved >= 1);
        Assert.DoesNotContain(page.Annotations, a => a.Type == PdfAnnotationType.Redact);

        string text = page.ExtractText();
        Assert.DoesNotContain("ALPHA-99", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Visible header", text, StringComparison.Ordinal);
        Assert.Contains("[REMOVED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_InsideFormXObject_ScrubsFormStreamGlyphs()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);

        var formResources = new PdfDictionary();
        document.Attach(PdfValue.Dictionary(formResources));
        byte[] formBytes;
        using (var formCanvas = ContentCanvas.ForStream(document, formResources))
        {
            formCanvas.DrawText("FormPublic FormSecret", 10, 20, document.Fonts.Helvetica, 12);
            formBytes = formCanvas.ToArray();
        }

        var formStream = PdfStream.FromData(formBytes);
        formStream.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        formStream.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Form));
        formStream.Dictionary.Set(PdfName.Get("BBox"), PdfValue.Array(PdfArray.OfNumbers(0, 0, 300, 60)));
        formStream.Dictionary.Set(PdfNames.Resources, PdfValue.Dictionary(formResources));
        PdfObjectId formId = document.AddObject(PdfValue.Stream(formStream));

        using (var pageCanvas = ContentCanvas.ForPage(page))
        {
            PdfName formName = pageCanvas.Resources.AddXObject(PdfValue.Reference(formId));
            pageCanvas.Save().Translate(72, 600).DrawXObject(formName).Restore();
            pageCanvas.Commit();
        }

        Assert.Contains("FormSecret", page.ExtractText(), StringComparison.Ordinal);

        PdfRedactionResult result = page.RedactText("FormSecret", new PdfRedactionOptions { FillColor = null });

        Assert.True(result.GlyphsRemoved >= 10);
        string after = page.ExtractText();
        Assert.DoesNotContain("FormSecret", after, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FormPublic", after, StringComparison.Ordinal);
    }
}
