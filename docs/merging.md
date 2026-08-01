# Merging and Splitting

## Join and Split

```csharp
using PdfDocument merged = PdfDocument.Merge(first, second, third);
merged.Save("book.pdf");

using PdfDocument chapter = book.ExtractPages(0, 1, 2, 7);
```

Source documents are not modified: pages are copied, not moved.

## What Moves Besides Pages

Copying pages is the easy half of merging. Bookmarks that point to them, names other files use to reference them, form fields whose widgets sit on them, and the structure tree that makes them accessible — all of that lives in the catalog, and a merge that forgets them yields a file that looks right but behaves like a stack of pictures.

| What | How it is transferred |
| --- | --- |
| Bookmarks | kept if they still lead somewhere; a node without its own destination remains if at least one child remains |
| Named destinations | conflicting names are renamed; references to them are redirected |
| AcroForm fields | conflicting full names are renamed (§12.7.3.2); `/DR`, `/DA`, `/Q`, and `/CO` are merged |
| Structure tree | `/StructParents` and `/StructParent` keys are shifted; `/ParentTree` is rebuilt (§14.7.4.4) |
| Page labels | ranges shift to wherever the pages landed |
| Attachments | conflicting names are renamed |
| Layers | groups and the off list are transferred; otherwise hidden content would become visible |

What the target document already had stays as it was: a resource name the target already owned keeps its previous meaning; otherwise its own fields would redraw with someone else's fonts.

## Why a Bookmark Loses Its Name

After merging, a bookmark that pointed to a named destination instead points directly at the page. That is more reliable: the name may have been renamed due to a conflict, but a page is a page.

## Page Labels During Merge

A document without its own labels still gets one range. Without that, the previous document's Roman numerals would spill onto pages that did not ask for them.

## Copying One Page at a Time

```csharp
PdfPage copy = target.Pages.Import(page);
IReadOnlyList<PdfPage> copies = target.Pages.ImportMany(source, [source.Pages[0], source.Pages[4]]);
```

`Import` copies one page's graph and nothing else. `ImportMany` and `ImportAll` additionally transfer all catalog scaffolding, as `Merge` does.

Shared objects are copied once: pages that shared a font or image still share them after the move. Links between transferred pages are preserved — each gets a number in the new document before the first one is copied. A link to a page that was not requested is broken: otherwise that page and everything it draws would have come along into the new document.
