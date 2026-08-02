# Inkwright

A modern .NET 10 PDF library (Apache-2.0) with not one native dependency. The architecture is
layered the way `System.Text.Json` is: COS primitives → streaming I/O → document model → add-ons
that only the people who need them pay for.

## What it does

- **Reads broadly**: cross-reference tables and streams, hybrid files, object streams, incremental
  updates. Recoverable structural damage is scanned and reported through `LoadDiagnostics`;
  unrecoverable input raises `PdfFormatException`. Across a corpus of **7,807 real files, 7,800
  open and 7,555 eligible files rewrite and reload**; five deliberately corrupt fixtures (no
  catalogue or header) are rejected, and two empty files never reach the parser.
- **Lossless editing**: an object the model never parsed is copied byte for byte on rewrite. Object
  numbers survive, and so do diagrams, annotations and everything the library did not understand.
- **Incremental saving**: changes are appended and not one original byte moves — the only way not to
  break signatures somebody else made.
- **Report composer** (`Inkwright.Layout`): two-phase `Measure`/`Draw` with `SpacePlan`, line
  breaking and justification, tables with repeating headers, running heads, "page X of Y", explicit
  breaks, and a `LayoutException` carrying the element tree instead of silently clipping.
- **Text**: built-in metrics for the fourteen standard fonts, TrueType/OpenType embedding with
  subsetting and `Identity-H`, letter extraction with coordinates. Checked against PdfPig on real
  files: mean similarity ≥ 0.90.
- **PDF → Markdown**: Tagged PDF becomes CommonMark/GFM headings, paragraphs, lists, links and
  tables; raster `Figure` elements come out as deduplicated PNG sidecars. The untagged fallback and
  every loss of semantics land in the diagnostics rather than passing quietly.
- **Images**: JPEG is embedded as it stands, PNG without a single re-encode — its deflate stream is
  already what PDF wants — and alpha becomes a soft mask.
- **Comments**: notes, highlight/underline/strikeout over words found by search, `FreeText` and
  callouts, shapes, polylines, ink, stamps, attachments and links. Reply threads and review states
  per ISO 32000-1, cascading deletion together with the popup and the thread, flattening into page
  content, export to flat records with the quoted text, and a ready appearance stream for every
  type. Review rounds travel to other tools and back through XFDF (ISO 19444-1) and FDF (§12.7.7),
  merging on `/NM` so a second import does not duplicate anything.
- **Forms** (`Inkwright.Forms`): typed fields, filling with generated appearance streams,
  flattening, and `[PdfForm]` plus an incremental source generator for reflection-free mapping and
  Native AOT.
- **Signatures** (`Inkwright.Signing`): PAdES building blocks from B-B to B-LTA — signature and
  document timestamps (RFC 3161), the validation data store (DSS/VRI), DocMDP certification
  signatures and FieldMDP field locking — plus external keys for an HSM, built entirely on the base
  class library with no BouncyCastle. For B-LT and B-LTA the application supplies OCSP/CRL data it
  has already verified and sets the trust policy. Digests are SHA-2, and also SHA-3 and SHAKE256 per
  ISO/TS 32001 where the platform provides them; the Edwards curves of ISO/TS 32002 are verified by
  arithmetic of the library's own.
- **Archival documents**: Tagged PDF and eleven PDF/A levels — eight from parts 1 to 3 (`A1B`,
  `A1A`, `A2B`, `A2U`, `A2A`, `A3B`, `A3U`, `A3A`) and three from part 4 (`A4`, `A4E`, `A4F`) — with
  a generated sRGB ICC profile and a validator that names the ISO 19005 clause a file breaks.
  Reconciled clause by clause against the texts of
  [parts 1–3](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/pdfa-matrix.md) and
  [part 4](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/pdfa4-matrix.md), and
  checked against 106 archival files from another implementation. PDF/A-4 is the only level a PDF
  2.0 file can claim: parts 1 to 3 require a `%PDF-1.n` header and part 4 requires `%PDF-2.n`.
  Factur-X and ZUGFeRD attachments with the `fx:` schema description.
