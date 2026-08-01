using System.Globalization;
using System.Text;
using Inkwright.Cos;

namespace Inkwright;

/// <summary>How the numbers in a labelling range are written (ISO 32000-1 Table 159).</summary>
public enum PageLabelStyle
{
    /// <summary>No number at all: the pages carry only the prefix, if there is one.</summary>
    None,

    /// <summary>Arabic numerals: 1, 2, 3.</summary>
    Decimal,

    /// <summary>Uppercase roman numerals: I, II, III.</summary>
    UppercaseRoman,

    /// <summary>Lowercase roman numerals: i, ii, iii.</summary>
    LowercaseRoman,

    /// <summary>Uppercase letters: A to Z, then AA to ZZ, and so on.</summary>
    UppercaseLetters,

    /// <summary>Lowercase letters: a to z, then aa to zz, and so on.</summary>
    LowercaseLetters,
}

/// <summary>
/// A run of pages labelled the same way, starting at a page index and running until the next range
/// begins.
/// </summary>
/// <param name="StartIndex">The zero-based index of the first page in the range.</param>
/// <param name="Style">How to write the number.</param>
/// <param name="Prefix">Text placed before the number, or <see langword="null"/> for none.</param>
/// <param name="Start">The number the first page of the range carries; at least 1.</param>
public sealed record PageLabelRange(int StartIndex, PageLabelStyle Style, string? Prefix = null, int Start = 1);

/// <summary>
/// What each page is called, as opposed to where it sits. A document whose front matter runs i, ii,
/// iii before the body starts again at 1 says so here, and a viewer showing "page 3 of 40" in its
/// toolbar is showing this rather than the index.
/// </summary>
public sealed class PdfPageLabels
{
    private readonly PdfDocument _document;

    internal PdfPageLabels(PdfDocument document) => _document = document;

    /// <summary>Whether the document labels its pages at all.</summary>
    public bool IsDefined => Tree() is not null;

