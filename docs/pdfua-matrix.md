# ISO 14289 (PDF/UA) Conformance Matrix

Line-by-line verification of the library against the normative texts of ISO 14289-1:2014 and ISO 14289-2:2024, plus structure namespaces per ISO/TS 32005.

The "UA-1" and "UA-2" columns list clause numbers. Part 1 states file-format requirements in Section 7; Part 2 — in Section 8; where the wording differs materially, the difference is described in the row.

Legend for the "Status" column:

| Symbol | Meaning |
| --- | --- |
| V | Validator checks: a violation in the file will be found |
| G | Generator sets: `PdfUaProfile.Declare` writes this into the document |
| GV | Both |
| A | Reported as advisory because the standard uses "should" |
| H | Requires human judgment; cannot be verified from the file; the row explains why |
| — | Not checked for another reason stated in the row |

Rules live in `src/Inkwright.Layout/PdfUa/`, one file per requirement group.

## Identification and document as a whole

| UA-1 | UA-2 | Requirement | Status | Notes |
| --- | --- | --- | --- | --- |
| 5 | 5 | `pdfuaid` identification schema in XMP | GV | `part` is compared against the part; for UA-2 the required `rev = 2024` is written and checked |
| 7.1 | 8.2 | All real content is tagged | H | presence of tree and `Marked` is checked; completeness of tagging requires human judgment |
| 7.1 | 8.2 | Artifacts are not in the structure tree | H | only a human can distinguish artifact from content |
| 7.1 | 8.2 | `Suspects` is false | GV | |
| 7.1 | 8.2.2 | Non-standard tags map to standard ones | V | `RoleMap` chain is traced to a standard type, with loop protection |
| 7.1 | 8.2.2 | Standard tags are not redefined | V | |
| 7.1 | 8.2 | No flashing or blinking | H | not inferable from the file |
| 7.1 | 8.2 | Meaning is not conveyed by color or layout alone | H | not inferable from the file |
| 7.1 | 8.11.1 | `dc:title` in metadata | GV | |
| 7.1 | 8.11.2 | `ViewerPreferences` with `DisplayDocTitle` | GV | |
| 7.2 | 8.4.5 | Natural language is declared | V | `Lang` in catalog or on all structure roots |
| 7.2 | 8.4.5 | Language changes are declared | H | requires understanding the text |
| 7.2 | 8.4 | Character codes map to Unicode | — | covered by PDF/A font checks |
| 7.2 | 8.4.4 | Stretched characters tagged with `ActualText` | H | |
| n/a | 8.6 | Human-readable text strings contain no private-use characters | V | UA-2 only; `Alt`, `ActualText`, `E`, `T` of structure elements, annotation `Contents` and `TU`, bookmark titles, and layer configuration names are checked. All three ranges: BMP and both supplementary planes |
| 7.16 | 8.1 | Encryption does not block assistive technology access | V | tenth bit of `P` |

## Structure tree

| UA-1 | UA-2 | Requirement | Status | Notes |
| --- | --- | --- | --- | --- |
| 7.3 | 8.5 | Illustrations have `Alt` or `ActualText` | V | presence is checked; meaningfulness of the text requires human judgment |
| 7.3 | 8.5 | Illustration caption tagged `Caption` | H | |
| 7.4.1 | 8.2.5.2 | Headings tagged with heading tags | H | what counts as a heading requires human judgment |
| 7.4.2 | 8.2.5.2 | First heading is `H1` | V | |
| 7.4.2 | 8.2.5.2 | Descending sequence does not skip a level | V | `H1 H2 H3` allowed, `H1 H3` not |
| 7.4.3 | 8.2.5.2 | Levels above six written as `H` plus Arabic digits without separators | V | tag parsing rejects anything except digits |
| 7.4.4 | 8.2.5.2 | Document is strongly or weakly structured, not both | V | simultaneous presence of `H` and numbered headings |
| 7.4.4 | 8.2.5.2 | Node has at most one child `H` | V | |
| 7.5 | 8.2.5.9 | Header cells tagged `TH` | A | absence of `TH` in a table is advisory |
| 7.5 | 8.2.5.9 | `TH` has `Scope` when structure is not inferred from `Headers` | V | `Scope` or `Headers` accepted |
| 7.5 | 8.2.5.9 | Table markup only for real rows and columns | H | table layout cannot be distinguished from a real table |
| 7.6 | 8.2.5.8 | List items tagged `LI` | V | |
| 7.6 | 8.2.5.8 | Ordered list has `ListNumbering` | V | order inferred from presence of `Lbl` |
| 7.7 | 8.5 | Formulas wrapped in `Formula` with `Alt` | V | presence is checked; whether all formulas are wrapped requires human judgment |
| 7.8 | 8.3 | Headers and footers are pagination artifacts | H | |
| 7.9 | 8.2.5.11 | Footnotes tagged `Note` with unique `ID` | V | uniqueness is checked |
| 7.12 | 8.12.4 | Article threads reflect reading order | H | |

## Interaction

