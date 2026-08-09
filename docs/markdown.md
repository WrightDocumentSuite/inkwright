# Exporting PDF to Markdown

Export produces a single `document.md` and, when raster `Figure` elements appear in the logical structure,
a sidecar image directory. Rendering happens entirely in memory first and does not modify the
source `PdfDocument`.

```csharp
using Inkwright;
using Inkwright.Markdown;

using PdfDocument pdf = PdfDocument.Load("report.pdf");

MarkdownDocument markdown = pdf.ToMarkdown();
Console.WriteLine(markdown.Text);

foreach (MarkdownExportWarning warning in markdown.Diagnostics)
    Console.WriteLine(warning);

await markdown.SaveAsync("export");
// export/document.md
// export/media/image1.png when referenced in Markdown
```

The short form performs the same in-memory pass and then saves the result:

```csharp
await pdf.ExportMarkdownAsync("export");
```

## Where Semantics Come From

PDF has no standard for conversion to Markdown. Inkwright connects two normative models:

- the logical structure of Tagged PDF from ISO 32000-2 §§14.7–14.9;
- CommonMark 0.31.2 syntax or GitHub Flavored Markdown.

`RoleMap` and PDF 2.0 `RoleMapNS` are resolved transitively. The order of the mixed `/K` array
is preserved across child elements, MCIDs, and `OBJR`; otherwise a paragraph, link, or image could
swap places. `ActualText` on a structure element and directly on a marked-content sequence
replaces visible glyphs character by character, without collapsing spaces and line breaks, as PDF prescribes.
Identical MCIDs in page content and Form XObject are distinguished by `/Stm`; attributes are read from both `/A`
and `ClassMap`, with the direct value taking priority. Tags are treated as standard only in the
PDF namespace; a matching name from a user namespace does not become, for example, a link.
`Alt` becomes the figure's alternative text.

Main mappings:

| Tagged PDF | Markdown |
| --- | --- |
| `H1`…`H6` | ATX headings `#`…`######` |
| `P`, `Caption` | paragraph |
| `L` / `LI` / `LBody` | ordered or unordered list per `ListNumbering` |
| description list | `<dl>` with HTML fallback diagnostic |
| simple `Table` | GFM pipe table |
| table with spans, row headers, mixed header row, or CommonMark mode | HTML `<table>` |
| `Link` + `OBJR` URI action | Markdown link after scheme check |
| raster `Figure` | PNG sidecar and Markdown image |
| `BlockQuote`, `Code`, `Strong`, `Em` | corresponding Markdown construct |
| `Formula` with presentation MathML | linear formula text |
| `Form` + Widget or `PrintField` | accessible name, value, or field state |

Decimal and Ordered numbering use the native Markdown marker, including explicit numeric `Lbl`. Roman/Alpha and
arbitrary `Lbl` are preserved as text inside the item with `StructureApproximated`: otherwise Markdown
would silently show a different label. `ContinuedList`/`ContinuedFrom` are also diagnosed, since
Markdown has no list identity across separate blocks.

External URIs are accepted only for `http`, `https`, `mailto`, `ftp`, and relative targets.
Executable and unknown absolute schemes, for example `javascript:`, do not become links and
are recorded as `UnsafeLinkSkipped`. HTML character references are decoded before checking, so
`javascript&#x3a;` does not bypass the allowlist; relative URIs are resolved via Catalog `/URI /Base`.
Internal PDF destinations remain plain text for now:
a PDF page address does not yield a stable anchor in Markdown.

## Untagged PDFs

By default `PreferTagged` uses the structure tree when it exists. For an untagged file the
exporter takes visible page text, escapes Markdown, and adds `UntaggedFallback`. It does not
silently guess headings, tables, and columns from appearance: a fixed-layout page has several
plausible semantic parses.

```csharp
var options = new MarkdownExportOptions
{
    StructureMode = MarkdownStructureMode.RequireTagged,
};
```

`RequireTagged` rejects a file without a structure tree. `PlainText` deliberately ignores tags and always
uses page text. This is useful for comparison but is also flagged in diagnostics.

Layout inference is available as an explicit untagged fallback:

```csharp
MarkdownDocument markdown = pdf.ToMarkdown(new MarkdownExportOptions
{
    StructureMode = MarkdownStructureMode.InferLayout,
});
```

