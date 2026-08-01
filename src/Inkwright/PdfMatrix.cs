using System.Globalization;
using Inkwright.Cos;

namespace Inkwright;

/// <summary>
/// The six-element affine transformation of ISO 32000-1 §8.3.3, laid out as
/// <c>[a b c d e f]</c> for the matrix <c>{{a b 0} {c d 0} {e f 1}}</c>. Points are row vectors,
/// so <c>p * M</c> maps a point and <c>A * B</c> applies <c>A</c> first.
/// </summary>
/// <param name="A">Row one, column one: horizontal scale and rotation.</param>
/// <param name="B">Row one, column two: vertical shear.</param>
/// <param name="C">Row two, column one: horizontal shear.</param>
/// <param name="D">Row two, column two: vertical scale and rotation.</param>
/// <param name="E">Row three, column one: horizontal translation.</param>
/// <param name="F">Row three, column two: vertical translation.</param>
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity transformation.</summary>
    public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    /// <summary>Whether this is the identity transformation.</summary>
    public bool IsIdentity => this == Identity;

    /// <summary>Creates a translation.</summary>
    /// <param name="dx">The horizontal offset.</param>
    /// <param name="dy">The vertical offset.</param>
    public static PdfMatrix Translation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);

    /// <summary>Creates a scaling about the origin.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    public static PdfMatrix Scaling(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary>Creates a counter-clockwise rotation about the origin.</summary>
    /// <param name="degrees">The rotation angle in degrees.</param>
    public static PdfMatrix Rotation(double degrees)
    {
        (double sin, double cos) = Math.SinCos(double.DegreesToRadians(degrees));
        return new PdfMatrix(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>Creates a skew.</summary>
    /// <param name="alphaDegrees">The angle that skews the x axis, in degrees.</param>
    /// <param name="betaDegrees">The angle that skews the y axis, in degrees.</param>
    public static PdfMatrix Skew(double alphaDegrees, double betaDegrees) => new(
        1,
        Math.Tan(double.DegreesToRadians(alphaDegrees)),
        Math.Tan(double.DegreesToRadians(betaDegrees)),
        1,
        0,
        0);

    /// <summary>Reads a matrix from a six-element PDF array, falling back to the identity.</summary>
    /// <param name="array">The array to read.</param>
    public static PdfMatrix FromArray(PdfArray? array) =>
        array is null || array.Count < 6
            ? Identity
            : new PdfMatrix(
                array.GetNumber(0), array.GetNumber(1), array.GetNumber(2),
                array.GetNumber(3), array.GetNumber(4), array.GetNumber(5));

    /// <summary>Writes the matrix as the six-element array PDF expects.</summary>
    public PdfArray ToArray() => PdfArray.OfNumbers(A, B, C, D, E, F);

    /// <summary>Applies this transformation to a point.</summary>
    /// <param name="point">The point to map.</param>
    public PdfPoint Transform(PdfPoint point) =>
        new((A * point.X) + (C * point.Y) + E, (B * point.X) + (D * point.Y) + F);

    /// <summary>Applies this transformation to a vector, ignoring translation.</summary>
    /// <param name="dx">The horizontal component.</param>
    /// <param name="dy">The vertical component.</param>
    public PdfPoint TransformVector(double dx, double dy) =>
        new((A * dx) + (C * dy), (B * dx) + (D * dy));

    /// <summary>Composes two transformations, applying <paramref name="left"/> first.</summary>
    /// <param name="left">The transformation applied first.</param>
    /// <param name="right">The transformation applied second.</param>
    public static PdfMatrix operator *(PdfMatrix left, PdfMatrix right) => new(
        (left.A * right.A) + (left.B * right.C),
        (left.A * right.B) + (left.B * right.D),
        (left.C * right.A) + (left.D * right.C),
        (left.C * right.B) + (left.D * right.D),
        (left.E * right.A) + (left.F * right.C) + right.E,
        (left.E * right.B) + (left.F * right.D) + right.F);

    /// <summary>Composes two transformations, applying this one first.</summary>
    /// <param name="next">The transformation applied second.</param>
    public PdfMatrix Then(PdfMatrix next) => this * next;

    /// <summary>The determinant, zero when the transformation collapses the plane.</summary>
    public double Determinant => (A * D) - (B * C);

    /// <summary>
    /// The approximate uniform scale factor, used to convert line widths and glyph sizes between
    /// spaces when the transformation is close to a similarity.
    /// </summary>
    public double ScaleFactor => Math.Sqrt(Math.Abs(Determinant));

    /// <summary>Inverts the transformation.</summary>
    /// <param name="inverse">Receives the inverse when the matrix is invertible.</param>
    public bool TryInvert(out PdfMatrix inverse)
    {
        double determinant = Determinant;
        if (Math.Abs(determinant) < 1e-12)
        {
            inverse = Identity;
            return false;
        }

        double inverseDeterminant = 1 / determinant;
        double a = D * inverseDeterminant;
        double b = -B * inverseDeterminant;
        double c = -C * inverseDeterminant;
        double d = A * inverseDeterminant;
        inverse = new PdfMatrix(a, b, c, d, -((E * a) + (F * c)), -((E * b) + (F * d)));
        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{A:0.####} {B:0.####} {C:0.####} {D:0.####} {E:0.###} {F:0.###}]");
}
