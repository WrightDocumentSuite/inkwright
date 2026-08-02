# ISO 19005 (PDF/A) Conformance Matrix

Line-by-line verification of the library against the normative texts of all three parts of the standard. Source: purchased texts in `DOCS/FULLSPEC`: ISO 19005-1:2005 with Cor.1:2007 and Cor.2:2011 amendments, ISO 19005-2:2011, ISO 19005-3:2012.

The "Part 1" and "Part 2/3" columns list clause numbers; where the parts differ materially, the difference is described in the row. Part 3 repeats Part 2 except for §6.8, so it does not need a separate column.

Legend for the "Status" column:

| Symbol | Meaning |
| --- | --- |
| G | Generator guarantees: what the library writes does not violate the requirement |
| V | Validator checks: a violation in a third-party or edited file will be found |
| GV | Both |
| — | Not checked; the row explains why |
| n/a | Not applicable to what the library can create |

Rules live in `src/Inkwright.Layout/PdfA/`, one file per standard section; each finding names the clause that was violated. The matrix was verified two ways: the library validates its own output at all eight levels, and the validator accepts 106 archived files from the iText test set that claim Parts 1–3.

## 6.1 File structure

| Part 1 | Part 2/3 | Requirement | Status | How |
| --- | --- | --- | --- | --- |
| 6.1.2 | 6.1.2 | `%PDF-1.n` from byte 0, followed by a comment of ≥4 bytes all greater than 127 | GV | the writer always does this; the validator reads file bytes |
| — | 6.1.2 | Header version from 1.0 to 1.7 | GV | Part 1 claim pins 1.4; both `Save` and `SaveIncremental` at version 1.4 do not write object streams or cross-reference streams |
| 6.1.3 | 6.1.3 | Trailer `ID` resolves to an array of exactly two byte strings | GV | `PdfAProfile.Apply` creates the pair if missing; the validator accepts both direct and indirect storage |
| 6.1.3 | 6.1.3 | Trailer has no `Encrypt` | GV | `PdfDocument.Save` rejects encryption when PDF/A is claimed — for documents created in the current session and for loaded files with existing `pdfaid` metadata; the validator inspects the trailer and `WasEncrypted` |
| 6.1.3 | 6.1.3 | No more than one EOL after the final `%%EOF` | GV | the writer ends the file with `%%EOF\n`; the validator checks the tail |
| 6.1.4 | 6.1.4 | Exactly one EOL between `xref` and the subsection header | GV | checked at the byte level |
| 6.1.4 | 6.1.4 | Objects outside the cross-reference table are exempt from requirements | G | file-scope rules inspect every live object named by xref; semantic rules traverse what is reachable from the catalog |
| 6.1.6 | 6.1.6 | Hexadecimal strings have an even number of digits | GV | the serializer writes in pairs; the validator scans object bytes |
| 6.1.7 | 6.1.7.1 | `stream` + LF or CRLF, `endstream` after EOL | GV | checked at the byte level |
| 6.1.7 | 6.1.7 | Every stream is an indirect object | GV | generated streams are added to the object table; validation distinguishes indirect stream dictionaries from streams nested directly inside another object |
| 6.1.7 | 6.1.7.1 | Stream dictionary has no `F`, `FFilter`, `FDecodeParms` | GV | the library does not write them; the validator checks every stream in a live xref object |
| 6.1.10 | 6.1.7.2 | No `LZWDecode`; `Crypt` only `Identity` and only as the first filter; only filters from Table 6 | GV | the library writes Flate; file-scope validation checks canonical full filter names and `DecodeParms` on every stream in a live xref object |
| — | 6.1.8 | Font, color, and structure type names are valid UTF-8 | GV | the writer escapes UTF-8 bytes as `#xx`; file-scope validation checks decoded raw bytes in every live object for font names, Separation/DeviceN colourants, and structure types |
| 6.1.8 | 6.1.9 | `N G obj` uses single separators; EOL before the object number, after `obj`, and before and after `endobj` | GV | all four boundaries are checked against the original object byte range |
| — | 6.1.10 | Inline images use only the full or abbreviated filters allowed by the inline-image table; no `LZW`, `Crypt`, `JBIG2Decode`, `JPXDecode`, or unknown filters | V | content stream parsing expands abbreviations and validates every filter in a chain |
| 6.1.9 | 6.1.11 | Linearization is allowed | n/a | the library does not linearize |
| — | 6.1.12 | Permissions contains only `UR3`/`DocMDP`; a `DocMDP` signature reference has no `DigestLocation`, `DigestMethod`, or `DigestValue` | V | catalog `Perms`, its certification signature, and indirect reference dictionaries are checked |
| 6.1.12 | 6.1.13 | Limits: integers, reals, string and name length, object count, page sizes | V | scalar and container limits cover every value rooted at a live xref object; page dimensions remain a reachable-page rule |
| 6.1.12 | 6.1.13 | Nesting of `q`/`Q` no deeper than 28; `DeviceN` has at most 32 colors | V | counting in content streams; parsing of the color space array |
| 6.1.13 | — | Part 1 forbids `OCProperties` | V | see the optional content section |