- **Accessibility**: PDF/UA-1 and UA-2 profiles (ISO 14289) with a validator that tells a broken
  requirement from an unmet recommendation and names the clause; conformance declarations per PDF
  Declarations, including the Well-Tagged PDF 1.0 levels.
- **Encryption**: reads RC4, AES-128, AES-256 and AES-GCM; writes AES-256 in cipher block chaining
  (revision 6) or Galois/counter mode (revision 7, ISO/TS 32003) with authentication. Certificate
  encryption to X.509 recipients (§7.6.5) both ways, on `EnvelopedCms`.
- **Navigation**: outlines (`PdfOutline`) are read and edited in the model and built by the
  composer, destinations to a page and to a structure element (§12.3.2.3), and output intents on
  the document and on a page.
- **Integrity**: the authentication code of ISO/TS 32004 — written and verified, as a standalone
  token or as a signature attribute; checked against 94 files from another implementation.

## A quick look

```csharp
using Inkwright;
using Inkwright.Layout;

// A report that flows across pages by itself
Report.Create()
    .Page(page =>
    {
        page.Size(PageSize.A4);
        page.Margin(45);
        page.Header().Text("Quarterly report", TextStyle.Default.FontSize(18).Bold());
        page.Content().Table(table =>
        {
            table.Columns(ColumnWidth.Fraction(4), ColumnWidth.Fraction(1));
            table.Grid();
            table.Header().Text("Item").Text("Amount");
            foreach (var line in lines)
                table.Row().Text(line.Name).Text(line.Total);
        });
        page.Footer().AlignCenter().Text(text => text.PageNumbers("{0} of {1}"));
    })
    .Save("report.pdf");
```

```csharp
// Editing somebody else's file: a stamp on top, original bytes untouched
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

document.SaveIncremental("invoice-paid.pdf");
```

```csharp
// The text back out, with coordinates
using PdfDocument document = PdfDocument.Load("scan.pdf");

foreach (PdfLetter letter in document.Pages[0].ExtractLetters())
    Console.WriteLine($"{letter.Text} at {letter.Origin} {letter.FontSize:0.#} pt");
```

```csharp
// Reviewing: highlight words, leave a note, reply, set a state
PdfPage page = document.Pages[0];

page.Annotations.MarkText("delivery dates", PdfAnnotationType.Highlight, author: "Priya");
PdfTextAnnotation note = page.Annotations.AddNote(
    new PdfPoint(505, 700), "Has the rate changed?", "Priya", PdfNoteIcon.Help);

note.Reply("No, unchanged", "Tom");
note.SetReviewState(PdfReviewState.Completed, "Priya");

document.SaveIncremental("reviewed.pdf");
```

```csharp
// A review round travels between reviewers apart from the document, as XFDF or FDF
document.ExportReview("round-4.xfdf");

PdfReviewImportResult round = other.ImportReview("round-4.xfdf");
Console.WriteLine($"{round.Added} added, {round.Updated} updated");
```

```csharp
// A form filled from a typed record
[PdfForm]
public partial record Invoice(
    [property: PdfField("customer.name")] string Customer,
    [property: PdfField("invoice.total", Format = "0.00")] decimal Total,
    [property: PdfField("invoice.paid")] bool Paid);

PdfForm form = PdfForm.Open(document)!;
form.Fill(new Invoice("Ada Lovelace", 1234.50m, true));
form.Flatten();
```

```csharp
// A signature as an incremental update
byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions
{
    Certificate = certificate,
    Reason = "Approved",
    Appearance = PdfRectangle.FromSize(60, 60, 220, 70),
});
```

## Performance

A 200-page report (8000 table rows) and a 90-page document, measured on an Intel Core i7-8700 under
Windows Server 2022 with .NET 10.0.10 and BenchmarkDotNet 0.15.8. Method and comparison notes are in
[docs/benchmarks.md](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/benchmarks.md);
numbers move with the machine, so treat them as a shape rather than a promise.

