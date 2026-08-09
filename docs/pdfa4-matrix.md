# ISO 19005-4 (PDF/A-4) Conformance Matrix

Line-by-line verification of the library against ISO 19005-4:2020 — the first PDF/A part written against PDF 2.0. The review uses a lawfully obtained copy of the standard plus the approved ISO errata; those external normative materials are not redistributed in this repository.

Parts 1–3 live in a separate [matrix](pdfa-matrix.md): they are incompatible with Part 4 in construction — those require a `%PDF-1.n` header; this requires `%PDF-2.n`.

## How Part 4 differs from earlier parts

Not "another level", but a different construction:

- **Levels A, U, and B are gone.** There is base PDF/A-4 and two extensions: **PDF/A-4e** (Annex B, engineering/3D content and embedded files of any type) and **PDF/A-4f** (Annex A, embedded files of any type). They do not grade strictness; they add permissions.
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
| 6.1.3 / ISO 32000-2 7.5.5 | Trailer `ID` resolves to two byte strings of at least 16 bytes | GV | the generator writes 16-byte identifiers; the validator checks count, type, and length while accepting indirect storage |
| 6.1.4 | `xref` and subsection header separated by one EOL | GV | shared check |
| 6.1.5 | Even number of digits in hexadecimal strings | GV | the writer serializes byte pairs; the validator scans the original object bytes |
| 6.1.6 | Every stream is an indirect object | GV | generated streams are added to the object table; validation distinguishes indirect stream dictionaries from streams nested directly inside another object |
| 6.1.6.1 | Stream dictionary has no `F`, `FFilter`, `FDecodeParms` | GV | shared file-scope check covers every stream in a live xref object |
| 6.1.6.2 | `LZWDecode` forbidden; `Crypt` only with `Identity` and only as the first filter | GV | shared file-scope check uses canonical full filter names and `DecodeParms` on every stream in a live xref object |
| 6.1.7 | Font, Separation/DeviceN colourant, and structure type names are valid UTF-8 after `#` unescaping | GV | the validator checks decoded raw name bytes in every live xref object; the writer emits UTF-8 bytes with `#xx` escaping |
| 6.1.8 | Single header separators and all four EOL boundaries around `obj` and `endobj` | GV | checked against the original indirect-object byte ranges |
| 6.1.9 | Inline images use only the full or abbreviated filters in ISO 32000-2 Table 92; no `LZW`, `Crypt`, `JBIG2Decode`, `JPXDecode`, or unknown filters | V | content stream parsing expands abbreviations and validates every filter in a chain |
| 6.1.10 | Linearization is allowed | n/a | the library does not linearize |
| 6.1.11 | Permissions dictionary contains only `UR3` and `DocMDP` | V | the Part 4 check does not import the removed Part 2/3 restriction on legacy digest keys in signature-reference dictionaries |
| 6.1.12 | Catalog `Version` is exactly three characters of the form `2.n` | GV | value is judged from the model: save will overwrite it with the document version anyway |

Validation deliberately keeps file scope and use-dependent semantics separate. Every live indirect
object advertised by the cross-reference data is parsed for base name encodings, stream dictionaries
and filters, and Metadata/XMP. Graphics, fonts, actions, annotations, and content semantics still
start at the catalogue, so an unused resource is not treated as if a page invoked it. The protected
byte/file reader runs with recovery disabled and preflights every xref-live non-attachment stream
before semantic rules consume it.

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
| 6.2.10.3.3 | Embedded CMap `usecmap` operators and dictionary `UseCMap` entries refer only to predefined CMaps | V | every parsed operand and the resolved stream-dictionary value are checked; a predefined name in a comment or another expression does not count |
| 6.2.10.3.1 | Font `Supplement` ≥ CMap `Supplement` | — | see "Errata" below: comparison direction is disputed and not ISO-approved |
| 6.2.10.9 | No references to the `.notdef` glyph | V | the current content parser detects zero CIDs under `Identity-H`/`Identity-V`; non-Identity encoding-to-glyph resolution is not yet performed |

## 6.3–6.6 Annotations, forms, signatures, actions

