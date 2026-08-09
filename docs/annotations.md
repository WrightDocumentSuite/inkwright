# Comments and annotations

A comment in PDF is an annotation: a dictionary in the page’s `/Annots` array. The library provides a typed layer over it, `Inkwright.Annotations`: the page returns a collection, the collection can add items, and each annotation can edit itself and be deleted together with everything attached to it.

```csharp
using Inkwright.Annotations;

using PdfDocument document = PdfDocument.Load("contract.pdf");
PdfPage page = document.Pages[0];

foreach (PdfMarkupAnnotation comment in page.Annotations.Comments)
    Console.WriteLine($"{comment.Author} ({comment.CreatedAt:d}): {comment.Contents}");
```

`Comments` are top-level comments: replies and pop-up windows are not included in the enumeration because the reader shows them inside the thread. The full unfiltered list is the collection itself, `page.Annotations`, which also includes links and form fields.

## Adding

```csharp
// Sticky note with icon
PdfTextAnnotation note = page.Annotations.AddNote(
    new PdfPoint(505, 700), "Check contract rate", "Priya Raman", PdfNoteIcon.Help);

// Highlight by words, not coordinates
page.Annotations.MarkText("Out-of-hours support", PdfAnnotationType.Highlight, author: "Priya Raman");
page.Annotations.MarkText("sprint 15", PdfAnnotationType.StrikeOut, "Postponed", "Priya Raman");

// Text directly on the page
page.Annotations.AddFreeText(new PdfRectangle(72, 500, 320, 580), "Needs new wording", "Editor");

// Shapes, ink, stamp, attachment, link
page.Annotations.AddRectangle(new PdfRectangle(45, 60, 550, 95), "Look here", "Editor", PdfColor.Red);
page.Annotations.AddEllipse(new PdfRectangle(240, 400, 380, 460), fill: PdfColor.Parse("#FFF3C4"));
page.Annotations.AddLine(new PdfPoint(72, 380), new PdfPoint(380, 380));
page.Annotations.AddInk([[new PdfPoint(80, 300), new PdfPoint(120, 340), new PdfPoint(160, 300)]]);
page.Annotations.AddStamp(new PdfRectangle(400, 720, 550, 770), "Approved");
page.Annotations.AddAttachment(new PdfPoint(72, 200), "calculation.xlsx", bytes);
page.Annotations.AddLink(new PdfRectangle(72, 100, 200, 120), "https://example.org");

// Outline a region, draw a polyline, mark a missing punctuation mark
page.Annotations.AddPolygon([new PdfPoint(80, 600), new PdfPoint(200, 660), new PdfPoint(260, 580)]);
page.Annotations.AddPolyLine([new PdfPoint(80, 500), new PdfPoint(160, 540), new PdfPoint(240, 500)]);
page.Annotations.AddCaret(new PdfPoint(300, 420), "Comma needed here", "Editor");

// On-page label with arrow to what it refers to
page.Annotations.AddCallout(
    new PdfRectangle(320, 620, 520, 680), new PdfPoint(240, 590), "Check this corner", "Editor");

// Line endings
page.Annotations.AddLine(new PdfPoint(60, 240), new PdfPoint(240, 240))
    .SetEndings(PdfLineEnding.None, PdfLineEnding.ClosedArrow);
```

For a callout, the annotation rectangle must cover both text and arrow, so `Rect` is computed from both, and the inset to the label itself is written in `/RD`; it is convenient to read through `TextBox`.

`MarkText` is the usual way to comment: the caller names the words, and the library finds them via glyph extraction (`page.FindText`) and lays out `/QuadPoints` on the lines the text hit. One annotation is returned per occurrence.

Vertex order in `/QuadPoints` is written the way readers expect (top-left, top-right, bottom-left, bottom-right), not the way ISO 32000-1’s “counter-clockwise” wording reads. On read, order does not matter: the region is computed from the bounds of all four points, so files from both conventions parse the same way.

