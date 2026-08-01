namespace Inkwright.Layout.Elements;

/// <summary>Insets on the four sides of a box.</summary>
/// <param name="Left">The inset on the left.</param>
/// <param name="Top">The inset on the top.</param>
/// <param name="Right">The inset on the right.</param>
/// <param name="Bottom">The inset on the bottom.</param>
public readonly record struct Insets(double Left, double Top, double Right, double Bottom)
{
    /// <summary>No inset at all.</summary>
    public static Insets Zero => default;

    /// <summary>The same inset on every side.</summary>
    /// <param name="all">The inset.</param>
    public static Insets All(double all) => new(all, all, all, all);

    /// <summary>The total inset across the width.</summary>
    public double Horizontal => Left + Right;

    /// <summary>The total inset down the height.</summary>
    public double Vertical => Top + Bottom;
}

/// <summary>Holds one element and keeps its state handling in one place.</summary>
public abstract class SingleChildElement : LayoutElement
{
    /// <summary>The slot the child lives in.</summary>
    public Container Content { get; } = new();

    /// <inheritdoc />
    public override IReadOnlyList<LayoutElement> Children => [Content];

    /// <inheritdoc />
    public override bool HasMoreContent => Content.HasMoreContent;
}

/// <summary>
/// Registers a bookmark that points at wherever its child ends up. It changes nothing about the
/// layout — it measures and draws exactly as its child does — but as the child is drawn it records
/// the page and the position, which the renderer turns into a document outline (ISO 32000-1
/// §12.3.3).
/// </summary>
public sealed class BookmarkElement : SingleChildElement
{
    /// <summary>The text shown in the navigation pane.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>How deeply the bookmark nests, counting from zero at the top level.</summary>
    public int Level { get; set; }

    private bool _recorded;

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context) =>
        Content.Measure(available, context);

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        // A bookmark is recorded once, when its content first lands on a page. The position is the
        // current origin turned from layout coordinates (y down from the top) into the PDF page's
        // (y up from the bottom).
        bool record = !_recorded && context.Canvas is not null && context.CurrentPage is not null &&
                      !string.IsNullOrEmpty(Title);

        // The position has to be read before the content draws, because drawing moves the origin.
        double top = record ? context.CurrentPageHeight - context.Canvas!.OriginY : 0;
        PdfPage? page = context.CurrentPage;

        // Which node the content will hang from, and how much is already under it: whatever appears
        // beyond this point is what this bookmark marks.
        Tagging.StructureElement? parent = record ? context.Tagging?.Current : null;
        int existing = parent?.Children.Count ?? 0;

        Content.Draw(available, context);

        if (!record)
            return;

        Tagging.StructureElement? element = parent is not null && parent.Children.Count > existing
            ? parent.Children[existing]
            : parent;

        context.Bookmarks.Add(new LayoutContext.BookmarkRequest(Level, Title, page!, top, element));
        _recorded = true;
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _recorded = false;
        base.Reset();
    }
}

/// <summary>Insets its child from the edges of the space it was given.</summary>
public sealed class PaddingElement : SingleChildElement
{
    /// <summary>How far the child is inset on each side.</summary>
    public Insets Padding { get; set; } = Insets.Zero;

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context)
    {
        LayoutSize inner = available.Shrink(Padding.Horizontal, Padding.Vertical);
        if (inner.IsEmpty)
            return SpacePlan.Wrap("no room left after padding");

        SpacePlan plan = Content.Measure(inner, context);
        return plan.Kind switch
        {
            SpacePlanKind.Empty => SpacePlan.Empty(),
            SpacePlanKind.Wrap => plan,
            SpacePlanKind.Partial => SpacePlan.Partial(plan.Width + Padding.Horizontal, plan.Height + Padding.Vertical),
            _ => SpacePlan.Full(plan.Width + Padding.Horizontal, plan.Height + Padding.Vertical),
        };
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        LayoutSize inner = available.Shrink(Padding.Horizontal, Padding.Vertical);
        if (inner.IsEmpty)
            return;

        if (context.Canvas is { } canvas)
        {
            canvas.Save().Translate(Padding.Left, Padding.Top);
            Content.Draw(inner, context);
            canvas.Restore();
            return;
        }

        Content.Draw(inner, context);
    }
}

/// <summary>Paints a solid background behind its child.</summary>
public sealed class BackgroundElement : SingleChildElement
{
    /// <summary>The fill colour.</summary>
    public PdfColor Color { get; set; } = PdfColor.White;

