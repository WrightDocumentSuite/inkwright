# Quick Start

Requires .NET 10 SDK.

```bash
dotnet add package Inkwright            # core: read, write, edit, text extraction
dotnet add package Inkwright.Layout     # report layout engine, tags, PDF/A
dotnet add package Inkwright.Forms      # interactive forms
dotnet add package Inkwright.Signing    # signatures
```

## Build a Document

```csharp
using Inkwright;
using Inkwright.Content;

using var document = PdfDocument.Create();
PdfPage page = document.Pages.Add(PageSize.A4);

using (ContentCanvas canvas = ContentCanvas.ForPage(page))
{
    canvas.DrawText("Hello", 72, 700, document.Fonts.Helvetica, 24, PdfColor.Black);
    canvas.FillColor(PdfColor.Parse("#2278D4")).Rectangle(72, 600, 200, 60).Fill();
    canvas.Commit();
}

document.Info.Title = "First file";
document.Save("hello.pdf");
```

## A Report That Flows Across Pages on Its Own

```csharp
using Inkwright.Layout;

Report.Create()
    .Page(page =>
    {
        page.Size(PageSize.A4);
        page.Margin(45);
        page.Header().Text("Quarterly Report", TextStyle.Default.FontSize(18).Bold());
        page.Content().Column(column =>
        {
            column.Spacing(10);
            column.Item().Text("Revenue is holding at last quarter's level.");
            column.Item().Table(table =>
            {
                table.Columns(ColumnWidth.Fraction(3), ColumnWidth.Fraction(1));
                table.Grid();
                table.Header().Text("Item").Text("Amount");
                table.Row().Text("Services").Text("29 620");
            });
        });
        page.Footer().AlignCenter().Text(text => text.PageNumbers("{0} of {1}"));
    })
    .Save("report.pdf");
```

## Read Someone Else's File

```csharp
using PdfDocument document = PdfDocument.Load("input.pdf");

Console.WriteLine($"{document.Pages.Count} pages, version {document.Version}");

foreach (PdfWarning warning in document.Diagnostics)
    Console.WriteLine($"  {warning}");

string text = document.Pages[0].ExtractText();
```

A file with a corrupted cross-reference table opens the same way: the reader finds objects by
scanning and records in `document.Diagnostics` exactly what had to be repaired.

## Edit and Append

```csharp
using PdfDocument document = PdfDocument.Load("invoice.pdf");

using (ContentCanvas canvas = ContentCanvas.ForPage(document.Pages[0]))
{
    canvas.Save()
          .Opacity(0.15)
          .Transform(PdfMatrix.Rotation(38) * PdfMatrix.Translation(140, 240))
          .DrawText("PAID", 0, 0, document.Fonts.HelveticaBold, 72, PdfColor.Parse("#1B7F3B"))
          .Restore();
    canvas.Commit();
}

document.SaveIncremental("invoice-paid.pdf");   // original bytes untouched
```

## Annotate

```csharp
using Inkwright.Annotations;

using PdfDocument document = PdfDocument.Load("contract.pdf");
PdfPage page = document.Pages[0];

page.Annotations.MarkText("delivery dates", PdfAnnotationType.Highlight, author: "Priya");

PdfTextAnnotation note = page.Annotations.AddNote(
    new PdfPoint(505, 700), "Rate unchanged?", "Priya", PdfNoteIcon.Help);

note.Reply("No, same as before", "Tom");
note.SetReviewState(PdfReviewState.Completed, "Priya");

document.SaveIncremental("reviewed.pdf");
```

Every annotation gets a ready-made appearance stream, so it looks the same in all viewers.
Deletion is cascading: `note.Delete()` removes the popup and the entire reply thread.

## What's Next

- [Architecture](architecture.md) — what the library is made of and why
- [Reading and Editing](reading.md) — diagnostics, recovery, encryption
- [Layout](layout.md) — Measure/Draw, tables, headers and footers, page breaks
- [Text and Fonts](text.md) — embedding, subsetting, extraction with positions
- [Comments and Annotations](annotations.md) — notes, highlights, reply threads, statuses
- [Forms](forms.md) — filling, appearance, flatten, typed mapping
- [Signing](signing.md) — PAdES, timestamps, external keys
- [Archival Documents](archival.md) — tags, PDF/A, Factur-X
- [Benchmarks](benchmarks.md)
