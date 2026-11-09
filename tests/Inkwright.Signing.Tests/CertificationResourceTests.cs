using System.Security.Cryptography.X509Certificates;
using System.Text;
using Inkwright.Cos;
using Inkwright.Forms;
using Xunit;

namespace Inkwright.Signing.Tests;

/// <summary>
/// A page's content stream only names what it draws, so a certified page can be changed without
/// touching a byte of it by changing what those names resolve to.
/// </summary>
public sealed class CertificationResourceTests
{
    private static readonly PdfSaveOptions Flat = new() { UseObjectStreams = false, Compress = false };
    private static readonly PdfName Fm = PdfName.Get("Fm");
    private static readonly PdfName Im = PdfName.Get("Im");

    private static PdfStream FormStream(string text, PdfDictionary fonts)
    {
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Font"), PdfValue.Dictionary(fonts));

        PdfStream form = PdfStream.FromData(Encoding.ASCII.GetBytes($"BT /F1 12 Tf 0 0 Td ({text}) Tj ET"), compress: false);
        form.Dictionary.Set(PdfName.Get("Type"), PdfValue.Name("XObject"));
        form.Dictionary.Set(PdfName.Get("Subtype"), PdfValue.Name("Form"));
        form.Dictionary.Set(PdfName.Get("BBox"), PdfValue.Array(PdfArray.OfNumbers(0, 0, 400, 40)));
        form.Dictionary.Set(PdfName.Get("Resources"), PdfValue.Dictionary(resources));
        return form;
    }