Validation deliberately keeps file scope and use-dependent semantics separate. Every live indirect
object advertised by the cross-reference data is parsed for base limits, name encodings, stream
dictionaries and filters, and Metadata/XMP. Graphics, fonts, actions, annotations, and content
semantics still start at the catalogue, so an unused resource is not treated as if a page invoked
it. The protected byte/file reader runs with recovery disabled and preflights every xref-live
non-attachment stream before semantic rules consume it.

## 6.2 Graphics

| Part 1 | Part 2/3 | Requirement | Status | How |
| --- | --- | --- | --- | --- |
| 6.2.10 | 6.2.2 | Content streams have no operators outside ISO 32000-1, even between `BX`/`EX` | GV | `ContentCanvas` writes only known operators; the validator looks for `Unknown` on pages, form XObjects, patterns, and Type 3 glyphs |
| 6.2.2 | 6.2.3 | Output intent with `S = GTS_PDFA1` and an ICC stream in `DestOutputProfile` | GV | `PdfAProfile.Apply` adds an sRGB intent; the validator reads the ICC header |
| 6.2.2 | 6.2.3 | Multiple intents reference the same profile | V | comparison of profile objects |
| — | 6.2.3 | No `DestOutputProfileRef`; profile class `prtr`/`mntr`; GRAY/RGB/CMYK space | V | ICC header |
| 6.2.3.1 | 6.2.4.1 | Color specified device-independently: via profile or Default color space | V | traversal of color operators and image `ColorSpace` |
| 6.2.3.3 | 6.2.4.3 | `DeviceRGB`/`DeviceCMYK` only with a suitable profile or `DefaultRGB`/`DefaultCMYK` | GV | our output is RGB with an sRGB intent; the validator compares against the profile |
| 6.2.3.3 | — | Part 1: `DeviceRGB` and `DeviceCMYK` not together | V | separate check for Part 1 |
| 6.2.3.2 | 6.2.4.2 | `ICCBased` profile is embedded and valid | G | the library does not create `ICCBased` outside output intent |
| 6.2.3.4 | 6.2.4.4 | `Separation`/`DeviceN`: alternate space follows the same rules | V | recursive parsing of the color space array |
| — | 6.2.4.4 | `Colorants` is required; same-named `Separation` spaces match | — | not checked: the library does not create such spaces |
| — | 6.2.4.5 | `Indexed`/`Pattern` — per base color space | V | recursive parsing |
| 6.2.8 | 6.2.5 | `ExtGState` has no `TR`, no `HTP`, `TR2` only `Default` | GV | the library does not write them; the validator traverses state dictionaries |
| — | 6.2.5 | Halftones only types 1 and 5, no `HalftoneName` | V | parsing of `HT` |
| 6.2.9 | 6.2.6 | Rendering intent is one of four values | GV | checked in `ExtGState`, on images, and on the `ri` operator |
| — | 6.2.7 | Reader ignores `FL` value and the `i` operator | n/a | requirement on the reader, not the file |
| 6.2.4 | 6.2.8.1 | Image has no `Alternates`, `OPI`; `Interpolate` is false | GV | `PdfImage` does not write them; the validator traverses images |
| — | 6.2.8.3 | JPEG 2000 restrictions | — | the library does not embed JPX; third-party files are not parsed at the codestream level |
| 6.2.5 | 6.2.9.1 | Form XObject has no `OPI`, `PS`, `Subtype2 = PS` | GV | the validator traverses XObjects |
| 6.2.6 | 6.2.9.2 | No reference XObject | V | `Ref` key |
| 6.2.7 | 6.2.9.3 | No PostScript XObject | V | `Subtype = PS` |
| 6.4 | — | Part 1: `SMask` only `None`, no transparency groups, `BM` Normal/Compatible, `CA`/`ca` = 1.0 | V | separate set of checks for Part 1 |
| — | 6.2.10 | Transparency allowed; without output intent the page must declare `Group/CS`; `BM` from the ISO list | V | traversal per Annex A markers: page resources, nested form XObjects, and annotation appearance streams |

