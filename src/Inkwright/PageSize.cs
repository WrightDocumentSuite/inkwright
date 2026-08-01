namespace Inkwright;

/// <summary>
/// Page dimensions in points, plus conversions from the units people actually measure paper in.
/// A point is 1/72 inch, the unit of PDF user space.
/// </summary>
/// <param name="Width">The page width in points.</param>
/// <param name="Height">The page height in points.</param>
public readonly record struct PageSize(double Width, double Height)
{
    /// <summary>ISO A0, 841 by 1189 millimetres.</summary>
    public static PageSize A0 => FromMillimeters(841, 1189);

    /// <summary>ISO A1, 594 by 841 millimetres.</summary>
    public static PageSize A1 => FromMillimeters(594, 841);

    /// <summary>ISO A2, 420 by 594 millimetres.</summary>
    public static PageSize A2 => FromMillimeters(420, 594);

    /// <summary>ISO A3, 297 by 420 millimetres.</summary>
    public static PageSize A3 => FromMillimeters(297, 420);

    /// <summary>ISO A4, 210 by 297 millimetres.</summary>
    public static PageSize A4 => FromMillimeters(210, 297);

    /// <summary>ISO A5, 148 by 210 millimetres.</summary>
    public static PageSize A5 => FromMillimeters(148, 210);

    /// <summary>ISO A6, 105 by 148 millimetres.</summary>
    public static PageSize A6 => FromMillimeters(105, 148);

    /// <summary>US Letter, 8.5 by 11 inches.</summary>
    public static PageSize Letter => new(612, 792);

    /// <summary>US Legal, 8.5 by 14 inches.</summary>
    public static PageSize Legal => new(612, 1008);

    /// <summary>US Tabloid, 11 by 17 inches.</summary>
    public static PageSize Tabloid => new(792, 1224);

    /// <summary>US Executive, 7.25 by 10.5 inches.</summary>
    public static PageSize Executive => new(522, 756);

    /// <summary>The page turned on its side.</summary>
    public PageSize Landscape => Width >= Height ? this : new PageSize(Height, Width);

    /// <summary>The page standing upright.</summary>
    public PageSize Portrait => Height >= Width ? this : new PageSize(Height, Width);

    /// <summary>Builds a size from millimetres.</summary>
    /// <param name="width">The width in millimetres.</param>
    /// <param name="height">The height in millimetres.</param>
    public static PageSize FromMillimeters(double width, double height) =>
        new(Units.FromMillimeters(width), Units.FromMillimeters(height));

    /// <summary>Builds a size from inches.</summary>
    /// <param name="width">The width in inches.</param>
    /// <param name="height">The height in inches.</param>
    public static PageSize FromInches(double width, double height) =>
        new(Units.FromInches(width), Units.FromInches(height));

    /// <summary>The page as a rectangle anchored at the origin, the shape a <c>MediaBox</c> takes.</summary>
    public PdfRectangle ToRectangle() => new(0, 0, Width, Height);
}

/// <summary>Conversions between points and the units documents are usually specified in.</summary>
public static class Units
{
    /// <summary>Points per inch.</summary>
    public const double PointsPerInch = 72d;

    /// <summary>Points per millimetre.</summary>
    public const double PointsPerMillimeter = PointsPerInch / 25.4d;

    /// <summary>Converts millimetres to points.</summary>
    /// <param name="millimeters">The length in millimetres.</param>
    public static double FromMillimeters(double millimeters) => millimeters * PointsPerMillimeter;

    /// <summary>Converts centimetres to points.</summary>
    /// <param name="centimeters">The length in centimetres.</param>
    public static double FromCentimeters(double centimeters) => centimeters * 10 * PointsPerMillimeter;

    /// <summary>Converts inches to points.</summary>
    /// <param name="inches">The length in inches.</param>
    public static double FromInches(double inches) => inches * PointsPerInch;

    /// <summary>Converts CSS pixels at 96 dpi to points.</summary>
    /// <param name="pixels">The length in pixels.</param>
    public static double FromPixels(double pixels) => pixels * PointsPerInch / 96d;

    /// <summary>Converts points to millimetres.</summary>
    /// <param name="points">The length in points.</param>
    public static double ToMillimeters(double points) => points / PointsPerMillimeter;

    /// <summary>Converts points to inches.</summary>
    /// <param name="points">The length in points.</param>
    public static double ToInches(double points) => points / PointsPerInch;
}
