using System.Globalization;

namespace Inkwright;

/// <summary>The device colour spaces a colour can be expressed in.</summary>
public enum PdfColorKind : byte
{
    /// <summary>A single grey component.</summary>
    Gray,

    /// <summary>Red, green and blue components.</summary>
    Rgb,

    /// <summary>Cyan, magenta, yellow and black components.</summary>
    Cmyk,
}

/// <summary>
/// A colour in one of PDF's device colour spaces, with components in the range zero to one. The
/// type stays in the space it was created in, because converting silently is how CMYK documents end
/// up with the wrong black.
/// </summary>
public readonly record struct PdfColor
{
    private PdfColor(PdfColorKind kind, float c0, float c1, float c2, float c3)
    {
        Kind = kind;
        Component0 = Clamp(c0);
        Component1 = Clamp(c1);
        Component2 = Clamp(c2);
        Component3 = Clamp(c3);
    }

    /// <summary>Which colour space the components belong to.</summary>
    public PdfColorKind Kind { get; }

    /// <summary>Grey, red or cyan, depending on the space.</summary>
    public float Component0 { get; }

    /// <summary>Green or magenta, depending on the space.</summary>
    public float Component1 { get; }

    /// <summary>Blue or yellow, depending on the space.</summary>
    public float Component2 { get; }

    /// <summary>Black, in CMYK only.</summary>
    public float Component3 { get; }

    /// <summary>How many components the space uses.</summary>
    public int ComponentCount => Kind switch
    {
        PdfColorKind.Gray => 1,
        PdfColorKind.Rgb => 3,
        _ => 4,
    };

    /// <summary>Creates a grey.</summary>
    /// <param name="value">Zero for black, one for white.</param>
    public static PdfColor Gray(float value) => new(PdfColorKind.Gray, value, 0, 0, 0);

    /// <summary>Creates an RGB colour from components in the range zero to one.</summary>
    /// <param name="red">The red component.</param>
    /// <param name="green">The green component.</param>
    /// <param name="blue">The blue component.</param>
    public static PdfColor Rgb(float red, float green, float blue) =>
        new(PdfColorKind.Rgb, red, green, blue, 0);

    /// <summary>Creates a CMYK colour from components in the range zero to one.</summary>
    /// <param name="cyan">The cyan component.</param>
    /// <param name="magenta">The magenta component.</param>
    /// <param name="yellow">The yellow component.</param>
    /// <param name="black">The black component.</param>
    public static PdfColor Cmyk(float cyan, float magenta, float yellow, float black) =>
        new(PdfColorKind.Cmyk, cyan, magenta, yellow, black);

    /// <summary>Creates an RGB colour from a packed <c>0xRRGGBB</c> value.</summary>
    /// <param name="rgb">The packed colour; any alpha byte is ignored.</param>
    public static PdfColor FromRgb(uint rgb) =>
        Rgb(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    /// <summary>Parses a CSS-style hex colour such as <c>#2278D4</c> or <c>#abc</c>.</summary>
    /// <param name="hex">The colour text, with or without the leading hash.</param>
    public static PdfColor Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        ReadOnlySpan<char> text = hex.AsSpan().Trim();
        if (text[0] == '#')
            text = text[1..];

        if (text.Length == 3)
        {
            Span<char> expanded = stackalloc char[6];
            for (int i = 0; i < 3; i++)
            {
                expanded[i * 2] = text[i];
                expanded[(i * 2) + 1] = text[i];
            }

            text = expanded.ToString();
        }

        if (text.Length is not (6 or 8) ||
            !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            throw new FormatException($"'{hex}' is not a hexadecimal colour.");
        }

        return FromRgb(value);
    }

    /// <summary>Black.</summary>
    public static PdfColor Black => Gray(0);

    /// <summary>White.</summary>
    public static PdfColor White => Gray(1);

    /// <summary>Pure red.</summary>
    public static PdfColor Red => Rgb(1, 0, 0);

    /// <summary>Pure green.</summary>
    public static PdfColor Green => Rgb(0, 1, 0);

    /// <summary>Pure blue.</summary>
    public static PdfColor Blue => Rgb(0, 0, 1);

    /// <summary>An approximation of this colour in RGB, for measuring contrast and for previews.</summary>
    public (float Red, float Green, float Blue) ToRgb() => Kind switch
    {
        PdfColorKind.Gray => (Component0, Component0, Component0),
        PdfColorKind.Rgb => (Component0, Component1, Component2),
        _ => ((1 - Component0) * (1 - Component3),
              (1 - Component1) * (1 - Component3),
              (1 - Component2) * (1 - Component3)),
    };

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        PdfColorKind.Gray => string.Create(CultureInfo.InvariantCulture, $"gray({Component0:0.###})"),
        PdfColorKind.Rgb => string.Create(CultureInfo.InvariantCulture,
            $"rgb({Component0:0.###}, {Component1:0.###}, {Component2:0.###})"),
        _ => string.Create(CultureInfo.InvariantCulture,
            $"cmyk({Component0:0.###}, {Component1:0.###}, {Component2:0.###}, {Component3:0.###})"),
    };

    private static float Clamp(float value) => float.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