## Reading order and PDF/UA

A visible annotation in PDF/UA is not accessible merely because it has `Contents`. ISO 14289-1
§7.18.1 also requires the annotation to occur in the logical reading order as a whole-object content
item. Add it to an `Annot` (or another semantically appropriate) structure element before building
the tree:

```csharp
using Inkwright.Tagging;

PdfTextAnnotation note = page.Annotations.AddNote(
    new PdfPoint(505, 700), "Check contract rate", "Priya Raman");

var structure = new StructureTreeBuilder(document);
StructureElement root = structure.AddRoot("Document");
root.Add("Annot").AddAnnotation(note);
structure.Build();
```

`Build` writes all three sides of the relationship together: `StructElem/K -> OBJR/Obj`, the
annotation's `StructParent`, and the matching entry in `StructTreeRoot/ParentTree`. It also sets
`Tabs /S` on the annotation's page. Keys are allocated above both the existing number-tree keys and
`ParentTreeNextKey`, so annotations on several pages cannot collide.

Calls to `StructureElement.AddContent`, `AddAnnotation`, and `Add` define one exact reading-order
sequence. `Build` writes that sequence to `K` without grouping items by type. For example,
`AddContent`, then `AddAnnotation`, then `AddContent` produces `[MCID, OBJR, MCID]`; moving the
`AddAnnotation` call moves the annotation in the logical reading order even though its icon remains
at the same page coordinate. This is an accessibility invariant, not merely a serialization detail:
place each annotation at the point where a reader should encounter it.

In accordance with ISO 32000-1 §14.7.4.2, when one structure element spans pages, the first page is
the element's `Pg` and its content may use compact integer MCIDs. Content on every other page is
written as an `MCR` dictionary with explicit `Pg` and `MCID`, while annotations remain `OBJR`
entries in their original position. This preserves both the ordered `K` ledger and the page identity
that `StructureReader` reports for every content kid.

When layout has already built the tree, insert the annotation at its position under the existing
content owner rather than creating a second document root:

```csharp
PdfStructureNode paragraph = document.Structure
    .SelectMany(root => root.Descendants())
    .First(node => node.Tag == "P");

var update = new StructureTreeBuilder(document);
update.AddAnnotation(paragraph, note);
update.Build();
```

The selected `P` remains the content owner: the update creates an indirect `Annot` child with a
back-link in `P`, then puts the `OBJR` under that child. It does not put a markup annotation's
`OBJR` directly under `P`. Links receive a `Link` child (`Reference` is also accepted as an existing
owner), widgets receive `Form`, and other annotations receive `Annot`. If the selected element
already resolves to the required semantic type, it owns the `OBJR` directly and no redundant child
is introduced.

The two-argument overload appends for compatibility. Existing `K` items are otherwise left in their
original order, and an exact position or an existing kid can place a late annotation in the middle:

```csharp
update.AddAnnotation(paragraph, note, index: 2);
PdfStructureKid anchor = paragraph.Kids[1];
update.AddAnnotationBefore(paragraph, secondNote, anchor);
update.AddAnnotationAfter(paragraph, thirdNote, anchor);
```

Indexes address the serialized `K` sequence from zero through its current count. Anchors must be
the actual objects obtained from the selected node's `Kids` list. The selected element and the
annotation must be indirect objects in the same document, and one annotation can have only one
structure owner. An annotation already carrying `StructParent` or already referenced by an `OBJR`
is rejected instead of creating an ambiguous second relationship.

`Build` preflights the complete pending update before it writes a structure object or changes `K`.
This includes duplicate pending MCIDs, conflicts with an existing page parent array, a page
`StructParents` key that actually belongs to an object, and exhaustion of `ParentTree` keys. A
preflight failure leaves the document and builder unchanged, so malformed input can be repaired and
the same builder retried. Producer-stated page keys are reserved before keys are allocated to
unkeyed pages, and that deterministic plan is reused during mutation; page traversal order therefore
cannot make two pages acquire the same `StructParents` key.