    private static PdfDictionary Fonts(PdfDocument document)
    {
        var font = new PdfDictionary();
        font.Set(PdfName.Get("Type"), PdfValue.Name("Font"));
        font.Set(PdfName.Get("Subtype"), PdfValue.Name("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));

        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(font))));
        return fonts;
    }

    private static PdfStream ImageStream(byte sample)
    {
        PdfStream image = PdfStream.FromData(new[] { sample }, compress: false);
        image.Dictionary.Set(PdfName.Get("Type"), PdfValue.Name("XObject"));
        image.Dictionary.Set(PdfName.Get("Subtype"), PdfValue.Name("Image"));
        image.Dictionary.Set(PdfName.Get("Width"), PdfValue.Integer(1));
        image.Dictionary.Set(PdfName.Get("Height"), PdfValue.Integer(1));
        image.Dictionary.Set(PdfName.Get("BitsPerComponent"), PdfValue.Integer(8));
        image.Dictionary.Set(PdfName.Get("ColorSpace"), PdfValue.Name("DeviceGray"));
        return image;
    }

    private static byte[] BuildPages(int pages)
    {
        using var document = PdfDocument.Create();
        PdfObjectId form = document.AddObject(PdfValue.Stream(FormStream("APPROVED", Fonts(document))));
        PdfObjectId image = document.AddObject(PdfValue.Stream(ImageStream(0x10)));

        for (int i = 0; i < pages; i++)
        {
            PdfPage page = document.Pages.Add(PageSize.A4);
            var xObjects = new PdfDictionary();
            xObjects.Set(Fm, PdfValue.Reference(form));
            xObjects.Set(Im, PdfValue.Reference(image));
            page.Resources.Set(PdfName.Get("XObject"), PdfValue.Dictionary(xObjects));
            page.SetContent("q 1 0 0 1 72 700 cm /Fm Do Q q 10 0 0 10 72 600 cm /Im Do Q"u8.ToArray(), compress: false);
        }

        return document.ToArray(Flat);
    }

    private static byte[] Certify(byte[] file, X509Certificate2 certificate, PdfCertificationLevel level) =>
        TestDocuments.Sign(file, new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Certification",
            CertificationLevel = level,
        });

    private static PdfObjectId XObjectId(PdfPage page, PdfName name) =>
        page.Resources.GetDictionary(PdfName.Get("XObject"))![name].AsReference;

    private static byte[] Update(byte[] certified, Action<PdfDocument> change)
    {
        using PdfDocument document = PdfDocument.Load(certified);
        change(document);
        return document.ToIncrementalArray(Flat);
    }

    private static PdfSignatureInfo Verified(byte[] file) =>
        PdfSignatureVerifier.Verify(file).Single(info => info.FieldName == "Certification");

    private static void ReplaceForm(PdfDocument document, string text)
    {
        PdfPage page = document.Pages[0];
        PdfObjectId id = XObjectId(page, Fm);
        PdfDictionary fonts = document.Resolve(PdfValue.Reference(id)).AsStream!.Dictionary
            .GetDictionary(PdfName.Get("Resources"))!.GetDictionary(PdfName.Get("Font"))!;
        document.SetObject(id, PdfValue.Stream(FormStream(text, fonts)));
        document.MarkChanged(id);
    }

    [Theory]
    [InlineData(PdfCertificationLevel.NoChanges)]
    [InlineData(PdfCertificationLevel.FormFilling)]
    [InlineData(PdfCertificationLevel.FormFillingAndAnnotations)]
    public void ChangingAFormBehindAnUnchangedContentStreamIsAViolation(PdfCertificationLevel level)
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Author");
        byte[] certified = Certify(BuildPages(1), certificate, level);

        byte[] changed = Update(certified, document => ReplaceForm(document, "REJECTED"));

        using (PdfDocument reopened = PdfDocument.Load(changed))
            Assert.Contains("REJECTED", reopened.Pages[0].ExtractText(), StringComparison.Ordinal);

        PdfSignatureInfo info = Verified(changed);
        Assert.True(info.IsIntact, info.Problem);
        Assert.False(info.CoversWholeDocument);
        Assert.False(info.AreSubsequentChangesPermitted);
        Assert.Contains(info.ModificationViolations, v => v.Contains("/Resources", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangingAnImageResourceIsAViolation()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Author");
        byte[] certified = Certify(BuildPages(1), certificate, PdfCertificationLevel.NoChanges);

        byte[] changed = Update(certified, document =>
        {
            PdfObjectId id = XObjectId(document.Pages[0], Im);
            document.SetObject(id, PdfValue.Stream(ImageStream(0xF0)));
            document.MarkChanged(id);
        });

        Assert.False(Verified(changed).AreSubsequentChangesPermitted);
    }

    [Fact]
    public void ChangingAFontResourceIsAViolation()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Author");
        byte[] certified = Certify(BuildPages(1), certificate, PdfCertificationLevel.NoChanges);

        byte[] changed = Update(certified, document =>
        {
            PdfDictionary fonts = document.Resolve(PdfValue.Reference(XObjectId(document.Pages[0], Fm))).AsStream!.Dictionary
                .GetDictionary(PdfName.Get("Resources"))!.GetDictionary(PdfName.Get("Font"))!;
            PdfObjectId fontId = fonts[PdfName.Get("F1")].AsReference;
            fonts.GetDictionary(PdfName.Get("F1"))!.Set(PdfName.Get("BaseFont"), PdfValue.Name("Courier"));
            document.MarkChanged(fontId);
        });

        Assert.False(Verified(changed).AreSubsequentChangesPermitted);
    }

    [Fact]
    public void ChangingASharedFormIsReportedOnEveryPageThatDrawsIt()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Author");
        byte[] certified = Certify(BuildPages(2), certificate, PdfCertificationLevel.NoChanges);

        byte[] changed = Update(certified, document => ReplaceForm(document, "REJECTED"));

        PdfSignatureInfo info = Verified(changed);
        Assert.Contains(info.ModificationViolations, v => v.StartsWith("Page 1 ", StringComparison.Ordinal));
        Assert.Contains(info.ModificationViolations, v => v.StartsWith("Page 2 ", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangingAnAnnotationAppearanceIsAViolationWhenNothingMayChange()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Author");

        byte[] file;
        PdfObjectId appearanceId;
        using (var document = PdfDocument.Create())
        {
            PdfPage page = document.Pages.Add(PageSize.A4);
            appearanceId = document.AddObject(PdfValue.Stream(FormStream("APPROVED", Fonts(document))));

            var appearance = new PdfDictionary();
            appearance.Set(PdfName.Get("N"), PdfValue.Reference(appearanceId));
            var stamp = new PdfDictionary();
            stamp.Set(PdfName.Get("Type"), PdfValue.Name("Annot"));
            stamp.Set(PdfName.Get("Subtype"), PdfValue.Name("Stamp"));
            stamp.Set(PdfName.Get("Rect"), PdfValue.Array(PdfArray.OfNumbers(72, 700, 272, 740)));
            stamp.Set(PdfName.Get("AP"), PdfValue.Dictionary(appearance));
            var annotations = new PdfArray();
            annotations.Add(PdfValue.Reference(document.AddObject(PdfValue.Dictionary(stamp))));
            page.Dictionary.Set(PdfName.Get("Annots"), PdfValue.Array(annotations));
            file = document.ToArray(Flat);
        }

        byte[] certified = Certify(file, certificate, PdfCertificationLevel.NoChanges);
        byte[] changed = Update(certified, document =>
        {
            PdfDictionary fonts = document.Resolve(PdfValue.Reference(appearanceId)).AsStream!.Dictionary
                .GetDictionary(PdfName.Get("Resources"))!.GetDictionary(PdfName.Get("Font"))!;
            document.SetObject(appearanceId, PdfValue.Stream(FormStream("REJECTED", fonts)));
            document.MarkChanged(appearanceId);
        });

        Assert.False(Verified(changed).AreSubsequentChangesPermitted);
    }

    [Fact]
    public void RewritingAFormWithTheSameContentIsNotAChange()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Author");
        byte[] certified = Certify(BuildPages(1), certificate, PdfCertificationLevel.NoChanges);

        byte[] rewritten = Update(certified, document =>
        {
            PdfPage page = document.Pages[0];
            PdfObjectId id = XObjectId(page, Fm);
            PdfStream original = document.Resolve(PdfValue.Reference(id)).AsStream!;

            PdfStream compressed = PdfStream.FromData(original.Decode(), compress: true);
            foreach (KeyValuePair<PdfName, PdfValue> entry in original.Dictionary)
            {
                if (entry.Key.Value is not ("Length" or "Filter" or "DecodeParms"))
                    compressed.Dictionary.Set(entry.Key, entry.Value);
            }

            document.SetObject(id, PdfValue.Stream(compressed));
            document.MarkChanged(id);
        });

        PdfSignatureInfo info = Verified(rewritten);
        Assert.True(info.AreSubsequentChangesPermitted, string.Join("; ", info.ModificationViolations));
    }

    [Fact]
    public void FillingAFormFieldStaysPermitted()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Author");

        byte[] file;
        using (var document = PdfDocument.Create())
        {
            PdfPage page = document.Pages.Add(PageSize.A4);
            PdfForm.OpenOrCreate(document).AddTextField(page, PdfRectangle.FromSize(72, 700, 150, 20), "name", "Alice");
            file = document.ToArray(Flat);
        }

        byte[] certified = Certify(file, certificate, PdfCertificationLevel.FormFilling);
        byte[] filled = Update(certified, document => PdfForm.Open(document)!.Set("name", "Bob"));

        PdfSignatureInfo info = Verified(filled);
        Assert.True(info.IsIntact, info.Problem);
        Assert.True(info.AreSubsequentChangesPermitted, string.Join("; ", info.ModificationViolations));
    }
}
