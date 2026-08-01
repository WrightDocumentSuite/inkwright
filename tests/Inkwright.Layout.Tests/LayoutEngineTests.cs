using System.Globalization;
using Inkwright.Layout;
using Inkwright.Layout.Elements;
using Xunit;

namespace Inkwright.Layout.Tests;

public class LayoutEngineTests
{
    [Fact]
    public void RendersASinglePageReport()
    {
        byte[] bytes = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A4);
                page.Margin(40);
                page.Header().Text("Quarterly report", TextStyle.Default.FontSize(20).Bold());
                page.Content().Column(column =>
                {
                    column.Spacing(8);
                    column.Item().Text("Revenue held steady across all regions.");
                    column.Item().Text("Costs fell by four per cent.");
                });
                page.Footer().AlignCenter().Text(text => text.PageNumbers("{0} / {1}"));
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);
        Assert.Single(document.Pages);

        string text = document.Pages[0].ExtractText();
        Assert.Contains("Quarterly report", text, StringComparison.Ordinal);
        Assert.Contains("Revenue held steady", text, StringComparison.Ordinal);
        Assert.Contains("1 / 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FlowsLongContentAcrossPagesAndNumbersThem()
    {
        byte[] bytes = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A4);
                page.Margin(40);
                page.Content().Column(column =>
                {
                    for (int i = 1; i <= 300; i++)
                        column.Item().Text($"Line {i} of a report that will not fit on one page.");
                });
                page.Footer().AlignRight().Text(text => text.PageNumbers("Page {0} of {1}"));
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);
        Assert.True(document.Pages.Count > 3, $"Expected several pages, got {document.Pages.Count}.");

        string first = document.Pages[0].ExtractText();
        string last = document.Pages[^1].ExtractText();

        Assert.Contains("Line 1 of a report", first, StringComparison.Ordinal);
        Assert.Contains("Line 300 of a report", last, StringComparison.Ordinal);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"Page 1 of {document.Pages.Count}"),
            first,
            StringComparison.Ordinal);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"Page {document.Pages.Count} of {document.Pages.Count}"),
            last,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BreaksLongParagraphsAtWordBoundaries()
    {
        const string Paragraph =
            "Layout engines earn their keep on the long paragraphs nobody proofreads, " +
            "where a single missed break opportunity turns a tidy column into a ragged mess " +
            "that runs past the margin and out of the page entirely.";

        byte[] bytes = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A6);
                page.Margin(20);
                page.Content().Text(Paragraph);
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);
        IReadOnlyList<Text.PdfLetter> letters = document.Pages[0].ExtractLetters();

        Assert.NotEmpty(letters);
        double right = PageSize.A6.Width - 20;
        Assert.All(letters, letter =>
            Assert.True(letter.Origin.X + letter.Width <= right + 1,
                $"'{letter.Text}' ends at {letter.Origin.X + letter.Width:0.#}, past the {right:0.#} pt margin."));

        string text = document.Pages[0].ExtractText();
        Assert.Contains("Layout engines earn", text, StringComparison.Ordinal);
        Assert.True(text.Count(c => c == '\n') >= 4, "The paragraph should have wrapped onto several lines.");
    }

    [Fact]
    public void RepeatsTableHeadersOnEveryPage()
    {
        byte[] bytes = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A5);
                page.Margin(25);
                page.Content().Table(table =>
                {
                    table.Columns(ColumnWidth.Fraction(3), ColumnWidth.Fraction(1), ColumnWidth.Fraction(1));
                    table.Grid(0.5, PdfColor.Gray(0.7f));

                    table.Header()
                        .Background(PdfColor.Parse("#EEEEEE"))
                        .Text("Item", TextStyle.Default.Bold())
                        .Text("Qty", TextStyle.Default.Bold())
                        .Text("Total", TextStyle.Default.Bold());

                    for (int i = 1; i <= 120; i++)
                    {
                        table.Row()
                            .Text($"Component {i}")
                            .Text(i.ToString(CultureInfo.InvariantCulture))
                            .Text((i * 12.5).ToString("0.00", CultureInfo.InvariantCulture));
                    }
                });
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);
        Assert.True(document.Pages.Count > 2);

        foreach (PdfPage page in document.Pages)
            Assert.Contains("Item", page.ExtractText(), StringComparison.Ordinal);

        Assert.Contains("Component 120", document.Pages[^1].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void StartsANewPageOnAnExplicitBreak()
    {
        byte[] bytes = Report.Create()
            .Page(page =>
            {
                page.Content().Column(column =>
                {
                    column.Item().Text("before the break");
                    column.Item().PageBreak();
                    column.Item().Text("after the break");
                });
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);
        Assert.Equal(2, document.Pages.Count);
        Assert.Contains("before the break", document.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Contains("after the break", document.Pages[1].ExtractText(), StringComparison.Ordinal);
        Assert.DoesNotContain("after the break", document.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void PlacesRowsSideBySide()
    {
        byte[] bytes = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A4);
                page.Margin(40);
                page.Content().Row(row =>
                {
                    row.Spacing(10);
                    row.ConstantItem(120).Text("left");
                    row.RelativeItem().Text("middle");
                    row.ConstantItem(80).AlignRight().Text("right");
                });
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);
        IReadOnlyList<Text.PdfLetter> letters = document.Pages[0].ExtractLetters();

        double leftX = letters.First(l => l.Text == "l").Origin.X;
        double middleX = letters.First(l => l.Text == "m").Origin.X;
        double rightX = letters.First(l => l.Text == "r").Origin.X;

        Assert.True(leftX < middleX, "The middle cell should start after the left one.");
        Assert.True(middleX < rightX, "The right cell should start after the middle one.");
        Assert.Equal(40, leftX, 1);
    }

    [Fact]
    public void ReportsAnElementThatCanNeverFit()
    {
        LayoutException failure = Assert.Throws<LayoutException>(() =>
            Report.Create()
                .Page(page =>
                {
                    page.Size(PageSize.A6);
                    page.Margin(20);
                    page.Content().Column(column =>
                    {
                        column.Item().Text("this part is fine");
                        column.Item().Height(5000).Background(PdfColor.Red);
                    });
                })
                .ToArray());

        Assert.Contains("never finish", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(failure.ElementTrace);
        Assert.Contains("ColumnElement", failure.ElementTrace!, StringComparison.Ordinal);
    }

    [Fact]
    public void AlignsAndPadsTheWayTheChainReads()
    {
        byte[] bytes = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A5);
                page.Margin(0);
                page.Content().Padding(50).Background(PdfColor.Parse("#F0F0F0")).Padding(10).AlignCenter().Text("centred");
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);
        Text.PdfLetter first = document.Pages[0].ExtractLetters()[0];

        double contentCenter = PageSize.A5.Width / 2;
        Assert.InRange(first.Origin.X, 60, contentCenter);
        Assert.True(first.Origin.Y >= 60, "The text should sit below the outer padding.");
    }

    [Fact]
    public void JustifiesTextToBothMargins()
    {
        byte[] bytes = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A5);
                page.Margin(30);
                page.Content().Text(text =>
                {
                    text.Justify();
                    text.Span(string.Join(' ', Enumerable.Repeat("justified words fill the measure", 8)));
                });
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);
        IReadOnlyList<Text.PdfLetter> letters = document.Pages[0].ExtractLetters();

        double right = PageSize.A5.Width - 30;

        // Page space runs bottom-up, so the first line of the paragraph has the largest y.
        var lines = letters.GroupBy(l => Math.Round(l.Origin.Y, 1)).OrderByDescending(g => g.Key).ToList();

        Assert.True(lines.Count >= 3);

        // Every line but the last should reach the right margin.
        foreach (var line in lines.Take(lines.Count - 1))
        {
            double lineEnd = line.Max(l => l.Origin.X + l.Width);
            Assert.InRange(lineEnd, right - 3, right + 1);
        }
    }
}