## Discussion threads and statuses

```csharp
PdfTextAnnotation answer = note.Reply("Yes, same rate", "Tom Ware");
answer.Reply("Thanks", "Priya Raman");

note.SetReviewState(PdfReviewState.Completed, "Priya Raman", "Fixed in fourth revision");

Console.WriteLine(note.Thread.Count);         // 4: the comment itself and three replies
Console.WriteLine(note.ReviewState);          // Completed
Console.WriteLine(note.ReviewStateBy("Tom Ware")); // None — he did not set a status
```

A reply is a separate annotation with `/IRT` pointing to the parent and `/RT /R`. Per the specification, review status is stored not in the annotation itself but in a reply with `/State` and `/StateModel`: that way two reviewers can disagree without overwriting each other.

Replies are not drawn on the page: they get an empty appearance stream. Simply removing `/AP` is not enough—a reader that invents its own icon for a text annotation (as Chrome’s built-in viewer does) would draw over the parent with an icon for each reply.

## Editing

```csharp
PdfMarkupAnnotation comment = page.Annotations.Comments.First();

comment.Contents = "Revised wording";
comment.Author = "Priya Raman";
comment.Subject = "Payment terms";
comment.Opacity = 0.6;
comment.Color = PdfColor.Parse("#4CAF50");
comment.Rectangle = comment.Rectangle.Translate(0, -20);
comment.IsOpen = true;
```

Any write updates `/M` and marks the object changed, so incremental save picks up only that object.

Paragraphs in comment text are separated by newline in the API and carriage return in the file: §12.5.6.2 requires `CR` specifically, and readers take that literally—text with `LF` shows as one line.

Redrawing after an edit is cautious. If the annotation came from a file with its own drawing—a stamp image, signature, figure drawn by another tool—that drawing stays: editing text is not a reason to discard what we cannot reproduce. Only what we drew ourselves, and what had no drawing at all, is redrawn. You can take over appearance explicitly:

```csharp
comment.RegenerateAppearance();
```

## Deletion

```csharp
comment.Delete();                                  // together with pop-up and the entire reply thread
page.Annotations.RemoveAll(a => a.Type == PdfAnnotationType.Highlight);
page.Annotations.RemoveComments();                 // links and form fields remain
int removed = document.RemoveAllComments();        // across the whole document

document.Save("clean.pdf", new PdfSaveOptions { RemoveUnusedObjects = true });
```

Deletion removes the reference from `/Annots` and, for a tagged annotation, removes the matching
`OBJR`, `StructParent`, and `ParentTree` entry as one operation. An empty `Annot` is pruned, as is the
minimal `Link` or `Form` shell created by existing-tree insertion. An authored `Link` or `Form` that
carries semantic metadata such as `Alt`, attributes, an ID, or a title is retained even when its last
annotation is removed; the library does not discard caller-owned meaning. Elements that still own
another object or marked content are always retained. This applies to the whole discussion thread,
including state replies, and to file attachment annotations.

For a damaged file that already lost `OBJR` but still has `StructParent` and `ParentTree`, deletion
also removes the orphaned key when no other live page, annotation, or XObject claims it. An
ambiguously shared key is left in place rather than breaking the surviving object; validate and
repair that malformed structure separately.

The annotation dictionary, appearance stream, and attachment stream can still exist as unreachable
objects until the file is rewritten. That is why a privacy-clean copy is saved with
`RemoveUnusedObjects = true`: incremental save cannot erase bytes already present in an earlier PDF
revision. With pruning enabled, no structure reference keeps the removed comment or attachment
payload alive.

Cascade is mandatory: if you delete a comment and leave the thread, replies hang in the air and the reader shows them as separate notes without a topic. With an attachment annotation, the file goes too: its entry is removed from the `/Names /EmbeddedFiles` tree and from `/AF`; otherwise a “cleaned” document would still carry the attachment.

