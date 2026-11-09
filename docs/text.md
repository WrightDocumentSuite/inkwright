# Text, Fonts, and Extraction

## Built-in Fourteen

Metrics for the fourteen fonts every reader must know are built into the library: glyph names,
widths, point sizes, italic angles, and bounding boxes from published Adobe metrics, and encoding tables
from Annex D of the specification itself.

```csharp
PdfFont helvetica = document.Fonts.Helvetica;
PdfFont times = document.Fonts.Standard(Standard14Font.TimesBoldItalic);

double width = helvetica.Measure("Hello", 24);   // 54.672 pt
```

`Standard14Font.Find` understands common aliases: `Arial`, `ArialMT`, `Arial,Bold`,
`TimesNewRomanPSMT`, and subset prefixes like `ABCDEF+Helvetica`.

Such a document embeds nothing and weighs a couple of kilobytes — but it is not suitable for PDF/A:
everything must be embedded there.

## Embedding and Subsetting

```csharp
PdfFont font = document.Fonts.Embed(@"C:\Windows\Fonts\segoeui.ttf");
PdfFont? system = document.Fonts.EmbedSystem("Segoe UI", bold: true);
```

`SystemFonts` indexes the family and PostScript names stored inside each TrueType/OpenType file,
not only its filename. This matters for Windows Office fonts whose files are named `times.ttf`,
`timesbd.ttf`, and so on. Subset prefixes and common PDF/Office aliases such as
`ABCDEF+TimesNewRomanPS-BoldMT`, `Times-Roman`, and `Times New Roman CYR` resolve to the same
installed family while preserving bold and italic face selection.

An embedded font is written as a composite (`Type0` with `Identity-H` encoding): text is encoded by
glyph index, so any script the font covers works, and the `ToUnicode` map keeps the result searchable
and copyable.

Before save, the font becomes a subset: only used glyphs and components of composite glyphs that
reference them remain. Glyph indices do not change, so `CIDToGIDMap` stays `Identity` and nothing
needs renumbering.

Fonts with Compact Font Format outlines — OpenType with a `CFF ` table — are subsetted the same way:
outlines of unused glyphs are replaced with an empty one-instruction `endchar` program, while glyph
count and numbering are preserved. That captures almost all the savings: font weight is its outlines.

Subroutines (subrs) — the second-largest part — are handled the same way. Each kept glyph is walked
by Type 2 charstring rules, nested calls are traced, and everything unreachable is replaced with a
single `return` byte. Entry count is intentionally unchanged: subroutine number is offset by the
entry count, so dropping one entry would renumber all following ones.

Composite glyphs are resolved for real. An accented letter is drawn as base plus accent, and a
composite `endchar` names their standard encoding codes, not glyph numbers; codes map to names via
Appendix B, names to glyphs via charset (formats 0, 1, and 2), and parts are added to the kept set.
Parts may themselves be composite, so the search runs to a fixed point.

For a CID-keyed CFF font, each kept glyph is matched to its font dictionary via `FDSelect`
(formats 0 and 3), each font dictionary's local subroutine index is trimmed independently while
preserving non-`Private` dictionary entries (`FontName`, `FontMatrix`, and so on), and global
subroutines are subsetted across the whole program.

The embedding-restriction flag from the `OS/2` table is reported by the library
(`TrueTypeFont.EmbeddingRestricted`), but the library does not enforce it: license compliance is the
caller's decision, not the library's.

## Encodings

`FontEncoding` provides four predefined Annex D encodings, the built-in Symbol encoding,
and `Differences` overlays:

```csharp
FontEncoding encoding = FontEncoding.WinAnsi.WithDifferences(differences);
string? glyph = encoding.GetGlyphName(0xE4);   // "adieresis"
string? text = encoding.GetText(0xE4);         // "ä"
```

`FontEncoding.GlyphToText` also understands vendor conventions: `uni0439`, `u20AC`, ligatures
`f_i`, variants `a.sc`.

