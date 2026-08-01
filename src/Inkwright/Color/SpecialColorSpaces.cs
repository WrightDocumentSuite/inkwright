using Inkwright.Cos;
using Inkwright.Functions;

namespace Inkwright.Color;

/// <summary>
/// An ICC-based space (§8.6.5.5). The profile itself is not interpreted; the space behaves as the
/// alternative it names, or failing that as the device space with the same number of components,
/// which is what the specification tells a reader to do.
/// </summary>
internal static class IccColorSpace
{
    internal static PdfColorSpace? Read(PdfValue value, PdfDictionary? resources, int depth)
    {
        PdfDictionary? dictionary = value.AsStream?.Dictionary ?? value.AsDictionary;
        if (dictionary is null)
            return null;

        if (dictionary[PdfNames.Alternate] is { IsNull: false } &&
            PdfColorSpace.Read(dictionary.Get(PdfNames.Alternate), resources) is { } alternate)
        {
            return alternate;
        }

        return dictionary.GetInteger(PdfNames.N) switch
        {
            1 => PdfColorSpace.DeviceGray,
            4 => PdfColorSpace.DeviceCmyk,
            3 => PdfColorSpace.DeviceRgb,
            _ => null,
        };
    }
}

/// <summary>A calibrated one-component CIE space (§8.6.5.2).</summary>
internal sealed class CalGrayColorSpace(double[] whitePoint, double gamma) : PdfColorSpace
{
    public override int ComponentCount => 1;

    public override PdfRgb ToRgb(ReadOnlySpan<double> components)
    {
        if (components.Length < 1)
            return default;

        double luminance = Math.Pow(Math.Clamp(components[0], 0, 1), gamma);
        return CieColor.ToSrgb(
            whitePoint[0] * luminance,
            whitePoint[1] * luminance,
            whitePoint[2] * luminance,
            whitePoint);
    }

    internal static CalGrayColorSpace? Read(PdfDictionary? dictionary)
    {
        if (!CieColor.TryReadWhitePoint(dictionary, out double[] whitePoint))
            return null;

        double gamma = dictionary!.GetNumber(PdfName.Get("Gamma"), 1);
        return new CalGrayColorSpace(whitePoint, gamma > 0 ? gamma : 1);
    }
}

/// <summary>A calibrated three-component CIE space (§8.6.5.3).</summary>
internal sealed class CalRgbColorSpace(double[] whitePoint, double[] gamma, double[] matrix) : PdfColorSpace
{
    public override int ComponentCount => 3;

    public override PdfRgb ToRgb(ReadOnlySpan<double> components)
    {
        if (components.Length < 3)
            return default;

        double a = Math.Pow(Math.Clamp(components[0], 0, 1), gamma[0]);
        double b = Math.Pow(Math.Clamp(components[1], 0, 1), gamma[1]);
        double c = Math.Pow(Math.Clamp(components[2], 0, 1), gamma[2]);

        // The matrix is stored column by column: XA YA ZA, XB YB ZB, XC YC ZC.
        double x = (matrix[0] * a) + (matrix[3] * b) + (matrix[6] * c);
        double y = (matrix[1] * a) + (matrix[4] * b) + (matrix[7] * c);
        double z = (matrix[2] * a) + (matrix[5] * b) + (matrix[8] * c);
        return CieColor.ToSrgb(x, y, z, whitePoint);
    }

    internal static CalRgbColorSpace? Read(PdfDictionary? dictionary)
    {
        if (!CieColor.TryReadWhitePoint(dictionary, out double[] whitePoint))
            return null;

        double[] gamma = [1, 1, 1];
        if (dictionary!.GetArray(PdfName.Get("Gamma")) is { Count: >= 3 } declaredGamma)
        {
            for (int i = 0; i < 3; i++)
            {
                double value = declaredGamma.GetNumber(i, 1);
                gamma[i] = value > 0 ? value : 1;
            }
        }

        double[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        if (dictionary.GetArray(PdfNames.Matrix) is { Count: >= 9 } declaredMatrix)
        {
            for (int i = 0; i < 9; i++)
                matrix[i] = declaredMatrix.GetNumber(i);
        }

        return new CalRgbColorSpace(whitePoint, gamma, matrix);
    }
}

/// <summary>
/// A CIE L*a*b* space (§8.6.5.4): lightness and two opponent axes, converted through the white
/// point the space declares.
/// </summary>
internal sealed class LabColorSpace(double[] whitePoint, double[] range) : PdfColorSpace
{
    public override int ComponentCount => 3;

