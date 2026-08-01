using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Inkwright.Annotations;

/// <summary>
/// The vocabulary of XFDF (ISO 19444-1), which is the XML spelling of the same annotations PDF
/// keeps in dictionaries. Element names stand for subtypes and attribute names for dictionary
/// keys, so the whole translation is a pair of lookup tables plus the handful of value formats the
/// standard writes differently: colours as <c>#RRGGBB</c>, flags as words, numbers as lists.
/// </summary>
internal static class Xfdf
{
    internal static readonly XNamespace Namespace = "http://ns.adobe.com/xfdf/";

    /// <summary>The annotation subtype an element stands for, or nothing when it stands for none.</summary>
    /// <param name="element">The element's local name, as XFDF spells it.</param>
    internal static string? SubtypeOf(string element) => element switch
    {
        "text" => "Text",
        "highlight" => "Highlight",
        "underline" => "Underline",
        "strikeout" => "StrikeOut",
        "squiggly" => "Squiggly",
        "freetext" => "FreeText",
        "line" => "Line",
        "circle" => "Circle",
        "square" => "Square",
        "polygon" => "Polygon",
        "polyline" => "PolyLine",
        "ink" => "Ink",
        "stamp" => "Stamp",
        "caret" => "Caret",
        "fileattachment" => "FileAttachment",
        _ => null,
    };

    /// <summary>The element name a subtype is written as, or nothing when XFDF has no element for it.</summary>
    /// <param name="subtype">The annotation subtype, as the dictionary spells it.</param>
    internal static string? ElementOf(string subtype) => subtype switch
    {
        "Text" => "text",
        "Highlight" => "highlight",
        "Underline" => "underline",
        "StrikeOut" => "strikeout",
        "Squiggly" => "squiggly",
        "FreeText" => "freetext",
        "Line" => "line",
        "Circle" => "circle",
        "Square" => "square",
        "Polygon" => "polygon",
        "PolyLine" => "polyline",
        "Ink" => "ink",
        "Stamp" => "stamp",
        "Caret" => "caret",
        "FileAttachment" => "fileattachment",
        _ => null,
    };

    /// <summary>Reads the comma-separated flag words of table 8 into the bits of table 165.</summary>
    /// <param name="text">The attribute value, such as <c>print,nozoom</c>.</param>
    internal static PdfAnnotationFlags ParseFlags(string text)
    {
        PdfAnnotationFlags flags = PdfAnnotationFlags.None;

        foreach (string word in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            flags |= word.ToLowerInvariant() switch
            {
                "invisible" => PdfAnnotationFlags.Invisible,
                "hidden" => PdfAnnotationFlags.Hidden,
                "print" => PdfAnnotationFlags.Print,
                "nozoom" => PdfAnnotationFlags.NoZoom,
                "norotate" => PdfAnnotationFlags.NoRotate,
                "noview" => PdfAnnotationFlags.NoView,
                "readonly" => PdfAnnotationFlags.ReadOnly,
                "locked" => PdfAnnotationFlags.Locked,
                "togglenoview" => PdfAnnotationFlags.ToggleNoView,
                "lockedcontents" => PdfAnnotationFlags.LockedContents,
                _ => PdfAnnotationFlags.None,
            };
        }

        return flags;
    }

    /// <summary>Writes the bits of table 165 as the comma-separated words of table 8.</summary>
    /// <param name="flags">The annotation flags.</param>
    internal static string FormatFlags(PdfAnnotationFlags flags)
    {
        List<string> words = [];

        Add(PdfAnnotationFlags.Invisible, "invisible");
        Add(PdfAnnotationFlags.Hidden, "hidden");
        Add(PdfAnnotationFlags.Print, "print");
        Add(PdfAnnotationFlags.NoZoom, "nozoom");
        Add(PdfAnnotationFlags.NoRotate, "norotate");
        Add(PdfAnnotationFlags.NoView, "noview");
        Add(PdfAnnotationFlags.ReadOnly, "readonly");
        Add(PdfAnnotationFlags.Locked, "locked");
        Add(PdfAnnotationFlags.ToggleNoView, "togglenoview");
        Add(PdfAnnotationFlags.LockedContents, "lockedcontents");

        return string.Join(',', words);

        void Add(PdfAnnotationFlags flag, string word)
        {
            if (flags.HasFlag(flag))
                words.Add(word);
        }
    }