## 6.3 / 6.2.11 Fonts

| Part 1 | Part 2/3 | Requirement | Status | How |
| --- | --- | --- | --- | --- |
| 6.3.2 | 6.2.11.2 | Font subtype is one defined in ISO 32000-1 | V | `Subtype` check |
| 6.3.3.1 | 6.2.11.3.1 | `CIDSystemInfo` of font and CMap agree | — | only CMap presence is checked; Registry/Ordering comparison against predefined tables is not implemented |
| 6.3.3.2 | 6.2.11.3.2 | `CIDFontType2` has `CIDToGIDMap` | GV | we write `Identity`; the validator checks presence |
| 6.3.3.3 | 6.2.11.3.3 | CMap is predefined or embedded; `WMode` matches; `usecmap`/`UseCMap` only reference predefined maps | GV | we use `Identity-H`; the validator checks the stream dictionary and tokenizes every actual `usecmap` operand, so comments and unrelated names cannot satisfy the rule |
| 6.3.4 | 6.2.11.4.1 | All fonts are embedded | GV | `PdfFontCollection.Embed`; the validator traverses all `/Type /Font` dictionaries |
| 6.3.5 | 6.2.11.4.2 | Part 1: subsets require `CharSet` (Type 1) and `CIDSet` (CID) | GV | the generator writes `CIDSet` from the subsetter's actual glyph set |
| 6.3.6 | 6.2.11.5 | Widths in the dictionary match the font program | GV | widths come from `hmtx`; the validator parses embedded TrueType and compares `W` (composite fonts; simple font `Widths` are not compared) |
| 6.3.7 | 6.2.11.6 | Non-symbolic TrueType: MacRoman/WinAnsi, no `Differences` (Part 1); symbolic: no `Encoding` | V | per descriptor flags |
| 6.3.8 | 6.2.11.7.2 | Levels A and U: `ToUnicode` with four exceptions accounted for | GV | embedded fonts always write `ToUnicode`; the validator knows the exceptions |
| 6.3.8 | 6.2.11.7.2 | Values in `ToUnicode` are not 0, not U+FEFF, not U+FFFE | GV | parsing of `bfchar`/`bfrange` |
| — | 6.2.11.7.3 | Level A: private-use characters need `ActualText` | — | not checked: requires mapping codes to PUA across all text |
| — | 6.2.11.8 | Do not show the `.notdef` glyph | V | for `Identity-H` code 0 = `.notdef`, searched in text-showing operators |

## 6.5 / 6.3 Annotations, 6.9 / 6.4 forms, 6.6 / 6.5 actions

