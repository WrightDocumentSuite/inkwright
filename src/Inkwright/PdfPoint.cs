using System.Globalization;

namespace Inkwright;

/// <summary>
/// A point in PDF user space, measured in points (1/72 inch) with the origin at the bottom-left
/// corner of the page and the y axis pointing up.
/// </summary>
/// <param name="X">The horizontal coordinate.</param>
/// <param name="Y">The vertical coordinate.</param>
public readonly record struct PdfPoint(double X, double Y)
{
    /// <summary>The origin of user space.</summary>
    public static PdfPoint Origin => default;

    /// <summary>Offsets the point.</summary>
    /// <param name="dx">The horizontal offset.</param>
    /// <param name="dy">The vertical offset.</param>
    public PdfPoint Translate(double dx, double dy) => new(X + dx, Y + dy);

    /// <summary>The straight-line distance to another point.</summary>
    /// <param name="other">The other point.</param>
    public double DistanceTo(PdfPoint other) => double.Hypot(other.X - X, other.Y - Y);

    /// <summary>Adds two points component-wise.</summary>
    public static PdfPoint operator +(PdfPoint left, PdfPoint right) => new(left.X + right.X, left.Y + right.Y);

    /// <summary>Subtracts two points component-wise.</summary>
    public static PdfPoint operator -(PdfPoint left, PdfPoint right) => new(left.X - right.X, left.Y - right.Y);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###})");
}