    /// <summary>Reads a <c>#RRGGBB</c> colour, or nothing when the text is not one.</summary>
    /// <param name="text">The attribute value.</param>
    internal static PdfColor? ParseColor(string text)
    {
        try
        {
            return PdfColor.Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a colour the only way XFDF spells one. Grey and CMYK are converted rather than
    /// dropped: a comment whose colour cannot be written is a comment that changes colour in
    /// transit, which is worse than an approximation.
    /// </summary>
    /// <param name="color">The colour to write.</param>
    internal static string FormatColor(PdfColor color)
    {
        (float red, float green, float blue) = color.Kind switch
        {
            PdfColorKind.Gray => (color.Component0, color.Component0, color.Component0),
            PdfColorKind.Cmyk => (
                (1 - color.Component0) * (1 - color.Component3),
                (1 - color.Component1) * (1 - color.Component3),
                (1 - color.Component2) * (1 - color.Component3)),
            _ => (color.Component0, color.Component1, color.Component2),
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"#{Byte(red):X2}{Byte(green):X2}{Byte(blue):X2}");

        static int Byte(float component) => (int)Math.Round(Math.Clamp(component, 0, 1) * 255);
    }

    /// <summary>Reads a list of numbers, however the producer chose to separate them.</summary>
    /// <param name="text">The attribute or element text.</param>
    internal static List<double> ParseNumbers(string text)
    {
        List<double> numbers = [];

        foreach (Range range in text.AsSpan().SplitAny(",;\r\n\t "))
        {
            ReadOnlySpan<char> part = text.AsSpan()[range].Trim();
            if (!part.IsEmpty && double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                numbers.Add(value);
        }

        return numbers;
    }

    /// <summary>
    /// Escapes the characters XML cannot carry (ISO 19444-1 §5.8.2). A PDF string is a string of
    /// bytes with no character interpretation, so it may hold codes that no XML document may
    /// contain at all; the standard spells those the way PDF itself does, as an octal escape.
    /// Tab, line feed and carriage return are left alone, because XML can carry them.
    /// </summary>
    /// <param name="text">The text as the dictionary holds it.</param>
    internal static string Escape(string text)
    {
        if (!NeedsEscaping(text))
            return text;

        var escaped = new StringBuilder(text.Length + 8);

        foreach (char c in text)
        {
            if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
                escaped.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else
                escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>Reads back what <see cref="Escape"/> wrote.</summary>
    /// <param name="text">The text as the payload spells it.</param>
    internal static string Unescape(string text)
    {
        int start = text.IndexOf('\\', StringComparison.Ordinal);
        if (start < 0)
            return text;

        var plain = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 3 < text.Length && Octal(text.AsSpan(i + 1, 3)) is { } value and < 0x20)
            {
                plain.Append((char)value);
                i += 3;
                continue;
            }

            plain.Append(text[i]);
        }

        return plain.ToString();
    }

    private static bool NeedsEscaping(string text)
    {
        foreach (char c in text)
        {
            if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
                return true;
        }

        return false;
    }

    private static int? Octal(ReadOnlySpan<char> digits)
    {
        int value = 0;

        foreach (char c in digits)
        {
            if (c is < '0' or > '7')
                return null;

            value = (value * 8) + (c - '0');
        }

        return value;
    }

    /// <summary>Writes numbers the way XFDF lists them, without exponents or trailing zeroes.</summary>
    /// <param name="numbers">The numbers to write.</param>
    internal static string FormatNumbers(IEnumerable<double> numbers)
    {
        var text = new StringBuilder();

        foreach (double number in numbers)
        {
            if (text.Length > 0)
                text.Append(',');

            text.Append(number.ToString("0.####", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