## Flattening

```csharp
page.Annotations.Flatten();                  // comments only
page.Annotations.Flatten(a => a.Type == PdfAnnotationType.Stamp);
int burnt = document.FlattenComments();
```

Flattening draws the annotation’s appearance stream into page content and removes the annotation
itself: marks stay visible everywhere, but they can no longer be opened, moved, or deleted. Its
`OBJR`, `StructParent`, and `ParentTree` entry are removed at the same time, so the logical tree does
not retain a dangling interactive object. The painted appearance is ordinary page content marked
as `Artifact`; this operation does not invent replacement document semantics for a review mark
whose conversation has been removed.

Placement follows §12.5.5—the form’s BBox transformed by its `/Matrix`, scaled to `Rect`, and only
that second step is written to the stream because `Do` applies `/Matrix` itself.

The discussion thread goes with the comment: a stroke remains on the page, the conversation does not. Hidden (`Hidden`, `NoView`) annotations are not drawn and not removed: they are invisible, and there is nothing to flatten.

## Export

```csharp
foreach (PdfCommentInfo comment in document.ExportComments())
{
    Console.WriteLine($"p.{comment.PageIndex + 1} {comment.Author}: {comment.Contents}");
    Console.WriteLine($"  on text: {comment.QuotedText}");

    foreach (PdfCommentInfo reply in comment.Replies)
        Console.WriteLine($"  ↳ {reply.Author}: {reply.Contents} [{reply.State}]");
}
```

`PdfCommentInfo` is a flat record with page, type, author, text, dates, status, threads, and quoted text; it serializes to JSON or a table without extra work.

`QuotedText` is the words the mark covers. Only types that sit on top of text provide it: highlights, underlines, strikeouts, squiggles, rectangles, ellipses, polygons, polylines, ink, and lines. A note sits beside text, `FreeText` carries its own words, and a stamp lies on top of anything—quoting text beneath them would misrepresent unrelated content as evidence.

For a single annotation there is `comment.GetQuotedText()`, but it re-reads the page each time; `ExportComments` extracts glyphs once per page.

## Review exchange: XFDF and FDF

Reviewers rarely work on one file. Review rounds travel between them separately from the document—in XFDF (ISO 19444-1 XML format) or the older FDF (ISO 32000-1 §12.7.7, PDF syntax itself). Both directions are available:

```csharp
// Send your edits
string xfdf = document.ExportXfdf();
byte[] fdf = document.ExportFdf();
document.ExportReview("round-4.xfdf");   // format from extension

// Accept someone else’s
PdfReviewImportResult result = document.ImportXfdf(xfdf);
document.ImportReview("round-4.fdf");    // format from first bytes of file

Console.WriteLine($"{result.Added} new, {result.Updated} updated, {result.Skipped} skipped");
foreach (string warning in result.Warnings)
    Console.WriteLine(warning);
```

Both XFDF and FDF carry two halves—form field values and comments—and which one you need depends on the task. Both are written by default; `PdfExchangeContent` selects one. For the form half, see [forms.md](forms.md).

```csharp
string comments = document.ExportXfdf(PdfExchangeContent.Annotations);
string both = document.ExportXfdf(PdfExchangeContent.All, href: "contract.pdf");

byte[] fdfComments = document.ExportFdf(PdfExchangeContent.Annotations);
byte[] fdfFields = document.ExportFormDataFdf();          // field values only
```

Import does not throw on content mismatches: someone else’s review round is never exactly what you expect. What could not be placed is counted in `Skipped` and explained in `Warnings`. `PdfFormatException` is thrown only when the file is neither XFDF nor FDF.

### How merging works

Comments are matched by `/NM` name—the same one the library assigns to each of its annotations. An already present comment is rewritten in place, same object: incremental save picks up only that object. An unknown one is added to the page named by the payload.