    /// <summary>The corner radius, zero for square corners.</summary>
    public double CornerRadius { get; set; }

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context) =>
        Content.Measure(available, context);

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        SpacePlan plan = Content.Measure(available, context);
        double width = plan.Kind == SpacePlanKind.Empty ? available.Width : Math.Max(plan.Width, 0);
        double height = plan.Kind == SpacePlanKind.Empty ? available.Height : Math.Max(plan.Height, 0);

        if (context.Canvas is { } canvas)
        {
            if (CornerRadius > 0)
                canvas.FillRoundedRectangle(0, 0, width, height, CornerRadius, Color);
            else
                canvas.FillRectangle(0, 0, width, height, Color);
        }

        Content.Draw(available, context);
    }
}

/// <summary>Draws lines along the edges of its child.</summary>
public sealed class BorderElement : SingleChildElement
{
    /// <summary>The line width on each side.</summary>
    public Insets Thickness { get; set; } = Insets.All(1);

    /// <summary>The line colour.</summary>
    public PdfColor Color { get; set; } = PdfColor.Black;

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context) =>
        Content.Measure(available, context);

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        SpacePlan plan = Content.Measure(available, context);
        Content.Draw(available, context);

        if (context.Canvas is not { } canvas)
            return;

        double width = plan.Kind == SpacePlanKind.Empty ? available.Width : plan.Width;
        double height = plan.Kind == SpacePlanKind.Empty ? available.Height : plan.Height;

        if (Thickness.Left > 0)
            canvas.DrawLine(Thickness.Left / 2, 0, Thickness.Left / 2, height, Thickness.Left, Color);

        if (Thickness.Right > 0)
            canvas.DrawLine(width - (Thickness.Right / 2), 0, width - (Thickness.Right / 2), height, Thickness.Right, Color);

        if (Thickness.Top > 0)
            canvas.DrawLine(0, Thickness.Top / 2, width, Thickness.Top / 2, Thickness.Top, Color);

        if (Thickness.Bottom > 0)
            canvas.DrawLine(0, height - (Thickness.Bottom / 2), width, height - (Thickness.Bottom / 2), Thickness.Bottom, Color);
    }
}

/// <summary>Where a child sits inside a box larger than it needs.</summary>
public enum HorizontalPlacement
{
    /// <summary>Take the whole width.</summary>
    Stretch,

    /// <summary>Sit against the left edge.</summary>
    Left,

    /// <summary>Sit in the middle.</summary>
    Center,

    /// <summary>Sit against the right edge.</summary>
    Right,
}

/// <summary>Where a child sits vertically inside a box larger than it needs.</summary>
public enum VerticalPlacement
{
    /// <summary>Take the whole height.</summary>
    Stretch,

    /// <summary>Sit against the top edge.</summary>
    Top,

    /// <summary>Sit in the middle.</summary>
    Middle,

    /// <summary>Sit against the bottom edge.</summary>
    Bottom,
}

/// <summary>Places its child inside a box that may be larger than the child needs.</summary>
public sealed class AlignElement : SingleChildElement
{
    /// <summary>How the child sits across the width.</summary>
    public HorizontalPlacement Horizontal { get; set; } = HorizontalPlacement.Stretch;

    /// <summary>How the child sits down the height.</summary>
    public VerticalPlacement Vertical { get; set; } = VerticalPlacement.Stretch;

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context)
    {
        SpacePlan plan = Content.Measure(available, context);
        if (plan.Kind is SpacePlanKind.Empty or SpacePlanKind.Wrap)
            return plan;

        double width = Horizontal == HorizontalPlacement.Stretch ? plan.Width : available.Width;
        double height = Vertical == VerticalPlacement.Stretch ? plan.Height : available.Height;

        // Only claim the full height when something actually asked for it.
        if (Vertical == VerticalPlacement.Stretch)
            height = plan.Height;

        return plan with { Width = width, Height = height };
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        SpacePlan plan = Content.Measure(available, context);
        if (plan.Kind == SpacePlanKind.Wrap)
            return;

        double dx = Horizontal switch
        {
            HorizontalPlacement.Center => Math.Max(0, (available.Width - plan.Width) / 2),
            HorizontalPlacement.Right => Math.Max(0, available.Width - plan.Width),
            _ => 0,
        };

        double dy = Vertical switch
        {
            VerticalPlacement.Middle => Math.Max(0, (available.Height - plan.Height) / 2),
            VerticalPlacement.Bottom => Math.Max(0, available.Height - plan.Height),
            _ => 0,
        };

        LayoutSize inner = Horizontal == HorizontalPlacement.Stretch
            ? available
            : new LayoutSize(Math.Min(plan.Width, available.Width), available.Height);

        if (context.Canvas is { } canvas && (dx != 0 || dy != 0))
        {
            canvas.Save().Translate(dx, dy);
            Content.Draw(inner, context);
            canvas.Restore();
            return;
        }

        Content.Draw(inner, context);
    }
}

