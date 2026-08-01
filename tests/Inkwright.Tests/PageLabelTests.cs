using Xunit;

namespace Inkwright.Tests;

public class PageLabelTests
{
    [Fact]
    public void NumbersPagesByIndexWhenTheDocumentSaysNothing()
    {
        using var document = PdfDocument.Create();
        for (int i = 0; i < 3; i++)
            document.Pages.Add();

        Assert.False(document.PageLabels.IsDefined);
        Assert.Equal(["1", "2", "3"], document.PageLabels.All());
    }

    [Fact]
    public void WritesAndReadsBackFrontMatterFollowedByABody()
    {
        using var document = PdfDocument.Create();
        for (int i = 0; i < 6; i++)
            document.Pages.Add();

        document.PageLabels.Set(
        [
            new PageLabelRange(0, PageLabelStyle.LowercaseRoman),
            new PageLabelRange(3, PageLabelStyle.Decimal),
        ]);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());

        Assert.True(reloaded.PageLabels.IsDefined);
        Assert.Equal(["i", "ii", "iii", "1", "2", "3"], reloaded.PageLabels.All());
    }

    [Fact]
    public void HonoursPrefixesAndStartingNumbers()
    {
        using var document = PdfDocument.Create();
        for (int i = 0; i < 4; i++)
            document.Pages.Add();

        document.PageLabels.Set([new PageLabelRange(0, PageLabelStyle.Decimal, "A-", 5)]);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        Assert.Equal(["A-5", "A-6", "A-7", "A-8"], reloaded.PageLabels.All());
    }

    [Fact]
    public void AddsTheRangeAtPageZeroTheSpecificationRequires()
    {
        using var document = PdfDocument.Create();
        for (int i = 0; i < 3; i++)
            document.Pages.Add();

        // A tree whose first range starts late is invalid, so a default range is put in front of it.
        document.PageLabels.Set([new PageLabelRange(2, PageLabelStyle.UppercaseRoman)]);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());

        Assert.Equal(0, reloaded.PageLabels.Ranges[0].StartIndex);
        Assert.Equal(["1", "2", "I"], reloaded.PageLabels.All());
    }

    [Theory]
    [InlineData(PageLabelStyle.UppercaseLetters, 1, "A")]
    [InlineData(PageLabelStyle.UppercaseLetters, 26, "Z")]
    [InlineData(PageLabelStyle.UppercaseLetters, 27, "AA")]
    [InlineData(PageLabelStyle.UppercaseLetters, 53, "AAA")]
    [InlineData(PageLabelStyle.LowercaseLetters, 28, "bb")]
    [InlineData(PageLabelStyle.LowercaseRoman, 4, "iv")]
    [InlineData(PageLabelStyle.UppercaseRoman, 1990, "MCMXC")]
    [InlineData(PageLabelStyle.None, 7, "")]
    public void WritesEachStyleTheWayTheStandardDoes(PageLabelStyle style, int number, string expected) =>
        Assert.Equal(expected, PdfPageLabels.Format(new PageLabelRange(0, style, null, number), 0));

    [Fact]
    public void DropsTheLabellingOnRequest()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add();
        document.PageLabels.Set([new PageLabelRange(0, PageLabelStyle.UppercaseRoman)]);
        document.PageLabels.Remove();

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());

        Assert.False(reloaded.PageLabels.IsDefined);
        Assert.Equal("1", reloaded.PageLabels[0]);
    }
}