| Part 1 | Part 2/3 | Requirement | Status | How |
| --- | --- | --- | --- | --- |
| 6.5.2 | 6.3.1 | Forbidden subtypes: `3D`, `Sound`, `Screen`, `Movie`; Part 1 additionally `FileAttachment` | GV | the library does not create them; the validator distinguishes lists by part |
| 6.5.3 | 6.3.2 | `/F` required except Popup; Print set, Hidden/Invisible/NoView cleared (Parts 2/3 also ToggleNoView) | GV | the annotation layer sets Print, NoZoom, NoRotate |
| 6.5.3 | — | Part 1: `C`/`IC` only with RGB profile; `CA` = 1.0 | V | comparison against output intent color space |
| 6.5.3 | 6.3.3 | Appearance required except Popup, Link, and degenerate `Rect`; `/AP` contains only `/N` | GV | the annotation layer always draws appearance; for buttons `/N` is a state dictionary |
| 6.9 | 6.4.1 | `NeedAppearances` absent or false | GV | `PdfForm` does not set it; the validator checks |
| 6.9 | 6.4.1 | Field and widget have no `A` and `AA` | GV | recursive traversal of the field tree |
| — | 6.4.2 | No `XFA` in the form and no `NeedsRendering` in the catalog | V | the catalog is checked |
| 6.6.1 | 6.5.1 | Forbidden actions and named actions except four navigational ones | V | action roots from the catalog, pages, annotations, fields, outlines, JavaScript name tree, and chained `Next` actions are traversed; an unrelated dictionary that merely has an `S` key is not misclassified as an action |
| 6.6.2 | 6.5.2 | No `AA` on catalog, fields, and (Parts 2/3) pages | V | the catalog, fields, and pages are checked |
| 6.6.3 | 6.5.3 | Reader displays addresses for `GoToR`, `URI`, `SubmitForm` | n/a | requirement on the interactive reader |

## 6.7 / 6.6 Metadata

| Part 1 | Part 2/3 | Requirement | Status | How |
| --- | --- | --- | --- | --- |
| 6.7.2 | 6.6.2.1 | Catalog has a `Metadata` stream with XMP | GV | `XmpMetadata.ApplyTo`; validation detects the XML encoding from its BOM/declaration, bounds XML, prohibits DTDs, and reuses one parsed packet for identification and metadata rules |
| 6.7.2 | 6.6.2.1 | Every file-scope Metadata stream has exact `Type Metadata`, `Subtype XML`, and valid XMP/RDF | V | Metadata entries and explicitly typed Metadata streams in live xref objects are deduplicated and checked, including packets outside the catalogue graph |
| 6.7.2 | — | Part 1: metadata stream has no `Filter` | GV | the packet is written uncompressed |
| — | 6.6.2.1 | Packet header has no `bytes` or `encoding` attributes | GV | the header is written without them |
| — | 6.6.2.1 | Packet content is valid XML/RDF | GV | the validator parses via `XDocument` and requires PDF/A identification and schema descriptions to belong to RDF document subjects |
| 6.7.8 | 6.6.2.3.2 | All non-standard schemas described via `pdfaExtension:schemas` | GV | `XmpMetadata.AddExtensionSchema`; Factur-X describes the `fx:` schema |
| 6.7.11 | 6.6.4 | `pdfaid:part` and `pdfaid:conformance` match the claim | GV | written from the level; the validator compares against the level under test |
| 6.1.5 + 6.7.3 | 6.6.3 | Part 1: DocInfo and XMP agree (Parts 2/3 — recommendation) | GV | `Apply` writes the same Producer and dates; each save synchronizes `pdf:Producer`, `xmp:ModifyDate`, and `xmp:MetadataDate` with DocInfo; the validator compares strings and dates for Part 1 |
| 6.7.6 | 6.6.5 | File identifiers | G | `ID` is created on claim |
| 6.7.7 | 6.6.6 | Change history `xmpMM:History` | — | recommendation, not written |
| 6.7.10 | — | Embedded font metadata | — | recommendation, not written |

## 6.8 / 6.7 Logical structure (Level A)

| Part 1 | Part 2/3 | Requirement | Status | How |
| --- | --- | --- | --- | --- |
| 6.8.2.2 | 6.7.2.2 | `MarkInfo` with `Marked = true` | GV | `page.Tagged(...)` in the layout engine; the validator checks when Level A is claimed |
| 6.8.3.3 | 6.7.3.3 | Structure hierarchy in `StructTreeRoot` | GV | `StructureTreeBuilder`; the validator requires the root |
| 6.8.3.4 | 6.7.3.4 | Non-standard structure types mapped to standard via `RoleMap` | GV | the layout engine writes standard types; the validator walks the `RoleMap` chain |
| 6.8.3.2 | 6.7.3.2 | Word boundaries marked by spaces in show strings | G | the layout engine writes spaces between words; automatic checking of third-party files is not implemented |
| 6.8.4 | 6.7.4 | `Lang` value is a valid language identifier | GV | tag form checked in catalog and structure elements |
| 6.8.5–6.8.8 | 6.7.5–6.7.8 | `Alt`, `ActualText`, `E`, non-text annotation descriptions | — | recommendations ("should"), not checked |

