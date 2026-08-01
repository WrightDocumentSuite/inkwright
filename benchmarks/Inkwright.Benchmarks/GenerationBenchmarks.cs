using System.Globalization;
using BenchmarkDotNet.Attributes;
using Inkwright.Content;
using Inkwright.Layout;
using Inkwright.Layout.Elements;
using PdfSharp.Drawing;

namespace Inkwright.Benchmarks;

/// <summary>
/// Building a report from scratch. The comparison is deliberately unfair to Inkwright in one
/// direction and fair in the other: PDFsharp is given pre-positioned draw calls with no layout to
/// do, so its number is a floor for what any library could achieve on the same page.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
public class GenerationBenchmarks
{
    private const int Rows = 40;

    /// <summary>How many pages the report covers.</summary>
    [Params(10, 200)]
    public int Pages { get; set; }

    /// <summary>Points PDFsharp at a font file, which its core package cannot find on its own.</summary>
    [GlobalSetup]
    public void Setup() => global::PdfSharp.Fonts.GlobalFontSettings.FontResolver ??= new SystemFontResolver();

    /// <summary>Builds the report through the layout engine, which measures and flows the content.</summary>
    [Benchmark(Baseline = true)]
    public int Inkwright()
    {
        byte[] file = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A4);
                page.Margin(36);
                page.Header().Text("Quarterly statement", TextStyle.Default.FontSize(16).Bold());
                page.Footer().AlignRight().Text(text => text.PageNumbers("{0} / {1}"));
                page.Content().Table(table =>
                {
                    table.Columns(ColumnWidth.Fraction(4), ColumnWidth.Fraction(1), ColumnWidth.Fraction(1));
                    table.Grid(0.4);
                    table.Header().Text("Description").Text("Quantity").Text("Amount");

                    for (int i = 0; i < Pages * Rows; i++)
                    {
                        table.Row()
                            .Text($"Line item number {i} on the statement")
                            .Text((i % 17).ToString(CultureInfo.InvariantCulture))
                            .Text((i * 3.75).ToString("0.00", CultureInfo.InvariantCulture));
                    }
                });
            })
            .ToArray();

        return file.Length;
    }

    /// <summary>Draws the same content with the low-level canvas, skipping the layout engine.</summary>
    [Benchmark]
    public int InkwrightCanvas()
    {
        using var document = PdfDocument.Create();
        Fonts.PdfFont font = document.Fonts.Helvetica;

        for (int page = 0; page < Pages; page++)
        {
            PdfPage target = document.Pages.Add(PageSize.A4);
            using var canvas = ContentCanvas.ForPage(target);

            double y = 780;
            for (int row = 0; row < Rows; row++, y -= 18)
            {
                canvas.DrawText($"Line item number {(page * Rows) + row} on the statement", 40, y, font, 10);
                canvas.DrawText((row % 17).ToString(CultureInfo.InvariantCulture), 400, y, font, 10);
                canvas.DrawText((row * 3.75).ToString("0.00", CultureInfo.InvariantCulture), 470, y, font, 10);
            }

            canvas.Commit();
        }

        return document.ToArray().Length;
    }

    /// <summary>Draws the same content with PDFsharp, which has no layout engine of its own.</summary>
    [Benchmark]
    public int PdfSharp()
    {
        using var document = new global::PdfSharp.Pdf.PdfDocument();
        var font = new XFont("Benchmark", 10);

        for (int page = 0; page < Pages; page++)
        {
            global::PdfSharp.Pdf.PdfPage target = document.AddPage();
            target.Width = XUnit.FromPoint(595);
            target.Height = XUnit.FromPoint(842);

            using XGraphics graphics = XGraphics.FromPdfPage(target);
            double y = 40;

            for (int row = 0; row < Rows; row++, y += 18)
            {
                graphics.DrawString($"Line item number {(page * Rows) + row} on the statement", font, XBrushes.Black, 40, y);
                graphics.DrawString((row % 17).ToString(CultureInfo.InvariantCulture), font, XBrushes.Black, 400, y);
                graphics.DrawString((row * 3.75).ToString("0.00", CultureInfo.InvariantCulture), font, XBrushes.Black, 470, y);
            }
        }

        using var buffer = new MemoryStream();
        document.Save(buffer);
        return (int)buffer.Length;
    }
}