When reading a standard Symbol font without `/Encoding`, extraction uses its built-in encoding
even if `/FontDescriptor` is absent. For example, bytes `61 62 67` represent `αβγ`, with Symbol's
own advance widths. `Standard14Font.Symbol.DefaultEncoding` exposes this mapping as
`FontEncoding.Symbol`. Its 189 codes follow the
[Adobe Symbol mapping](https://www.unicode.org/Public/MAPPINGS/VENDORS/ADOBE/symbol.txt).
Where that table lists Unicode alternatives, existing Latin glyph mappings are preserved;
otherwise the first listed value is used.

An explicit `/Encoding` or `/BaseEncoding` overrides the built-in encoding, and `/Differences`
overlays the selected base. A `/ToUnicode` entry has priority for each code it covers; uncovered
codes still use the selected encoding. This matters when extracting mathematical text from older
PDFs that omit a Unicode map.

## Text Extraction

Extraction replays the content stream the way a renderer would: each glyph's position comes from the
text and transformation matrices active at draw time, not from guessing bytes.

```csharp
string text = page.ExtractText();

foreach (PdfLetter letter in page.ExtractLetters())
{
    Console.WriteLine($"{letter.Text} at {letter.Origin}, {letter.FontSize:0.#} pt, {letter.FontName}");
}
```

`PdfLetter` carries text, baseline origin, width, height, point size, PDF base-font/PostScript name,
and baseline angle. The exposed name is the actual font name from the resource dictionary rather
than the page-local resource key (`F1`, `TT0`, and similar), so downstream conversion can identify
the family and face.
Working with letters rather than a flat string is worth it when you need columns, tables, or reading
order: you can always assemble a string from them, not the other way around.

Simple and composite fonts, `ToUnicode`, embedded CMaps, `Differences`, `cmap` formats 0, 4, 6, and
12, invisible text under scans (mode 3 is skipped), and Form XObject content nested up to twelve levels
are all understood. Extraction starts with the page `CropBox` and follows the graphics-state clipping
path, including `W`, `W*`, save/restore, text clipping modes, and a Form XObject's `BBox`. A glyph is
kept when its positioned glyph box intersects the visible region; clipping against the exact font
outline is intentionally not attempted.

`TextExtractor.Compose` groups letters into lines by baseline and inserts a space where the gap is
wider than roughly one-fifth of the point size.

### Optional Extraction Behavior

```csharp
string text = page.ExtractText(new TextExtractionOptions
{
    ResolveBidirectional = true,   // Arabic and Hebrew in logical order
    SkipHiddenLayers = true,       // skip disabled layers
    UseLogicalOrder = true,        // reading order from structure tree
    InferReadingOrder = false,     // opt-in geometry order for untagged pages
    UseActualText = true,          // exact replacement from marked-content /ActualText
});
```

**Bidirectional text.** PDF draws glyphs in content-stream order, so Arabic and Hebrew come out
backwards: the last letter of a word is drawn leftmost and read first. `Bidi` runs Unicode Annex #9
over each line and returns the order in which the text was written — the order in which it should be
stored, searched, and copied.

Explicit embedding controls (X1–X10), combining-mark reordering (L3), and mirrored glyphs (L4) are
not implemented: PDF resolved all three before it drew the glyphs.

**Reading order.** In a tagged document, the structure tree says what is read after what. Without it,
two columns come out interleaved line by line because that is how they were drawn.

```csharp
foreach (PdfStructureNode node in document.Structure)
    Console.WriteLine($"{node.Tag}: {node.AlternateText ?? node.Title}");
```

Each letter carries `MarkedContentId` — the marked-content sequence number in which it was drawn.
That links the glyph to the structure tree. Glyphs the tree says nothing about are not dropped: they
follow everything else in draw order, because untagged text is simply untagged text, not decoration.

For an untagged page, `InferReadingOrder = true` reconstructs physical rows, looks for a stable vertical
gutter, and reads each detected column from top to bottom. Full-width headings split the page into
independent column bands. The default remains `false`: geometry is useful evidence, but is not an
authoritative logical order. `UseLogicalOrder` takes priority if both options are enabled.

**ActualText.** With `UseActualText` (default `false` for the general text API), glyphs inside `BDC`
with `/ActualText` are replaced by one exact string. Spaces and line breaks are not normalized; a
nested `Span` without its own MCID inherits the outer one. MCID context includes the content stream,
so the same number inside a page and a Form XObject does not mix two sequences. Markdown export enables
this mode automatically.

## Predefined CMaps: Inkwright.Cjk Package

ISO 32000-2 Table 116 lists encodings Adobe published separately — they are not in the specification.
(In ISO 32000-1:2008 the same list was Table 118; in PDF 2.0, 118 holds additional CMap stream
dictionary entries.)
The core does not carry them: that is more than a megabyte of tables. The `Inkwright.Cjk` package
carries all 168 in compact binary form.

```csharp
CjkCMaps.Register();

using var document = PdfDocument.Load("japanese.pdf");
string text = document.Pages[0].ExtractText();
```

After registration, the third method of §9.10.2 works fully: code → CID via the real encoding map,
CID → text via the character collection map `<Registry>-<Ordering>-UCS2`. That is what lets you read
a Japanese, Chinese, or Korean document whose font has no `ToUnicode` at all.

Without the package, `Uni*` names are still parsed as two-byte identity: the code space is correct,
but CIDs equal the codes themselves.

The set covers Adobe-GB1, Adobe-CNS1, Adobe-Japan1, Adobe-Korea1, and Adobe-KR (PDF 2.0), including
UTF16 variants from PDF 2.0.

## Search

By default, search behaves like a viewer's find box, not byte-by-byte comparison:

```csharp
IReadOnlyList<PdfTextMatch> matches = page.FindText("resume");     // finds "résumé"
IReadOnlyList<PdfTextMatch> exact = page.FindText("resume", TextSearchOptions.Exact);
```

- **Diacritics** are ignored — but only when they are not in the query itself: if you typed accents,
  you meant them. Marks that change meaning, not shape (kana voicing, viramas), are not stripped.
- **Ligatures** are read as the letters they consist of: `find` matches printed `ﬁnd`.
- **Hyphenation** across a line break is joined: `hyphenated` is found even when printed as `hyphen-`
  and `ated` on the next line. A hyphen mid-line stays a hyphen — it joins a compound word, not
  split one.
- **Typographic quotes** match straight quotes; fractions ¼ ½ ¾ match slash notation.
- `WholeWords` requires the phrase to stand as its own word.

A match always returns what is printed on the page and bounding boxes per line it occupies — exactly
what highlighting needs.

### How Much to Trust This

Validation runs against PdfPig on a sample of real files: average text similarity is at least 0.90,
and total extracted character count differs by no more than 5%. Agreement with an independent
implementation on hundreds of documents is the only proof that encodings, widths, and matrices are
handled correctly; a fixture written by the same hand as the extractor proves nothing.
