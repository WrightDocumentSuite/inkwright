using System.Globalization;

namespace Inkwright.Cos;

/// <summary>
/// Reads and writes the date strings of ISO 32000-1 §7.9.4, shaped
/// <c>D:YYYYMMDDHHmmSSOHH'mm</c> with every part after the year optional.
/// </summary>
public static class PdfDate
{
    /// <summary>Formats a moment in time as a PDF date string.</summary>
    /// <param name="value">The moment to format, including its offset from UTC.</param>
    public static string Format(DateTimeOffset value)
    {
        TimeSpan offset = value.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan magnitude = offset.Duration();

        return string.Create(CultureInfo.InvariantCulture,
            $"D:{value.Year:D4}{value.Month:D2}{value.Day:D2}{value.Hour:D2}{value.Minute:D2}{value.Second:D2}" +
            $"{sign}{magnitude.Hours:D2}'{magnitude.Minutes:D2}'");
    }

    /// <summary>Parses a PDF date string, tolerating truncated and slightly malformed input.</summary>
    /// <param name="text">The date string, with or without the <c>D:</c> prefix.</param>
    /// <param name="value">Receives the parsed moment.</param>
    /// <returns>Whether at least a year could be read.</returns>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrEmpty(text))
            return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.StartsWith("D:", StringComparison.Ordinal))
            span = span[2..];

        // Some producers wrap the value in parentheses or pad it with apostrophes.
        span = span.Trim('(').Trim(')').Trim();
        if (span.Length < 4)
            return false;

        if (!TryReadDigits(span, 0, 4, out int year))
            return false;

        int month = ReadPart(span, 4, 1);
        int day = ReadPart(span, 6, 1);
        int hour = ReadPart(span, 8, 0);
        int minute = ReadPart(span, 10, 0);
        int second = ReadPart(span, 12, 0);

        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month));
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        second = Math.Clamp(second, 0, 59);

        TimeSpan offset = ParseOffset(span);

        try
        {
            value = new DateTimeOffset(Math.Clamp(year, 1, 9999), month, day, hour, minute, second, offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static TimeSpan ParseOffset(ReadOnlySpan<char> span)
    {
        if (span.Length <= 14)
            return TimeSpan.Zero;

        char sign = span[14];
        if (sign is 'Z' or 'z')
            return TimeSpan.Zero;

        if (sign is not ('+' or '-'))
            return TimeSpan.Zero;

        int hours = ReadPart(span, 15, 0);
        int minutes = span.Length > 18 ? ReadPart(span, 18, 0) : 0;
        var magnitude = new TimeSpan(Math.Clamp(hours, 0, 14), Math.Clamp(minutes, 0, 59), 0);
        return sign == '-' ? -magnitude : magnitude;
    }

    private static int ReadPart(ReadOnlySpan<char> span, int start, int fallback) =>
        TryReadDigits(span, start, 2, out int value) ? value : fallback;

    private static bool TryReadDigits(ReadOnlySpan<char> span, int start, int length, out int value)
    {
        value = 0;
        if (start + length > span.Length)
            return false;

        ReadOnlySpan<char> slice = span.Slice(start, length);
        foreach (char c in slice)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return int.TryParse(slice, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
