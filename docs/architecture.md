# Inkwright Architecture

Inkwright is a PDF library for .NET 10, designed along the layered model of `System.Text.Json`:
low-level primitives, streaming I/O on top of them, a document model above that, and add-ons on top
that only users who need them pay for. Each layer is public and self-contained.

## Layers

```
L3  Inkwright.Layout    layout engine: Measure/Draw, text layout, tables, tags, PDF/A
    Inkwright.Forms     AcroForm: fields, appearance, flatten, [PdfForm] + source generator
    Inkwright.Signing   signatures: CMS/PAdES, RFC 3161, verification — BCL only
    Inkwright.Cjk       168 predefined CMap tables from ISO 32000-2 Table 116, in binary form
L2  DOM                 PdfDocument / PdfPage / ContentCanvas / fonts / text extraction
                        color, functions, image decoding, merging, structure tree
L1  IO                  PdfReader (lazy, recovery) / PdfWriter (forward-only) / append updates
L0  COS + filters       PdfValue, PdfName, lexer, Flate/LZW/A85/AHx/RL/Brotli, predictors, encryption
```

`Inkwright.Layout`, `Inkwright.Forms`, `Inkwright.Signing`, and `Inkwright.Cjk` are add-ons on top of L2.
The core does not reference any of them, so someone who only needs reading and writing pays nothing
for the layout engine, appearance-stream generation, cryptography, or megabytes of CJK tables.

`Inkwright.Cjk` is wired through `CMapProvider`: the core asks a registered source for predefined
CMaps and falls back to two-byte identity when no source is present. Registration is explicit —
`CjkCMaps.Register()` — because silently changing behavior from a package reference alone is worse
than one line at application startup.

## L0: COS Objects

`PdfValue` is a 24-byte tagged union: `object?` for reference types, `long` for numbers and references,
a single-byte tag. Numbers, booleans, `null`, and indirect references allocate nothing at all.
`default(PdfValue)` is a `null` object, as §7.3.9 requires.

`PdfName` is compared by value and interned: ~700 names from the specification live in a
`FrozenDictionary` and are retrieved by `ReadOnlySpan<char>` via alternate lookup; the rest are
allocated once per document.

`PdfDictionary` stores keys and values in two parallel arrays and searches linearly while there are
fewer than twelve entries — the overwhelming majority of dictionaries in real files. Insertion order
is preserved, so a rewritten file is byte-stable.

The lexer is a `ref struct` over `ReadOnlySpan<byte>`; §7.2.2 character classes are defined as
`SearchValues<byte>`, so token boundary search is vectorized. The object parser is total: any syntax
breakdown degrades to a `null` object, not an exception, and a depth limit prevents hostile nesting
from eating the stack.

### Key Decision: The Resolver Lives in the Graph

A container read from a file remembers how to resolve indirect references. The resolver propagates
down on assignment (`PdfResolverBinder`), so a dictionary built by hand and placed into a dictionary
from a file starts resolving references on its own. Without this you get a class of errors that
never surface: `GetStream(...)` silently returns `null` because the value remained a reference.

## L1: Reading

The entire file sits in memory as `ReadOnlyMemory<byte>`; objects are parsed on first access.
xref tables, xref streams, hybrid files (`/XRefStm`), object streams, and `/Prev` chains are all
understood.

Recovery is built in, not bolted on:

| Breakage | What the reader does |
| --- | --- |
| Missing or broken `startxref` | Scans the file for `N G obj` and rebuilds the table |
| Object not where indicated | Finds it by scanning and remembers the real offset |
| Offsets from header, not file start | Tries a shift to the `%PDF-` position |
| Stream `/Length` mismatch | Finds `endstream` and takes the actual length |
| No `/Root` | Finds the catalog by scanning, including object streams |
| Broken Flate stream | Returns whatever decompressed successfully |

All of this becomes `PdfWarning` with a code in `LoadDiagnostics`. `PdfFormatException` is thrown in
exactly one case: the data contains neither a header nor a single indirect object.

This tolerance is the general-purpose reading contract. The byte and file entry points of the
PDF/A validator use a protected reader instead: recovery is disabled before the root file or an
embedded child parses its cross-reference and object streams, and strict stream decoding is
installed with the validation tree's shared resource budgets. That path does not replace missing
catalogues, pages, xref entries, or object-stream slots with scan results.

