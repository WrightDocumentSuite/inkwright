using System.Text;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Fonts;
using Xunit;

namespace Inkwright.Tests;

public class FontTests
{
    [Theory]
    [InlineData("Times-Roman", "timesnewroman")]
    [InlineData("Times New Roman CYR", "timesnewroman")]
    [InlineData("Times New Roman CYR,Bold", "timesnewroman")]
    [InlineData("ABCDEF+ArialMT", "arialmt")]
    public void NormalisesPdfAndLegacyOfficeFamilyNames(string name, string expected)
    {
        Assert.Equal(expected, SystemFonts.FamilyKeys(name).Last());
    }

    [Fact]
    public void IndexesAFontByItsInternalNamesRatherThanOnlyItsFileName()
    {
        string? source = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVu Sans") ??
                         SystemFonts.Find("Liberation Sans");
        Assert.SkipWhen(source is null, "No regular TrueType font was found on this machine.");

        var program = TrueTypeFont.Load(source!);
        Assert.SkipWhen(program.Weight >= 600 || Math.Abs(program.ItalicAngle) > 0.01,
            "The available fallback is not a regular face.");

        string directory = Path.Combine(Path.GetTempPath(), "inkwright-font-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string renamed = Path.Combine(directory, "opaque-file-name.ttf");

        try
        {
            File.Copy(source!, renamed);
            IReadOnlyDictionary<string, string> index = SystemFonts.BuildIndex([renamed]);

            string familyKey = SystemFonts.FamilyKeys(program.FamilyName).First();
            string postScriptKey = SystemFonts.FamilyKeys(program.PostScriptName).First();
            Assert.Equal(renamed, index[familyKey]);
            Assert.Equal(renamed, index[postScriptKey]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolvesWindowsTimesNewRomanFilesAndLegacyAliases()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This regression covers Windows font file aliases.");

        string fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        string regular = Path.Combine(fonts, "times.ttf");
        string bold = Path.Combine(fonts, "timesbd.ttf");
        Assert.SkipWhen(!File.Exists(regular) || !File.Exists(bold), "Times New Roman is not installed.");

        Assert.Equal(regular, SystemFonts.Find("Times New Roman"));
        Assert.Equal(bold, SystemFonts.Find("Times New Roman", bold: true));
        Assert.Equal(regular, SystemFonts.Find("Times-Roman"));
        Assert.Equal(regular, SystemFonts.Find("Times New Roman CYR"));
    }

    [Fact]
    public void KnowsTheStandardFourteen()
    {
        Assert.Equal(14, Standard14Font.Names.Count);

        Standard14Font helvetica = Standard14Font.Helvetica;
        Assert.Equal(718, helvetica.CapHeight);
        Assert.Equal(667, helvetica.GetWidth("A"));
        Assert.Equal(278, helvetica.GetWidth("space"));
        Assert.Equal(556, helvetica.GetWidth("a"));

        Assert.Equal(600, Standard14Font.Courier.GetWidth("W"));
        Assert.Equal(722, Standard14Font.TimesBold.GetWidth("A"));
    }

    [Fact]
    public void ResolvesTheUsualSubstituteNames()
    {
        Assert.Equal("Helvetica", Standard14Font.Find("Arial")!.Name);
        Assert.Equal("Helvetica-Bold", Standard14Font.Find("Arial,Bold")!.Name);
        Assert.Equal("Times-Roman", Standard14Font.Find("TimesNewRomanPSMT")!.Name);
        Assert.Equal("Helvetica", Standard14Font.Find("ABCDEF+Helvetica")!.Name);
        Assert.Null(Standard14Font.Find("Comic Sans MS"));
    }

    [Fact]
    public void BuildsTheEncodingTablesFromTheSpecification()
    {
        Assert.Equal("A", FontEncoding.WinAnsi.GetGlyphName(65));
        Assert.Equal("adieresis", FontEncoding.WinAnsi.GetGlyphName(0xE4));
        Assert.Equal("Euro", FontEncoding.WinAnsi.GetGlyphName(0x80));
        Assert.Equal("quoteright", FontEncoding.Standard.GetGlyphName(39));
        Assert.Equal("quotesingle", FontEncoding.WinAnsi.GetGlyphName(39));
        Assert.Equal("Adieresis", FontEncoding.MacRoman.GetGlyphName(0x80));

        Assert.Equal("ä", FontEncoding.WinAnsi.GetText(0xE4));
        Assert.Equal("€", FontEncoding.WinAnsi.GetText(0x80));
    }

    [Fact]
    public void UnderstandsGlyphNameConventions()
    {
        Assert.Equal("A", FontEncoding.GlyphToText("A"));
        Assert.Equal("\u0439", FontEncoding.GlyphToText("uni0439"));
        Assert.Equal("\u20AC", FontEncoding.GlyphToText("u20AC"));
        Assert.Equal("fi", FontEncoding.GlyphToText("f_i"));
        Assert.Equal("a", FontEncoding.GlyphToText("a.sc"));
        Assert.Null(FontEncoding.GlyphToText("nonesuch"));
    }

    [Fact]
    public void AppliesDifferencesOnTopOfABaseEncoding()
    {
        var differences = new PdfArray(
            PdfValue.Integer(65),
            PdfValue.Name("bullet"),
            PdfValue.Name("dagger"));

        FontEncoding encoding = FontEncoding.WinAnsi.WithDifferences(differences);

        Assert.Equal("bullet", encoding.GetGlyphName(65));
        Assert.Equal("dagger", encoding.GetGlyphName(66));
        Assert.Equal("C", encoding.GetGlyphName(67));
    }

    [Fact]
    public void DrawsWithAStandardFontAndMeasuresIt()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfFont font = document.Fonts.Helvetica;

        // H 722 + e 556 + l 222 + l 222 + o 556 = 2278/1000 em at 24 pt.
        Assert.Equal(2278 * 24 / 1000d, font.Measure("Hello", 24), 3);

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("Hello, world", 72, 700, font, 24, PdfColor.Black);
            canvas.Commit();
        }

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        string content = Encoding.ASCII.GetString(reloaded.Pages[0].GetContent());
        Assert.Contains("(Hello, world) Tj", content, StringComparison.Ordinal);

        PdfDictionary fonts = reloaded.Pages[0].Resources.GetDictionary(PdfName.Get("Font"))!;
        PdfDictionary dictionary = fonts.GetDictionary(fonts.Keys[0])!;
        Assert.Equal("Helvetica", dictionary.GetName(PdfName.Get("BaseFont"))!.Value);
        Assert.Equal("WinAnsiEncoding", dictionary.GetName(PdfName.Get("Encoding"))!.Value);
    }

    [Fact]
    public void EmbedsAndSubsetsASystemFont()
    {
        string? path = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans") ?? SystemFonts.Find("LiberationSans");
        Assert.SkipWhen(path is null, "No TrueType font was found on this machine.");

        var program = TrueTypeFont.Load(path!);
        Assert.True(program.GlyphCount > 100);
        Assert.True(program.UnitsPerEm >= 16);
        Assert.NotEqual(0, program.GetGlyph('A'));

        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfFont font = document.Fonts.Embed(path!);

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("Привет, мир — Hello", 60, 600, font, 18);
            canvas.Commit();
        }

        byte[] bytes = document.ToArray();
        Assert.True(bytes.Length < new FileInfo(path!).Length + 100_000,
            "The subset should be far smaller than the whole font plus the page.");

        using PdfDocument reloaded = PdfDocument.Load(bytes);
        PdfDictionary fonts = reloaded.Pages[0].Resources.GetDictionary(PdfName.Get("Font"))!;
        PdfDictionary type0 = fonts.GetDictionary(fonts.Keys[0])!;

        Assert.Equal("Type0", type0.GetName(PdfName.Get("Subtype"))!.Value);
        Assert.Equal("Identity-H", type0.GetName(PdfName.Get("Encoding"))!.Value);
        Assert.Contains("+", type0.GetName(PdfName.Get("BaseFont"))!.Value, StringComparison.Ordinal);

        PdfDictionary cid = type0.GetArray(PdfName.Get("DescendantFonts"))!.GetDictionary(0)!;
        Assert.Equal("CIDFontType2", cid.GetName(PdfName.Get("Subtype"))!.Value);
        Assert.NotNull(cid.GetArray(PdfName.Get("W")));

        PdfDictionary descriptor = cid.GetDictionary(PdfName.Get("FontDescriptor"))!;
        PdfStream fontFile = descriptor.GetStream(PdfName.Get("FontFile2"))!;
        Assert.True(fontFile.Decode().Length > 1000);

        PdfStream toUnicode = type0.GetStream(PdfName.Get("ToUnicode"))!;
        Assert.Contains("beginbfchar", Encoding.ASCII.GetString(toUnicode.DecodedSpan()), StringComparison.Ordinal);

        // The subset must still parse as a font and keep the glyphs that were used.
        var subset = TrueTypeFont.Parse(fontFile.Decode().ToArray());
        Assert.Equal(program.GlyphCount, subset.GlyphCount);
        Assert.True(subset.GetGlyphRange(program.GetGlyph('H')).Length > 0);
    }

    [Fact]
    public void SubsetKeepsCompositeGlyphComponents()
    {
        string? path = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans");
        if (path is null)
            return;

        var program = TrueTypeFont.Load(path);
        ushort accented = program.GetGlyph('é');
        if (accented == 0 || program.GetComponents(accented).Count == 0)
            return;

        byte[] subsetBytes = TrueTypeSubsetter.Subset(program, [accented]);
        var subset = TrueTypeFont.Parse(subsetBytes);

        foreach (ushort component in program.GetComponents(accented))
            Assert.True(subset.GetGlyphRange(component).Length > 0, $"Component {component} was dropped.");
    }
}
