# ISO 19005-4 (PDF/A-4) Conformance Matrix

Line-by-line verification of the library against ISO 19005-4:2020 — the first PDF/A part written against PDF 2.0. Source: full text in `E:\Docs\ISO_19005-4_2020`, plus approved ISO errata from `E:\Docs\PDF\pdf-issues-main/docs/19005-4-2020`.

Parts 1–3 live in a separate [matrix](pdfa-matrix.md): they are incompatible with Part 4 in construction — those require a `%PDF-1.n` header; this requires `%PDF-2.n`.

## How Part 4 differs from earlier parts

Not "another level", but a different construction:

- **Levels A, U, and B are gone.** There is base PDF/A-4 and two extensions: **PDF/A-4e** (Annex B, 3D graphics) and **PDF/A-4f** (Annex A, embedded files of any type). They do not grade strictness; they add permissions.
- **A structure tree is not required at any level.** §6.8 only recommends it and refers to ISO 14289. The "text must be extractable" requirement carried by Levels U and A is also removed: §6.2.10.7 formulates `ToUnicode` as "should".
- **The document information dictionary is effectively removed** (§6.1.3): PDF 2.0 declared it deprecated, and Part 4 makes that normative. It is allowed only alongside `PieceInfo` and only with `ModDate`.
- **JavaScript is allowed in the file** (§6.6.2): the restriction moved to the handler, which may execute such an action only on explicit human request. Parts 1–3 forbid it in the file.
- **Transparency and JPEG 2000 are allowed**, as in Part 2.

The legend is the same as in the Parts 1–3 matrix:

| Symbol | Meaning |
| --- | --- |
| G | Generator guarantees: what the library writes does not violate the requirement |
| V | Validator checks: a violation in a third-party or edited file will be found |
| GV | Both |
| — | Not checked; the row explains why |
| n/a | Not applicable to what the library can create |

## 6.1 File structure

| Clause | Requirement | Status | How |
| --- | --- | --- | --- |
| 6.1.2 | `%PDF-2.n` from byte 0, followed by a comment of ≥4 bytes all greater than 127 | GV | `PdfAProfile` sets version 2.0; the validator reads file bytes and distinguishes 1.n from 2.n |
| 6.1.3 | No data after the final `%%EOF` | GV | shared check with Parts 1–3 |
| 6.1.3 | `Encrypt` in the trailer is forbidden | GV | encryption is rejected when archival claim is made |
| 6.1.3 | `Info` forbidden without `PieceInfo`; with it — only `ModDate` | GV | the generator does not write the dictionary or stamp `Producer` into it; the validator distinguishes a file that omits `Info` from in-memory model artifacts |
| 6.1.4 | `xref` and subsection header separated by one EOL | GV | shared check |
| 6.1.5 | Even number of digits in hexadecimal strings | — | the writer always writes an even count; reading third-party files is not checked |
| 6.1.6.1 | Stream dictionary has no `F`, `FFilter`, `FDecodeParams` | GV | shared check |
| 6.1.6.2 | `LZWDecode` forbidden; `Crypt` only with `Identity` | GV | shared check |
| 6.1.7 | Names are valid UTF-8 after `#` unescaping | — | the generator writes ASCII names only |
| 6.1.8 | Delimiters around `obj` and `endobj` | G | the writer always does this |
| 6.1.9 | Embedded images have no `LZW`, `Crypt`, or unknown filters | V | content stream parsing |
| 6.1.10 | Linearization is allowed | n/a | the library does not linearize |
| 6.1.11 | Permissions dictionary contains only `UR3` and `DocMDP` | V | shared check |
| 6.1.12 | Catalog `Version` is exactly three characters of the form `2.n` | GV | value is judged from the model: save will overwrite it with the document version anyway |

## 6.2 Graphics

| Clause | Requirement | Status | How |
| --- | --- | --- | --- |
| 6.2.2 | Only operators defined in ISO 32000-2 | V | shared check |
| 6.2.3 | Output intent with `S = GTS_PDFA1` and valid `DestOutputProfile` | GV | generated sRGB profile; `DestOutputProfileRef` forbidden |
| 6.2.3 | Page-level output intent when there is no document-level one | V | `PdfPage.AddOutputIntent` can write it |
| 6.2.4 | Color specified device-independently | V | shared check, including `DeviceCMYK` behind an RGB profile |
| 6.2.5 | No `TR`, `HTO`; `TR2` only `Default`; `HalftoneType` 1 or 5 | V | shared check |
| 6.2.7.1 | No `Alternates`, `OPI`; `Interpolate` is false | V | shared check |
| 6.2.7.3 | JPEG 2000 restrictions: 1/3/4 channels, bit depth 1–38, not `CIEJab` | — | JPX passes through; the library does not parse it |
| 6.2.8.2 | Reference XObjects forbidden | V | `Ref` key in form stream |
| 6.2.9 | Transparency allowed; blending space declared explicitly | V | output intent or page `Group`/`CS` required |
| 6.2.10.4 | All used fonts are embedded | GV | shared check |
| 6.2.10.5 | Widths in dictionary and font program match | V | shared check |
| 6.2.10.6 | TrueType encodings: `cmap`, `MacRoman`/`WinAnsi`, `Differences` | V | shared check |
| 6.2.10.3.1 | Font `Supplement` ≥ CMap `Supplement` | — | see "Errata" below: comparison direction is disputed and not ISO-approved |
| 6.2.10.9 | No references to the `.notdef` glyph | — | requires parsing all show strings with encoding resolution |

