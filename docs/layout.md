# Layout

`Inkwright.Layout` describes a document as content, not as pages. The engine lays it out twice: the
first pass counts pages, the second draws — so a footer can say "page 3 of 12" even though nobody
knew that number when the document was described.

## Measure and Draw

Every element answers two questions. `Measure` reports what it would do with the offered space and
changes nothing. `Draw` does it.

```csharp
public abstract SpacePlan Measure(LayoutSize available, LayoutContext context);
public abstract void Draw(LayoutSize available, LayoutContext context);
```

`SpacePlan` is not a size but a decision: `Empty`, `Wrap` (does not fit here at all), `Partial` (part
fits, the rest on the next page), or `Full`. It is `Partial` that lets content flow across pages
without a second layout model.

An element that drew part of itself remembers its position; `Reset` returns it to the start. The
counting pass runs without a canvas: state advances, but no drawing happens.

## Coordinate System

Layout thinks top-down; PDF thinks bottom-up. The flip happens once in the page matrix; afterward
`(0, 0)` is the top-left corner of the current element and `y` grows downward. Text and images are
flipped back inside `LayoutCanvas`; everything else — rectangles, lines, clips — works naturally in
y-down.

## Fluent Description

Every decoration returns a slot inside itself, so the chain reads like the nesting it creates:

```csharp
page.Content()
    .Padding(20)
    .Background("#F5F5F5", cornerRadius: 4)
    .Padding(12)
    .Text("Framed with padding");
```

Containers:

```csharp
container.Column(column => { column.Spacing(8); column.Item().Text("…"); });
container.Row(row => { row.ConstantItem(120).Text("left"); row.RelativeItem(2).Text("wider"); });
container.Table(table => { … });
container.Image(image, ImageFit.Contain);
container.Canvas(60, (canvas, size) => canvas.FillRectangle(0, 0, size.Width, size.Height, PdfColor.Red));
container.PageBreak();
container.Line(0.5, PdfColor.Gray(0.8f));
container.Spacer(12);
```

Placement and sizing: `AlignLeft`, `AlignCenter`, `AlignRight`, `AlignTop`, `AlignMiddle`,
`AlignBottom`, `Width`, `Height`, `MinHeight`, `MaxWidth`, `ShowIf`.

## Text

```csharp
container.Text(text =>
{
    text.Style(TextStyle.Default.FontSize(11).FontFamily("Segoe UI"));
    text.Justify();
    text.Span("Normal text, ");
    text.Span("emphasized", TextStyle.Default.Bold().FontColor("#B3261E"));
    text.Span(", and normal again.");
    text.Line(string.Empty);
    text.PageNumbers("Page {0} of {1}");
});
```

Lines break greedily at points where wrapping is allowed: spaces, hyphens, slashes, and CJK glyph
boundaries. A word that does not fit whole is split character by character. Justification stretches
gaps between words and never the words themselves; the last line of a paragraph stays ragged.

Style is an immutable record, so inheritance is free and identical styles compare by value. One text
object is emitted per line, and consecutive fragments of the same style are merged into one show
operator: a page with a hundred-word paragraph contains a few operators, not six hundred.

The font resolves by family: the built-in fourteen fonts first, then system-installed (`SystemFonts`), then
Helvetica. Explicit `TextStyle.Font(font)` overrides everything.

## Tables

```csharp
container.Table(table =>
{
    table.Columns(ColumnWidth.Fraction(4), ColumnWidth.Fixed(60), ColumnWidth.Fraction(1));
    table.Grid(0.4, PdfColor.Gray(0.8f));
    table.CellPadding(6);

    table.Header()
        .Background(PdfColor.Parse("#EFF3F8"))
        .Text("Description", TextStyle.Default.Bold())
        .Text("Qty")
        .Text("Amount");

    foreach (var line in lines)
        table.Row().Text(line.Name).Text(line.Quantity).Text(line.Total);
});
```

Header rows repeat at the top of every page the table occupies. Their state is reset before each
page — otherwise the repeat would come out empty because text in a cell remembers how much it already
drew.

## Page Breaks

`container.PageBreak()` ends the page. A break that lands at the top of a page has already done its
job and yields space instead of leaving a blank sheet behind.

## When Nothing Fits

An element that will not fit on any page throws `LayoutException` with an element tree in
`ElementTrace`:

```
Nothing fitted on page 2, so the layout would never finish.

Element tree:
Container
  ColumnElement
    Container
      TextElement
    Container
      ConstrainedElement
```

The name in the trace comes from `DebugName` when set; otherwise from the element type.

## Custom Drawing

Where the model is not enough, you can take the canvas:

```csharp
container.Canvas(80, (canvas, size) =>
{
    canvas.FillRectangle(0, 0, size.Width, size.Height, PdfColor.Parse("#EEF2FF"));
    canvas.DrawLine(0, size.Height / 2, size.Width, size.Height / 2, 1, PdfColor.Blue);
});
```

`LayoutCanvas` provides rectangles, rounded rectangles, lines, strokes, text, images, and clips in
layout coordinates; `canvas.Content` is the full low-level `ContentCanvas`.
