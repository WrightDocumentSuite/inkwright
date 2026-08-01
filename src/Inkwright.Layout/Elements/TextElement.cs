using System.Text;
using Inkwright.Content;
using Inkwright.Fonts;

namespace Inkwright.Layout.Elements;

/// <summary>
/// A paragraph of styled runs. Lines are broken greedily at the points UAX #14 calls direct break
/// opportunities — spaces, hyphens and the ends of CJK glyphs — and a paragraph that runs past the
/// bottom of a page keeps its place so the rest continues on the next one.
/// </summary>
public sealed class TextElement : LayoutElement
{
    private readonly List<Run> _runs = [];
    private List<Line>? _lines;
    private double _lineWidth = -1;
    private string _dynamicKey = string.Empty;
    private int _drawnLines;

    /// <summary>The style runs inherit when they set nothing of their own.</summary>
    public TextStyle BaseStyle { get; set; } = TextStyle.Default;

    /// <summary>The structure type this text takes in a tagged document.</summary>
    public string SemanticTag { get; set; } = "P";

    /// <inheritdoc />
    public override bool HasMoreContent => _lines is not null && _drawnLines < _lines.Count;

    /// <summary>Appends a run of text.</summary>
    /// <param name="text">The text to append.</param>
    /// <param name="style">The style, or the base style when omitted.</param>
    public void Add(string text, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        _runs.Add(new Run(text, null, style));
        Invalidate();
    }