    /// <summary>Lightness runs from 0 to 100, and the two axes over whatever the space declares.</summary>
    public override double[] DefaultDecode(int bitsPerComponent) =>
        [0, 100, range[0], range[1], range[2], range[3]];

    public override PdfRgb ToRgb(ReadOnlySpan<double> components)
    {
        if (components.Length < 3)
            return default;

        double lightness = Math.Clamp(components[0], 0, 100);
        double a = Math.Clamp(components[1], range[0], range[1]);
        double b = Math.Clamp(components[2], range[2], range[3]);

        double m = (lightness + 16) / 116;
        double l = m + (a / 500);
        double n = m - (b / 200);

        double x = whitePoint[0] * Inverse(l);
        double y = whitePoint[1] * Inverse(m);
        double z = whitePoint[2] * Inverse(n);

        return CieColor.ToSrgb(x, y, z, whitePoint);
    }

    internal static LabColorSpace? Read(PdfDictionary? dictionary)
    {
        if (dictionary is null)
            return null;

        double[] white = [0.9505, 1.0, 1.089];
        if (dictionary.GetArray(PdfNames.WhitePoint) is { Count: >= 3 } declared)
        {
            for (int i = 0; i < 3; i++)
                white[i] = declared.GetNumber(i);
        }

        double[] range = [-100, 100, -100, 100];
        if (dictionary.GetArray(PdfNames.Range) is { Count: >= 4 } declaredRange)
        {
            for (int i = 0; i < 4; i++)
                range[i] = declaredRange.GetNumber(i);
        }

        return new LabColorSpace(white, range);
    }

    /// <summary>The inverse of the cube-root companding the definition applies.</summary>
    private static double Inverse(double value) =>
        value >= 6.0 / 29 ? value * value * value : 108.0 / 841 * (value - (4.0 / 29));

}

/// <summary>Chromatically adapts CIE XYZ values to the D65 white used by sRGB.</summary>
internal static class CieColor
{
    private static ReadOnlySpan<double> D65 => [0.95047, 1.0, 1.08883];

    internal static bool TryReadWhitePoint(PdfDictionary? dictionary, out double[] whitePoint)
    {
        whitePoint = [];
        if (dictionary?.GetArray(PdfNames.WhitePoint) is not { Count: >= 3 } declared)
            return false;

        double x = declared.GetNumber(0);
        double y = declared.GetNumber(1);
        double z = declared.GetNumber(2);
        if (x <= 0 || Math.Abs(y - 1) > 0.000_001 || z <= 0)
            return false;

        whitePoint = [x, y, z];
        return true;
    }

    internal static PdfRgb ToSrgb(double x, double y, double z, ReadOnlySpan<double> whitePoint)
    {
        // Bradford adaptation keeps the declared white neutral on the fixed sRGB display white.
        (double sourceL, double sourceM, double sourceS) = Bradford(
            whitePoint[0], whitePoint[1], whitePoint[2]);
        (double targetL, double targetM, double targetS) = Bradford(D65[0], D65[1], D65[2]);
        (double l, double m, double s) = Bradford(x, y, z);

        l *= targetL / sourceL;
        m *= targetM / sourceM;
        s *= targetS / sourceS;

        double adaptedX = (0.9869929 * l) - (0.1470543 * m) + (0.1599627 * s);
        double adaptedY = (0.4323053 * l) + (0.5183603 * m) + (0.0492912 * s);
        double adaptedZ = (-0.0085287 * l) + (0.0400428 * m) + (0.9684867 * s);

        return new PdfRgb(
            ToByte(Transfer((3.2406 * adaptedX) - (1.5372 * adaptedY) - (0.4986 * adaptedZ))),
            ToByte(Transfer((-0.9689 * adaptedX) + (1.8758 * adaptedY) + (0.0415 * adaptedZ))),
            ToByte(Transfer((0.0557 * adaptedX) - (0.2040 * adaptedY) + (1.0570 * adaptedZ))));
    }

    private static (double L, double M, double S) Bradford(double x, double y, double z) =>
        ((0.8951 * x) + (0.2664 * y) - (0.1614 * z),
         (-0.7502 * x) + (1.7135 * y) + (0.0367 * z),
         (0.0389 * x) - (0.0685 * y) + (1.0296 * z));

