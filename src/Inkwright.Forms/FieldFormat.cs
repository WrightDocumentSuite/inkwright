using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Inkwright.Cos;

namespace Inkwright.Forms;

/// <summary>The kinds of formatting Acrobat's own form scripts apply to a field's value.</summary>
public enum FieldFormatKind
{
    /// <summary>The value is shown exactly as it is stored.</summary>
    None,

    /// <summary>A number, with separators, a currency symbol and a choice of negative styles.</summary>
    Number,

    /// <summary>A fraction shown as a percentage.</summary>
    Percent,

    /// <summary>A date, in one of Acrobat's picture formats.</summary>
    Date,

    /// <summary>A telephone number, postal code or social security number.</summary>
    Special,
}

/// <summary>
/// The display format a field asks for. Acrobat stores it not as data but as a call to one of its
/// built-in scripts in the field's <c>/AA /F</c> action, so a document that formats a total as
/// <c>$1,234.50</c> is really carrying the line <c>AFNumber_Format(2, 0, 0, 0, "$", true)</c>.
/// This reads those calls without running any JavaScript, which is what lets a filled field look
/// right in viewers that have no scripting engine at all.
/// </summary>
/// <remarks>
/// The formats follow <c>src/scripting_api/aform.js</c> of pdf.js, which Mozilla licenses under
/// Apache 2.0.
/// </remarks>
public sealed partial record FieldFormat
{
    /// <summary>The formats <c>AFDate_Format</c> selects by number.</summary>
    private static readonly string[] DatePictures =
    [
        "m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy",
        "yy-mm-dd", "mmm-yy", "mmmm-yy", "mmm d, yyyy", "mmmm d, yyyy",
        "m/d/yy h:MM tt", "m/d/yy HH:MM",
    ];

    /// <summary>The formats <c>AFTime_Format</c> selects by number.</summary>
    private static readonly string[] TimePictures = ["HH:MM", "h:MM tt", "HH:MM:ss", "h:MM:ss tt"];

    /// <summary>The separator pairs a style number selects: thousands first, then decimal.</summary>
    private static readonly (string Thousands, string Decimal)[] Separators =
    [
        (",", "."), ("", "."), (".", ","), ("", ","), ("'", "."),
    ];

    /// <summary>A field that is shown as it is stored.</summary>
    public static FieldFormat None { get; } = new();

    /// <summary>Which of Acrobat's formats applies.</summary>
    public FieldFormatKind Kind { get; init; }

    /// <summary>How many digits follow the decimal separator.</summary>
    public int Decimals { get; init; } = 2;

    /// <summary>Which pair of separators to use, from 0 to 4.</summary>
    public int SeparatorStyle { get; init; }

    /// <summary>
    /// How a negative number is shown: 0 shows a minus sign, 1 colours it red, 2 puts it in
    /// parentheses, and 3 does both.
    /// </summary>
    public int NegativeStyle { get; init; }

    /// <summary>The currency symbol, if any.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>Whether the currency symbol goes before the number rather than after it.</summary>
    public bool CurrencyBefore { get; init; } = true;

    /// <summary>The picture string a date or time format uses, such as <c>mm/dd/yyyy</c>.</summary>
    public string Picture { get; init; } = string.Empty;

    /// <summary>Which of the special formats applies: 0 zip, 1 zip+4, 2 phone, 3 social security.</summary>
    public int SpecialKind { get; init; }

    /// <summary>Whether this format changes anything.</summary>
    public bool IsDefined => Kind != FieldFormatKind.None;

    /// <summary>Reads the format a field declares in its additional-actions dictionary.</summary>
    /// <param name="field">The field to inspect.</param>
    /// <returns>The format, or <see cref="None"/> when the field asks for none.</returns>
    public static FieldFormat Read(PdfFormField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        PdfDictionary? actions = field.Inherited(PdfName.Get("AA")).AsDictionary;
        if (actions?.GetDictionary(PdfName.Get("F")) is not { } format)
            return None;

        return Parse(Script(format));
    }

    /// <summary>Reads a format out of a line of Acrobat's own form script.</summary>
    /// <param name="script">The script body of a format action.</param>
    public static FieldFormat Parse(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return None;

        if (NumberCall().Match(script) is { Success: true } number)
        {
            return new FieldFormat
            {
                Kind = FieldFormatKind.Number,
                Decimals = Integer(number.Groups[1].Value, 2),
                SeparatorStyle = Math.Clamp(Integer(number.Groups[2].Value, 0), 0, 4),
                NegativeStyle = Integer(number.Groups[3].Value, 0),
                Currency = Unquote(number.Groups[5].Value),
                CurrencyBefore = !string.Equals(number.Groups[6].Value.Trim(), "false", StringComparison.Ordinal),
            };
        }

        if (PercentCall().Match(script) is { Success: true } percent)
        {
            return new FieldFormat
            {
                Kind = FieldFormatKind.Percent,
                Decimals = Integer(percent.Groups[1].Value, 2),
                SeparatorStyle = Math.Clamp(Integer(percent.Groups[2].Value, 0), 0, 4),
                CurrencyBefore = string.Equals(percent.Groups[3].Value.Trim(), "true", StringComparison.Ordinal),
            };
        }

        if (DateExCall().Match(script) is { Success: true } dateEx)
            return new FieldFormat { Kind = FieldFormatKind.Date, Picture = Unquote(dateEx.Groups[1].Value) };

        if (DateCall().Match(script) is { Success: true } date)
            return new FieldFormat { Kind = FieldFormatKind.Date, Picture = Pick(DatePictures, date.Groups[1].Value) };

        if (TimeCall().Match(script) is { Success: true } time)
            return new FieldFormat { Kind = FieldFormatKind.Date, Picture = Pick(TimePictures, time.Groups[1].Value) };

        if (SpecialCall().Match(script) is { Success: true } special)
            return new FieldFormat { Kind = FieldFormatKind.Special, SpecialKind = Integer(special.Groups[1].Value, 0) };

        return None;
    }

