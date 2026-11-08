using System.Globalization;
using Inkwright.Images;
using Inkwright.Layout.Elements;

namespace Inkwright.Layout;

/// <summary>
/// The fluent surface of the layout engine. Every decoration returns the slot inside itself, so a
/// chain reads as the nesting it produces: <c>Padding(10).Background(grey).Text("…")</c> is a padded
/// box with a grey fill holding some text.
/// </summary>
public static class ContainerExtensions
{
    extension(Container container)
    {
        /// <summary>Puts an element into the slot and hands it back.</summary>
        /// <param name="element">The element to place.</param>
        public T Element<T>(T element) where T : LayoutElement
        {
            ArgumentNullException.ThrowIfNull(element);
            container.Child = element;
            return element;
        }

        /// <summary>Insets the content on every side.</summary>
        /// <param name="all">The inset in points.</param>
        public Container Padding(double all) => container.Padding(all, all, all, all);

        /// <summary>Insets the content on the left and right.</summary>
        /// <param name="value">The inset in points.</param>
        public Container PaddingHorizontal(double value) => container.Padding(value, 0, value, 0);

        /// <summary>Insets the content on the top and bottom.</summary>
        /// <param name="value">The inset in points.</param>
        public Container PaddingVertical(double value) => container.Padding(0, value, 0, value);

        /// <summary>Insets the content edge by edge.</summary>
        /// <param name="left">The inset on the left.</param>
        /// <param name="top">The inset on the top.</param>
        /// <param name="right">The inset on the right.</param>
        /// <param name="bottom">The inset on the bottom.</param>
        public Container Padding(double left, double top, double right, double bottom)
        {
            var element = new PaddingElement { Padding = new Insets(left, top, right, bottom) };
            container.Child = element;
            return element.Content;
        }

        /// <summary>Paints a colour behind the content.</summary>
        /// <param name="color">The fill colour.</param>
        /// <param name="cornerRadius">The corner radius, zero for square corners.</param>
        public Container Background(PdfColor color, double cornerRadius = 0)
        {
            var element = new BackgroundElement { Color = color, CornerRadius = cornerRadius };
            container.Child = element;
            return element.Content;
        }

        /// <summary>Paints a colour behind the content.</summary>
        /// <param name="hex">A colour such as <c>#F5F5F5</c>.</param>
        public Container Background(string hex) => container.Background(PdfColor.Parse(hex));

        /// <summary>Draws a line around the content.</summary>
        /// <param name="thickness">The line width.</param>
        /// <param name="color">The line colour, black when omitted.</param>
        public Container Border(double thickness, PdfColor? color = null)
        {
            var element = new BorderElement
            {
                Thickness = Insets.All(thickness),
                Color = color ?? PdfColor.Black,
            };

            container.Child = element;
            return element.Content;
        }

        /// <summary>Draws a line under the content only.</summary>
        /// <param name="thickness">The line width.</param>
        /// <param name="color">The line colour, black when omitted.</param>
        public Container BorderBottom(double thickness, PdfColor? color = null)
        {
            var element = new BorderElement
            {
                Thickness = new Insets(0, 0, 0, thickness),
                Color = color ?? PdfColor.Black,
            };

            container.Child = element;
            return element.Content;
        }

        /// <summary>Places the content against the left edge.</summary>
        public Container AlignLeft() => container.Align(HorizontalPlacement.Left, null);

        /// <summary>Centres the content across the width.</summary>
        public Container AlignCenter() => container.Align(HorizontalPlacement.Center, null);

        /// <summary>Places the content against the right edge.</summary>
        public Container AlignRight() => container.Align(HorizontalPlacement.Right, null);

        /// <summary>Places the content against the top edge.</summary>
        public Container AlignTop() => container.Align(null, VerticalPlacement.Top);

        /// <summary>Centres the content down the height.</summary>
        public Container AlignMiddle() => container.Align(null, VerticalPlacement.Middle);

