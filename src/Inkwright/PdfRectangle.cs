using System.Globalization;
using Inkwright.Cos;

namespace Inkwright;

/// <summary>
/// An axis-aligned rectangle in PDF user space (ISO 32000-1 §7.9.5). Rectangles are stored
/// normalised, so <see cref="Left"/> is never greater than <see cref="Right"/> however the source
/// file ordered the corners.
/// </summary>
public readonly record struct PdfRectangle
{
    /// <summary>Creates a rectangle from two opposite corners, in any order.</summary>
    /// <param name="x1">The x coordinate of the first corner.</param>
    /// <param name="y1">The y coordinate of the first corner.</param>
    /// <param name="x2">The x coordinate of the opposite corner.</param>
    /// <param name="y2">The y coordinate of the opposite corner.</param>
    public PdfRectangle(double x1, double y1, double x2, double y2)
    {
        Left = Math.Min(x1, x2);
        Bottom = Math.Min(y1, y2);
        Right = Math.Max(x1, x2);
        Top = Math.Max(y1, y2);
    }

    /// <summary>The smaller x coordinate.</summary>
    public double Left { get; }

    /// <summary>The smaller y coordinate.</summary>
    public double Bottom { get; }

    /// <summary>The larger x coordinate.</summary>
    public double Right { get; }

    /// <summary>The larger y coordinate.</summary>
    public double Top { get; }

    /// <summary>The horizontal extent.</summary>
    public double Width => Right - Left;

    /// <summary>The vertical extent.</summary>
    public double Height => Top - Bottom;

    /// <summary>Whether the rectangle has no area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The bottom-left corner.</summary>
    public PdfPoint BottomLeft => new(Left, Bottom);

    /// <summary>The top-right corner.</summary>
    public PdfPoint TopRight => new(Right, Top);

    /// <summary>The geometric centre.</summary>
    public PdfPoint Center => new((Left + Right) / 2, (Bottom + Top) / 2);

    /// <summary>Creates a rectangle from its lower-left corner and size.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The bottom edge.</param>
    /// <param name="width">The horizontal extent.</param>
    /// <param name="height">The vertical extent.</param>
    public static PdfRectangle FromSize(double x, double y, double width, double height) =>
        new(x, y, x + width, y + height);

    /// <summary>Reads a rectangle from a four-element PDF array.</summary>
    /// <param name="array">The array to read; anything shorter than four numbers yields an empty rectangle.</param>
    public static PdfRectangle FromArray(PdfArray? array) =>
        array is null || array.Count < 4
            ? default
            : new PdfRectangle(array.GetNumber(0), array.GetNumber(1), array.GetNumber(2), array.GetNumber(3));

    /// <summary>Writes the rectangle as the four-element array PDF expects.</summary>
    public PdfArray ToArray() => PdfArray.OfNumbers(Left, Bottom, Right, Top);

    /// <summary>Shrinks the rectangle by the same amount on every side, clamping at zero size.</summary>
    /// <param name="amount">The inset to apply to each edge.</param>
    public PdfRectangle Deflate(double amount) => Deflate(amount, amount, amount, amount);

    /// <summary>Shrinks the rectangle edge by edge, clamping at zero size.</summary>
    /// <param name="left">The inset applied to the left edge.</param>
    /// <param name="bottom">The inset applied to the bottom edge.</param>
    /// <param name="right">The inset applied to the right edge.</param>
    /// <param name="top">The inset applied to the top edge.</param>
    public PdfRectangle Deflate(double left, double bottom, double right, double top)
    {
        double newLeft = Left + left;
        double newBottom = Bottom + bottom;
        double newRight = Math.Max(newLeft, Right - right);
        double newTop = Math.Max(newBottom, Top - top);
        return new PdfRectangle(newLeft, newBottom, newRight, newTop);
    }

    /// <summary>Grows the rectangle by the same amount on every side.</summary>
    /// <param name="amount">The outset to apply to each edge.</param>
    public PdfRectangle Inflate(double amount) => new(Left - amount, Bottom - amount, Right + amount, Top + amount);

    /// <summary>Whether the rectangle contains a point, edges included.</summary>
    /// <param name="point">The point to test.</param>
    public bool Contains(PdfPoint point) =>
        point.X >= Left && point.X <= Right && point.Y >= Bottom && point.Y <= Top;

    /// <summary>Whether the rectangle shares any area with another.</summary>
    /// <param name="other">The other rectangle.</param>
    public bool IntersectsWith(PdfRectangle other) =>
        other.Left <= Right && other.Right >= Left && other.Bottom <= Top && other.Top >= Bottom;

    /// <summary>The overlapping area of two rectangles, or an empty rectangle when they are disjoint.</summary>
    /// <param name="other">The other rectangle.</param>
    public PdfRectangle Intersect(PdfRectangle other)
    {
        double left = Math.Max(Left, other.Left);
        double bottom = Math.Max(Bottom, other.Bottom);
        double right = Math.Min(Right, other.Right);
        double top = Math.Min(Top, other.Top);
        return right < left || top < bottom ? default : new PdfRectangle(left, bottom, right, top);
    }

    /// <summary>The smallest rectangle containing both rectangles.</summary>
    /// <param name="other">The other rectangle.</param>
    public PdfRectangle Union(PdfRectangle other) => new(
        Math.Min(Left, other.Left),
        Math.Min(Bottom, other.Bottom),
        Math.Max(Right, other.Right),
        Math.Max(Top, other.Top));

    /// <summary>Moves the rectangle without changing its size.</summary>
    /// <param name="dx">The horizontal offset.</param>
    /// <param name="dy">The vertical offset.</param>
    public PdfRectangle Translate(double dx, double dy) => new(Left + dx, Bottom + dy, Right + dx, Top + dy);

    /// <summary>The axis-aligned bounds of this rectangle after a transformation.</summary>
    /// <param name="matrix">The transformation to apply to the four corners.</param>
    public PdfRectangle Transform(PdfMatrix matrix)
    {
        PdfPoint p1 = matrix.Transform(new PdfPoint(Left, Bottom));
        PdfPoint p2 = matrix.Transform(new PdfPoint(Right, Bottom));
        PdfPoint p3 = matrix.Transform(new PdfPoint(Right, Top));
        PdfPoint p4 = matrix.Transform(new PdfPoint(Left, Top));

        double left = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        double right = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        double bottom = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        double top = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
        return new PdfRectangle(left, bottom, right, top);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Left:0.###} {Bottom:0.###} {Right:0.###} {Top:0.###}]");
}
