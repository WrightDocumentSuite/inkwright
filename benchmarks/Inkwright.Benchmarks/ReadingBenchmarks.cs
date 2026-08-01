using BenchmarkDotNet.Attributes;
using Inkwright.Layout;

namespace Inkwright.Benchmarks;

/// <summary>Reading files: opening them, walking the page tree, and pulling the text back out.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
public class ReadingBenchmarks
{
    private byte[] _file = [];

    /// <summary>Builds the document the benchmarks read, so the numbers do not depend on a fixture.</summary>
    [GlobalSetup]
    public void Setup() =>
        _file = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A4);
                page.Margin(40);
                page.Content().Column(column =>
                {
                    for (int i = 0; i < 4000; i++)
                        column.Item().Text($"Paragraph {i}: the quick brown fox jumps over the lazy dog, repeatedly.");
                });
            })
            .ToArray();

    /// <summary>Opens the file and counts its pages.</summary>
    [Benchmark(Baseline = true)]
    public int InkwrightOpen()
    {
        using PdfDocument document = PdfDocument.Load(_file);
        return document.Pages.Count;
    }

    /// <summary>Opens the file with PdfPig and counts its pages.</summary>
    [Benchmark]
    public int PdfPigOpen()
    {
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(_file);
        return document.NumberOfPages;
    }

    /// <summary>Extracts every character of text.</summary>
    [Benchmark]
    public int InkwrightExtract()
    {
        using PdfDocument document = PdfDocument.Load(_file);
        int length = 0;

        foreach (PdfPage page in document.Pages)
            length += page.ExtractText().Length;

        return length;
    }

    /// <summary>Extracts every character of text with PdfPig.</summary>
    [Benchmark]
    public int PdfPigExtract()
    {
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(_file);
        int length = 0;

        foreach (UglyToad.PdfPig.Content.Page page in document.GetPages())
            length += page.Text.Length;

        return length;
    }

    /// <summary>
    /// Rewrites the file. Objects the model never looked at are copied byte for byte, which is why
    /// this costs so much less than parsing everything would.
    /// </summary>
    [Benchmark]
    public int InkwrightRewrite()
    {
        using PdfDocument document = PdfDocument.Load(_file);
        return document.ToArray().Length;
    }
}