    /// <summary>The ranges the document declares, in page order.</summary>
    public IReadOnlyList<PageLabelRange> Ranges
    {
        get
        {
            List<PageLabelRange> ranges = [];

            foreach ((int key, PdfValue value) in PdfNumberTree.Enumerate(Tree()))
            {
                if (value.AsDictionary is { } entry && key >= 0)
                    ranges.Add(Read(key, entry));
            }

            ranges.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));
            return ranges;
        }
    }

    /// <summary>The label of one page.</summary>
    /// <param name="pageIndex">The zero-based page index.</param>
    /// <returns>The label, or the page number as text when the document declares none.</returns>
    public string this[int pageIndex]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

            PageLabelRange? covering = null;
            foreach (PageLabelRange range in Ranges)
            {
                if (range.StartIndex > pageIndex)
                    break;

                covering = range;
            }

            return covering is null
                ? (pageIndex + 1).ToString(CultureInfo.InvariantCulture)
                : Format(covering, pageIndex - covering.StartIndex);
        }
    }

    /// <summary>The labels of every page in the document.</summary>
    public IReadOnlyList<string> All()
    {
        var labels = new List<string>(_document.Pages.Count);
        for (int i = 0; i < _document.Pages.Count; i++)
            labels.Add(this[i]);

        return labels;
    }

    /// <summary>
    /// Replaces the labelling. The specification requires a range starting at page zero, so one is
    /// added in plain arabic numerals when the caller does not give one.
    /// </summary>
    /// <param name="ranges">The ranges to declare; they are sorted by start index.</param>
    public void Set(IEnumerable<PageLabelRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        List<PageLabelRange> ordered = [.. ranges];
        ordered.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));

        if (ordered.Count == 0)
        {
            Remove();
            return;
        }

        if (ordered[0].StartIndex != 0)
            ordered.Insert(0, new PageLabelRange(0, PageLabelStyle.Decimal));

        PdfDictionary tree = PdfNumberTree.Build(ordered.Select(range =>
            (range.StartIndex, PdfValue.Dictionary(Write(range)))));

        _document.Attach(PdfValue.Dictionary(tree));
        _document.Catalog.Set(PdfNames.PageLabels, PdfValue.Dictionary(tree));
        _document.MarkChanged(_document.CatalogId);
    }

    /// <summary>Drops the labelling, leaving pages known by their index alone.</summary>
    public void Remove()
    {
        if (_document.Catalog.Remove(PdfNames.PageLabels))
            _document.MarkChanged(_document.CatalogId);
    }

    /// <summary>Writes one range as the label dictionary of Table 159.</summary>
    internal static PdfDictionary Write(PageLabelRange range)
    {
        var entry = new PdfDictionary(3);

        if (Suffix(range.Style) is { } style)
            entry.Set(PdfNames.S, PdfValue.Name(PdfName.Get(style)));

        if (!string.IsNullOrEmpty(range.Prefix))
            entry.Set(PdfNames.P, PdfValue.TextString(range.Prefix));

        if (range.Start > 1)
            entry.Set(PdfNames.St, PdfValue.Integer(range.Start));

        return entry;
    }

    /// <summary>Renders the label of the nth page inside a range.</summary>
    /// <param name="range">The range the page falls in.</param>
    /// <param name="offset">How far into the range the page sits.</param>
    internal static string Format(PageLabelRange range, int offset)
    {
        int number = range.Start + offset;

        string body = range.Style switch
        {
            PageLabelStyle.Decimal => number.ToString(CultureInfo.InvariantCulture),
            PageLabelStyle.UppercaseRoman => Roman(number, uppercase: true),
            PageLabelStyle.LowercaseRoman => Roman(number, uppercase: false),
            PageLabelStyle.UppercaseLetters => Letters(number, 'A'),
            PageLabelStyle.LowercaseLetters => Letters(number, 'a'),
            _ => string.Empty,
        };

        return range.Prefix + body;
    }

    private static PageLabelRange Read(int startIndex, PdfDictionary entry)
    {
        PageLabelStyle style = entry.GetName(PdfNames.S)?.Value switch
        {
            "D" => PageLabelStyle.Decimal,
            "R" => PageLabelStyle.UppercaseRoman,
            "r" => PageLabelStyle.LowercaseRoman,
            "A" => PageLabelStyle.UppercaseLetters,
            "a" => PageLabelStyle.LowercaseLetters,
            _ => PageLabelStyle.None,
        };

        // A start below one is not a number the styles can render, so it is read as the default.
        int start = (int)Math.Clamp(entry.GetInteger(PdfNames.St, 1), 1, int.MaxValue);
        return new PageLabelRange(startIndex, style, entry.GetTextString(PdfNames.P), start);
    }

    private static string? Suffix(PageLabelStyle style) => style switch
    {
        PageLabelStyle.Decimal => "D",
        PageLabelStyle.UppercaseRoman => "R",
        PageLabelStyle.LowercaseRoman => "r",
        PageLabelStyle.UppercaseLetters => "A",
        PageLabelStyle.LowercaseLetters => "a",
        _ => null,
    };

    /// <summary>
    /// Letters count A to Z, then AA to ZZ, then AAA to ZZZ: the letter says which of twenty-six,
    /// and how many times it is repeated says which pass through the alphabet.
    /// </summary>
    private static string Letters(int number, char first)
    {
        if (number < 1)
            return string.Empty;

        int index = (number - 1) % 26;
        int repeats = ((number - 1) / 26) + 1;
        return new string((char)(first + index), repeats);
    }

    private static string Roman(int number, bool uppercase)
    {
        if (number is < 1 or > 3999)
            return number.ToString(CultureInfo.InvariantCulture);

        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];

        var text = new StringBuilder(15);
        for (int i = 0; i < values.Length; i++)
        {
            while (number >= values[i])
            {
                text.Append(symbols[i]);
                number -= values[i];
            }
        }

        return uppercase ? text.ToString() : text.ToString().ToLowerInvariant();
    }

    private PdfDictionary? Tree() => _document.Catalog.GetDictionary(PdfNames.PageLabels);
}
