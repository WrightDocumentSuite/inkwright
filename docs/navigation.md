# Navigation: bookmarks, destinations, output intents

## Bookmarks

The bookmark tree (§12.3.3) is read and edited through the document model. `document.Outline` returns `null` if there are no bookmarks; `PdfOutline.OpenOrCreate` creates an empty tree you can add to.

```csharp
using PdfDocument document = PdfDocument.Load("book.pdf");

PdfOutline outline = PdfOutline.OpenOrCreate(document);
PdfOutlineItem chapter = outline.Add("Chapter 1", PdfDestination.FitPage(document.Pages[0]));
chapter.Add("Section 1.1", PdfDestination.XyzLocation(document.Pages[1], top: 800));
chapter.SetColor(PdfColor.Rgb(0.1f, 0.1f, 0.6f));
chapter.SetStyle(italic: false, bold: true);

document.SaveIncremental("book-bookmarked.pdf");
```

The doubly linked list `/First`–`/Last`–`/Next`–`/Prev` and signed counter `/Count` (positive—expanded, negative—collapsed) are rebuilt from the root on every change, no matter how deep in the tree the change was: a section added at the third level fixes totals for all ancestors above it.

Deletion removes the subtree and repairs the sibling chain:

```csharp
outline.Remove(chapter);          // together with all sections under it
```

## Destinations

`PdfDestination` is a document-independent value: the same destination can go to a bookmark, link, or open action. It becomes an array only when written, and the page gets an object number then if the model has not assigned one yet.

| Factory | What it does |
| --- | --- |
| `FitPage(page)` | fit entire page (`/Fit`) |
| `XyzLocation(page, left, top, zoom)` | exact position and scale (`/XYZ`); `null`—leave unchanged |
| `FitWidth(page, top)` | fit width, given row at top (`/FitH`) |
| `FitHeight(page, left)` | fit height, given column at left (`/FitV`) |

### Structure destinations (PDF 2.0)

A destination can point not to a page but to a structure element (§12.3.2.3): the reader finds the page from the element’s first content, so the link survives reflow that moved text. The first array element is an indirect reference to a structure element, not a page.

```csharp
PdfStructureNode heading = document.Structure[0];      // read as indirect object
item.SetStructureDestination(PdfDestination.ToStructureElement(heading, top: 750));
```

`SetStructureDestination` writes a normal `/Dest` entry, but the first element of its array is a reference to a structure element instead of a page. The document is upgraded to PDF 2.0 in the process. The `/SD` key applies only inside a GoTo action and is not a bookmark dictionary key. Important for PDF/UA-2.

## Bookmarks from the layout engine

`Inkwright.Layout` can build a bookmark tree from content: the `Bookmark` decorator does not change layout, but when content is placed on a page it remembers where, and after rendering that becomes `/Outlines`. The heading and its bookmark will not drift apart no matter how pages fall.

```csharp
Report.Create()
    .Page(page => page.Content().Column(column =>
    {
        column.Item().Bookmark("Chapter 1").Text("Chapter 1", TextStyle.Default.FontSize(18).Bold());
        column.Item().Bookmark("Section 1.1", level: 1).Text("Section 1.1");
        column.Item().Bookmark("Chapter 2").Text("Chapter 2", TextStyle.Default.FontSize(18).Bold());
    }))
    .Save("report.pdf");
```

`level` sets nesting: a bookmark whose level is deeper than the current tree depth attaches under the deepest existing entry.

In a tagged section (`page.Tagged()`) a bookmark gets not a page but a **structure destination**: it points to the structure element its content created, or to the enclosing node if content created none. ISO 14289-2 §8.8 requires this for any destination inside the document, and without it the report would fail its own PDF/UA-2 validator. An untagged section has no structure, and its bookmarks remain page destinations.

## Output intents

The device characteristics the document was prepared for (§14.11.5)—an embedded ICC profile—can be added for the whole document and, as PDF 2.0 allows, for an individual page: useful for a file assembled from pages prepared for different devices.

```csharp
document.AddOutputIntent(PdfOutputIntent.SRgb());
document.Pages[0].AddOutputIntent(PdfOutputIntent.FromIccProfile(cmykProfile, components: 4, "GTS_PDFX", "Press"));
```

Adding a page-level output intent upgrades the document version to PDF 2.0.

`PdfOutputIntent.SRgb()` uses a profile the library generates itself; `FromIccProfile` embeds any other.