## 6.3–6.6 Annotations, forms, signatures, actions

| Clause | Requirement | Status | How |
| --- | --- | --- | --- |
| 6.3.1 | `Sound`, `Screen`, `Movie` always forbidden | V | shared check |
| 6.3.1 | `3D` and `RichMedia` — only in PDF/A-4e | V | level decides, not part |
| 6.3.1 | `FileAttachment` — only in PDF/A-4f | V | shared check |
| 6.3.2 | Annotations have `F`; `Print` set, `Hidden`/`NoView` cleared | V | shared check |
| 6.3.3 | `AP` contains only key `N` | V | shared check |
| 6.4.1 | Widget has no `A` key; `NeedAppearances` false or absent | V | shared check |
| 6.4.2 | No `XFA`, no `NeedsRendering` | V | shared check |
| 6.5 | Signatures per PAdES profiles from ISO 32000-2 or ISO 14533-3 | — | `Inkwright.Signing` provides B-B…B-LTA blocks; conformance to ISO 14533-3 profile is not checked |
| 6.6.1 | `Launch`, `Sound`, `Movie`, `ResetForm`, `ImportData`, `Hide`, `Rendition`, `Trans` forbidden | V | shared check |
| 6.6.1 | `SetOCGState` and `GoTo3DView` — only in PDF/A-4e | V | level decides, not part |
| 6.6.1 | Named actions — only four navigational ones | V | shared check |
| 6.6.2 | JavaScript allowed in the file | V | Part 4 does not report it as a violation; Parts 1–3 do |
| 6.6.3 | Catalog and page `AA` — only `E`, `X`, `D`, `U`, `Fo`, `Bl` | — | not checked |

## 6.7 Metadata

| Clause | Requirement | Status | How |
| --- | --- | --- | --- |
| 6.7.2.1 | Catalog has `Metadata`; XMP is valid | GV | shared check |
| 6.7.2.1 | XMP packet header has no `bytes` or `encoding` | GV | shared check |
| 6.7.3 | `pdfaid:part` equals 4 | GV | |
| 6.7.3 | `pdfaid:rev` required, four-digit year | GV | the generator writes `2020` |
| 6.7.3 | `pdfaid:conformance` only `E` or `F`, and only at those levels | GV | base PDF/A-4 must omit the property, and this is checked |
| 6.7.2.3 | Metadata stream has an associated file with `AFRelationship = Schema` | — | the standard says "should" |
| 6.7.5 | `xmpMM:History` describes processing history | — | "should"; the library does not maintain history |

## 6.9–6.15 Other

| Clause | Requirement | Status | How |
| --- | --- | --- | --- |
| 6.9 | Attachments have `AFRelationship`, `F`, and `UF` | V | shared check with Part 3 |
| 6.9 | Base PDF/A-4 attachments must themselves conform to PDF/A | V | by MIME type: non-PDF is rejected with advice to use PDF/A-4f. Substantive conformance of embedded PDF is not checked — recursive validation is needed |
| 6.10 | Each layer configuration has a unique `Name` | V | shared check; in Part 4 clause 6.10, in Parts 2–3 the same requirement is 6.9 |
| 6.10 | Configuration has no `AS`; `Order` lists all groups | V | shared check |
| 6.11 | No `AlternatePresentations`, no `PresSteps` | V | shared check |
| 6.12 | Catalog has no `Requirements` | V | shared check |
| 6.13 | Handler respects `PrintScaling` | n/a | requirement on the handler, not the file |
| A.2 | PDF/A-4f has an embedded file | GV | a 4f claim without an attachment is rejected: not a file defect, but a wrong level choice |
| A.2 / B.4 | PDF/A-4f and PDF/A-4e attachments may be of any type | V | MIME restriction is lifted specifically by these levels |

## Errata

Approved ISO errata from PDF Association are accounted for:

- **#123 (approved).** Table 2 was printed with a typo: the level property is named `pdfa:conformance`, whereas in practice it is **`pdfaid:conformance`**. The same correction restores the namespace URI form `http://www.aiim.org/pdfa/ns/id/` instead of the printed `https://…` and allows readers to accept both. The library writes and reads `http`, as for Parts 1–3.
- **#104 (approved).** Editorial renumbering of notes in §6.2.9 and reference to Annex Q of ISO 32000-2. No impact on code.
- **#77 (proposed, not ISO-approved).** Proposes replacing "greater than or equal" with "less than or equal" in the §6.2.10.3.1 `Supplement` comparison. The direction is opposite to what was printed, and the same wording in WTPDF 1.0 §8.4.5.3.1 matches the current standard text, not the erratum. Until ISO decides, the library does not check this comparison at all — a rule that may end up reversed is worse than none.

The errata index warns that a dated revision of PDF/A-4 is being prepared that will incorporate all published decisions and align wording with PDF/X-6. When it appears, `pdfaid:rev` will need to change from `2020` to the revision year — the only place in code it will affect (`PdfAProfile.PartFourRevision`).

## What this matrix does not promise

Same as the Parts 1–3 matrix: conformance is a property of the file as a whole, and the validator checks what can be inferred from bytes and the object graph. Rows marked "—" are listed honestly and mean exactly one thing: the library is silent about that requirement, not that it confirms it.