The protected xref pass also journals every row by revision, including free, compressed, and future
row types. A single per-object revision index recreates first-wins lookup with a lower-bound search;
it does not retain a full object map for every historical revision. After live objects are
preflighted, shadowed normal rows are parsed by an isolated resolver for one revision group at a time.
Indirect stream `Length` references therefore cannot pick up a later override, and historical parsing
neither decrypts payloads nor mutates the live object map or cache. Provenance checks use the union of
live and historical object/payload ranges, so a reached `Prev`/`XRefStm` section cannot be hidden
inside an obsolete stream or string; only an xref stream's exact own object and payload overlap is
permitted. Each ordinary incremental body, from the preceding `%%EOF` (from the original `/L` when
appending to a linearized file, or from the oldest header line) to its primary xref, must additionally
consist only of that revision's indexed normal objects and inter-object whitespace/comments. This
catches an otherwise invisible, entirely unindexed carrier.

For files that advertise Annex F linearization, the protected reader validates its structural xref
envelope: the classic first-page/main journals and trailers, exact `H` stream ranges, original object
groups, and `O`/`E`/`N`/`P` against Part 6 and the current ordered page tree. The binary F.4 hint-table
payload remains opaque because the reader does not use range hints; this is not a claim of full Annex F
conformance. The structural check is deliberately fail-closed: linearized xref streams are rejected
until they have an equivalent exact journal, and a page-changing incremental update can invalidate
otherwise historical linearization metadata. The tolerant reader keeps its compatibility behaviour.

Protected validation separates file scope from document semantics. Base-format rules for
implementation limits, name encodings, stream dictionaries and filters, and XMP inspect every
value, dictionary, and stream rooted at a live xref object, including objects not reachable from the
catalogue. Graphics, fonts, actions, annotations, and other use-dependent rules remain
catalogue-reachable: an unused object is not silently treated as page content. In Parts 1–3,
attachment discovery is likewise file-scoped and includes specifications rooted at every live xref
object; Part 4 keeps its normative `Catalog/Names/EmbeddedFiles` condition. Every live stream except
a recognised `EF`/`RF` attachment payload is decoded once in a strict preflight; attachment payloads
keep their dedicated decode and recursive-validation reservations.

The stream expansion limit (`PdfFilterLimits`, 128 MB by default) closes zip bombs: a few kilobytes
of deflate can claim a terabyte.

## L1: Writing

`PdfWriter` is forward-only: an object is serialized to the stream immediately; only the offset table
remains in memory. Numbers are written directly to UTF-8 via `IUtf8SpanFormattable`, keywords as u8
literals, strings go in literal form while they stay readable and in hexadecimal once they stop.

Small objects are packed into object streams; the cross-reference table is written as an xref stream.
Incremental save (`SaveIncremental`) appends a section to the end of the file and chains it to the
previous one via `/Prev`; original bytes are not shifted by a single byte — otherwise signatures break.

## L2: Document Model

`PdfDocument` is a model, not a handle: loading reads the package; there are no open handles afterward.

**Preservation of the unparsed.** An object the model never parsed is copied byte-for-byte on rewrite,
including the header and `endobj`. This is more reliable than tracking "dirty" objects: if nobody
parsed an object, nobody could have changed it.

**Numbering.** Object numbers are preserved on rewrite. New objects start above not only the maximum
existing number but also above `/Size` from the trailer: a dangling reference into a reserved range
must stay dangling; otherwise an object we put there silently becomes what that reference pointed to.

**The page tree** is flattened into a flat list; inherited attributes (`Resources`, `MediaBox`,
`CropBox`, `Rotate`) are pushed down into each page on load, so the page dictionary is self-contained
afterward. Saving writes a single `/Pages` node with all `/Kids`.

**Content** is parsed data-oriented: one flat operand array plus a list of triples
`(operator, offset, count)`. No object is allocated per operator, so a page with a hundred thousand
path segments stays cheap.

**Annotations** are a typed facade over the dictionary, not a replacement: everything the model knows
lives in the same `/Annots`, and everything it does not know is available through `annotation.Dictionary`
and survives rewrite. The library draws appearance itself because viewers render comments their own way
or not at all — but a foreign appearance stream is not touched when editing text: a comment is no
reason to discard an image we cannot reproduce. Details in [annotations.md](annotations.md).

## .NET 10 / C# 14 Features Used

