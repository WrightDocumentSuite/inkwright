# Accessibility: PDF/UA

## What It Is and What to Expect

PDF/UA is a PDF accessibility profile standardized as ISO 14289. Part 1 (2014) is based on
ISO 32000-1 and states requirements in clause 7; Part 2 (2024) is based on ISO 32000-2 and states
them in clause 8, adding structure namespaces from ISO/TS 32005.

The main thing to understand about automated accessibility checking: most of the standard's requirements
are judgments that software cannot make. Was the correct tag chosen for this piece of text?
Does the reading order match how a person would read the document? Does the alternative text
describe what is in the image? No validator in the world answers such questions.

Hence the rule: **a document that passes validation is not necessarily accessible; a document that
fails validation is definitely not accessible.** Validation filters out violations visible from the file itself.

## How to Use

```csharp
using Inkwright.Layout;

using PdfDocument document = PdfDocument.Load("report.pdf");

IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

foreach (PdfUaProblem problem in PdfUaProfile.Violations(problems))
    Console.WriteLine(problem);
```

Each finding carries a clause number from the standard and a level: `Violation` means a requirement
was broken, `Advice` means a recommendation was not met. The standard formulates many items using
"should", and a report that does not distinguish "shall" from "should" is a report you cannot use to prioritize issues.

Declare conformance:

```csharp
PdfUaProfile.Declare(document, PdfUaConformance.Ua1, "Annual Report for 2025");
```

The method writes an identification schema to XMP, the document title, and two required catalog keys: `MarkInfo`
with `Marked` and `ViewerPreferences` with `DisplayDocTitle`. It does not make the document conforming —
it makes a declaration. You still need to validate.

## What Is Checked

### Document as a Whole

| Clause (UA-1 / UA-2) | Requirement | How It Is Checked |
| --- | --- | --- |
| 5 | The file declares conformance via an identification schema in XMP | XMP parsing requires a single `part` of the correct value; for UA-2, mandatory `rev = 2024` as well |
| 7.1 / 8.2 | Content is marked up; a structure tree exists | `StructTreeRoot`, `MarkInfo` with `Marked`, tree is non-empty |
| 7.1 / 8.2 | Markup is not flagged as suspect | `Suspects` must be false |
| 7.1 / 8.2.2 | Non-standard tags map to standard ones | The `RoleMap` chain is traced to a standard type |
| 7.1 / 8.2.2 | Standard tags are not overridden | `RoleMap` keys are checked against the list of standard types |
| 7.1 / 8.11.1 | Metadata contains a title | `dc:title` is looked up |
| 7.1 / 8.11.2 | The reader is instructed to show the title | `ViewerPreferences` with `DisplayDocTitle` is true |
| 7.2 / 8.4.5 | Natural language is declared | `Lang` in the catalog or on all structure roots |
| 7.16 / 8.1 | Encryption does not block assistive technology extraction | Tenth bit of `P` |
| 7.10 / 8.7 | Layer configurations have names; no auto-switching | `Name` when `Configs` is present; absence of `AS` |
| 7.17 / 8.12.2 | Long documents have a table of contents | Advice when ten or more pages without `Outlines` |

### Structure Tree

| Clause | Requirement | How It Is Checked |
| --- | --- | --- |
| 7.4.2 / 8.2.5.2 | Headings start at the first level and do not skip levels | Sequence of `H1`…`Hn` in traversal order |
| 7.4.4 | The document is either strongly or weakly structured, but not both | Joint presence of `H` and numbered headings |
| 7.4.4 | A node has at most one child `H` | Count of children tagged `H` |
| 7.3 / 8.5 | Illustrations have a text replacement | `Alt` or `ActualText` on `Figure` |
| 7.7 / 8.5 | Formulas have a text replacement | `Alt` or `ActualText` on `Formula` |
| 7.5 / 8.2.5.9 | Table header cells indicate scope | `Scope` or `Headers` on `TH`; absence of `TH` is advice |
| 7.6 / 8.2.5.8 | Lists consist of list items | Children of `L` are only `LI`, `Caption`, or `L` |
| 7.6 / 8.2.5.8 | An ordered list states how it is numbered | `ListNumbering` when elements have `Lbl` |
| 7.9 / 8.2.5.11 | Footnotes have unique identifiers | `ID` on `Note` and `FENote`; duplicate check |

### Interaction

| Clause | Requirement | How It Is Checked |
| --- | --- | --- |
| 7.18.1 / 8.9.2 | Visible annotations have a description | `Contents` on everything except links and form fields |
| 7.18.2 | Trapping network annotations are forbidden | Subtype `TrapNet` |
| 7.18.3 / 8.9.2.2 | Tab order is defined by structure | `Tabs` equals `S` on pages with annotations |
| 7.18.4 / 8.10 | Form fields are nested in `Form` elements | Presence of `Form` in the tree when widgets exist |
| 7.18.5 / 8.9.2.4 | Links have a description; no server-side maps | `Contents`; `IsMap` is not true |
| 7.15 | Dynamic XFA forms are forbidden | Search for `dynamicRender` with value `required` |
| 7.11 / 8.14 | Attachments have both file names | `F` and `UF`; `Desc` is advice |
| 7.20 / 8.2.2 | Reference XObjects are forbidden | `Ref` key on an XObject stream |