/// <summary>Pins its child to an exact size, or bounds it between a minimum and a maximum.</summary>
public sealed class ConstrainedElement : SingleChildElement
{
    /// <summary>The exact width, when one is required.</summary>
    public double? Width { get; set; }

    /// <summary>The exact height, when one is required.</summary>
    public double? Height { get; set; }

    /// <summary>The smallest acceptable width.</summary>
    public double? MinWidth { get; set; }

    /// <summary>The largest acceptable width.</summary>
    public double? MaxWidth { get; set; }

    /// <summary>The smallest acceptable height.</summary>
    public double? MinHeight { get; set; }

    /// <summary>The largest acceptable height.</summary>
    public double? MaxHeight { get; set; }

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context)
    {
        LayoutSize inner = Constrain(available);
        if (inner.Width <= 0 || inner.Height <= 0)
            return SpacePlan.Wrap("the constraint leaves no room");

        SpacePlan plan = Content.Measure(inner, context);
        if (plan.Kind == SpacePlanKind.Wrap)
            return plan;

        double width = Width ?? Math.Clamp(plan.Width, MinWidth ?? 0, MaxWidth ?? double.MaxValue);
        double height = Height ?? Math.Clamp(plan.Height, MinHeight ?? 0, MaxHeight ?? double.MaxValue);

        if (height > available.Height + 0.01)
            return SpacePlan.Wrap($"a fixed height of {height:0.##} pt does not fit in {available.Height:0.##} pt");

        return plan with { Width = width, Height = height };
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context) => Content.Draw(Constrain(available), context);

    private LayoutSize Constrain(LayoutSize available)
    {
        double width = Width ?? Math.Clamp(available.Width, 0, MaxWidth ?? double.MaxValue);
        double height = Height ?? Math.Clamp(available.Height, 0, MaxHeight ?? double.MaxValue);
        return new LayoutSize(Math.Min(width, available.Width), Math.Min(height, available.Height));
    }
}

/// <summary>Takes up space and draws nothing.</summary>
public sealed class SpacerElement : LayoutElement
{
    /// <summary>The width to take, or the whole width on offer.</summary>
    public double? Width { get; set; }

    /// <summary>The height to take.</summary>
    public double Height { get; set; }

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context)
    {
        double height = Math.Min(Height, available.Height);
        return height < Height
            ? SpacePlan.Wrap("the spacer does not fit")
            : SpacePlan.Full(Width ?? available.Width, height);
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
    }
}

/// <summary>
/// Ends the page, so whatever comes next starts on a fresh one. A break that lands at the top of a
/// page has already done its job and steps aside rather than leaving a blank sheet.
/// </summary>
public sealed class PageBreakElement : LayoutElement
{
    private bool _consumed;

    /// <inheritdoc />
    public override bool HasMoreContent => !_consumed;

    /// <inheritdoc />
    public override void Reset() => _consumed = false;

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context)
    {
        if (_consumed || !context.Pages.ContentStartedOnPage)
            return SpacePlan.Empty();

        return SpacePlan.Wrap("page break");
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context) => _consumed = true;
}

/// <summary>Hands the raw canvas to a delegate, for drawing the layout does not model.</summary>
public sealed class CustomDrawElement : LayoutElement
{
    /// <summary>The height the drawing needs.</summary>
    public double Height { get; set; }

    /// <summary>What to draw, given the canvas and the size it may use.</summary>
    public Action<LayoutCanvas, LayoutSize>? Draws { get; set; }

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context) =>
        Height > available.Height + 0.01
            ? SpacePlan.Wrap($"the drawing needs {Height:0.##} pt")
            : SpacePlan.Full(available.Width, Height);

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        if (context.Canvas is { } canvas)
            Draws?.Invoke(canvas, new LayoutSize(available.Width, Math.Min(Height, available.Height)));
    }
}