    /// <summary>Renders a stored value the way the format asks for it.</summary>
    /// <param name="value">The value as stored in the field.</param>
    /// <returns>The text a viewer would show.</returns>
    public string Apply(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return Kind == FieldFormatKind.Percent ? "%" : string.Empty;

        return Kind switch
        {
            FieldFormatKind.Number => FormatNumber(value),
            FieldFormatKind.Percent => FormatPercent(value),
            FieldFormatKind.Date => FormatDate(value),
            FieldFormatKind.Special => FormatSpecial(value),
            _ => value,
        };
    }

    /// <summary>
    /// Reads a number the way Acrobat does, which is leniently: a value that already carries
    /// separators, a currency symbol or trailing text still yields the number inside it.
    /// </summary>
    /// <remarks>
    /// Which separator is the decimal point is decided by what is there. Text carrying both kinds
    /// says so outright — the rightmost is the point and the other groups digits. Text carrying one
    /// kind more than once can only be grouping it. A single separator is read as the decimal
    /// point, which is what Acrobat's own <c>AFMakeNumber</c> does and means a lone
    /// <c>1,234</c> reads as one and a bit rather than as a thousand.
    /// </remarks>
    /// <param name="value">The text to read.</param>
    /// <param name="number">Receives the number.</param>
    public static bool TryReadNumber(string? value, out double number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int periods = value.Count(c => c == '.');
        int commas = value.Count(c => c == ',');

        char point = (periods, commas) switch
        {
            (> 0, > 0) => value.LastIndexOf('.') > value.LastIndexOf(',') ? '.' : ',',
            (1, 0) => '.',
            (0, 1) => ',',
            _ => '\0',
        };

        var digits = new StringBuilder(value.Length);
        bool seenPoint = false;

        foreach (char c in value)
        {
            if (char.IsAsciiDigit(c))
                digits.Append(c);
            else if (c is '-' or '+' && digits.Length == 0)
                digits.Append(c);
            else if (c == point && !seenPoint)
            {
                digits.Append('.');
                seenPoint = true;
            }
        }

        return double.TryParse(digits.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private string FormatNumber(string value)
    {
        if (!TryReadNumber(value, out double number))
            return string.Empty;

        bool negative = number < 0;
        bool parenthesised = negative && NegativeStyle is 2 or 3;
        string body = Group(Math.Abs(number));

        var text = new StringBuilder(body.Length + 8);

        if (negative && !parenthesised)
            text.Append('-');

        if (parenthesised)
            text.Append('(');

        if (CurrencyBefore)
            text.Append(Currency);

        text.Append(body);

        if (!CurrencyBefore)
            text.Append(Currency);

        if (parenthesised)
            text.Append(')');

        return text.ToString();
    }

    private string FormatPercent(string value)
    {
        if (!TryReadNumber(value, out double number))
            return "%";

        string body = Group(Math.Abs(number * 100));
        string signed = number < 0 ? "-" + body : body;

        return CurrencyBefore ? "%" + signed : signed + "%";
    }

    private string FormatDate(string value)
    {
        if (!TryReadDate(value, out DateTime date))
            return value;

        return Render(date, Picture);
    }

    private string FormatSpecial(string value)
    {
        string digits = new([.. value.Where(char.IsAsciiDigit)]);

        return SpecialKind switch
        {
            0 when digits.Length == 5 => digits,
            1 when digits.Length == 9 => $"{digits[..5]}-{digits[5..]}",
            2 when digits.Length == 10 => $"({digits[..3]}) {digits[3..6]}-{digits[6..]}",
            2 when digits.Length == 7 => $"{digits[..3]}-{digits[3..]}",
            3 when digits.Length == 9 => $"{digits[..3]}-{digits[3..5]}-{digits[5..]}",
            _ => value,
        };
    }

    /// <summary>Writes a non-negative number with the separators the style selects.</summary>
    private string Group(double number)
    {
        (string thousands, string point) = Separators[Math.Clamp(SeparatorStyle, 0, Separators.Length - 1)];
        int decimals = Math.Clamp(Decimals, 0, 15);

        // Acrobat rounds a half away from zero, where .NET's default would round it to even.
        double rounded = Math.Round(number, decimals, MidpointRounding.AwayFromZero);
        string plain = rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int split = plain.IndexOf('.', StringComparison.Ordinal);
        string whole = split < 0 ? plain : plain[..split];
        string fraction = split < 0 ? string.Empty : plain[(split + 1)..];

        if (thousands.Length > 0)
        {
            var grouped = new StringBuilder(whole.Length + (whole.Length / 3 * thousands.Length));
            for (int i = 0; i < whole.Length; i++)
            {
                if (i > 0 && (whole.Length - i) % 3 == 0)
                    grouped.Append(thousands);

                grouped.Append(whole[i]);
            }

            whole = grouped.ToString();
        }

        return fraction.Length > 0 ? whole + point + fraction : whole;
    }

    /// <summary>
    /// Reads a date from whatever a field holds. A value written by a viewer is usually already in
    /// the picture format, but one filled in programmatically is often an ISO date.
    /// </summary>
    private static bool TryReadDate(string value, out DateTime date)
    {
        // A plain date is tried first: the PDF date reader is forgiving enough to accept the leading
        // digits of an ISO date as a bare year and report the first of January.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
            DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        if (PdfDate.TryParse(value, out DateTimeOffset stamp))
        {
            date = stamp.DateTime;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renders a date through one of Acrobat's picture strings, where <c>m</c> is the month,
    /// <c>d</c> the day, <c>y</c> the year, <c>H</c> and <c>h</c> the hour, <c>M</c> the minute,
    /// <c>s</c> the second and <c>tt</c> the meridiem.
    /// </summary>
    private static string Render(DateTime date, string picture)
    {
        var text = new StringBuilder(picture.Length + 8);

        for (int i = 0; i < picture.Length;)
        {
            char token = picture[i];
            int run = 1;
            while (i + run < picture.Length && picture[i + run] == token)
                run++;

            text.Append(Token(date, token, run, picture));
            i += run;
        }

        return text.ToString();
    }

    private static string Token(DateTime date, char token, int run, string picture) => token switch
    {
        'm' => run switch
        {
            1 => date.Month.ToString(CultureInfo.InvariantCulture),
            2 => date.Month.ToString("D2", CultureInfo.InvariantCulture),
            3 => date.ToString("MMM", CultureInfo.InvariantCulture),
            _ => date.ToString("MMMM", CultureInfo.InvariantCulture),
        },
        'd' => run == 1
            ? date.Day.ToString(CultureInfo.InvariantCulture)
            : date.Day.ToString("D2", CultureInfo.InvariantCulture),
        'y' => run <= 2
            ? (date.Year % 100).ToString("D2", CultureInfo.InvariantCulture)
            : date.Year.ToString("D4", CultureInfo.InvariantCulture),
        'H' => date.Hour.ToString(run == 1 ? "D1" : "D2", CultureInfo.InvariantCulture),
        'h' => Hour12(date).ToString(run == 1 ? "D1" : "D2", CultureInfo.InvariantCulture),
        'M' => date.Minute.ToString(run == 1 ? "D1" : "D2", CultureInfo.InvariantCulture),
        's' => date.Second.ToString(run == 1 ? "D1" : "D2", CultureInfo.InvariantCulture),
        't' => date.Hour < 12 ? Meridiem("am", run) : Meridiem("pm", run),
        _ => new string(token, run),
    };

    private static string Meridiem(string text, int run) => run == 1 ? text[..1] : text;

    private static int Hour12(DateTime date) => date.Hour % 12 == 0 ? 12 : date.Hour % 12;

    /// <summary>The body of a JavaScript action, which is either a string or a stream.</summary>
    private static string? Script(PdfDictionary action)
    {
        PdfValue js = action.Get(PdfName.Get("JS"));

        if (js.AsStream is { } stream)
            return Encoding.UTF8.GetString(stream.Decode().Span);

        return js.AsTextString;
    }

    private static int Integer(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();

        if (trimmed.Length >= 2 && trimmed[0] is '"' or '\'' && trimmed[^1] == trimmed[0])
            return trimmed[1..^1];

        return trimmed;
    }

    private static string Pick(string[] pictures, string index)
    {
        int number = Integer(index, -1);
        return number >= 0 && number < pictures.Length ? pictures[number] : Unquote(index);
    }

    [GeneratedRegex(@"AFNumber_Format\s*\(([^,]*),([^,]*),([^,]*),([^,]*),([^,]*),([^)]*)\)")]
    private static partial Regex NumberCall();

    [GeneratedRegex(@"AFPercent_Format\s*\(([^,]*),([^,)]*)(?:,([^)]*))?\)")]
    private static partial Regex PercentCall();

    [GeneratedRegex(@"AF(?:Date|Time)_FormatEx\s*\(([^)]*)\)")]
    private static partial Regex DateExCall();

    [GeneratedRegex(@"AFDate_Format\s*\(([^)]*)\)")]
    private static partial Regex DateCall();

    [GeneratedRegex(@"AFTime_Format\s*\(([^)]*)\)")]
    private static partial Regex TimeCall();

    [GeneratedRegex(@"AFSpecial_Format\s*\(([^)]*)\)")]
    private static partial Regex SpecialCall();
}