| UA-1 | UA-2 | Requirement | Status | Notes |
| --- | --- | --- | --- | --- |
| n/a | 8.8 | Intra-document destinations are structure destinations | GV | UA-2 only: they do not exist in PDF 1.7. `Dest` and `GoTo` actions on annotations, bookmarks, and `OpenAction` are checked, as are named destinations themselves; `GoToR` and `GoToE` lead outside and are not judged. The layout engine assigns bookmarks a structure element in the tagged section, not a page |
| n/a | 8.8 | Source references target via structure element `Ref` | H | second of two permitted mechanisms; which content "actionably points" to which target requires human judgment |
| 7.10 | 8.7 | Layer configurations have names | V | required when `Configs` is present |
| 7.10 | 8.7 | Key `AS` is not present | V | |
| 7.11 | 8.14 | Attachments have `F` and `UF` | V | |
| 7.11 | 8.14.1 | Attachments have `Desc` | GV | advice in UA-1; required in UA-2 for file specifications in the `EmbeddedFiles` name tree |
| 7.11 | 8.14 | Attachment is accessible on its own | H | depends on its format |
| 7.13 | 8.10 | Signature appearance meets content requirements | H | |
| 7.14 | 8.10 | Non-interactive forms tagged with `PrintField` attributes | H | |
| 7.15 | n/a | Dynamic XFA forms forbidden | V | looks for `dynamicRender` with value `required`; in UA-2 XFA is forbidden entirely |
| 7.17 | 8.12.2 | Long document has a table of contents | A | advisory at ten or more pages |
| 7.17 | 8.12.3 | Page labels are semantically appropriate | H | |
| 7.18.1 | 8.9.2 | Real-content annotation is represented exactly once as a whole object in the structure tree | GV | `StructureElement.AddAnnotation` and `StructureTreeBuilder.AddAnnotation` author the complete bidirectional link; validation checks one `OBJR`, its page, the annotation's `StructParent`, the exact `ParentTree` owner, duplicate keys, and stale annotation targets no longer present in a page's `Annots` array, while explicit `XObject` targets remain valid whole-object content |
| 7.18.1 | 8.9.2.3 | Annotation has the structure element required by its semantics | V | markup uses `Annot`, widgets use `Form`, and links use `Link` or `Reference`; UA-2 also rejects `Popup` in the structure tree |
| 7.18.1 | 8.9.2.2 | Artifact annotations are absent from real-content structure | V | UA-2 treats `Invisible`, unconditional `NoView`, printer marks, and zero-area widgets as artifacts; `Hidden` alone is not an artifact |
| 7.18.1 | 8.9.2.3–8.9.2.4 | Annotation descriptions satisfy subtype rules | GV | UA-1 checks visible non-link/non-widget annotations; UA-2 enforces mandatory `Contents`/`RC` only where the subtype requires it and reports missing link `Contents` as advice |
| 7.18.2 | 8.9.2.4.15 | `TrapNet` forbidden | V | UA-2 also rejects deprecated `Sound` and `Movie` annotations |
| 7.18.3 | 8.9.3.3 | Page annotation tab order is permitted | V | UA-1 requires `Tabs /S`; UA-2 permits `/A`, `/W`, or `/S` on every page containing an annotation |
| 7.18.4 | 8.10 | Every widget is owned by exactly one `Form` element | V | checks the widget's own `OBJR` owner, not merely the presence of some `Form` in the tree |
| 7.18.5 | 8.9.2.4.2 | Links have a description | GV | violation in UA-1; advice in UA-2 |
| 7.18.5 | 8.9.2.4 | `IsMap` is not true | V | |
| n/a | 8.9.2.4.10 | File attachment dictionary has `AFRelationship` | V | the string file-specification form is implicitly `Unspecified` |
| 7.18.6 | 8.9 | Media clip has `CT` and `Alt` | — | the library does not create media |
| 7.18.8 | 8.9.2.4.14 | `PrinterMark` treated as artifact | V | accepted as incidental/artifact content rather than requiring a real-content annotation owner in both profiles |
| 7.19 | 8.13 | Scripts declare changes accessibly | H | |
| 7.20 | 8.2.2 | Form reference objects forbidden | V | `Ref` key in stream |
| 7.21 | 8.4 | Fonts embedded and displayed in Unicode | — | covered by PDF/A font checks |

## Structure namespaces (UA-2)

Per ISO 32000-2 §14.8.6 and ISO/TS 32005.

| Requirement | Status | Notes |
| --- | --- | --- |
| Element with `NS` reference points to a namespace dictionary | V | |
| Namespace declared in root tree `Namespaces` array | V | |
| Custom namespace has `RoleMapNS` | V | standard and MathML excluded |
| No element names a namespace | A | the entire document falls under the PDF 1.7 default namespace |

## What the "H" column means

Rows marked "requires human judgment" outnumber those in the PDF/A matrix, and that is not an implementation gap but a property of the subject. PDF/A asks what is in the file; PDF/UA asks whether the file is understandable to someone who cannot see it. The second question is inaccessible to software, and a validator that pretends to answer it is harmful: it certifies accessibility for a document no one can actually use.

Therefore the rule: a document that passes validation is not necessarily accessible. A document that fails is definitely not accessible.
