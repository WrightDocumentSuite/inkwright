using Inkwright.Cos;
using Inkwright.Functions;

namespace Inkwright.Color;

/// <summary>A colour as three bytes, which is what any consumer of decoded image data wants.</summary>
/// <param name="R">The red component.</param>
/// <param name="G">The green component.</param>
/// <param name="B">The blue component.</param>
public readonly record struct PdfRgb(byte R, byte G, byte B);

/// <summary>
/// A colour space (§8.6): how many numbers name a colour, what range each runs over, and how to
/// turn them into something that can be shown. Every space is reduced to red, green and blue here,
/// because that is what an image consumer needs; the device spaces convert directly, the CIE-based
/// ones through their own definitions, and the special spaces through the functions they carry.
/// </summary>
public abstract class PdfColorSpace
{
    /// <summary>How deeply a space may name another before it is taken to be looping.</summary>
    private const int MaxDepth = 8;

    /// <summary>The one-component grey space.</summary>
    public static PdfColorSpace DeviceGray { get; } = new DeviceColorSpace(1);

    /// <summary>The three-component red, green and blue space.</summary>
    public static PdfColorSpace DeviceRgb { get; } = new DeviceColorSpace(3);

    /// <summary>The four-component cyan, magenta, yellow and black space.</summary>
    public static PdfColorSpace DeviceCmyk { get; } = new DeviceColorSpace(4);

    /// <summary>How many numbers name one colour in this space.</summary>
    public abstract int ComponentCount { get; }

    /// <summary>Whether the space addresses a palette, so that samples are indices rather than values.</summary>
    public virtual bool IsIndexed => false;

    /// <summary>
    /// The interval each component runs over by default, as pairs of bounds. This is what an image
    /// with no <c>Decode</c> array of its own uses (§8.9.5.2, Table 90).
    /// </summary>
    /// <param name="bitsPerComponent">How many bits each sample takes.</param>
    public virtual double[] DefaultDecode(int bitsPerComponent)
    {
        double[] decode = new double[ComponentCount * 2];
        for (int i = 0; i < ComponentCount; i++)
            decode[(2 * i) + 1] = 1;

        return decode;
    }

    /// <summary>Converts one colour to red, green and blue.</summary>
    /// <param name="components">The colour, one number per component.</param>
    public abstract PdfRgb ToRgb(ReadOnlySpan<double> components);

    /// <summary>Reads a colour space from a resource entry.</summary>
    /// <param name="value">The name or array that names the space.</param>
    /// <param name="resources">The resource dictionary, for spaces named indirectly.</param>
    /// <returns>The space, or <see langword="null"/> when it is not one this library reads.</returns>
    public static PdfColorSpace? Read(PdfValue value, PdfDictionary? resources = null) =>
        Read(value, resources, 0);

    private protected static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static PdfColorSpace? Read(PdfValue value, PdfDictionary? resources, int depth)
    {
        if (depth > MaxDepth)
            return null;

        if (value.AsName is { } name)
            return Named(name.Value, resources, depth);

        if (value.AsArray is not { Count: > 0 } array)
            return null;

        string family = array.GetName(0)?.Value ?? string.Empty;

        return family switch
        {
            "DeviceGray" or "G" => DeviceGray,
            "DeviceRGB" or "RGB" => DeviceRgb,
            "DeviceCMYK" or "CMYK" => DeviceCmyk,
            "CalGray" => CalGrayColorSpace.Read(array.GetDictionary(1)),
            "CalRGB" => CalRgbColorSpace.Read(array.GetDictionary(1)),
            "Lab" => LabColorSpace.Read(array.GetDictionary(1)),
            "ICCBased" => IccColorSpace.Read(array.Get(1), resources, depth),
            "Indexed" or "I" => IndexedColorSpace.Read(array, resources, depth),
            "Separation" => SeparationColorSpace.Read(array, resources, depth, components: 1),
            "DeviceN" => SeparationColorSpace.Read(array, resources, depth, components: 0),
            "Pattern" => array.Count > 1 ? Read(array.Get(1), resources, depth + 1) : null,
            _ => null,
        };
    }

    private static PdfColorSpace? Named(string name, PdfDictionary? resources, int depth) => name switch
    {
        "DeviceGray" or "G" or "CalGray" => DeviceGray,
        "DeviceRGB" or "RGB" => DeviceRgb,
        "DeviceCMYK" or "CMYK" => DeviceCmyk,
        "Pattern" => null,
        _ => resources?.GetDictionary(PdfNames.ColorSpace)?.Get(PdfName.Get(name)) is { IsNull: false } entry
            ? Read(entry, resources, depth + 1)
            : null,
    };

    /// <summary>The grey, red-green-blue and cyan-magenta-yellow-black spaces of §8.6.4.</summary>
    private sealed class DeviceColorSpace(int components) : PdfColorSpace
    {
        public override int ComponentCount => components;

        public override PdfRgb ToRgb(ReadOnlySpan<double> values) => components switch
        {
            1 when values.Length >= 1 => Grey(values[0]),
            3 when values.Length >= 3 => new PdfRgb(Byte(values[0]), Byte(values[1]), Byte(values[2])),
            4 when values.Length >= 4 => Cmyk(values),
            _ => default,
        };

        private static PdfRgb Grey(double value)
        {
            byte grey = Byte(value);
            return new PdfRgb(grey, grey, grey);
        }

        /// <summary>The subtractive conversion of §8.6.4.4: each ink takes light away.</summary>
        private static PdfRgb Cmyk(ReadOnlySpan<double> values) => new(
            Byte(1 - Math.Min(1, values[0] + values[3])),
            Byte(1 - Math.Min(1, values[1] + values[3])),
            Byte(1 - Math.Min(1, values[2] + values[3])));
    }
}
