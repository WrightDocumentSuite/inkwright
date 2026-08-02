using Inkwright.Cos;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Layout.Tests;

public sealed class PdfACMapTests
{
    [Theory]
    [InlineData(PdfAConformance.A1B, "6.3.3.3")]
    [InlineData(PdfAConformance.A2B, "6.2.11.3.3")]
    [InlineData(PdfAConformance.A4, "6.2.10.3.3")]
    public void RejectsAnUnknownUseCMapOperandEvenWhenACommentNamesAPredefinedMap(
        PdfAConformance conformance, string clause)
    {
        using PdfDocument document = PdfAConformanceTests.Fresh(out _);
        AddCompositeFont(document, "/Bogus usecmap % Identity-H\n"u8.ToArray());

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("not one of the predefined", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsAPredefinedUseCMapOperandEvenWhenACommentNamesAnUnknownMap()
    {
        using PdfDocument document = PdfAConformanceTests.Fresh(out _);
        AddCompositeFont(document, "/Identity-H usecmap % Bogus\n"u8.ToArray());

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.2.11.3.3" &&
                       problem.Message.Contains("not one of the predefined", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("42 usecmap\n")]
    [InlineData("/Bogus usecmap /Identity-H usecmap\n")]
    public void RejectsEveryUseCMapOperatorWithoutAPredefinedName(string program)
    {
        using PdfDocument document = PdfAConformanceTests.Fresh(out _);
        AddCompositeFont(document, System.Text.Encoding.ASCII.GetBytes(program));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.2.11.3.3" &&
                       problem.Message.Contains("not one of the predefined", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "6.3.3.3")]
    [InlineData(PdfAConformance.A2B, "6.2.11.3.3")]
    [InlineData(PdfAConformance.A4, "6.2.10.3.3")]
    public void RejectsAnUnknownUseCMapDictionaryEntry(
        PdfAConformance conformance, string clause)
    {
        using PdfDocument document = PdfAConformanceTests.Fresh(out _);
        AddCompositeFont(
            document,
            [],
            PdfValue.Name(PdfName.Get("Bogus")));

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("not one of the predefined", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsAPredefinedUseCMapDictionaryEntry()
    {
        using PdfDocument document = PdfAConformanceTests.Fresh(out _);
        AddCompositeFont(
            document,
            [],
            PdfValue.Name(PdfName.Get("Identity-H")));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.2.10.3.3" &&
                       problem.Message.Contains("not one of the predefined", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAnEmbeddedUseCMapDictionaryEntry()
    {
        using PdfDocument document = PdfAConformanceTests.Fresh(out _);
        AddCompositeFont(
            document,
            [],
            PdfValue.Stream(PdfStream.FromData(Array.Empty<byte>(), compress: false)));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.2.10.3.3" &&
                       problem.Message.Contains("not one of the predefined", StringComparison.Ordinal));
    }

    private static void AddCompositeFont(
        PdfDocument document,
        byte[] cmapProgram,
        PdfValue? dictionaryUseCMap = null)
    {
        PdfStream encoding = PdfStream.FromData(cmapProgram, compress: false);
        if (dictionaryUseCMap is { } useCMap)
            encoding.Dictionary.Set(PdfName.Get("UseCMap"), useCMap);

        var font = new PdfDictionary(4);
        font.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Font")));
        font.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Type0")));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name(PdfName.Get("CMapProbe")));
        font.Set(PdfNames.Encoding, PdfValue.Stream(encoding));

        PdfDictionary fonts = document.Pages[0].Resources.GetDictionary(PdfName.Get("Font"))!;
        fonts.Set(PdfName.Get("FCMapProbe"), PdfValue.Dictionary(font));
    }
}