    private static double Transfer(double value)
    {
        double clamped = Math.Clamp(value, 0, 1);
        return clamped <= 0.0031308 ? 12.92 * clamped : (1.055 * Math.Pow(clamped, 1 / 2.4)) - 0.055;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}

/// <summary>
/// An indexed space (§8.6.6.3): a palette of colours in some other space, addressed by number. The
/// samples of an image in this space are indices, not colour values, which is why it has to say so.
/// </summary>
internal sealed class IndexedColorSpace(PdfColorSpace @base, byte[] palette, int highest) : PdfColorSpace
{
    public override int ComponentCount => 1;

    public override bool IsIndexed => true;

    /// <summary>An index runs over the whole range the sample width can express.</summary>
    public override double[] DefaultDecode(int bitsPerComponent) =>
        [0, (1 << bitsPerComponent) - 1];

    public override PdfRgb ToRgb(ReadOnlySpan<double> components)
    {
        if (components.Length < 1)
            return default;

        int index = (int)Math.Clamp(Math.Round(components[0]), 0, highest);
        int width = @base.ComponentCount;
        int offset = index * width;

        Span<double> values = stackalloc double[Math.Min(width, 32)];
        for (int i = 0; i < values.Length; i++)
        {
            int position = offset + i;
            values[i] = position < palette.Length ? palette[position] / 255.0 : 0;
        }

        // Lab components are stored over their own ranges rather than the unit interval.
        if (@base is LabColorSpace)
        {
            double[] decode = @base.DefaultDecode(8);
            for (int i = 0; i < values.Length && (2 * i) + 1 < decode.Length; i++)
                values[i] = decode[2 * i] + (values[i] * (decode[(2 * i) + 1] - decode[2 * i]));
        }

        return @base.ToRgb(values);
    }

    internal static IndexedColorSpace? Read(PdfArray array, PdfDictionary? resources, int depth)
    {
        if (array.Count < 4 || PdfColorSpace.Read(array.Get(1), resources) is not { } @base)
            return null;

        int highest = (int)array.GetInteger(2);
        if (highest is < 0 or > 255)
            return null;

        PdfValue lookup = array.Get(3);
        byte[] palette = lookup.AsStream is { } stream
            ? stream.DecodedSpan().ToArray()
            : lookup.AsStringBytes.ToArray();

        _ = depth;
        return new IndexedColorSpace(@base, palette, highest);
    }
}

/// <summary>
/// A separation or device-n space (§8.6.6.4): one or more named inks, turned into a colour by the
/// tint transform the space carries. The names <c>All</c> and <c>None</c> are special: the first
/// paints every plate at once, the second paints nothing.
/// </summary>
internal sealed class SeparationColorSpace(
    int components, PdfColorSpace alternate, IReadOnlyList<PdfFunction> tint, bool paintsNothing) : PdfColorSpace
{
    public override int ComponentCount => components;

    public override PdfRgb ToRgb(ReadOnlySpan<double> values)
    {
        if (paintsNothing)
            return new PdfRgb(255, 255, 255);

        if (tint.Count == 0)
        {
            // Without a usable transform, a tint is read as how dark the ink is.
            double tone = values.Length > 0 ? 1 - Math.Clamp(values[0], 0, 1) : 1;
            byte grey = Byte(tone);
            return new PdfRgb(grey, grey, grey);
        }

        Span<double> converted = stackalloc double[Math.Max(1, alternate.ComponentCount)];
        PdfFunction.EvaluateMany(tint, values, converted);
        return alternate.ToRgb(converted);
    }

    internal static SeparationColorSpace? Read(PdfArray array, PdfDictionary? resources, int depth, int components)
    {
        if (array.Count < 4)
            return null;

        int count = components;
        bool nothing = false;

        if (components == 1)
        {
            string? name = array.GetName(1)?.Value;
            nothing = name == "None";
        }
        else
        {
            if (array.GetArray(1) is not { Count: > 0 } names)
                return null;

            count = names.Count;

            // A device-n space paints nothing only when every one of its colorants is None.
            nothing = true;
            for (int i = 0; i < names.Count && nothing; i++)
                nothing = names.GetName(i)?.Value == "None";
        }

        if (PdfColorSpace.Read(array.Get(2), resources) is not { } alternate)
            return null;

        _ = depth;
        return new SeparationColorSpace(count, alternate, PdfFunction.ReadMany(array.Get(3)), nothing);
    }
}
