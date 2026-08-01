using System.Net;
using System.Text;

namespace Inkwright.Markdown;

/// <summary>Context-specific escaping for generated Markdown.</summary>
internal static class MarkdownText
{
    private static readonly HashSet<string> SafeAbsoluteSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "ftp" };

    public static string Escape(string text, bool tableCell = false)
    {
        var builder = new StringBuilder(text.Length + 8);
        bool lineStart = true;
        Append(builder, text.AsSpan(), tableCell, ref lineStart);
        return builder.ToString();
    }

    public static void Append(StringBuilder builder, ReadOnlySpan<char> text, bool tableCell, ref bool lineStart)
    {
        int orderedPunctuation = lineStart ? OrderedMarkerPunctuation(text) : -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\r' or '\n' or '\v')
            {
                if (tableCell)
                    builder.Append("<br>");
                else
                    builder.Append("  \n");

                lineStart = true;
                orderedPunctuation = i + 1 < text.Length
                    ? OffsetOrderedMarker(text[(i + 1)..], i + 1)
                    : -1;
                continue;
            }

            bool escape = c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '~' or '&' ||
                          (tableCell && c == '|') ||
                          (c == '!' && i + 1 < text.Length && text[i + 1] == '[') ||
                          (lineStart && c is '#' or '>' or '+' or '-') ||
                          i == orderedPunctuation;

            if (escape)
                builder.Append('\\');
            builder.Append(c);

            if (!char.IsWhiteSpace(c))
                lineStart = false;
        }
    }

    public static string LinkDestination(string destination)
    {
        if (destination.Any(c => c is '\r' or '\n' or '\0' || char.IsControl(c)))
            throw new ArgumentException("A Markdown link destination cannot contain control characters.", nameof(destination));

        bool angled = destination.Any(c => char.IsWhiteSpace(c) || c is '(' or ')');
        var builder = new StringBuilder(destination.Length + 4);
        if (angled)
            builder.Append('<');

        foreach (char c in destination)
        {
            bool escape = c is '\\' or '&' ||
                           (angled && c is '<' or '>') ||
                           (!angled && c is '(' or ')' or '<' or '>');
            if (escape)
                builder.Append('\\');
            builder.Append(c);
        }

        if (angled)
            builder.Append('>');
        return builder.ToString();
    }

    /// <summary>
    /// Checks a destination after applying the character-reference decoding that a CommonMark
    /// parser performs inside URLs. Without that canonicalization, an active scheme such as
    /// <c>javascript:</c> can be hidden as <c>javascript&amp;#x3a;</c> until after this check.
    /// </summary>
    public static bool IsSafeLinkDestination(string destination, out string scheme)
    {
        scheme = "relative";
        if (destination.Any(c => c is '\r' or '\n' or '\0' || char.IsControl(c)))
        {
            scheme = "invalid";
            return false;
        }

        string decoded = WebUtility.HtmlDecode(destination)
            .Replace("&colon;", ":", StringComparison.OrdinalIgnoreCase)
            .TrimStart();
        if (decoded.Any(c => c is '\r' or '\n' or '\0' || char.IsControl(c)))
        {
            scheme = "invalid";
            return false;
        }

        int colon = decoded.IndexOf(':');
        if (colon <= 0 || !IsUriScheme(decoded.AsSpan(0, colon)))
            return true;

        scheme = decoded[..colon].ToLowerInvariant();
        return SafeAbsoluteSchemes.Contains(scheme);
    }

    public static string CodeSpan(string text)
    {
        int delimiterLength = Math.Max(1, LongestRun(text, '`') + 1);
        string delimiter = new('`', delimiterLength);
        bool pad = text.StartsWith('`') || text.EndsWith('`') ||
                   text.StartsWith(' ') && text.EndsWith(' ') && text.Trim(' ').Length > 0;
        return pad ? $"{delimiter} {text} {delimiter}" : $"{delimiter}{text}{delimiter}";
    }

    /// <summary>Creates a CommonMark fenced code block whose delimiter cannot occur in the text.</summary>
    public static string CodeBlock(string text)
    {
        text = text.ReplaceLineEndings("\n").Replace('\0', '\uFFFD');
        int backticks = LongestRun(text, '`');
        int tildes = LongestRun(text, '~');
        char fenceCharacter = backticks <= tildes ? '`' : '~';
        int longest = fenceCharacter == '`' ? backticks : tildes;
        string fence = new(fenceCharacter, Math.Max(3, longest + 1));
        return fence + "\n" + text + (text.EndsWith('\n') ? string.Empty : "\n") + fence;
    }

    public static string HtmlText(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static int OffsetOrderedMarker(ReadOnlySpan<char> text, int offset)
    {
        int relative = OrderedMarkerPunctuation(text);
        return relative < 0 ? -1 : relative + offset;
    }

    private static int OrderedMarkerPunctuation(ReadOnlySpan<char> text)
    {
        int start = 0;
        while (start < text.Length && start < 3 && text[start] == ' ')
            start++;

        int digits = start;
        while (digits < text.Length && digits - start < 9 && char.IsAsciiDigit(text[digits]))
            digits++;

        return digits > start && digits < text.Length && text[digits] is '.' or ')' &&
               digits + 1 < text.Length && char.IsWhiteSpace(text[digits + 1])
            ? digits
            : -1;
    }

    private static int LongestRun(string text, char wanted)
    {
        int longest = 0;
        int current = 0;
        foreach (char c in text)
        {
            if (c == wanted)
            {
                current++;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }

    private static bool IsUriScheme(ReadOnlySpan<char> candidate)
    {
        if (candidate.IsEmpty || !char.IsAsciiLetter(candidate[0]))
            return false;

        foreach (char c in candidate[1..])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '+' and not '-' and not '.')
                return false;
        }

        return true;
    }
}