        /// <summary>Places the content against the bottom edge.</summary>
        public Container AlignBottom() => container.Align(null, VerticalPlacement.Bottom);

        /// <summary>Places the content inside a larger box.</summary>
        /// <param name="horizontal">How it sits across the width.</param>
        /// <param name="vertical">How it sits down the height.</param>
        public Container Align(HorizontalPlacement? horizontal, VerticalPlacement? vertical)
        {
            var element = new AlignElement();
            if (horizontal is { } h)
                element.Horizontal = h;

            if (vertical is { } v)
                element.Vertical = v;

            container.Child = element;
            return element.Content;
        }

        /// <summary>Fixes the width.</summary>
        /// <param name="value">The width in points.</param>
        public Container Width(double value) => container.Constrain(element => element.Width = value);

        /// <summary>Fixes the height.</summary>
        /// <param name="value">The height in points.</param>
        public Container Height(double value) => container.Constrain(element => element.Height = value);

        /// <summary>Sets the least height the content may take.</summary>
        /// <param name="value">The height in points.</param>
        public Container MinHeight(double value) => container.Constrain(element => element.MinHeight = value);

        /// <summary>Sets the greatest width the content may take.</summary>
        /// <param name="value">The width in points.</param>
        public Container MaxWidth(double value) => container.Constrain(element => element.MaxWidth = value);

        /// <summary>Applies a size constraint.</summary>
        /// <param name="configure">Sets the bounds on the constraint element.</param>
        public Container Constrain(Action<ConstrainedElement> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var element = new ConstrainedElement();
            configure(element);
            container.Child = element;
            return element.Content;
        }

        /// <summary>Draws the content only when a condition holds.</summary>
        /// <param name="condition">Whether to keep the content.</param>
        public Container ShowIf(bool condition) => condition ? container : new Container();

        /// <summary>
        /// Adds a bookmark to the document outline that points at the content placed inside it
        /// (ISO 32000-1 §12.3.3). The bookmark lands on the page the content flows onto, whichever
        /// that turns out to be, so a heading and its bookmark never drift apart.
        /// </summary>
        /// <param name="title">The text shown in the reader's navigation pane.</param>
        /// <param name="level">How deeply the bookmark nests: zero for a top-level entry, more for the ones beneath it.</param>
        public Container Bookmark(string title, int level = 0)
        {
            ArgumentNullException.ThrowIfNull(title);

            var element = new BookmarkElement { Title = title, Level = level };
            container.Child = element;
            return element.Content;
        }

        /// <summary>Writes a run of text.</summary>
        /// <param name="text">The text to write.</param>
        /// <param name="style">The style, or the inherited one when omitted.</param>
        public void Text(string text, TextStyle? style = null)
        {
            var element = new TextElement();
            if (style is not null)
                element.BaseStyle = style;

            element.Add(text);
            container.Child = element;
        }

        /// <summary>Writes text made of several runs.</summary>
        /// <param name="configure">Adds the runs.</param>
        public void Text(Action<TextDescriptor> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var element = new TextElement();
            configure(new TextDescriptor(element));
            container.Child = element;
        }

        /// <summary>Stacks content vertically.</summary>
        /// <param name="configure">Adds the items.</param>
        public void Column(Action<ColumnDescriptor> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var element = new ColumnElement();
            configure(new ColumnDescriptor(element));
            container.Child = element;
        }

        /// <summary>Lays content out side by side.</summary>
        /// <param name="configure">Adds the cells.</param>
        public void Row(Action<RowDescriptor> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var element = new RowElement();
            configure(new RowDescriptor(element));
            container.Child = element;
        }

        /// <summary>Builds a table.</summary>
        /// <param name="configure">Declares the columns and rows.</param>
        public void Table(Action<TableDescriptor> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var element = new TableElement();
            configure(new TableDescriptor(element));
            container.Child = element;
        }

        /// <summary>Draws an image.</summary>
        /// <param name="image">The image to draw.</param>
        /// <param name="fit">How it is fitted into the space.</param>
        public void Image(PdfImage image, ImageFit fit = ImageFit.Width) =>
            container.Child = new ImageElement { Image = image, Fit = fit };

