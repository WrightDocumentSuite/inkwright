namespace Inkwright.Layout.Elements;

/// <summary>
/// A grid of cells with optional header rows. When a table spills onto another page the header rows
/// are drawn again at the top, which is the behaviour that makes a long table readable and the one
/// thing a plain column of rows cannot do.
/// </summary>
public sealed class TableElement : LayoutElement
{
    private readonly List<ColumnWidth> _columns = [];
    private readonly List<TableRow> _headerRows = [];
    private readonly List<TableRow> _bodyRows = [];
    private int _index;

    /// <summary>The line width drawn between cells; zero for no grid.</summary>
    public double GridLineWidth { get; set; }

    /// <summary>The colour of the grid lines.</summary>
    public PdfColor GridColor { get; set; } = PdfColor.Gray(0.75f);

    /// <summary>The inset applied inside every cell.</summary>
    public Insets CellPadding { get; set; } = Insets.All(4);

    /// <inheritdoc />
    public override IReadOnlyList<LayoutElement> Children =>
        [.. _headerRows.Concat(_bodyRows).SelectMany(row => row.Cells)];

    /// <inheritdoc />
    public override bool HasMoreContent => _index < _bodyRows.Count;

    /// <summary>Declares a column.</summary>
    /// <param name="width">How wide the column should be.</param>
    public void AddColumn(ColumnWidth width) => _columns.Add(width);

    /// <summary>Adds a row that repeats at the top of every page the table covers.</summary>
    public TableRow AddHeaderRow()
    {
        var row = new TableRow();
        _headerRows.Add(row);
        return row;
    }

    /// <summary>Adds a body row.</summary>
    public TableRow AddRow()
    {
        var row = new TableRow();
        _bodyRows.Add(row);
        return row;
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _index = 0;
        base.Reset();
    }

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context)
    {
        if (_columns.Count == 0 || (_index >= _bodyRows.Count && _headerRows.Count == 0))
            return SpacePlan.Empty();

        double[] widths = Distribute(available.Width);
        double used = 0;

        ResetHeaders();

        foreach (TableRow header in _headerRows)
        {
            double height = MeasureRow(header, widths, available.Height - used, context);
            if (height < 0)
                return SpacePlan.Wrap("the table header does not fit");

            used += height;
        }

        int fitted = 0;
        for (int i = _index; i < _bodyRows.Count; i++)
        {
            double height = MeasureRow(_bodyRows[i], widths, available.Height - used, context);
            if (height < 0)
                break;

            used += height;
            fitted++;
        }

        if (fitted == 0 && _index < _bodyRows.Count)
            return SpacePlan.Wrap($"no table row fits in {available.Height:0.##} pt");

        return _index + fitted >= _bodyRows.Count
            ? SpacePlan.Full(available.Width, used)
            : SpacePlan.Partial(available.Width, used);
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        if (_columns.Count == 0)
            return;

        double[] widths = Distribute(available.Width);
        double y = 0;

        // Header cells were drawn on the previous page and hold their own progress; rewind them so
        // the repeat at the top of this page comes out whole.
        ResetHeaders();

        using IDisposable tableTag = context.GroupOrNothing("Table");

        foreach (TableRow header in _headerRows)
        {
            double height = MeasureRow(header, widths, available.Height - y, context);
            if (height < 0)
                return;

            DrawRow(header, widths, y, height, context, "TH");
            y += height;
        }

        while (_index < _bodyRows.Count)
        {
            TableRow row = _bodyRows[_index];
            double height = MeasureRow(row, widths, available.Height - y, context);
            if (height < 0)
                break;

            DrawRow(row, widths, y, height, context, "TD");
            y += height;
            _index++;
        }
    }

    private void ResetHeaders()
    {
        foreach (TableRow header in _headerRows)
        {
            foreach (Container cell in header.Cells)
                cell.Reset();
        }
    }

    private double MeasureRow(TableRow row, double[] widths, double availableHeight, LayoutContext context)
    {
        if (availableHeight <= 0)
            return -1;

        double height = row.MinHeight;

        for (int i = 0; i < row.Cells.Count && i < widths.Length; i++)
        {
            double width = SpanWidth(widths, i, row.Spans[i]) - CellPadding.Horizontal;
            SpacePlan plan = row.Cells[i].Measure(
                new LayoutSize(Math.Max(0, width), Math.Max(0, availableHeight - CellPadding.Vertical)), context);

            if (plan.Kind == SpacePlanKind.Wrap)
                return -1;

            height = Math.Max(height, plan.Height + CellPadding.Vertical);
        }

        return height > availableHeight + 0.01 ? -1 : height;
    }

    private void DrawRow(TableRow row, double[] widths, double top, double height, LayoutContext context, string cellTag)
    {
        LayoutCanvas? canvas = context.Canvas;
        using IDisposable rowTag = context.GroupOrNothing("TR");

        if (canvas is not null && row.Background is { } background)
            canvas.FillRectangle(0, top, widths.Sum(), height, background);

        double x = 0;
        for (int i = 0; i < row.Cells.Count && i < widths.Length; i++)
        {
            double width = SpanWidth(widths, i, row.Spans[i]);
            var size = new LayoutSize(
                Math.Max(0, width - CellPadding.Horizontal),
                Math.Max(0, height - CellPadding.Vertical));

            using IDisposable cell = context.GroupOrNothing(cellTag);

            if (canvas is not null)
            {
                canvas.Save().Translate(x + CellPadding.Left, top + CellPadding.Top);
                row.Cells[i].Draw(size, context);
                canvas.Restore();

                if (GridLineWidth > 0)
                    canvas.StrokeRectangle(x, top, width, height, GridLineWidth, GridColor);
            }
            else
            {
                row.Cells[i].Draw(size, context);
            }

            x += width;
        }
    }

    private static double SpanWidth(double[] widths, int index, int span)
    {
        double total = 0;
        for (int i = index; i < index + Math.Max(1, span) && i < widths.Length; i++)
            total += widths[i];

        return total;
    }

    private double[] Distribute(double totalWidth)
    {
        double[] widths = new double[_columns.Count];
        double fixedTotal = 0;
        double shares = 0;

        foreach (ColumnWidth column in _columns)
        {
            if (column.IsRelative)
                shares += column.Relative;
            else
                fixedTotal += column.Constant;
        }

        double free = Math.Max(0, totalWidth - fixedTotal);

        for (int i = 0; i < _columns.Count; i++)
        {
            widths[i] = _columns[i].IsRelative && shares > 0
                ? free * _columns[i].Relative / shares
                : _columns[i].Constant;
        }

        return widths;
    }
}

/// <summary>One row of a table.</summary>
public sealed class TableRow
{
    private readonly List<Container> _cells = [];
    private readonly List<int> _spans = [];

    /// <summary>The cells, left to right.</summary>
    public IReadOnlyList<Container> Cells => _cells;

    /// <summary>How many columns each cell covers.</summary>
    public IReadOnlyList<int> Spans => _spans;

    /// <summary>The colour painted behind the whole row, if any.</summary>
    public PdfColor? Background { get; set; }

    /// <summary>The least height the row may take, in points.</summary>
    public double MinHeight { get; set; }

    /// <summary>Appends a cell.</summary>
    /// <param name="columnSpan">How many columns the cell covers.</param>
    public Container AddCell(int columnSpan = 1)
    {
        var cell = new Container();
        _cells.Add(cell);
        _spans.Add(Math.Max(1, columnSpan));
        return cell;
    }
}
