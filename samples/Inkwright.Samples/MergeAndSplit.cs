using Inkwright;
using Inkwright.Content;

namespace Inkwright.Samples;

/// <summary>Creates two independent PDFs, joins them, then extracts the second page.</summary>
internal static class MergeAndSplit
{
    public static Task RunAsync(string directory)
    {
        using var first = PdfDocument.Create();
        using var second = PdfDocument.Create();
        foreach ((PdfDocument document, string title) in new[] { (first, "Cover page"), (second, "Delivery checklist") })
        {
            PdfPage page = document.Pages.Add(PageSize.A4);
            using ContentCanvas canvas = ContentCanvas.ForPage(page);
            canvas.DrawText(title, 72, 750, document.Fonts.Helvetica, 18);
            canvas.Commit();
        }

        using PdfDocument merged = PdfDocument.Merge(first, second);
        merged.Save(Path.Combine(directory, "merged-pack.pdf"));
        using PdfDocument extracted = merged.ExtractPages(1); // Zero-based page indices.
        string path = Path.Combine(directory, "extracted-checklist.pdf");
        extracted.Save(path);
        using PdfDocument reloaded = PdfDocument.Load(path);
        if (merged.Pages.Count != 2 || first.Pages.Count != 1 || second.Pages.Count != 1 || reloaded.Pages.Count != 1)
            throw new InvalidOperationException("Merge or extraction changed the expected page counts.");
        Console.WriteLine("  Merge/split: 2 pages joined, 1 page extracted; source documents unchanged");
        return Task.CompletedTask;
    }
}