        /// <summary>Leaves empty vertical space.</summary>
        /// <param name="height">The height in points.</param>
        public void Spacer(double height) => container.Child = new SpacerElement { Height = height };

        /// <summary>Draws a horizontal rule.</summary>
        /// <param name="thickness">The line width.</param>
        /// <param name="color">The line colour, black when omitted.</param>
        public void Line(double thickness = 1, PdfColor? color = null)
        {
            PdfColor stroke = color ?? PdfColor.Black;
            container.Child = new CustomDrawElement
            {
                Height = thickness,
                Draws = (canvas, size) => canvas.DrawLine(0, thickness / 2, size.Width, thickness / 2, thickness, stroke),
            };
        }

        /// <summary>Ends the page here.</summary>
        public void PageBreak() => container.Child = new PageBreakElement();

        /// <summary>Hands the canvas to a delegate for drawing the layout does not model.</summary>
        /// <param name="height">The height the drawing needs.</param>
        /// <param name="draw">What to draw.</param>
        public void Canvas(double height, Action<LayoutCanvas, LayoutSize> draw) =>
            container.Child = new CustomDrawElement { Height = height, Draws = draw };
    }
}

/// <summary>Adds runs to a block of text.</summary>
public sealed class TextDescriptor
{
    private readonly TextElement _element;

    internal TextDescriptor(TextElement element) => _element = element;

    /// <summary>Sets the style the runs inherit and the alignment of the whole block.</summary>
    /// <param name="style">The base style.</param>
    public void Style(TextStyle style) => _element.BaseStyle = style;

    /// <summary>Adjusts the base style.</summary>
    /// <param name="configure">Produces the new style from the current one.</param>
    public void Style(Func<TextStyle, TextStyle> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _element.BaseStyle = configure(_element.BaseStyle);
    }

    /// <summary>Aligns the block to the left.</summary>
    public void AlignLeft() => _element.BaseStyle = _element.BaseStyle with { Alignment = TextAlignment.Left };

    /// <summary>Centres the block.</summary>
    public void AlignCenter() => _element.BaseStyle = _element.BaseStyle with { Alignment = TextAlignment.Center };

    /// <summary>Aligns the block to the right.</summary>
    public void AlignRight() => _element.BaseStyle = _element.BaseStyle with { Alignment = TextAlignment.Right };

    /// <summary>Spreads the block to both margins.</summary>
    public void Justify() => _element.BaseStyle = _element.BaseStyle with { Alignment = TextAlignment.Justify };

    /// <summary>Appends a run.</summary>
    /// <param name="text">The text.</param>
    /// <param name="style">The style, or the base style when omitted.</param>
    public void Span(string text, TextStyle? style = null) => _element.Add(text, style);

    /// <summary>Appends a run and a line break.</summary>
    /// <param name="text">The text.</param>
    /// <param name="style">The style, or the base style when omitted.</param>
    public void Line(string text, TextStyle? style = null) => _element.Add(text + "\n", style);

    /// <summary>Appends the number of the page the text lands on.</summary>
    /// <param name="style">The style, or the base style when omitted.</param>
    public void CurrentPageNumber(TextStyle? style = null) =>
        _element.Add(pages => pages.CurrentPage.ToString(CultureInfo.CurrentCulture), style);

    /// <summary>Appends the total number of pages, which the second layout pass supplies.</summary>
    /// <param name="style">The style, or the base style when omitted.</param>
    public void TotalPages(TextStyle? style = null) =>
        _element.Add(pages => pages.TotalPages.ToString(CultureInfo.CurrentCulture), style);

    /// <summary>Appends text built from the page numbers.</summary>
    /// <param name="format">A composite format string taking the current page and the total.</param>
    /// <param name="style">The style, or the base style when omitted.</param>
    public void PageNumbers(string format, TextStyle? style = null) =>
        _element.Add(pages => string.Format(CultureInfo.CurrentCulture, format, pages.CurrentPage, pages.TotalPages), style);
}