| | Time | Allocated |
| --- | ---: | ---: |
| **Inkwright, canvas, 200 pp.** | **46 ms** | **13.6 MB** |
| PDFsharp 6.2, 200 pp. | 177 ms | 38.4 MB |
| Inkwright, composer, 200 pp. | 345 ms | 110.3 MB |
| **Inkwright, open a file** | **382 µs** | **188 KB** |
| PdfPig 0.1.11, open a file | 1,048 µs | 485 KB |
| **Inkwright, extract text** | **177 ms** | **155 MB** |
| PdfPig 0.1.11, extract text | 191 ms | 103 MB |

## Documentation

- [Getting started](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/getting-started.md)
- [Architecture](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/architecture.md)
- [Reading and editing](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/reading.md)
- [Navigation: outlines, destinations, output intents](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/navigation.md)
- [Layout](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/layout.md)
- [Text and fonts](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/text.md)
- [Exporting PDF to Markdown](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/markdown.md)
- [Images](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/images.md)
- [Merging and splitting](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/merging.md)
- [Comments and annotations](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/annotations.md)
- [Forms](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/forms.md)
- [Signatures](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/signing.md)
- [Tagging, PDF/A and Factur-X](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/archival.md)
- [Accessibility and PDF/UA](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/accessibility.md)
- [ISO 19005 conformance matrix, parts 1–3](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/pdfa-matrix.md)
- [ISO 19005-4 conformance matrix (PDF/A-4)](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/pdfa4-matrix.md)
- [ISO 14289 conformance matrix](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/pdfua-matrix.md)
- [Reconciliation with ISO 32000-2](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/pdf20-audit.md)
- [Verification baseline and regression policy](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/verification.md)
- [Benchmarks](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/benchmarks.md)
- [Building the NuGet packages](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/packaging.md)

## Building

```bash
dotnet build Inkwright.slnx
dotnet test Inkwright.slnx
dotnet pack Inkwright.slnx -c Release --output artifacts/packages
dotnet run --project samples/Inkwright.Samples
dotnet run -c Release --project benchmarks/Inkwright.Benchmarks -- --filter *
```

## Layout of the repository

```text
src/Inkwright                  core: COS, filters, reader, writer, DOM, content, fonts, text,
                               colour, functions, image decoding, merging
src/Inkwright.Cjk              the 168 predefined CMaps of ISO 32000-2 Table 116
src/Inkwright.Layout           composer, Tagged PDF, PDF/A profiles
src/Inkwright.Forms            AcroForm: fields, appearances, flattening
src/Inkwright.Forms.Generator  Roslyn incremental generator (packed into Forms)
src/Inkwright.Signing          CMS/PAdES signatures on the base class library
tests/                         xUnit: round-trip over 7807 real PDFs, extraction checked against
                               PdfPig, layout, tagging, PDF/A, forms, signatures
benchmarks/                    BenchmarkDotNet against PDFsharp and PdfPig
samples/                       an invoice, low-level drawing, editing, a form, an archival document
```

The limits chosen on purpose are listed in
[architecture.md](https://github.com/WrightDocumentSuite/inkwright/blob/main/docs/architecture.md):
no rasterisation, no shaping of complex scripts, JPX and progressive JPEG pass through untouched,
and trust-chain policy remains the caller's responsibility.

## Built on this

`Quillwright.Pdf` prints Word documents: it does the pagination, tables, running heads and tagging
itself and writes the file through Inkwright. A useful example of what `ContentCanvas` gives you
alongside font metrics and a structure tree.

`Cellwright.Pdf` prints Excel workbooks: the styled grid with number formats, two-dimensional
pagination with scaling, repeating headers and running heads. The text layer it shares with
Quillwright — `Inkwright.Text.BidiLayout` and `ArabicShaper` — lives here: the same UAX #9, only
pointing the producer's way, logical order → drawing order.

Third-party code this project borrows from is listed in
[THIRD-PARTY-NOTICES.md](https://github.com/WrightDocumentSuite/inkwright/blob/main/THIRD-PARTY-NOTICES.md).