The main property follows: **re-importing the same round does not duplicate anything**. The second time everything lands in `Updated`, and the annotation count on the page does not change.

A comment without `/NM` gets a name on export and on import—without it the next round could not tell a reply from a second copy.

Replies are linked by name, not object number: a number from another document means nothing here. The thread is restored in a second pass after all annotations from the round have found their place. Review statuses arrive the same way they are stored—as separate replies with `/State`.

### What to choose

FDF is PDF syntax, so appearance streams travel in it too: the recipient sees exactly the drawing the sender made. XFDF does not carry appearance, and accepted comments are redrawn from type, color, and geometry. For edits made with this library the result is indistinguishable; for a stamp with an image from Acrobat, it is not.

An embedded file travels in both formats: as a stream in FDF, as a `<data>` element with base64 in XFDF.

### Limits

- `<appearance>` in XFDF (serialized appearance) is not parsed—the comment is redrawn and a warning appears in `Warnings`. If appearance matters, send FDF.
- Form field values (`<fields>`, `/Fields`) now travel both ways in both formats—XFDF and FDF. On import they are written to same-named fields in the document; a field missing from the document is flagged with a warning and not created, because the format does not say what type it would be. Details are in [forms.md](forms.md).
- `/Differences`, `/JavaScript`, and template `/Pages` from FDF are ignored with a warning.
- Exotic `/Encoding` from FDF (Shift_JIS, BigFive, GBK, UHC) is not supported—as throughout the core, strings are read as PDFDocEncoding or UTF-16.
- Links (`Link`) are not part of exchange: they are not comments, and FDF explicitly forbids them in `/Annots`.

## Appearance

The specification allows a reader to draw a comment its own way, and each draws differently—some not at all. So the library writes an appearance stream up front: note icon, highlight fill, underline, squiggle, frame, ink strokes, stamp, attachment paperclip, `FreeText` with wrapping to the frame width.

Highlight is drawn in `/Multiply` mode; otherwise the fill would cover the very words being highlighted. `/CA` transparency is baked into the stream via `ExtGState`, because when appearance is present the reader ignores the dictionary value.

The form `BBox` is set in page coordinates and `/Matrix` stays identity: then the §12.5.5 transform degenerates to identity and the drawing lands exactly where geometry points.

Editing one comment again does not spawn streams—the drawing is overwritten in the same object.

## Text search

```csharp
foreach (PdfTextMatch match in page.FindText("total"))
    Console.WriteLine($"{match.Text} {match.Bounds}");
```

`FindText` works on glyphs, not a flat string: the result is rectangles on the page, one per line the match crossed. Spaces between words are inferred from glyph spacing, because content streams usually do not draw a space character.

## Comments and PDF/A

`PdfAProfile.Validate` checks annotations against archival standard section 6.3: forbidden subtypes (`3D`, `Sound`, `Screen`, `Movie`), required `/F` with Print set and Hidden, Invisible, NoView, and ToggleNoView cleared, `NoZoom` and `NoRotate` on notes, appearance stream on everything except `Popup`, `Link`, and degenerate rectangles, and `/AP` with only the `/N` key.

A comment added by this library passes validation as-is. But `IsHidden = true` breaks compliance—and that is exactly the case where you learn about the problem from someone else’s validator a month later without checking yourself.

Separately, it is checked that a PDF/A-2 document does not carry attachments that are clearly not PDF: part 2 allows embedding only files that are themselves PDF/A-compliant—for an XML invoice you need part 3.

## What is read but not modeled

Types absent from the model (`Sound`, `Movie`, `Screen`, `Watermark`, `Redact`, `3D`) are returned as `PdfGenericAnnotation`: common properties are available, the rest through `annotation.Dictionary`. On rewrite they are preserved intact. They do not participate in review exchange: XFDF describes them with elements not in the comment list, and such an element is flagged with a warning.
