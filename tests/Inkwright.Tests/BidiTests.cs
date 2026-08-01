using Inkwright.Text;
using Xunit;

namespace Inkwright.Tests;

public class BidiTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Hello, world")]
    [InlineData("Price: 42.50 EUR (net)")]
    [InlineData("日本語のテキスト")]
    public void LeavesTextWithoutRightToLeftCharactersAlone(string text)
    {
        BidiText resolved = Bidi.Resolve(text);

        Assert.Equal(text, resolved.Text);
        Assert.False(resolved.IsRightToLeft);
    }

    [Fact]
    public void ReversesAHebrewLineIntoLogicalOrder()
    {
        // A PDF paints Hebrew left to right on the page, so the content stream holds the last
        // letter first. Reading it back must undo that.
        const string Visual = "\u05DD\u05D5\u05DC\u05E9";
        const string Logical = "\u05E9\u05DC\u05D5\u05DD";

        BidiText resolved = Bidi.Resolve(Visual);

        Assert.Equal(Logical, resolved.Text);
        Assert.True(resolved.IsRightToLeft);
    }

    [Fact]
    public void IsItsOwnInverseForASingleRun()
    {
        const string Logical = "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD";

        Assert.Equal(Logical, Bidi.Resolve(Bidi.Resolve(Logical).Text).Text);
    }

    [Fact]
    public void KeepsLatinRunsReadableInsideRightToLeftText()
    {
        // The Latin word sits at a deeper even level than the Arabic around it, so rule L2 reverses
        // the line as a whole and then puts the Latin back the right way round.
        BidiText resolved = Bidi.Resolve("\u0639\u0631\u0628\u064A Inkwright \u0639\u0631\u0628\u064A");

        Assert.True(resolved.IsRightToLeft);
        Assert.Contains("Inkwright", resolved.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsNumbersInPrintOrderInsideRightToLeftText()
    {
        // Numbers read left to right even in an Arabic sentence; a reversed "2026" would be wrong.
        BidiText resolved = Bidi.Resolve("\u0639\u0631\u0628\u064A 2026 \u0639\u0631\u0628\u064A");

        Assert.Contains("2026", resolved.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TreatsAMostlyLatinLineAsLeftToRight()
    {
        // One Hebrew word in a long English sentence should not flip the whole line.
        BidiText resolved = Bidi.Resolve("The Hebrew word for peace is \u05DD\u05D5\u05DC\u05E9 in print");

        Assert.False(resolved.IsRightToLeft);
        Assert.StartsWith("The Hebrew word", resolved.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvesLinesOnlyWhenAskedTo()
    {
        const string Visual = "\u05DD\u05D5\u05DC\u05E9";

        IReadOnlyList<PdfLetter> letters = Letters(Visual);

        Assert.Equal(Visual, TextExtractor.Compose(letters).Trim());
        Assert.Equal(
            "\u05E9\u05DC\u05D5\u05DD",
            TextExtractor.Compose(letters, new TextExtractionOptions { ResolveBidirectional = true }).Trim());
    }

    private static IReadOnlyList<PdfLetter> Letters(string text)
    {
        var letters = new List<PdfLetter>(text.Length);
        for (int i = 0; i < text.Length; i++)
            letters.Add(new PdfLetter(text[i].ToString(), new PdfPoint(100 + (i * 10), 700), 10, 12, 12, "F1", 0));

        return letters;
    }
}