| Clause | Requirement | Status | How |
| --- | --- | --- | --- |
| 6.3.1 | `Sound`, `Screen`, `Movie` always forbidden | V | shared check |
| 6.3.1 | `3D` and `RichMedia` — only in PDF/A-4e | V | level decides, not part |
| 6.3.1 | `FileAttachment` — only in PDF/A-4f | V | shared check |
| 6.3.2 | Annotations have `F`; `Print` set, `Hidden`/`NoView` cleared | V | shared check |
| 6.3.3 | `AP` contains only key `N` | V | shared check |
| 6.4.1 | Widget and field dictionaries have no `A` key but may have `AA`; `NeedAppearances` false or absent | V | the field tree and page annotations are checked without applying the older-parts ban on `AA`; their trigger keys are limited to the union defined by ISO 32000-2 Tables 197–200 |
| 6.4.2 | No `XFA`, no `NeedsRendering` | V | shared check |
| 6.5 | Signatures per PAdES profiles from ISO 32000-2 or ISO 14533-3 | — | `Inkwright.Signing` provides B-B…B-LTA blocks; conformance to ISO 14533-3 profile is not checked, and the obsolete Annex B rules from Parts 2/3 are not applied to Part 4 |
| 6.6.1 | `Launch`, `Sound`, `Movie`, `ResetForm`, `ImportData`, `Hide`, `Rendition`, `Trans` forbidden | V | shared check |
| 6.6.1 | `SetOCGState` and `GoTo3DView` — only in PDF/A-4e | V | level decides, not part |
| 6.6.1 | Named actions — only four navigational ones | V | shared check |
| 6.6.2 | JavaScript allowed in the file | V | Part 4 does not report it as a violation; Parts 1–3 do |
| 6.6.3 | Catalog, page, and non-Widget annotation `AA` — only `E`, `X`, `D`, `U`, `Fo`, `Bl` | V | the additional-actions dictionary is resolved, every trigger key is allowlisted, and every value must resolve to an action dictionary |

## 6.7 Metadata

| Clause | Requirement | Status | How |
| --- | --- | --- | --- |
| 6.7.2.1 | Catalog has `Metadata`; XMP is valid | GV | shared check detects the XML encoding from its BOM/declaration, bounds XML, prohibits DTDs, and reuses one parsed packet for identification and metadata rules |
| 6.7.2.1 | Every file-scope Metadata stream has exact `Type Metadata`, `Subtype XML`, and valid XMP/RDF | V | Metadata entries and explicitly typed Metadata streams in live xref objects are deduplicated and checked, including packets outside the catalogue graph |
| 6.7.2.1 | XMP packet header has no `bytes` or `encoding` | GV | shared check |
| 6.7.3 | `pdfaid:part` equals 4 | GV | |
| 6.7.3 | `pdfaid:rev` required, four-digit year | GV | the generator writes `2020` |
| 6.7.3 | `pdfaid:conformance` only `E` or `F`, and only at those levels | GV | base PDF/A-4 must omit the property, and this is checked |
| 6.7.2.3 | Metadata stream has an associated file with `AFRelationship = Schema` | — | the standard says "should" |
| 6.7.5 | `xmpMM:History` describes processing history | — | "should"; the library does not maintain history |

## 6.9–6.15 Other

| Clause | Requirement | Status | How |
| --- | --- | --- | --- |
| 6.9 | Entries in the catalog `EmbeddedFiles` name tree are indirect `/Type /Filespec` dictionaries with `AFRelationship` and string `F`/`UF`; an embedded stream's optional `Type` is `/EmbeddedFile` | V | the name tree must have an exact `Names`/`Kids` shape, strictly ordered string keys and child ranges, and matching descendant `Limits`; malformed specifications and payload types fail closed, while an `AF`-only file specification remains outside this conditional clause |
| 6.9 | `EF` and `RF` use the canonical `F`, `UF`, `DOS`, `Mac`, and `Unix` keys | V | unknown aliases are rejected; every `EF` key has its matching file-specification string, every `RF` key also exists in `EF` and contains string/stream pairs, and payload streams are deduplicated by identity before validation |
| 6.9 | Base PDF/A-4 attachments conform to PDF/A-1, PDF/A-2, or PDF/A-4; Annex A/B exempt PDF/A-4f and PDF/A-4e from the file-type restriction | V | decoded base-profile attachments are opened separately; the exact `pdfaid:part`, `pdfaid:conformance`, and supported Part 4 revision select the child profile, then every validator rule runs recursively and nested failures retain the complete attachment path |
| 6.10 | Each layer configuration has a unique `Name` | V | shared check; in Part 4 clause 6.10, in Parts 2–3 the same requirement is 6.9 |
| 6.10 | `Order` lists all groups; `AS` may appear but processors ignore it | V | nested `Order` arrays are compared with the OCG set by object/reference identity; label strings and duplicate references are not counted as groups |
| 6.11 | No `AlternatePresentations`, no `PresSteps` | V | shared check |
| 6.12 | Catalog has no `Requirements` | V | shared check |
| 6.13 | Handler respects `PrintScaling` | n/a | requirement on the handler, not the file |
| A.2 | PDF/A-4f catalog name dictionary contains `EmbeddedFiles` | GV | the key itself is required; an `AF` array elsewhere cannot substitute for it, while a branched name tree with no root `Names` array is accepted |
| A.2 / B.4 | PDF/A-4f and PDF/A-4e attachments may be of any type | V | the embedded-file conformance restriction is lifted specifically by these levels |