Hidden annotations and pop-up windows are excluded from checking — the standard
explicitly removes them from the requirements of clause 7.18.

### Text Strings and Destinations (UA-2 Only)

| Clause | Requirement | How It Is Checked |
| --- | --- | --- |
| 8.6 | Human-readable text strings contain no private-use area characters | `Alt`, `ActualText`, `E`, `T` of structure elements; `Contents` and `TU` of annotations; bookmark titles; layer configuration names |
| 8.8 | Intra-document destinations are structure destinations | `Dest` and `GoTo` actions on annotations, bookmarks, and `OpenAction`; named destinations themselves |

Both rules exist only in Part 2, and for different reasons. The private-use area is forbidden
precisely where text is read by a person: code `U+E123` means exactly what the font that inserted it
decided, and assistive technology cannot see that font — there is nothing to speak aloud. All three
ranges are checked: the basic plane and both supplementary planes arriving via surrogate pairs.

Destinations in PDF 1.7 simply could not point to a structure element, so there is nothing to require
this of UA-1. `GoToR` and `GoToE` lead to another file and do not fall under §8.8, and a destination
that could not be reduced to an explicit array is not judged at all: a rule that guesses produces violations
for files that have none.

The second mechanism that §8.8 permits alongside a link is the `Ref` key on the source structure
element. Which content "actionably points" to which cannot be inferred from the file, so this half of
the requirement remains human judgment.

### Namespaces (UA-2 Only)

Per ISO 32000-2 §14.8.6 and ISO/TS 32005:

- The standard PDF 2.0 namespace is `http://iso.org/pdf2/ssn`.
- The standard PDF 1.7 namespace is `http://iso.org/pdf/ssn`. It is also the default: an element
  that did not name a namespace is considered to belong to it.
- The only domain namespace defined in PDF 2.0 is MathML,
  `http://www.w3.org/1998/Math/MathML`.

What is checked: an element with an `NS` reference points to a valid namespace dictionary; that
namespace is declared in the root tree's `Namespaces` array; a custom namespace has a
`RoleMapNS`. If no element declares a namespace, advice is issued: the entire document falls into
the PDF 1.7 default and loses everything PDF 2.0 added to the tag set.

## What Is Not Checked and Why

Requirements that need a human:

- Semantic appropriateness of a tag. That a paragraph is marked as `P` can be checked; that this `P`
  is actually a heading cannot.
- Logical reading order. The structure tree defines an order, but whether it matches how a person reads
  the document does not follow from the file.
- Meaningfulness of alternative text. Presence of `Alt` is checked; that it says "image1.png"
  formally satisfies the requirement.
- Flashing and flickering (clause 7.1), conveying meaning by color or layout alone.
- Contrast, WCAG 2.0 conformance in substance.
- Conformance of embedded files to their own accessibility standards (7.11).

## Recommendations from Guides

WTPDF 1.0 and the Tagged PDF Best Practice Guide are not standards but guides. Their advice is useful, but
following them is not a condition of conformance, and the validator does not check them. The most valuable
recommendations when building a document:

- Mark headers and footers as pagination artifacts, not as content.
- Do not use tables for layout: tabular markup is acceptable only for real rows and
  columns, and the standard says so directly (7.5).
- Give each section a heading and build the heading hierarchy from the document, not from font size.
- Mark content as a list only when it is actually read as a list.

Line-by-line cross-reference with the text of both parts is in [pdfua-matrix.md](pdfua-matrix.md).

## Well-Tagged PDF and Conformance Declarations

WTPDF 1.0 is not an ISO standard but a PDF Association specification, and it presents itself not as an
identification schema but as a **PDF Declarations** mechanism: an XMP entry that names the URI of the standard
under which the document is declared to conform.

```csharp
using Inkwright.Layout;
using Inkwright.Metadata;

WellTaggedPdf.Declare(document, WellTaggedLevel.Accessibility, "Our converter");

foreach (PdfDeclaration declaration in PdfDeclarations.Read(document))
    Console.WriteLine(declaration.ConformsTo);
```

There are two levels and they do not exclude each other: `Reuse` — content is reliably extracted and
reformatted; `Accessibility` — the same plus everything ISO 14289-2 requires, so when this level is declared
the metadata also receives the PDF/UA-2 identification schema.

A declaration can be made for any third-party standard, not only WTPDF: `PdfDeclarations.Build`
accepts an arbitrary URI and optional information about who, when, and on what basis the claim is made.
PDF Association maintains the list of industry-agreed declarations.

The nature of the mechanism is the same as everything in this file: **a declaration is an author's claim, not
proof**. The specification states this directly in the very first section.

## Relationship to PDF/A

The profiles are independent, and a document may claim both: PDF/A-2a and PDF/A-3a require tagging, which
covers part of the PDF/UA requirements but not all. The identification schemas differ (`pdfaid` and `pdfuaid`) and
both are written. See [archival.md](archival.md) and [pdfa-matrix.md](pdfa-matrix.md).