/// <summary>Adds items to a vertical stack.</summary>
public sealed class ColumnDescriptor
{
    private readonly ColumnElement _element;

    internal ColumnDescriptor(ColumnElement element) => _element = element;

    /// <summary>Sets the gap between items.</summary>
    /// <param name="value">The gap in points.</param>
    public void Spacing(double value) => _element.Spacing = value;

    /// <summary>Appends an item.</summary>
    public Container Item() => _element.Add();
}

/// <summary>Adds cells to a horizontal row.</summary>
public sealed class RowDescriptor
{
    private readonly RowElement _element;

    internal RowDescriptor(RowElement element) => _element = element;

    /// <summary>Sets the gap between cells.</summary>
    /// <param name="value">The gap in points.</param>
    public void Spacing(double value) => _element.Spacing = value;

    /// <summary>Appends a cell of a fixed width.</summary>
    /// <param name="width">The width in points.</param>
    public Container ConstantItem(double width) => _element.Add(ColumnWidth.Fixed(width));

    /// <summary>Appends a cell taking a share of what the fixed cells leave over.</summary>
    /// <param name="share">The relative weight.</param>
    public Container RelativeItem(double share = 1) => _element.Add(ColumnWidth.Fraction(share));
}

/// <summary>Declares the shape of a table and fills it in.</summary>
public sealed class TableDescriptor
{
    private readonly TableElement _element;

    internal TableDescriptor(TableElement element) => _element = element;

    /// <summary>Declares the columns.</summary>
    /// <param name="widths">One width per column.</param>
    public void Columns(params ReadOnlySpan<ColumnWidth> widths)
    {
        foreach (ColumnWidth width in widths)
            _element.AddColumn(width);
    }

    /// <summary>Declares equally wide columns.</summary>
    /// <param name="count">How many columns.</param>
    public void RelativeColumns(int count)
    {
        for (int i = 0; i < count; i++)
            _element.AddColumn(ColumnWidth.Fraction());
    }

    /// <summary>Draws lines between the cells.</summary>
    /// <param name="thickness">The line width.</param>
    /// <param name="color">The line colour.</param>
    public void Grid(double thickness = 0.5, PdfColor? color = null)
    {
        _element.GridLineWidth = thickness;
        if (color is { } value)
            _element.GridColor = value;
    }

    /// <summary>Sets the inset inside every cell.</summary>
    /// <param name="value">The inset in points.</param>
    public void CellPadding(double value) => _element.CellPadding = Insets.All(value);

    /// <summary>Adds a row that repeats at the top of every page.</summary>
    public TableRowDescriptor Header() => new(_element.AddHeaderRow());

    /// <summary>Adds a body row.</summary>
    public TableRowDescriptor Row() => new(_element.AddRow());
}

/// <summary>Fills in one row of a table.</summary>
public sealed class TableRowDescriptor
{
    private readonly TableRow _row;

    internal TableRowDescriptor(TableRow row) => _row = row;

    /// <summary>Paints a colour behind the whole row.</summary>
    /// <param name="color">The fill colour.</param>
    public TableRowDescriptor Background(PdfColor color)
    {
        _row.Background = color;
        return this;
    }

    /// <summary>Sets the least height the row may take.</summary>
    /// <param name="value">The height in points.</param>
    public TableRowDescriptor MinHeight(double value)
    {
        _row.MinHeight = value;
        return this;
    }

    /// <summary>Appends a cell.</summary>
    /// <param name="columnSpan">How many columns the cell covers.</param>
    public Container Cell(int columnSpan = 1) => _row.AddCell(columnSpan);

    /// <summary>Appends a cell holding nothing but text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="style">The style, or the inherited one when omitted.</param>
    public TableRowDescriptor Text(string text, TextStyle? style = null)
    {
        _row.AddCell().Text(text, style);
        return this;
    }
}
