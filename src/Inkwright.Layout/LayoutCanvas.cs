using Inkwright.Content;
using Inkwright.Fonts;
using Inkwright.Images;

namespace Inkwright.Layout;

/// <summary>
/// Drawing in layout coordinates: the origin is the top-left corner of the current element and y
/// grows downwards, which is how page designs are described. The flip lives in the page's
/// transformation matrix, so only text and images — the two things PDF anchors bottom-up — need the
/// counter-flip that this class applies for them.
/// </summary>
public sealed class LayoutCanvas
{
    private readonly ContentCanvas _canvas;
    private readonly Stack<(double X, double Y)> _origins = new();
    private double _originX;
    private double _originY;

    internal LayoutCanvas(ContentCanvas canvas) => _canvas = canvas;

    /// <summary>The content stream being written.</summary>
    public ContentCanvas Content => _canvas;

    /// <summary>
    /// How far down the page the current origin sits, in layout coordinates (y grows downwards from
    /// the top of the page). The layout engine only ever translates, so tracking the accumulated
    /// offset is enough to place a bookmark exactly where its content landed.
    /// </summary>
    internal double OriginY => _originY;

    /// <summary>How far across the page the current origin sits, in layout coordinates.</summary>
    internal double OriginX => _originX;

    /// <summary>Saves the graphics state.</summary>
    public LayoutCanvas Save()
    {
        _origins.Push((_originX, _originY));
        _canvas.Save();
        return this;
    }

    /// <summary>Restores the graphics state.</summary>
    public LayoutCanvas Restore()
    {
        if (_origins.Count > 0)
            (_originX, _originY) = _origins.Pop();

        _canvas.Restore();
        return this;
    }

    /// <summary>Moves the origin.</summary>
    /// <param name="dx">The horizontal offset.</param>
    /// <param name="dy">The vertical offset, positive downwards.</param>
    public LayoutCanvas Translate(double dx, double dy)
    {
        if (dx != 0 || dy != 0)
        {
            _originX += dx;
            _originY += dy;
            _canvas.Translate(dx, dy);
        }

        return this;
    }

    /// <summary>Clips everything that follows to a box.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public LayoutCanvas Clip(double x, double y, double width, double height)
    {
        _canvas.Rectangle(x, y, width, height).Clip();
        return this;
    }

    /// <summary>Fills a box.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="color">The fill colour.</param>
    public LayoutCanvas FillRectangle(double x, double y, double width, double height, PdfColor color)
    {
        if (width <= 0 || height <= 0)
            return this;

        _canvas.FillColor(color).Rectangle(x, y, width, height).Fill();
        return this;
    }

    /// <summary>Fills a box with rounded corners.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="radius">The corner radius.</param>
    /// <param name="color">The fill colour.</param>
    public LayoutCanvas FillRoundedRectangle(
        double x, double y, double width, double height, double radius, PdfColor color)
    {
        if (width <= 0 || height <= 0)
            return this;

        _canvas.FillColor(color).RoundedRectangle(PdfRectangle.FromSize(x, y, width, height), radius).Fill();
        return this;
    }

    /// <summary>Draws a straight line.</summary>
    /// <param name="x1">The first point's horizontal coordinate.</param>
    /// <param name="y1">The first point's vertical coordinate.</param>
    /// <param name="x2">The second point's horizontal coordinate.</param>
    /// <param name="y2">The second point's vertical coordinate.</param>
    /// <param name="width">The line width.</param>
    /// <param name="color">The stroke colour.</param>
    public LayoutCanvas DrawLine(double x1, double y1, double x2, double y2, double width, PdfColor color)
    {
        if (width <= 0)
            return this;

        _canvas.StrokeColor(color).LineWidth(width).MoveTo(x1, y1).LineTo(x2, y2).Stroke();
        return this;
    }

    /// <summary>Draws the outline of a box.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="thickness">The line width.</param>
    /// <param name="color">The stroke colour.</param>
    public LayoutCanvas StrokeRectangle(
        double x, double y, double width, double height, double thickness, PdfColor color)
    {
        if (thickness <= 0 || width <= 0 || height <= 0)
            return this;

        double inset = thickness / 2;
        _canvas.StrokeColor(color)
               .LineWidth(thickness)
               .Rectangle(x + inset, y + inset, width - thickness, height - thickness)
               .Stroke();
        return this;
    }

    /// <summary>Draws a run of text with its baseline at the given point.</summary>
    /// <param name="text">The text to draw.</param>
    /// <param name="x">The horizontal position of the baseline start.</param>
    /// <param name="baseline">The vertical position of the baseline, measured downwards.</param>
    /// <param name="font">The font to draw with.</param>
    /// <param name="style">The style supplying size, colour and letter spacing.</param>
    public LayoutCanvas DrawText(string text, double x, double baseline, PdfFont font, TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(style);

        if (text.Length == 0)
            return this;

        _canvas.BeginText().FillColor(style.Color).Font(font, style.Size);

        if (style.LetterSpacing != 0)
            _canvas.CharSpacing(style.LetterSpacing);

        // The page is drawn with a flipped matrix, so the text matrix flips back to keep glyphs upright.
        _canvas.TextMatrix(new PdfMatrix(1, 0, 0, -1, x, baseline));
        _canvas.ShowText(text);
        _canvas.EndText();

        if (style.LetterSpacing != 0)
            _canvas.CharSpacing(0);

        return this;
    }

    /// <summary>Draws an image into a box, filling it exactly.</summary>
    /// <param name="image">The image to draw.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public LayoutCanvas DrawImage(PdfImage image, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (width <= 0 || height <= 0)
            return this;

        // Images occupy the unit square with y upwards; the negative height flips them into place.
        _canvas.Save();
        _canvas.Transform(new PdfMatrix(width, 0, 0, -height, x, y + height));
        _canvas.DrawXObject(_canvas.Resources.AddXObject(image.Reference));
        _canvas.Restore();
        return this;
    }
}
