using System.Globalization;
using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>
/// The <c>DA</c> string that says how an annotation's text is drawn: a font selection followed by
/// colour operators. It is a fragment of a content stream stored as a string, so reading it means
/// parsing operators and writing it means emitting them.
/// </summary>
/// <param name="FontName">The resource name of the font, such as <c>Helv</c>.</param>
/// <param name="Size">The size in points; zero asks the reader to fit the box.</param>
/// <param name="Color">The fill colour for the glyphs.</param>
internal readonly record struct DefaultAppearance(string FontName, double Size, PdfColor Color)
{
    internal static DefaultAppearance Default => new("Helv", 11, PdfColor.Black);

    internal static DefaultAppearance Read(PdfDictionary dictionary)
    {
        if (dictionary.GetTextString(AnnotationNames.DA) is not { Length: > 0 } text)
            return Default;

        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        DefaultAppearance appearance = Default;

        for (int i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "Tf" when i >= 2:
                    appearance = appearance with
                    {
                        FontName = tokens[i - 2].TrimStart('/'),
                        Size = Number(tokens[i - 1], appearance.Size),
                    };
                    break;

                case "g" when i >= 1:
                    appearance = appearance with { Color = PdfColor.Gray((float)Number(tokens[i - 1], 0)) };
                    break;

                case "rg" when i >= 3:
                    appearance = appearance with
                    {
                        Color = PdfColor.Rgb(
                            (float)Number(tokens[i - 3], 0), (float)Number(tokens[i - 2], 0), (float)Number(tokens[i - 1], 0)),
                    };
                    break;

                case "k" when i >= 4:
                    appearance = appearance with
                    {
                        Color = PdfColor.Cmyk(
                            (float)Number(tokens[i - 4], 0), (float)Number(tokens[i - 3], 0),
                            (float)Number(tokens[i - 2], 0), (float)Number(tokens[i - 1], 0)),
                    };
                    break;

                default:
                    break;
            }
        }

        return appearance;
    }

    internal static void Write(PdfDictionary dictionary, DefaultAppearance appearance) =>
        dictionary.Set(AnnotationNames.DA, PdfValue.TextString(appearance.ToString()));

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"/{FontName} {Size:0.###} Tf {ColorOperators(Color)}");

    internal static string ColorOperators(PdfColor color) => color.Kind switch
    {
        PdfColorKind.Gray => string.Create(CultureInfo.InvariantCulture, $"{color.Component0:0.###} g"),
        PdfColorKind.Rgb => string.Create(
            CultureInfo.InvariantCulture, $"{color.Component0:0.###} {color.Component1:0.###} {color.Component2:0.###} rg"),
        _ => string.Create(
            CultureInfo.InvariantCulture,
            $"{color.Component0:0.###} {color.Component1:0.###} {color.Component2:0.###} {color.Component3:0.###} k"),
    };

    private static double Number(string token, double fallback) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
}
