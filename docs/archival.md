# Tagging, PDF/A, and Hybrid Invoices

## Tagged PDF

The logical structure tree is built during rendering: only then is the order known in which
content is placed on the page, and no subsequent parsing can recover it.

```csharp
Report.Create()
    .Page(page =>
    {
        page.Tagged("ru-RU");
        page.Content().Column(column =>
        {
            column.Item().Text("Heading", TextStyle.Default.FontSize(18).Bold());
            column.Item().Text("Paragraph of text.");
            column.Item().Table(table => { … });
        });
    })
    .Render(document);
```

Text is marked up as `P`, images as `Figure` with alternative text, tables as
`Table` / `TR` / `TH` / `TD`. Content is bound to the tree via `BDC`/`EMC` with `MCID`,
pages receive `/StructParents`, the catalog receives `/StructTreeRoot`, `/MarkInfo`, and `/Lang`.

The paragraph tag is configurable: `element.SemanticTag = "H1"`. For custom markup there are
`TaggingSession.Group` and `TaggingSession.Marked`, and decorations that should not be read aloud
are wrapped in `TaggingSession.Artifact`.

The tree can also be built without the layout engine — `Inkwright.Tagging.StructureTreeBuilder` works on top of
any document. `StructureElement.AddAnnotation` represents a new annotation with `OBJR`, `StructParent`, and
the reverse `ParentTree` entry; `StructureTreeBuilder.AddAnnotation(PdfStructureNode, PdfAnnotation)` appends
one to a semantic element in a tree already produced by layout. Both paths retain existing parent-tree entries
and set page tab order to `S`, as required by PDF/UA-1 §7.18. During new-tree authoring, calls to `AddContent`,
`AddAnnotation`, and `Add` are written to `K` in their exact call order, including mixed MCID, OBJR, and child
structure-element entries.

## PDF/A

```csharp
using Inkwright.Layout;

PdfAProfile.Apply(document, PdfAConformance.A2B, new PdfAOptions { Language = "ru-RU" });
document.Save("archival.pdf");
```

Part 1–3 levels: `A1B`, `A1A`, `A2B`, `A2U`, `A2A`, `A3B`, `A3U`, `A3A` — three parts of ISO 19005 and three
conformance levels. Part 1 is written against PDF 1.4, so claiming it pins the version to 1.4, and
the writer stops using object streams and cross-reference streams that did not exist in 1.4 yet;
this also applies to `SaveIncremental` — the appended section of a 1.4 file remains a classic
`xref` table. Level A requires a structure tree — for that the page must be tagged via
`page.Tagged(...)`.

### PDF/A-4: A Different Design, Not Another Level

`A4`, `A4E`, `A4F` — Part 4 (ISO 19005-4:2020), the only one written against PDF 2.0.

```csharp
PdfAProfile.Apply(document, PdfAConformance.A4, new PdfAOptions { Language = "ru-RU" });
```

What is different here:

- **There are no A, U, and B levels.** There is base `A4` and two extensions — `A4E` (3D graphics, Annex B)
  and `A4F` (embedded files of any type, Annex A). They do not grade strictness; they add
  permissions: what is forbidden in `A4` becomes allowed in them.
- **Header `%PDF-2.n`** (§6.1.2). Parts 1–3 require `%PDF-1.n`, so **no file can claim Part 4 and any
  earlier part at the same time** — these are mutually exclusive requirements for the first
  eight bytes.
- **There will be no document information dictionary** (§6.1.3): PDF 2.0 declared it deprecated, Part 4
  made that normative. The profile removes it and stops stamping `Producer` there on every
  save; title, author, and dates live in the XMP packet, where `Apply` moves them. One
  exception — a document with `PieceInfo` in the catalog, where the dictionary is allowed for `ModDate`.
- **A structure tree is not required at any level** (§6.8 only recommends it and refers to
  ISO 14289), nor is reverse text extraction required: `ToUnicode` in §6.2.10.7 is formulated with "should".
  Tagging the document is still worthwhile — it is just no longer a condition of an archival claim.
- **JavaScript is allowed in the file** (§6.6.2): the restriction moved to the handler, which may
  execute an action only on explicit human request.
- **The claim looks different** (§6.7.3): `pdfaid:part` equals 4, mandatory `pdfaid:rev` is added with a
  four-digit revision year, and `pdfaid:conformance` is written **only** for `A4E` and `A4F`.
  Base `A4` must not have this property, and the validator will report that.

The profile adds what the standard requires: output intent with sRGB ICC profile, file identifier,
XMP metadata (for parts 1–3, kept consistent with the information dictionary), and `pdfaid:part` and
`pdfaid:conformance` claims. The ICC profile is generated, not stored as a binary blob: sRGB is a few
hundred bytes of a well-described structure, and writing it in code is more honest than hiding it in resources.

