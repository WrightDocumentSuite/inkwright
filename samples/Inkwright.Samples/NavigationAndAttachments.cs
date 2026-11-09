using System.Text;
using Inkwright;
using Inkwright.Content;

namespace Inkwright.Samples;

/// <summary>Adds a bookmark and embeds the source CSV, then reads both back.</summary>
internal static class NavigationAndAttachments
{
    public static Task RunAsync(string directory)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("Quarterly sales", 72, 750, document.Fonts.HelveticaBold, 20);
            canvas.DrawText("Source data is attached as sales.csv.", 72, 710, document.Fonts.Helvetica, 12);
            canvas.Commit();
        }

        PdfOutline.OpenOrCreate(document).Add("Sales overview", PdfDestination.FitPage(page));
        byte[] source = Encoding.UTF8.GetBytes("Month,Revenue\nJanuary,12000\nFebruary,15500\n");
        document.Attachments.Add("sales.csv", source, "text/csv", "Source sales data", AttachmentRelationship.Source);
        string path = Path.Combine(directory, "navigable-report.pdf");
        document.Save(path);

        using PdfDocument reloaded = PdfDocument.Load(path);
        byte[] attached = reloaded.Attachments.Read("sales.csv") ?? throw new InvalidOperationException("Missing attachment.");
        if (reloaded.Outline?.Count != 1 || !attached.SequenceEqual(source))
            throw new InvalidOperationException("The bookmark or attachment did not round-trip.");
        Console.WriteLine($"  Navigation: 1 bookmark, {attached.Length} attachment bytes verified");
        return Task.CompletedTask;
    }
}