| Feature | Where |
| --- | --- |
| Extension members (C# 14) | Fluent layout `Container`, `TextStyle` styles, form builder |
| `field` keyword | Lazy properties `Fonts`, `Attachments` in `PdfDocument` |
| `params ReadOnlySpan<T>` | `PdfArray.OfNumbers`, `ContentCanvas.Dash`, `TableDescriptor.Columns` |
| u8 literals + `IUtf8SpanFormattable` | `Utf8Writer`, object serialization, lexer |
| `SearchValues<byte>` | §7.2.2 character classes, token parsing |
| `FrozenDictionary` + alternate lookup | Name interning, content operator table |
| `Vector128` | PNG "Up" predictor reversal |
| ref struct + `scoped` | Lexer and object parser |
| static abstract members | `IPdfFormSchema<T>` |
| Incremental source generator | `Inkwright.Forms.Generator` |
| `AsnWriter` / `SignedCms` / `Rfc3161TimestampRequest` | Signatures entirely on BCL |
| `IsAotCompatible` | All four runtime packages |

## What Compliance Is Based On

- PDF 1.7 — ISO 32000-1:2008.
- PDF 2.0 — ISO 32000-2:2020, second edition, with Errata Collection 3. Compliance previously
  tracked the 2017 ISO/DIS draft; what changed and what follows from it is in [pdf20-audit.md](pdf20-audit.md).
- PDF/A — ISO 19005-1, -2, and -3 with corrigenda.
- XFDF — ISO 19444-1:2016.
- Fax encodings — ITU-T T.4 and T.6; JBIG2 — ITU-T T.88, also ISO/IEC 14492:2019.
- Fonts — Adobe Technical Note #5176 (CFF) and #5177 (Type 2 Charstring).
- PDF 2.0 extensions — ISO/TS 32001 (SHA-3), 32002 (Edwards curves), 32003 (AES-GCM),
  32004 (integrity), 32005 (structure namespaces).
- Conformance declarations — PDF Association, PDF Declarations; Well-Tagged PDF 1.0.

## Deliberate Limitations

- No rasterization: the library reads and writes PDF but does not turn it into an image. Individual
  images decode to pixels (see [images.md](images.md)), but a full page does not.
- Arabic and Indic script shaping goes through an external engine; built-in layout handles wrapping,
  metric kerning, and justification, but not glyph reordering. Bidirectional *order* is restored on
  text extraction, but Arabic letter form joining on drawing is not.
- JPX (JPEG 2000) and progressive JPEG are not decoded: images in those codecs pass through on edit
  and are returned as-is on extraction. Failure is explicit, not a silently wrong result.
- Cubic interpolation of sampled functions (`/Order 3`) is implemented as a tensor product of
  Catmull–Rom splines across all input axes. If any axis has fewer than four samples, `/Order 3` is
  ignored, as §7.10.2 requires.
- Shadings are read as objects but not evaluated to pixels; the functions they are built on are
  evaluated fully.
- CFF subsetting drops outlines of unused glyphs and unreachable subroutines while preserving the
  numbering of both — including local subroutines of each CID-keyed font dictionary: FDSelect maps a
  glyph to a dictionary, and each dictionary's subroutines are trimmed to what its glyphs need.
- Encryption is read for RC4, AES-128, AES-256, and AES-GCM; it is written as AES-256 in cipher block
  chaining mode (revision 6) or Galois counter mode (revision 7, ISO/TS 32003). Certificate encryption
  (§7.6.5, `EnvelopedCms`) is read and written — 256-bit AES with crypt filters. The reader
  distinguishes `/StmF`, `/StrF`, `/EFF`, and explicit stream `/Crypt /Name`; `/Identity` is not run
  through document encryption.
- Signatures: SHA-2 always; SHA-3 and SHAKE256 (ISO/TS 32001) when the system crypto library provides
  them. Edwards curves (ISO/TS 32002) are signed only through an external signer (there is no Ed25519/Ed448
  private-key primitive in .NET), but are verified with built-in arithmetic. PAdES B-LTA building blocks
  are present: document timestamp (§12.8.5), validation data storage (§12.8.4), DocMDP certification
  signatures and FieldMDP field lock (§12.8.2). The library does not fetch OCSP/CRL — it accepts
  ready-made blobs pre-validated by the application, because trust policy is not the format's job.
  The verifier checks cryptographic integrity and TSA tokens, but not chain trust or whether subsequent
  changes are allowed under DocMDP.
- Integrity protection (ISO/TS 32004) is read, verified, and written — as a separate token in the
  trailer or a signature attribute. Salt is derived once at encryption and cannot be added later.
- JBIG2 is fully decoded except for extended templates with twelve adaptive pixels. Symbol
  refinement aggregates are supported in both arithmetic and Huffman forms, and retained
  symbol-dictionary coding contexts use snapshot isolation. Page-information-only streams produce
  their default-pixel page; unknown optional extension segments are skipped, while unknown
  necessary extensions are rejected rather than silently changing the intended bitmap.
- Linearization is read but not written.
- PDF/A and PDF/UA are checked for what can be checked from the file. Everything that requires human
  judgment — tag appropriateness, reading order, meaningful alternative text — is not checked and
  cannot be. Full PDF/A validation is done by veraPDF; see [accessibility.md](accessibility.md).
