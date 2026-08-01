using System.Text;
using Inkwright.Content;
using Inkwright.Fonts;
using Inkwright.Text;
using Xunit;

namespace Inkwright.Tests;

public class TextExtractionTests
{
    [Fact]
    public void ReadsBackWhatItWrote()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfFont font = document.Fonts.Helvetica;

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("The quick brown fox", 72, 700, font, 14);
            canvas.DrawText("jumps over the lazy dog", 72, 680, font, 14);
            canvas.Commit();
        }

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        string text = reloaded.Pages[0].ExtractText();

        Assert.Contains("The quick brown fox", text, StringComparison.Ordinal);
        Assert.Contains("jumps over the lazy dog", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("The quick", StringComparison.Ordinal) <
                    text.IndexOf("jumps over", StringComparison.Ordinal),
            "Lines should come out top to bottom.");
    }

    [Fact]
    public void PlacesLettersWhereTheyWereDrawn()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfFont font = document.Fonts.Helvetica;

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("AB", 100, 500, font, 20);
            canvas.Commit();
        }

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        IReadOnlyList<PdfLetter> letters = reloaded.Pages[0].ExtractLetters();

        Assert.Equal(2, letters.Count);
        Assert.Equal("A", letters[0].Text);
        Assert.Equal(100, letters[0].Origin.X, 1);
        Assert.Equal(500, letters[0].Origin.Y, 1);

        // Helvetica 'A' is 667/1000 em, so 'B' starts 13.34 pt to the right at size 20.
        Assert.Equal(100 + (667 * 20 / 1000d), letters[1].Origin.X, 1);
        Assert.Equal(20, letters[0].FontSize, 1);
    }

    [Fact]
    public void HonoursTransformationsAndTextMatrices()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfFont font = document.Fonts.Helvetica;

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.Save().Translate(200, 300).Scale(2, 2);
            canvas.DrawText("X", 0, 0, font, 10);
            canvas.Restore();
            canvas.Commit();
        }

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfLetter letter = reloaded.Pages[0].ExtractLetters()[0];

        Assert.Equal(200, letter.Origin.X, 1);
        Assert.Equal(300, letter.Origin.Y, 1);
        Assert.Equal(20, letter.FontSize, 1);
    }

    [Fact]
    public void SkipsInvisibleTextUsedForScannedPages()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfFont font = document.Fonts.Helvetica;

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.BeginText()
                  .Font(font, 12)
                  .TextRenderMode(PdfTextRenderMode.Invisible)
                  .TextMatrix(PdfMatrix.Translation(50, 50))
                  .ShowText("hidden")
                  .EndText();
            canvas.Commit();
        }

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        Assert.Empty(reloaded.Pages[0].ExtractLetters());
    }

    [Fact]
    public void ReadsTextFromEmbeddedUnicodeFonts()
    {
        string? path = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans");
        Assert.SkipWhen(path is null, "No TrueType font was found on this machine.");

        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfFont font = document.Fonts.Embed(path!);

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("Привет, мир", 60, 600, font, 18);
            canvas.Commit();
        }

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        Assert.Contains("Привет, мир", reloaded.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsTextDrawnInsideAFormXObject()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfFont font = document.Fonts.Helvetica;

        var resources = new Cos.PdfDictionary();
        byte[] formContent;
        using (ContentCanvas inner = ContentCanvas.ForStream(document, resources))
        {
            inner.DrawText("inside a form", 0, 0, font, 12);
            formContent = inner.ToArray();
        }

        var form = Cos.PdfStream.FromData(formContent);
        form.Dictionary.Set(Cos.PdfName.Get("Type"), Cos.PdfValue.Name("XObject"));
        form.Dictionary.Set(Cos.PdfName.Get("Subtype"), Cos.PdfValue.Name("Form"));
        form.Dictionary.Set(Cos.PdfName.Get("BBox"), Cos.PdfValue.Array(new PdfRectangle(0, 0, 300, 50).ToArray()));
        form.Dictionary.Set(Cos.PdfName.Get("Resources"), Cos.PdfValue.Dictionary(resources));
        form.Dictionary.Set(Cos.PdfName.Get("Matrix"), Cos.PdfValue.Array(PdfMatrix.Translation(90, 400).ToArray()));

        Cos.PdfObjectId formId = document.AddObject(Cos.PdfValue.Stream(form));

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawXObject(canvas.Resources.AddXObject(Cos.PdfValue.Reference(formId)));
            canvas.Commit();
        }

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        IReadOnlyList<PdfLetter> letters = reloaded.Pages[0].ExtractLetters();

        Assert.Contains("inside a form", reloaded.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Equal(90, letters[0].Origin.X, 1);
        Assert.Equal(400, letters[0].Origin.Y, 1);
    }

    [Fact]
    public void ParsesToUnicodeCMaps()
    {
        byte[] cmap = Encoding.ASCII.GetBytes("""
            /CIDInit /ProcSet findresource begin
            begincmap
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            2 beginbfchar
            <0003> <0020>
            <0024> <0041>
            endbfchar
            1 beginbfrange
            <0025> <0027> <0042>
            endbfrange
            endcmap
            """);

        CMap map = CMap.Parse(cmap);

        Assert.Equal(" ", map.GetText(0x0003));
        Assert.Equal("A", map.GetText(0x0024));
        Assert.Equal("B", map.GetText(0x0025));
        Assert.Equal("D", map.GetText(0x0027));

        uint code = map.ReadCode([0x00, 0x24], 0, out int consumed);
        Assert.Equal(2, consumed);
        Assert.Equal(0x0024u, code);
    }

    [Fact]
    public void DoesNotTruncateARealNumberIntoAMarkedContentIdentifier()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        var font = new Cos.PdfDictionary(3);
        font.Set(Cos.PdfName.Get("Type"), Cos.PdfValue.Name("Font"));
        font.Set(Cos.PdfName.Get("Subtype"), Cos.PdfValue.Name("Type1"));
        font.Set(Cos.PdfName.Get("BaseFont"), Cos.PdfValue.Name("Helvetica"));
        var fonts = new Cos.PdfDictionary(1);
        fonts.Set(Cos.PdfName.Get("F1"), Cos.PdfValue.Reference(document.AddObject(font)));
        page.Resources.Set(Cos.PdfName.Get("Font"), fonts);
        page.SetContent(Encoding.ASCII.GetBytes(
            "/P <</MCID 0.5>> BDC BT /F1 12 Tf 60 700 Td (text) Tj ET EMC\n"));
        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());

        IReadOnlyList<PdfLetter> letters = reloaded.Pages[0].ExtractLetters();

        Assert.NotEmpty(letters);
        Assert.All(letters, letter => Assert.Null(letter.MarkedContentId));
    }
}
