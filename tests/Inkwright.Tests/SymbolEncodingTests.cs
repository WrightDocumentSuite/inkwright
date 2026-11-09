using System.Text;
using Inkwright.Cos;
using Inkwright.Fonts;
using Xunit;

namespace Inkwright.Tests;

public class SymbolEncodingTests
{
    [Theory]
    [InlineData("Symbol", false, "αβγ")]
    [InlineData("Symbol", true, "αβγ")]
    [InlineData("ABCDEF+Symbol", false, "αβγ")]
    [InlineData("Symbol,Bold", false, "αβγ")]
    [InlineData("Helvetica", false, "abg")]
    public void ExtractsBuiltInEncodingWithoutToUnicode(string name, bool descriptor, string expected)
    {
        PdfDictionary font = Font(name);
        if (descriptor)
        {
            var flags = new PdfDictionary();
            flags.Set(PdfName.Get("Flags"), PdfValue.Integer(4));
            font.Set(PdfName.Get("FontDescriptor"), PdfValue.Dictionary(flags));
        }

        using PdfDocument document = Document(font);
        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        Assert.Equal(expected, reloaded.Pages[0].ExtractText().Trim());
    }

    [Theory]
    [InlineData(0x22, "universal", "∀")]
    [InlineData(0x41, "Alpha", "Α")]
    [InlineData(0x44, "Delta", "Δ")]
    [InlineData(0x57, "Omega", "Ω")]
    [InlineData(0x61, "alpha", "α")]
    [InlineData(0x62, "beta", "β")]
    [InlineData(0x67, "gamma", "γ")]
    [InlineData(0x6D, "mu", "μ")]
    [InlineData(0xA0, "Euro", "€")]
    [InlineData(0xA5, "infinity", "∞")]
    [InlineData(0xAE, "arrowright", "→")]
    [InlineData(0xD6, "radical", "√")]
    [InlineData(0xE5, "summation", "∑")]
    [InlineData(0xF2, "integral", "∫")]
    [InlineData(0xFE, "bracerightbt", "\uF8FE")]
    public void UsesAdobeSymbolGlyphsAndUnicode(int code, string glyph, string text)
    {
        FontEncoding encoding = Standard14Font.Symbol.DefaultEncoding;
        Assert.Equal(glyph, encoding.GetGlyphName(code));
        Assert.Equal(text, encoding.GetText(code));
        Assert.Equal(code, encoding.GetCode(glyph));
    }

    [Fact]
    public void EveryBuiltInSymbolCodeHasTextAndMetrics()
    {
        FontEncoding encoding = Standard14Font.Symbol.DefaultEncoding;
        int count = 0;
        for (int code = 0; code < 256; code++)
        {
            if (encoding.GetGlyphName(code) is not { } glyph)
                continue;

            count++;
            Assert.NotNull(encoding.GetText(code));
            Assert.True(Standard14Font.Symbol.GetWidth(glyph) > 0, glyph);
        }

        Assert.Equal(189, count);
    }

    [Fact]
    public void UsesBuiltInSymbolWidths()
    {
        LoadedFont font = LoadedFont.Read(Font("Symbol"));
        Assert.Equal(631, font.GetWidth(0x61));
        Assert.Equal(549, font.GetWidth(0x62));
        Assert.Equal(411, font.GetWidth(0x67));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitBaseEncodingOverridesBuiltInEncoding(bool dictionary)
    {
        PdfDictionary font = Font("Symbol");
        PdfValue encoding = PdfValue.Name("WinAnsiEncoding");
        if (dictionary)
        {
            var value = new PdfDictionary();
            value.Set(PdfName.Get("BaseEncoding"), encoding);
            encoding = PdfValue.Dictionary(value);
        }

        font.Set(PdfName.Get("Encoding"), encoding);
        using PdfDocument document = Document(font);
        Assert.Equal("abg", document.Pages[0].ExtractText().Trim());
    }

    [Fact]
    public void ToUnicodeOverridesDifferencesAndUnmappedCodesUseSymbol()
    {
        PdfDictionary font = Font("Symbol");
        var encoding = new PdfDictionary();
        encoding.Set(PdfName.Get("Differences"), PdfValue.Array(new PdfArray(
            PdfValue.Integer(97), PdfValue.Name("Omega"), PdfValue.Name("Gamma"))));
        font.Set(PdfName.Get("Encoding"), PdfValue.Dictionary(encoding));

        using (PdfDocument document = Document(font))
            Assert.Equal("ΩΓγ", document.Pages[0].ExtractText().Trim());

        const string cmap = "1 begincodespacerange <00> <FF> endcodespacerange " +
                            "1 beginbfchar <61> <0058> endbfchar";
        font.Set(PdfName.Get("ToUnicode"), PdfValue.Stream(
            new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(cmap))));
        using PdfDocument mapped = Document(font);
        using PdfDocument reloaded = PdfDocument.Load(mapped.ToArray());
        Assert.Equal("XΓγ", reloaded.Pages[0].ExtractText().Trim());
    }

    private static PdfDictionary Font(string name)
    {
        var font = new PdfDictionary();
        font.Set(PdfName.Get("Type"), PdfValue.Name("Font"));
        font.Set(PdfName.Get("Subtype"), PdfValue.Name("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name(name));
        return font;
    }

    private static PdfDocument Document(PdfDictionary font)
    {
        PdfDocument document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        if (font.GetStream(PdfName.Get("ToUnicode")) is { } toUnicode)
        {
            font.Set(PdfName.Get("ToUnicode"),
                PdfValue.Reference(document.AddObject(PdfValue.Stream(toUnicode))));
        }

        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(font))));
        page.Resources.Set(PdfName.Get("Font"), PdfValue.Dictionary(fonts));
        page.SetContent("BT /F1 12 Tf 10 100 Td (abg) Tj ET"u8.ToArray());
        return document;
    }
}
