using Inkwright.Text;
using Xunit;

namespace Inkwright.Tests;

public class TextSearchTests
{
    [Fact]
    public void FindsAPhraseAndPointsAtTheGlyphs()
    {
        IReadOnlyList<PdfLetter> letters = Line("the quick brown fox");

        IReadOnlyList<PdfTextMatch> matches = TextSearch.Find(letters, "quick");

        PdfTextMatch match = Assert.Single(matches);
        Assert.Equal("quick", match.Text);
        Assert.Equal(5, match.Letters.Count);

        // The box starts at the 'q', which is the fourth glyph: the space is not one.
        Assert.Equal(letters[3].Origin.X, match.Bounds.Left, 1);
    }

    [Fact]
    public void LooksPastAccentsUnlessThePhraseCarriesThem()
    {
        IReadOnlyList<PdfLetter> letters = Line("r\u00E9sum\u00E9 attached");

        Assert.Single(TextSearch.Find(letters, "resume"));
        Assert.Single(TextSearch.Find(letters, "r\u00E9sum\u00E9"));

        // The accents come back on demand, and a phrase that carries them is matched with them.
        Assert.Empty(TextSearch.Find(letters, "resume", TextSearchOptions.Exact));
        Assert.Empty(TextSearch.Find(Line("resume attached"), "r\u00E9sum\u00E9"));
    }

    [Fact]
    public void MatchesTextWhereverTheAccentsWereStored()
    {
        // The same word decomposed: 'e' followed by a combining acute accent.
        IReadOnlyList<PdfLetter> letters = Line("cafe\u0301");

        Assert.Single(TextSearch.Find(letters, "cafe"));
        Assert.Single(TextSearch.Find(letters, "caf\u00E9"));
    }

    [Fact]
    public void ReadsLigaturesAsTheLettersTheyStandFor()
    {
        IReadOnlyList<PdfLetter> letters = Line("the \uFB01nal \uFB02ourish");

        Assert.Single(TextSearch.Find(letters, "final"));
        Assert.Single(TextSearch.Find(letters, "flourish"));

        // The match still reports what is actually printed on the page.
        Assert.Equal("\uFB01nal", TextSearch.Find(letters, "final")[0].Text);
        Assert.Empty(TextSearch.Find(letters, "final", TextSearchOptions.Exact));
    }

    [Fact]
    public void JoinsAWordBrokenAcrossTwoLines()
    {
        List<PdfLetter> letters = [.. Line("a hyphen-", 700), .. Line("ated word", 680)];

        PdfTextMatch match = Assert.Single(TextSearch.Find(letters, "hyphenated"));

        // The match spans both lines, so a highlight needs a box on each.
        Assert.Equal(2, match.Areas.Count);
        Assert.Empty(TextSearch.Find(letters, "hyphenated", TextSearchOptions.Exact));
    }

    [Fact]
    public void KeepsAHyphenThatIsPartOfTheWord()
    {
        // A hyphen in the middle of a line joins a compound word rather than breaking one.
        IReadOnlyList<PdfLetter> letters = Line("a well-known fact");

        Assert.Single(TextSearch.Find(letters, "well-known"));
        Assert.Empty(TextSearch.Find(letters, "wellknown"));
    }

    [Fact]
    public void ReadsTypographicQuotesAsPlainOnes()
    {
        IReadOnlyList<PdfLetter> letters = Line("it\u2019s \u201Cquoted\u201D");

        Assert.Single(TextSearch.Find(letters, "it's"));
        Assert.Single(TextSearch.Find(letters, "\"quoted\""));
    }

    [Fact]
    public void MatchesWholeWordsOnlyWhenAsked()
    {
        IReadOnlyList<PdfLetter> letters = Line("format the formatted form");

        Assert.Equal(3, TextSearch.Find(letters, "form").Count);
        Assert.Single(TextSearch.Find(letters, "form", new TextSearchOptions { WholeWords = true }));
    }

    [Fact]
    public void FindsEveryOccurrenceWithoutOverlapping()
    {
        IReadOnlyList<PdfLetter> letters = Line("aaaa");

        Assert.Equal(2, TextSearch.Find(letters, "aa").Count);
    }

    [Fact]
    public void ComposesGlyphsIntoOneLineOfText()
    {
        List<PdfLetter> letters = [.. Line("first", 700), .. Line("second", 680)];

        Assert.Equal("first second", TextSearch.Compose(letters));
    }

    private static List<PdfLetter> Line(string text, double y = 700)
    {
        var letters = new List<PdfLetter>(text.Length);
        double x = 100;

        foreach (char c in text)
        {
            if (c == ' ')
            {
                x += 10;
                continue;
            }

            letters.Add(new PdfLetter(c.ToString(), new PdfPoint(x, y), 6, 12, 12, "F1", 0));
            x += 6;
        }

        return letters;
    }
}
