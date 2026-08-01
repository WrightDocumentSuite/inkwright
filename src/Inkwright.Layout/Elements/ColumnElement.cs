namespace Inkwright.Layout.Elements;

/// <summary>
/// Stacks children top to bottom. When the stack runs past the bottom of the page it remembers where
/// it stopped, so the next page carries on from the same child rather than from the top.
/// </summary>
public sealed class ColumnElement : LayoutElement
{
    private readonly List<Container> _items = [];
    private int _index;

    /// <summary>The gap between children, in points.</summary>
    public double Spacing { get; set; }

    /// <inheritdoc />
    public override IReadOnlyList<LayoutElement> Children => _items;

    /// <inheritdoc />
    public override bool HasMoreContent =>
        _index < _items.Count || (_items.Count > 0 && _items[^1].HasMoreContent);

    /// <summary>Appends a slot to the bottom of the stack.</summary>
    public Container Add()
    {
        var item = new Container();
        _items.Add(item);
        return item;
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
        if (_index >= _items.Count)
            return SpacePlan.Empty();

        double used = 0;
        double width = 0;
        bool drewAnything = false;
        bool more = false;
        bool startedOnPage = context.Pages.ContentStartedOnPage;

        for (int i = _index; i < _items.Count; i++)
        {
            double offset = drewAnything ? Spacing : 0;
            double remaining = available.Height - used - offset;

            if (remaining <= 0)
            {
                more = true;
                break;
            }

            // Elements that care whether the page is still blank, such as a page break, need to see
            // what this column is about to put above them.
            context.Pages.ContentStartedOnPage = startedOnPage || drewAnything;
            SpacePlan plan = _items[i].Measure(new LayoutSize(available.Width, remaining), context);

            if (plan.Kind == SpacePlanKind.Empty)
                continue;

            if (plan.Kind == SpacePlanKind.Wrap)
            {
                more = true;
                break;
            }

            used += offset + plan.Height;
            width = Math.Max(width, plan.Width);
            drewAnything = true;

            if (plan.Kind == SpacePlanKind.Partial)
            {
                more = true;
                break;
            }
        }

        context.Pages.ContentStartedOnPage = startedOnPage;

        if (!drewAnything)
        {
            return more
                ? SpacePlan.Wrap($"nothing in the column fits in {available.Height:0.##} pt")
                : SpacePlan.Empty();
        }

        return more ? SpacePlan.Partial(width, used) : SpacePlan.Full(width, used);
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        double used = 0;
        bool drewAnything = false;

        while (_index < _items.Count)
        {
            Container item = _items[_index];
            double offset = drewAnything ? Spacing : 0;
            double remaining = available.Height - used - offset;

            if (remaining <= 0)
                break;

            SpacePlan plan = item.Measure(new LayoutSize(available.Width, remaining), context);

            if (plan.Kind == SpacePlanKind.Empty)
            {
                item.Draw(new LayoutSize(available.Width, 0), context);
                _index++;
                continue;
            }

            if (plan.Kind == SpacePlanKind.Wrap)
                break;

            used += offset;
            context.Pages.ContentStartedOnPage = true;

            if (context.Canvas is { } canvas)
            {
                canvas.Save().Translate(0, used);
                item.Draw(new LayoutSize(available.Width, plan.Height), context);
                canvas.Restore();
            }
            else
            {
                item.Draw(new LayoutSize(available.Width, plan.Height), context);
            }

            used += plan.Height;
            drewAnything = true;

            if (plan.Kind == SpacePlanKind.Partial || item.HasMoreContent)
                break;

            _index++;
        }
    }
}

/// <summary>How wide a cell in a row is.</summary>
/// <param name="Constant">A fixed width in points, or zero when the width is relative.</param>
/// <param name="Relative">A share of what is left after the fixed widths, or zero for a fixed cell.</param>
public readonly record struct ColumnWidth(double Constant, double Relative)
{
    /// <summary>A fixed width.</summary>
    /// <param name="points">The width in points.</param>
    public static ColumnWidth Fixed(double points) => new(points, 0);

    /// <summary>A share of the space that fixed widths leave over.</summary>
    /// <param name="share">The relative weight; one is the default share.</param>
    public static ColumnWidth Fraction(double share = 1) => new(0, share);

    /// <summary>Whether the width is a share rather than a fixed number of points.</summary>
    public bool IsRelative => Relative > 0;
}

/// <summary>
/// Lays children out left to right. Cells are sized before anything is measured, so a row is a
/// straightforward division of the width rather than a negotiation.
/// </summary>
public sealed class RowElement : LayoutElement
{
    private readonly List<(Container Item, ColumnWidth Width)> _items = [];

    /// <summary>The gap between cells, in points.</summary>
    public double Spacing { get; set; }

    /// <inheritdoc />
    public override IReadOnlyList<LayoutElement> Children => [.. _items.Select(entry => entry.Item)];

    /// <inheritdoc />
    public override bool HasMoreContent => _items.Exists(entry => entry.Item.HasMoreContent);

    /// <summary>Appends a cell.</summary>
    /// <param name="width">How wide the cell should be.</param>
    public Container Add(ColumnWidth width)
    {
        var item = new Container();
        _items.Add((item, width));
        return item;
    }

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context)
    {
        if (_items.Count == 0)
            return SpacePlan.Empty();

        double[] widths = Distribute(available.Width);
        double height = 0;
        bool more = false;

        for (int i = 0; i < _items.Count; i++)
        {
            SpacePlan plan = _items[i].Item.Measure(new LayoutSize(widths[i], available.Height), context);

            if (plan.Kind == SpacePlanKind.Wrap)
                return plan;

            height = Math.Max(height, plan.Height);
            more |= plan.Kind == SpacePlanKind.Partial;
        }

        return more ? SpacePlan.Partial(available.Width, height) : SpacePlan.Full(available.Width, height);
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        double[] widths = Distribute(available.Width);
        double x = 0;

        for (int i = 0; i < _items.Count; i++)
        {
            var size = new LayoutSize(widths[i], available.Height);

            if (context.Canvas is { } canvas)
            {
                canvas.Save().Translate(x, 0);
                _items[i].Item.Draw(size, context);
                canvas.Restore();
            }
            else
            {
                _items[i].Item.Draw(size, context);
            }

            x += widths[i] + Spacing;
        }
    }

    private double[] Distribute(double totalWidth)
    {
        double[] widths = new double[_items.Count];
        double fixedTotal = Spacing * Math.Max(0, _items.Count - 1);
        double shares = 0;

        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Width.IsRelative)
                shares += _items[i].Width.Relative;
            else
                fixedTotal += _items[i].Width.Constant;
        }

        double free = Math.Max(0, totalWidth - fixedTotal);

        for (int i = 0; i < _items.Count; i++)
        {
            ColumnWidth width = _items[i].Width;
            widths[i] = width.IsRelative && shares > 0 ? free * width.Relative / shares : width.Constant;
        }

        return widths;
    }
}
