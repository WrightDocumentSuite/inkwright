namespace Inkwright.Annotations;

/// <summary>
/// Converts comment text between what the file wants and what a caller expects. ISO 32000-1
/// §12.5.6.2 asks for a CARRIAGE RETURN between paragraphs and says a LINE FEED is not it; readers
/// take that literally, and a comment written with line feeds arrives as one long line.
/// </summary>
internal static class AnnotationText
{
    /// <summary>Turns any line ending into the single carriage return the specification asks for.</summary>
    internal static string ToPdf(string text)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0)
            return text;

        return text.ReplaceLineEndings("\r");
    }

    /// <summary>Turns the carriage returns of a file back into the line feeds .NET code expects.</summary>
    internal static string FromPdf(string text)
    {
        if (text.AsSpan().IndexOf('\r') < 0)
            return text;

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