    /// <summary>Appends a run whose text depends on the page it lands on.</summary>
    /// <param name="resolve">Produces the text from the page numbering.</param>
    /// <param name="style">The style, or the base style when omitted.</param>
    public void Add(Func<PageContext, string> resolve, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _runs.Add(new Run(null, resolve, style));
        Invalidate();
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _drawnLines = 0;
        Invalidate();
    }

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context)
    {
        if (available.Width <= 0)
            return SpacePlan.Wrap("no width for text");

        Build(available.Width, context);

        if (_lines!.Count == 0)
            return SpacePlan.Empty();

        if (_drawnLines >= _lines.Count)
            return SpacePlan.Empty();

        double height = 0;
        double width = 0;
        int fitted = 0;

        for (int i = _drawnLines; i < _lines.Count; i++)
        {
            double next = height + _lines[i].Height;
            if (next > available.Height + 0.01)
                break;

            height = next;
            width = Math.Max(width, _lines[i].Width);
            fitted++;
        }

        if (fitted == 0)
            return SpacePlan.Wrap($"a line of {_lines[_drawnLines].Height:0.##} pt does not fit in {available.Height:0.##} pt");

        return _drawnLines + fitted >= _lines.Count
            ? SpacePlan.Full(width, height)
            : SpacePlan.Partial(width, height);
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        Build(available.Width, context);

        if (_lines!.Count == 0)
            return;

        double y = 0;
        using IDisposable tag = context.MarkedOrNothing(SemanticTag);

        // The counting pass runs without a canvas: the state still has to advance, or the paragraph
        // would claim to have more content for ever and the page count would never settle.
        while (_drawnLines < _lines.Count)
        {
            Line line = _lines[_drawnLines];
            if (y + line.Height > available.Height + 0.01)
                break;

            if (context.Canvas is { } canvas)
                DrawLine(canvas, line, available.Width, y);

            y += line.Height;
            _drawnLines++;
        }
    }

    private void DrawLine(LayoutCanvas canvas, Line line, double availableWidth, double top)
    {
        TextAlignment alignment = BaseStyle.Alignment;
        double free = Math.Max(0, availableWidth - line.Width);
        double x = alignment switch
        {
            TextAlignment.Center => free / 2,
            TextAlignment.Right => free,
            _ => 0,
        };

        // Justification stretches the gaps between words, never the words themselves.
        double extraPerGap = 0;
        if (alignment == TextAlignment.Justify && !line.IsLast && line.GapCount > 0)
            extraPerGap = free / line.GapCount;

        double baseline = top + line.Ascent;
        IReadOnlyList<Fragment> fragments = line.Fragments;

        // One text object per line, with runs of the same style joined into a single showing
        // operator. Emitting a whole text object per word triples the size of a page and makes
        // reading it back measurably slower.
        ContentCanvas content = canvas.Content;
        content.BeginText();

        TextStyle? active = null;
        List<(Fragment Fragment, double X)>? decorations = null;
        int index = 0;

        while (index < fragments.Count)
        {
            Fragment first = fragments[index];
            double runStart = x;
            double runWidth = first.Width + (first.IsGap ? extraPerGap : 0);
            int end = index + 1;

            while (extraPerGap == 0 && end < fragments.Count && fragments[end].Style == first.Style)
            {
                runWidth += fragments[end].Width;
                end++;
            }

            string text = end == index + 1 ? first.Text : Join(fragments, index, end);

            if (text.Length > 0)
            {
                if (active != first.Style)
                {
                    content.FillColor(first.Style.Color);
                    content.Font(first.Font, first.Style.Size);
                    content.CharSpacing(first.Style.LetterSpacing);
                    active = first.Style;
                }

                content.TextMatrix(new PdfMatrix(1, 0, 0, -1, runStart, baseline));
                content.ShowText(first.Font.Encode(text));

                if (first.Style.Underline || first.Style.Strikethrough)
                {
                    decorations ??= [];
                    decorations.Add((first with { Width = runWidth }, runStart));
                }
            }

            x += runWidth;
            index = end;
        }

        content.EndText();

        if (decorations is null)
            return;

        foreach ((Fragment fragment, double at) in decorations)
            DrawDecorations(canvas, fragment, at, baseline);
    }

    private static string Join(IReadOnlyList<Fragment> fragments, int start, int end)
    {
        var text = new StringBuilder();
        for (int i = start; i < end; i++)
            text.Append(fragments[i].Text);

        return text.ToString();
    }

    private static void DrawDecorations(LayoutCanvas canvas, Fragment fragment, double x, double baseline)
    {
        double thickness = Math.Max(0.4, fragment.Style.Size / 16);

        if (fragment.Style.Underline)
        {
            double y = baseline + (fragment.Style.Size * 0.12);
            canvas.DrawLine(x, y, x + fragment.Width, y, thickness, fragment.Style.Color);
        }

        if (fragment.Style.Strikethrough)
        {
            double y = baseline - (fragment.Style.Size * 0.26);
            canvas.DrawLine(x, y, x + fragment.Width, y, thickness, fragment.Style.Color);
        }
    }

    private void Invalidate()
    {
        _lines = null;
        _lineWidth = -1;
    }

    private void Build(double width, LayoutContext context)
    {
        string dynamicKey = BuildDynamicKey(context);

        if (_lines is not null && Math.Abs(_lineWidth - width) < 0.01 &&
            string.Equals(_dynamicKey, dynamicKey, StringComparison.Ordinal))
        {
            return;
        }

        _lineWidth = width;
        _dynamicKey = dynamicKey;
        _lines = BreakLines(width, context);
    }

    private string BuildDynamicKey(LayoutContext context)
    {
        // Text without page-dependent runs has a constant key, and building one would allocate on
        // every measure of every cell in a long table.
        StringBuilder? key = null;

        foreach (Run run in _runs)
        {
            if (run.Resolve is null)
                continue;

            key ??= new StringBuilder();
            key.Append(run.Resolve(context.Pages)).Append('\u001F');
        }

        return key?.ToString() ?? string.Empty;
    }

    private List<Line> BreakLines(double maxWidth, LayoutContext context)
    {
        List<Line> lines = [];
        List<Fragment> current = [];
        double currentWidth = 0;
        int gaps = 0;

        void Commit(bool last)
        {
            if (current.Count == 0 && !last)
            {
                lines.Add(Line.Blank(BaseStyle.LineSpacing));
                return;
            }

            if (current.Count == 0)
                return;

            // Trailing spaces do not push a line over its limit.
            while (current.Count > 0 && current[^1].IsGap)
            {
                currentWidth -= current[^1].Width;
                gaps--;
                current.RemoveAt(current.Count - 1);
            }

            if (current.Count == 0)
                return;

            lines.Add(Line.From(current, currentWidth, gaps, last));
            current = [];
            currentWidth = 0;
            gaps = 0;
        }

        foreach (Token token in Tokenize(context))
        {
            if (token.IsLineBreak)
            {
                if (current.Count == 0)
                    lines.Add(Line.Blank(token.Style.LineSpacing));
                else
                    Commit(last: true);

                continue;
            }

            if (token.IsGap && current.Count == 0)
                continue;

            double width = token.Width;

            if (currentWidth + width > maxWidth + 0.01 && current.Count > 0)
            {
                Commit(last: false);

                if (token.IsGap)
                    continue;
            }

            if (width > maxWidth && !token.IsGap)
            {
                foreach (Fragment piece in SplitLongWord(token, maxWidth, context))
                {
                    if (currentWidth + piece.Width > maxWidth + 0.01 && current.Count > 0)
                        Commit(last: false);

                    current.Add(piece);
                    currentWidth += piece.Width;
                }

                continue;
            }

            current.Add(new Fragment(token.Text, token.Font, token.Style, width, token.IsGap));
            currentWidth += width;
            if (token.IsGap)
                gaps++;
        }

        Commit(last: true);
        return lines;
    }

    private static IEnumerable<Fragment> SplitLongWord(Token token, double maxWidth, LayoutContext context)
    {
        var piece = new StringBuilder();
        double width = 0;

        foreach (char c in token.Text)
        {
            double advance = Measure(c.ToString(), token.Font, token.Style);
            if (width + advance > maxWidth && piece.Length > 0)
            {
                yield return new Fragment(piece.ToString(), token.Font, token.Style, width, false);
                piece.Clear();
                width = 0;
            }

            piece.Append(c);
            width += advance;
        }

        _ = context;

        if (piece.Length > 0)
            yield return new Fragment(piece.ToString(), token.Font, token.Style, width, false);
    }

    private IEnumerable<Token> Tokenize(LayoutContext context)
    {
        foreach (Run run in _runs)
        {
            TextStyle style = Combine(BaseStyle, run.Style);
            PdfFont font = context.ResolveFont(style);
            string text = run.Text ?? run.Resolve!(context.Pages);

            int index = 0;
            while (index < text.Length)
            {
                char c = text[index];

                if (c == '\n')
                {
                    yield return Token.LineBreak(style);
                    index++;
                    continue;
                }

                if (c == '\r')
                {
                    index++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    int start = index;
                    while (index < text.Length && char.IsWhiteSpace(text[index]) && text[index] != '\n')
                        index++;

                    string gap = text[start..index];
                    yield return new Token(gap, font, style, Measure(gap, font, style), IsGap: true, IsLineBreak: false);
                    continue;
                }

                int wordStart = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index]) && !IsBreakAfter(text[index]))
                    index++;

                // A hyphen or a CJK glyph ends a word and stays with it.
                if (index < text.Length && IsBreakAfter(text[index]))
                    index++;

                string word = text[wordStart..index];
                yield return new Token(word, font, style, Measure(word, font, style), IsGap: false, IsLineBreak: false);
            }
        }
    }

    private static bool IsBreakAfter(char c) =>
        c is '-' or '\u2013' or '\u2014' or '/' ||
        c is >= '\u4E00' and <= '\u9FFF' ||
        c is >= '\u3040' and <= '\u30FF';

    private static double Measure(string text, PdfFont font, TextStyle style) =>
        font.Measure(text, style.Size) + (style.LetterSpacing * text.Length);

    private static TextStyle Combine(TextStyle baseStyle, TextStyle? style) => style ?? baseStyle;

    private readonly record struct Run(string? Text, Func<PageContext, string>? Resolve, TextStyle? Style);

    private readonly record struct Token(
        string Text, PdfFont Font, TextStyle Style, double Width, bool IsGap, bool IsLineBreak)
    {
        internal static Token LineBreak(TextStyle style) => new(string.Empty, null!, style, 0, false, true);
    }

    private readonly record struct Fragment(string Text, PdfFont Font, TextStyle Style, double Width, bool IsGap);

    private sealed record Line(IReadOnlyList<Fragment> Fragments, double Width, double Height, double Ascent, int GapCount, bool IsLast)
    {
        internal static Line Blank(double height) => new([], 0, height, height * 0.8, 0, false);

        internal static Line From(List<Fragment> fragments, double width, int gaps, bool last)
        {
            double height = 0;
            double ascent = 0;

            foreach (Fragment fragment in fragments)
            {
                height = Math.Max(height, fragment.Style.LineSpacing);
                ascent = Math.Max(ascent, fragment.Font.Ascent * fragment.Style.Size / 1000);
            }

            // Centre the text block inside the line box so leading is shared above and below.
            double leading = height - ascent;
            return new Line([.. fragments], width, height, ascent + (leading / 3), gaps, last);
        }
    }
}
