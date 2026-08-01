using System.Text;
using Inkwright.Cos;
using Inkwright.Fonts;
using Xunit;

namespace Inkwright.Cjk.Tests;

/// <summary>
/// The whole of ISO 32000-1 9.10.2 as a document sees it: a composite font that names one of
/// Adobe's predefined encodings and carries no <c>ToUnicode</c> map at all, which before these
/// tables could only be read as the raw codes it was written with.
/// </summary>
public class CjkExtractionTests
{
    public CjkExtractionTests() => CjkCMaps.Register();

    [Fact]
    public void ReadsJapaneseFromAFontWithNoToUnicodeMap()
    {
        // 日本語 in Shift-JIS.
        byte[] shiftJis = [0x93, 0xFA, 0x96, 0x7B, 0x8C, 0xEA];

        using PdfDocument document = PdfDocument.Load(
            Build("90ms-RKSJ-H", "Adobe", "Japan1", 2, shiftJis));

        Assert.Equal("日本語", document.Pages[0].ExtractText().Trim());
    }

    [Fact]
    public void ReadsSimplifiedChineseFromAFontWithNoToUnicodeMap()
    {
        // 中文 in GBK.
        byte[] gbk = [0xD6, 0xD0, 0xCE, 0xC4];

        using PdfDocument document = PdfDocument.Load(Build("GBK-EUC-H", "Adobe", "GB1", 2, gbk));

        Assert.Equal("中文", document.Pages[0].ExtractText().Trim());
    }

    [Fact]
    public void ReadsUnicodeEncodedTextFromAFontWithNoToUnicodeMap()
    {
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes("한국어");

        using PdfDocument document = PdfDocument.Load(Build("UniKS-UCS2-H", "Adobe", "Korea1", 1, utf16));

        Assert.Equal("한국어", document.Pages[0].ExtractText().Trim());
    }

    [Fact]
    public void FallsBackToTheCodesWhenNoTablesAreRegistered()
    {
        byte[] shiftJis = [0x93, 0xFA];
        byte[] file = Build("90ms-RKSJ-H", "Adobe", "Japan1", 2, shiftJis);

        ICMapSource? source = CMapProvider.Source;
        try
        {
            CMapProvider.Source = null;

            using PdfDocument document = PdfDocument.Load(file);
            Assert.DoesNotContain("日", document.Pages[0].ExtractText(), StringComparison.Ordinal);
        }
        finally
        {
            CMapProvider.Source = source;
        }
    }

    /// <summary>
    /// Builds a one-page document that shows a string through a Type 0 font. The font has no
    /// embedded program and no <c>ToUnicode</c> entry, so the only route to text is the encoding
    /// and the character collection it names.
    /// </summary>
    private static byte[] Build(string encoding, string registry, string ordering, int supplement, byte[] text)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var systemInfo = new PdfDictionary(3);
        systemInfo.Set(PdfName.Get("Registry"), PdfValue.String(Encoding.ASCII.GetBytes(registry)));
        systemInfo.Set(PdfName.Get("Ordering"), PdfValue.String(Encoding.ASCII.GetBytes(ordering)));
        systemInfo.Set(PdfName.Get("Supplement"), PdfValue.Integer(supplement));

        var descriptor = new PdfDictionary(5);
        descriptor.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("FontDescriptor")));
        descriptor.Set(PdfName.Get("FontName"), PdfValue.Name(PdfName.Get("KozMinPro-Regular")));
        descriptor.Set(PdfName.Get("Flags"), PdfValue.Integer(4));
        descriptor.Set(PdfName.Get("ItalicAngle"), PdfValue.Integer(0));

        var descendant = new PdfDictionary(6);
        descendant.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("Font")));
        descendant.Set(PdfName.Get("Subtype"), PdfValue.Name(PdfName.Get("CIDFontType0")));
        descendant.Set(PdfName.Get("BaseFont"), PdfValue.Name(PdfName.Get("KozMinPro-Regular")));
        descendant.Set(PdfName.Get("CIDSystemInfo"), PdfValue.Dictionary(systemInfo));
        descendant.Set(PdfName.Get("FontDescriptor"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(descriptor))));
        descendant.Set(PdfName.Get("DW"), PdfValue.Integer(1000));

        var descendants = new PdfArray(1);
        descendants.Add(PdfValue.Reference(document.AddObject(PdfValue.Dictionary(descendant))));

        var font = new PdfDictionary(5);
        font.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("Font")));
        font.Set(PdfName.Get("Subtype"), PdfValue.Name(PdfName.Get("Type0")));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name(PdfName.Get("KozMinPro-Regular")));
        font.Set(PdfName.Get("Encoding"), PdfValue.Name(PdfName.Get(encoding)));
        font.Set(PdfName.Get("DescendantFonts"), PdfValue.Array(descendants));

        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(font))));

        var resources = new PdfDictionary(1);
        resources.Set(PdfName.Get("Font"), PdfValue.Dictionary(fonts));
        page.Dictionary.Set(PdfName.Get("Resources"), PdfValue.Dictionary(resources));

        var content = new MemoryStream();
        content.Write("BT /F1 24 Tf 72 700 Td <"u8);
        foreach (byte b in text)
            content.Write(Encoding.ASCII.GetBytes(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

        content.Write("> Tj ET"u8);

        var stream = new PdfStream(new PdfDictionary(1), content.ToArray());
        page.Dictionary.Set(PdfName.Get("Contents"), PdfValue.Reference(document.AddObject(PdfValue.Stream(stream))));

        return document.ToArray();
    }
}