The default recursive-validation budget is 16 nested PDF levels and 128 embedded PDFs. The root and
all recursively opened children share three separate 128 MiB byte ledgers: encoded input for
attachment payloads and reader-created child streams, cumulative bytes materialized by filter and
predictor stages, and final decoded child bytes accepted for recursion. The byte/file entry point
installs a strict policy before the root reader loads, and every embedded reader inherits it with
recovery disabled, so cross-reference streams, object streams, and XMP cannot bypass the ledgers.
Every xref-live stream that is not a recognised `EF`/`RF` attachment payload is preflighted;
recognised payloads retain their own bounded decode and, where required, recursive reservation.
Strict decoding uses only the canonical stream `Filter` and `DecodeParms` entries: inline-image
abbreviations and external-file aliases cannot stand in for them. A terminal image codec remains
encoded in that codec after all preceding filters have been checked. `/Params/Size` remains an
advisory preflight value.

The ledgers stop stage output at their bounds; they are work/input/output counters, not an exact
process-memory/RSS ceiling. Bounded decoder buffers and returned arrays add copy overhead, while the
parser/object cache, graph lists and identity sets allocate up to their value caps; bounded XMP also
holds an `XDocument` and canonical text (up to 16 Mi characters), and reported problems allocate
strings. The strict PDF/A paths reject malformed encodings, trailing data, unsupported filters and
invalid filter ordering fail-closed; ordinary public decoder APIs remain compatibility-oriented and
tolerant. A stream chain is limited to 64 filters. `EmbeddedFiles` discovery has separate default
bounds: 64 name-tree levels, 4,096 name-tree nodes or entries, and 1,024 unique file specifications.
The shared graph budget is 1,048,576 values across both the catalogue-reachable graph and values
rooted at all xref-live objects. Protected readers have separate shared caps of 1,048,576 parsed
values and 1,048,576 xref/object-stream structural entries; all
declared xref rows, including free and future entry types, consume that structural budget. Recovery
scans for catalogs, pages, xref entries, and object-stream slots are disabled. Active-path SHA-256
fingerprints stop content cycles; malformed discovery and reached limits are reported fail-closed
with the attachment path instead of throwing or silently accepting a truncated traversal.

XMP has its own exact shared limits: by default every file-scope Metadata stream in the root and
recursively opened children shares at most 16 MiB of decoded metadata bytes and 16 MiB of XML
characters in total. Malformed encodings and DTD declarations fail closed, while XML 1.0 encodings
such as BOM-marked UTF-16 remain
valid; `MaxCharactersInDocument` bounds the parsed content. The bounded
`XDocument` (or its failure) is cached per document, so reading the embedded PDF/A claim and then
running the metadata rules neither parses nor charges the same packet twice. Stream decoding is
bounded before the complete packet is materialized, including when one stream is aliased as both
catalog `Metadata` and an `EF` payload.

## Errata

Approved ISO errata from PDF Association are accounted for:

- **#123 (approved).** Table 2 was printed with a typo: the level property is named `pdfa:conformance`, whereas in practice it is **`pdfaid:conformance`**. The same correction restores the namespace URI form `http://www.aiim.org/pdfa/ns/id/` instead of the printed `https://…` and allows readers to accept both. The library writes and reads `http`, as for Parts 1–3.
- **#104 (approved).** Editorial renumbering of notes in §6.2.9 and reference to Annex Q of ISO 32000-2. No impact on code.
- **#77 (proposed, not ISO-approved).** Proposes replacing "greater than or equal" with "less than or equal" in the §6.2.10.3.1 `Supplement` comparison. The direction is opposite to what was printed, and the same wording in WTPDF 1.0 §8.4.5.3.1 matches the current standard text, not the erratum. Until ISO decides, the library does not check this comparison at all — a rule that may end up reversed is worse than none.

The errata index warns that a dated revision of PDF/A-4 is being prepared that will incorporate all published decisions and align wording with PDF/X-6. When it appears, `pdfaid:rev` will need to change from `2020` to the revision year — the only place in code it will affect (`PdfAProfile.PartFourRevision`).

## What this matrix does not promise

Same as the Parts 1–3 matrix: conformance is a property of the file as a whole, and the validator checks what can be inferred from bytes and the object graph. Rows marked "—" are listed honestly and mean exactly one thing: the library is silent about that requirement, not that it confirms it.
