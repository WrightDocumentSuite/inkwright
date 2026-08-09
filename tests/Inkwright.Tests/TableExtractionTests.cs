using Inkwright.Content;
using Inkwright.Tables;
using Xunit;

namespace Inkwright.Tests;

public sealed class TableExtractionTests
{
    [Fact]
    public void PaintedGridBecomesCellsAndMissingRulesBecomeSpans()
    {
        using var source = PdfDocument.Create();
        PdfPage page = source.Pages.Add(PageSize.A4);
        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.StrokeColor(PdfColor.Black).LineWidth(0.6);
            foreach (double y in new[] { 500d, 550d, 600d, 650d })
                canvas.MoveTo(50, y).LineTo(350, y);
            canvas.MoveTo(50, 500).LineTo(50, 650)
                .MoveTo(350, 500).LineTo(350, 650)
                .MoveTo(150, 500).LineTo(150, 600)
                .MoveTo(250, 500).LineTo(250, 600)
                .Stroke();

            canvas.DrawText("Spanning header", 120, 620, source.Fonts.HelveticaBold, 12);
            canvas.DrawText("A1", 70, 570, source.Fonts.Helvetica, 11);
            canvas.DrawText("B1", 170, 570, source.Fonts.Helvetica, 11);
            canvas.DrawText("C1", 270, 570, source.Fonts.Helvetica, 11);
            canvas.DrawText("A2", 70, 520, source.Fonts.Helvetica, 11);
            canvas.DrawText("B2", 170, 520, source.Fonts.Helvetica, 11);
            canvas.DrawText("C2", 270, 520, source.Fonts.Helvetica, 11);
            canvas.Commit();
        }

        using PdfDocument document = PdfDocument.Load(source.ToArray());
        PdfTable table = Assert.Single(document.Pages[0].ExtractTables());

        Assert.Equal(3, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal(7, table.Cells.Count);
        PdfTableCell header = Assert.Single(table.Cells, cell => cell.Row == 0);
        Assert.Equal(3, header.ColumnSpan);
        Assert.Equal("Spanning header", header.Text);
        Assert.True(header.IsBold);
        Assert.Equal("A1", Assert.Single(table.Cells, cell => cell.Row == 1 && cell.Column == 0).Text);
        Assert.Equal("C2", Assert.Single(table.Cells, cell => cell.Row == 2 && cell.Column == 2).Text);
    }

    [Fact]
    public void ADecorativeSingleRectangleIsNotReportedAsATable()
    {
        using var source = PdfDocument.Create();
        PdfPage page = source.Pages.Add(PageSize.A4);
        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.Rectangle(50, 500, 200, 60).Stroke();
            canvas.Commit();
        }

        using PdfDocument document = PdfDocument.Load(source.ToArray());

        Assert.Empty(document.Pages[0].ExtractTables());
    }
}