## 6.1.11 / 6.8 Embedded files, 6.9 optional content, 6.10 presentations, 6.11 requirements

| Part 1 | Part 2/3 | Requirement | Status | How |
| --- | --- | --- | --- | --- |
| 6.1.11 | — | Part 1 forbids `EF` and `EmbeddedFiles` | V | the validator rejects both a recursive `EmbeddedFiles` name tree and every file specification with `EF` rooted at a live xref object, including catalog-unreachable and `AF`-only specifications |
| — | 6.8 | Part 2 allows attachments only if they conform to PDF/A-1 or PDF/A-2; Part 3 allows any type | V | every file specification rooted at a live xref object is validated, including catalog-unreachable specifications; decoded Part 2 attachments are opened as separate documents, and the exact `pdfaid:part`/`pdfaid:conformance` claim selects the child profile and complete recursive validator |
| — | 6.8 | An embedded file specification is an indirect `/Type /Filespec` dictionary with string `F` and `UF`; an embedded stream's optional `Type` is `/EmbeddedFile` | GV | `PdfAttachments.Add` and merge/relist write indirect specifications and streams; validation requires a well-formed, strictly ordered `EmbeddedFiles` name tree with exact descendant `Limits`, and malformed file specifications or payload types fail closed |
| — | 6.8 | `EF` and `RF` use the canonical `F`, `UF`, `DOS`, `Mac`, and `Unix` keys | V | unknown aliases are rejected; every `EF` key has its matching file-specification string, every `RF` key also exists in `EF` and contains string/stream pairs, and payload streams are deduplicated by identity before validation |
| — | Annex E.1 | A Part 3 associated file has a MIME `Subtype` | GV | the writer supplies it and the validator reports a missing value against the Annex rather than extending this rule to Part 4 |
| — | Annex E.2, Table E.1 | A Part 3 associated file has `AFRelationship`; custom second-class relationship names are allowed | GV | the writer supplies a predefined value; the validator requires the key without restricting the relationship name |
| 6.1.13 | 6.9 | Part 1 forbids `OCProperties`; Parts 2/3 require `Name` on each configuration, forbid `AS`, require complete `Order` | V | `Order` is traversed recursively and compared by OCG object/reference identity; hierarchy label strings and duplicates cannot mask a missing group |
| — | 6.10 | No `AlternatePresentations` and `PresSteps` | V | the catalog and pages are checked |
| — | 6.11 | No `Requirements` in the catalog | V | the catalog is checked |

The default recursive-validation budget is 16 nested PDF levels and 128 embedded PDFs. The root and
all children share three separate 128 MiB byte ledgers: encoded input for attachment payloads and
reader-created child streams, cumulative bytes materialized by filter and predictor stages, and final
decoded child bytes accepted for recursion. The byte/file entry point installs a strict policy before
the root reader loads, and every embedded reader inherits it with recovery disabled, so
cross-reference streams, object streams, and XMP cannot bypass the ledgers. Every xref-live stream
that is not a recognised `EF`/`RF` attachment payload is preflighted; recognised payloads retain
their own bounded decode and, where required, recursive reservation. Strict decoding uses only the
canonical stream `Filter` and `DecodeParms` entries: inline-image abbreviations and external-file
aliases cannot stand in for them. A terminal image codec remains encoded in that codec after every
preceding filter has been checked. `/Params/Size` is only a preflight hint.