The claim is remembered in the document: attempting to save it with encryption after that is rejected, because
encryption is forbidden by all four parts — Part 4 does this by forbidding the `Encrypt` key
in the trailer (§6.1.3). This also applies to loaded files — their claim is
read from `pdfaid` in the XMP packet, so encrypting someone else's PDF/A without realizing its status
will not work.

Saving does not desynchronize metadata. When an information dictionary is present (parts 1–3), each
`Save` stamps the modification date and producer name into it — and immediately updates the same values in the XMP packet (`xmp:ModifyDate`,
`xmp:MetadataDate`, `pdf:Producer`) if the packet contains them. Otherwise an archival file would stop
conforming to Part 1 (§6.7.3, DocInfo and XMP agreement) on any re-save.

## Validation

`Apply` validates the document and refuses to place a claim the document cannot support:

```
The document does not satisfy PDF/A-2b:
  6.2.11.4.1: The font 'Helvetica' is not embedded. PDF/A has no built-in fonts, so even
  Helvetica has to travel with the file.
```

The same validation can be run separately — on the model, on the bytes, or on the file:

```csharp
foreach (PdfAProblem problem in PdfAProfile.ValidateFile("report.pdf", PdfAConformance.A2U))
    Console.WriteLine($"{problem.Clause}: {problem.Message}");
```

Each finding is named by the standard clause it violates — so it can be found in the text and
matched with a third-party validator's verdict.

What is checked for each clause is listed in the conformance matrices — separately for
[parts 1–3](pdfa-matrix.md) and for [part 4](pdfa4-matrix.md), because the requirements diverge
from the first bytes of the file. In short: file structure and byte syntax, filters,
implementation limits, output intent and ICC profile, device-dependent color against the profile, graphics
state, transparency per Annex A, fonts (embedding, `CIDSet`, widths against the font program,
TrueType encodings, `ToUnicode`, `.notdef`), annotations, forms, actions, XMP and extension schemas,
structure tree for level A, attachments, optional content, and Annex B signature requirements.

Validated against 106 archival files from the iText test set that claim parts 1–3: all are
recognized as conforming to their claim. CI checks one sample per claim with veraPDF, which shares
no code with this library; `.github/scripts/verapdf-known-failures.txt` lists any rule accepted as
failing, with the reason, and a listed rule that starts passing fails the job. Full formal validation is still done by veraPDF —
some requirements (for example, legality of font embedding or conformance of an embedded PDF)
cannot in principle be inferred from the file.

The gate pins veraPDF 1.30.3 and the installer's SHA-256. Run it locally after generating samples:

```powershell
dotnet run --project .github/verapdf-samples/VeraPdfSamples.csproj -c Release -- artifacts/verapdf/samples
./.github/scripts/Test-VeraPdf.ps1 -SamplesDirectory artifacts/verapdf/samples -WorkDirectory artifacts/verapdf/tool
```

Java and an embeddable Arial or DejaVu Sans font are required. All eleven samples must be present;
an execution error, missing report or unexplained noncompliant result fails the gate. XML reports
are saved under the work directory's `reports/` and uploaded by CI even after a failure. Validation
on 2026-11-09 exposed a CIDSet error: preserving TrueType glyph indices leaves empty glyphs in the
program, so its CIDSet must include those indices as well as glyphs with outlines. The regression
test compares the declaration against the embedded program's glyph count.

Attachments are allowed only in Part 3: Part 2 permits embedded files only if they themselves
conform to PDF/A, so the profile rejects an XML invoice alongside a Part 2 claim.

## Factur-X and ZUGFeRD

A hybrid invoice is a page for humans and XML for machines in one file:

```csharp
document.Attachments.AddFacturX(invoiceXml);                       // factur-x.xml, EN 16931 profile
document.Attachments.AddFacturX(invoiceXml, "zugferd-invoice.xml", "EXTENDED");

PdfAProfile.Apply(document, PdfAConformance.A3B);
```

The attachment receives `/AFRelationship /Data`, enters the `/EmbeddedFiles` name tree and the catalog's
`/AF` array, and the XMP adds an `fx:` schema with file name, version, and conformance level —
exactly what an invoice processor uses to find the data.

Attachments are read just as simply:

```csharp
foreach (string name in document.Attachments.Names)
    Console.WriteLine(name);

byte[]? xml = document.Attachments.Read("factur-x.xml");
```

Any file can be attached without Factur-X:

```csharp
document.Attachments.Add("source.csv", bytes, "text/csv", "Source data", AttachmentRelationship.Source);
document.Attachments.AddFile(@"C:\data\report.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
```