`InferLayout` still uses the structure tree whenever one exists, so Tagged PDF keeps the same strict,
predictable rendering path. Only an untagged document is reconstructed from positioned glyphs. The
heuristics detect a stable two-column gutter, larger or bold headings, common bullet and ordered-list
markers, and repeated cell alignments for GFM pipe tables. With `MarkdownFlavor.CommonMark`, inferred
tables use the same HTML fallback policy as tagged tables. Consecutive text lines in one column are
joined into paragraphs, including conservative lowercase hyphen rejoining.

Inferred structure is deliberately reported as `StructureApproximated`. It does not recover semantics
that are absent from the file, and complex tables, nested lists, irregular multi-column layouts, or
font-size-only decoration may be classified differently from an author's intent. Applications that
need a reproducible semantic contract should keep the default `PreferTagged` or use `RequireTagged`.

## Options

```csharp
var options = new MarkdownExportOptions
{
    Flavor = MarkdownFlavor.GitHub,             // default
    StructureMode = MarkdownStructureMode.PreferTagged,
    IncludeImages = true,
    IncludeHiddenLayers = false,
    MediaDirectoryName = "assets/pdf images",
    MaximumPages = 10_000,
    MaximumStructureElements = 1_000_000,
    MaximumOutputCharacters = 64 * 1024 * 1024,
    MaximumImages = 10_000,
    MaximumTotalImageBytes = 512L * 1024 * 1024,
};

MarkdownDocument markdown = pdf.ToMarkdown(options);
```

`MediaDirectoryName` is a portable relative path. Absolute paths, `.`/`..`, empty segments,
control characters, and Windows device names are rejected before rendering. In Markdown, spaces and
other special bytes in path segments are URL-encoded; original directory names are preserved on disk.

Limits on pages, structure, and text stop export with an exception before returning a partial
`MarkdownDocument`. The sidecar image limit is soft: further raster data is not kept in memory,
the `Figure` keeps alternative text, and diagnostics receive `ImageSkipped`. An overall limit on
decoded stream size is additionally enforced by `PdfFilterLimits.MaxDecodedBytes`.

An image is linked to a `Figure` via `OBJR` or via the actual XObject invocation within the same
MCID, including Form XObject. Decodable rasters are converted to PNG; identical PNG bytes share one
sidecar in first-mention order. A vector figure, JPX, or damaged image is not
rasterized: `Alt` is used and `ImageSkipped` appears. A disabled layer is respected both on
marked content/Form and on the Image XObject itself. A repeat reference to the same not-yet-decoded
resource does not run the decoder again.

## Correctness Metrics

`MarkdownDocument.Metrics` is intended for corpus/CI checks. This is not a subjective quality score
but reproducible counters: pages and page errors, structure, distinct `(page, stream, MCID)` and
number of resolved links, sidecar images, failed image resources, warnings, and output length.

```csharp
MarkdownExportMetrics metrics = markdown.Metrics;
Console.WriteLine($"MCID coverage: {metrics.MarkedContentCoverage:P1}");
Console.WriteLine($"Skipped pages: {metrics.SkippedPages}");
```

`MarkedContentCoverage == 1` means every distinct MCR reference produced text; zero references also
counts as full coverage. A lower value is not necessarily a library error: Figure, vector
content, and intentionally empty marked-content sequences legitimately produce no glyphs, so the cause should
be matched against `Diagnostics`.

## Determinism and Writing

The same model with the same options yields the same text, diagnostic order, image order,
and names `image1.png`, `image2.png`, … Text uses LF and exactly one trailing LF.
`SaveAsync` writes UTF-8 without BOM, overwrites only files belonging to the current result, and does not
delete foreign or stale files. An existing symlink/junction in the path to `document.md`, media
directory, or sidecar is rejected: a lexical `..` check alone does not protect against escape via reparse point.
A pre-cancelled `ExportMarkdownAsync` stops before render and directory creation. Cancellation after writing
starts is not transactional: files already written may remain on disk.

Damage to decode/content on one page yields `ContentSkipped` with the page number and does not block
processing the next pages. This includes a page content stream whose remaining filter is unknown or
is an image-only codec: its encoded bytes are never misread as PDF drawing operators. U+0000 never
enters Markdown: it is replaced with U+FFFD.

Diagnostics are part of the contract, not an exception log. They report untagged fallback,
structural approximation, HTML fallback, skipped content, unsafe link, or an image
that could not be saved reliably. Identical `Kind` and `Subject` are emitted once.

The exporter does not apply the owner's `SourcePermissions.CopyContents` permission automatically. If
the application must honor the source PDF's restrictions, it should check `SourcePermissions`
before `ToMarkdown` or `ExportMarkdownAsync`.
