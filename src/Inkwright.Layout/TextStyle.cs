using Inkwright.Fonts;

namespace Inkwright.Layout;

/// <summary>How a run of text is aligned inside the space it was given.</summary>
public enum TextAlignment
{
    /// <summary>Flush left, ragged right.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Flush right, ragged left.</summary>
    Right,

    /// <summary>Flush on both sides, with the space distributed between words.</summary>
    Justify,
}

/// <summary>
/// The appearance of a run of text. Styles are immutable records, so a paragraph can inherit one and
/// change a single aspect without disturbing anything else, and equal styles compare equal for free.
/// </summary>
public sealed record TextStyle
{
    /// <summary>Eleven point Helvetica in black.</summary>
    public static TextStyle Default { get; } = new();

    /// <summary>The font to use, overriding <see cref="FontFamily"/> when set.</summary>
    public PdfFont? Font { get; init; }

    /// <summary>The family to resolve, such as <c>Helvetica</c> or a font installed on the machine.</summary>
    public string? FontFamily { get; init; }

    /// <summary>The size in points.</summary>
    public double Size { get; init; } = 11;

    /// <summary>The colour glyphs are filled with.</summary>
    public PdfColor Color { get; init; } = PdfColor.Black;

    /// <summary>Whether to pick a bold face.</summary>
    public bool Bold { get; init; }

    /// <summary>Whether to pick an italic face.</summary>
    public bool Italic { get; init; }

    /// <summary>Whether to draw a line under the text.</summary>
    public bool Underline { get; init; }

    /// <summary>Whether to draw a line through the text.</summary>
    public bool Strikethrough { get; init; }

    /// <summary>The distance between baselines, as a multiple of the size.</summary>
    public double LineHeight { get; init; } = 1.25;

    /// <summary>Extra space inserted after every glyph, in points.</summary>
    public double LetterSpacing { get; init; }

    /// <summary>How lines are aligned inside their box.</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>The distance between baselines in points.</summary>
    public double LineSpacing => Size * LineHeight;
}

/// <summary>Fluent shorthands for building a <see cref="TextStyle"/> from another one.</summary>
public static class TextStyleExtensions
{
    extension(TextStyle style)
    {
        /// <summary>Sets the size in points.</summary>
        /// <param name="size">The size.</param>
        public TextStyle FontSize(double size) => style with { Size = size };

        /// <summary>Sets the family to resolve.</summary>
        /// <param name="family">The family name.</param>
        public TextStyle FontFamily(string family) => style with { FontFamily = family, Font = null };

        /// <summary>Uses a font that has already been created.</summary>
        /// <param name="font">The font.</param>
        public TextStyle Font(PdfFont font) => style with { Font = font };

        /// <summary>Sets the fill colour.</summary>
        /// <param name="color">The colour.</param>
        public TextStyle FontColor(PdfColor color) => style with { Color = color };

        /// <summary>Sets the fill colour from a hexadecimal string.</summary>
        /// <param name="hex">A colour such as <c>#2278D4</c>.</param>
        public TextStyle FontColor(string hex) => style with { Color = PdfColor.Parse(hex) };

        /// <summary>Picks a bold face.</summary>
        public TextStyle Bold() => style with { Bold = true };

        /// <summary>Picks an italic face.</summary>
        public TextStyle Italic() => style with { Italic = true };

        /// <summary>Draws a line under the text.</summary>
        public TextStyle Underline() => style with { Underline = true };

        /// <summary>Draws a line through the text.</summary>
        public TextStyle Strikethrough() => style with { Strikethrough = true };

        /// <summary>Sets the distance between baselines as a multiple of the size.</summary>
        /// <param name="factor">The multiple.</param>
        public TextStyle LineHeight(double factor) => style with { LineHeight = factor };

        /// <summary>Sets the extra space after every glyph.</summary>
        /// <param name="points">The spacing in points.</param>
        public TextStyle LetterSpacing(double points) => style with { LetterSpacing = points };

        /// <summary>Aligns lines to the left.</summary>
        public TextStyle AlignLeft() => style with { Alignment = TextAlignment.Left };

        /// <summary>Centres lines.</summary>
        public TextStyle AlignCenter() => style with { Alignment = TextAlignment.Center };

        /// <summary>Aligns lines to the right.</summary>
        public TextStyle AlignRight() => style with { Alignment = TextAlignment.Right };

        /// <summary>Spreads lines to both margins.</summary>
        public TextStyle Justify() => style with { Alignment = TextAlignment.Justify };
    }
}