The ledgers stop stage output at their bounds; they are work/input/output counters, not an exact
process-memory/RSS ceiling. Bounded decoder buffers and returned arrays add copy overhead, while the
parser/object cache, graph lists and identity sets allocate up to their value caps; bounded XMP also
holds an `XDocument` and canonical text (up to 16 Mi characters), and reported problems allocate
strings. The strict PDF/A paths reject malformed encodings, trailing data, unsupported filters and
invalid filter ordering fail-closed; ordinary public decoder APIs remain compatibility-oriented and
tolerant. A stream chain is limited to 64 filters. Attachment discovery has separate default bounds:
64 name-tree levels, 4,096 name-tree nodes or entries, and 1,024 unique file specifications. The root
and all children share a 1,048,576-value graph budget across both the catalogue-reachable graph and
values rooted at all xref-live objects. Protected readers have separate shared caps of 1,048,576
parsed values and 1,048,576 xref/object-stream structural entries;
all declared xref rows, including free and future entry types, consume that structural budget.
Recovery scans for catalogs, pages, xref entries, and object-stream slots are disabled. A SHA-256
fingerprint set detects content cycles on the active path. Malformed or truncated discovery and
every reached limit produce a fail-closed problem instead of an exception or silent acceptance.

XMP has its own exact shared limits: by default every file-scope Metadata stream in the root and
recursively opened children shares at most 16 MiB of decoded metadata bytes and 16 MiB of XML
characters in total. Malformed encodings and DTD declarations fail closed, while XML 1.0 encodings
such as BOM-marked UTF-16 remain
valid; `MaxCharactersInDocument` bounds the parsed content. The bounded
`XDocument` (or its failure) is cached per document, so reading the embedded PDF/A claim and then
running the metadata rules neither parses nor charges the same packet twice. Stream decoding is
bounded before the complete packet is materialized, including when one stream is aliased as both
catalog `Metadata` and an `EF` payload.

## Annex B Signatures

| Part 1 | Part 2/3 | Requirement | Status | How |
| --- | --- | --- | --- | --- |
| — | B.1 | `ByteRange` covers the complete signed revision except the signature itself | GV | `PdfSigner` covers the finished revision; the validator requires four direct integers, a direct hexadecimal `Contents`, direct top-level signature values, and an exact `<Contents>` gap, then accepts the current EOF or an earlier incremental revision ending at its own `%%EOF` |
| — | B.1 | Reader supports the standard `SubFilter` handlers | n/a | this is a processor capability, not a restriction on files; the validator does not reject a vendor handler merely because it is not registered locally |
| — | B.1 | Exactly one signer in PKCS#7 | G | `CmsBuilder` creates one signature; parsing third-party CMS in the validator is not done — the layout package deliberately does not pull in cryptography |
| — | B.1 | Timestamp and revocation information | G | `PdfSignatureOptions.TimestampUri` (PAdES B-T) |

## What is intentionally not checked

These requirements cannot be verified from the file, and the validator is silent about them by design, not by oversight:

- legality of font embedding (6.3.4 / 6.2.11.4.1) — a licensing question, not bytes;
- structural validity of the cross-reference table and trailer at an earlier signed-revision boundary — Annex B validation confirms its `%%EOF` boundary but does not reopen that historical revision separately;
- JPEG 2000 codestream restrictions (6.2.8.3) — the library does not parse JPX;
- agreement of font `CIDSystemInfo` with a predefined CMap (6.2.11.3.1) — Adobe tables are needed;
- `ActualText` for private-use characters (6.2.11.7.3);
- exception for fonts used only for invisible text (mode 3): the validator requires embedding for all fonts, which is stricter than the standard but safer for archiving;
- comparison of simple (non-composite) font `Widths` with the font program — the library does not write simple fonts, and third-party files do not get this check;
- word separation by spaces in show strings (6.7.3.2) — no reliable automatic check exists;
- recommendations ("should"): `xmpMM:History`, font metadata, `Alt`, `E`, non-text annotation descriptions;
- reader-behavior requirements, not file requirements: displaying annotation `Contents`, handling `GoToR`, `URI`, and `SubmitForm`, ignoring `FL`, `Trans`, and `Dur`.

Full formal verification should still be done with veraPDF; this matrix shows where the library guarantees compliance itself and where it relies on an external validator.
