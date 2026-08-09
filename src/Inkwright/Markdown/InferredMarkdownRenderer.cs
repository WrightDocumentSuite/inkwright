using System.Text;
using System.Text.RegularExpressions;
using Inkwright.Text;

namespace Inkwright.Markdown;

/// <summary>
/// Opt-in semantic reconstruction for untagged PDFs. The renderer is deliberately separate from
/// the Tagged PDF path: geometry can suggest structure, but it cannot carry the same guarantee.
/// </summary>
internal sealed partial class InferredMarkdownRenderer(
    PdfDocument document,
    MarkdownExportOptions options,
    MarkdownExportDiagnostics diagnostics)
{
    private readonly List<PageLayout> pages = [];
    private double bodyFontSize;
    private double[] headingSizes = [];

    internal int SkippedPages { get; private set; }

    internal string Render()
    {
        ReadPages();
        IReadOnlyList<TextLayoutLine> allLines = pages.SelectMany(page => page.Layout.Lines).ToArray();
        if (allLines.Count == 0)
        {
            diagnostics.Add(
                MarkdownExportWarningKind.ContentSkipped,
                "No extractable visible text was found in the PDF.",
                "Text");
            return "\n";
        }

        bodyFontSize = FindBodyFontSize(allLines);
        headingSizes = FindHeadingSizes(allLines, bodyFontSize);
        diagnostics.Add(
            MarkdownExportWarningKind.StructureApproximated,
            "Columns and Markdown structure were inferred from glyph geometry because the PDF is untagged.",
            "Inferred layout");

        var output = new StringBuilder();
        foreach (PageLayout page in pages)
            RenderPage(page.Layout.Lines, output);

        if (output.Length == 0)
            return "\n";

        output.Append('\n');
        return output.ToString();
    }

    private void ReadPages()
    {
        var extraction = new TextExtractionOptions
        {
            ResolveBidirectional = true,
            SkipHiddenLayers = !options.IncludeHiddenLayers,
            UseActualText = true,
        };

        for (int pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
        {
            PdfPage page = document.Pages[pageIndex];
            try
            {
                IReadOnlyList<PdfLetter> letters = page.ExtractLetters(extraction);
                pages.Add(new PageLayout(pageIndex, TextLayoutAnalyzer.Analyze(page.CropBox, letters)));
            }
            catch (Exception exception) when (MarkdownExporter.IsRecoverableContentException(exception))
            {
                SkippedPages++;
                diagnostics.Add(
                    MarkdownExportWarningKind.ContentSkipped,
                    "A page content stream could not be decoded or parsed and was skipped.",
                    $"Page {pageIndex + 1}");
            }
        }
    }

    private void RenderPage(IReadOnlyList<TextLayoutLine> lines, StringBuilder output)
    {
        int index = 0;
        while (index < lines.Count)
        {
            if (TryRenderTable(lines, index, out int tableLines, out string table))
            {
                AppendBlock(output, table);
                index += tableLines;
                continue;
            }

            TextLayoutLine line = lines[index];
            int heading = HeadingLevel(line);
            if (heading > 0)
            {
                AppendBlock(output, new string('#', heading) + " " + MarkdownText.Escape(line.Text.Trim()));
                index++;
                continue;
            }

            if (TryListItem(line.Text, out string marker, out string item))
            {
                var list = new StringBuilder();
                while (index < lines.Count && TryListItem(lines[index].Text, out marker, out item))
                {
                    if (list.Length > 0)
                        list.Append('\n');
                    list.Append(marker);
                    list.Append(MarkdownText.Escape(item));
                    index++;
                }

                AppendBlock(output, list.ToString());
                continue;
            }

            var paragraph = new StringBuilder(line.Text.Trim());
            TextLayoutLine previous = line;
            index++;
            while (index < lines.Count)
            {
                TextLayoutLine next = lines[index];
                if (HeadingLevel(next) > 0 || TryListItem(next.Text, out _, out _) ||
                    IsTableStart(lines, index) || !CanContinueParagraph(previous, next))
                {
                    break;
                }

                AppendContinuedLine(paragraph, next.Text.Trim());
                previous = next;
                index++;
            }

            AppendBlock(output, MarkdownText.Escape(paragraph.ToString()));
        }
    }

    private bool IsTableStart(IReadOnlyList<TextLayoutLine> lines, int index) =>
        TryBuildTable(lines, index, out _, out _);

    private bool TryRenderTable(
        IReadOnlyList<TextLayoutLine> lines,
        int start,
        out int consumed,
        out string markdown) =>
        TryBuildTable(lines, start, out consumed, out markdown);

    private bool TryBuildTable(
        IReadOnlyList<TextLayoutLine> lines,
        int start,
        out int consumed,
        out string markdown)
    {
        consumed = 0;
        markdown = string.Empty;
        if (lines[start].Cells.Count < 2)
            return false;

        int column = lines[start].Column;
        var rows = new List<TextLayoutLine>();
        for (int index = start; index < lines.Count; index++)
        {
            TextLayoutLine line = lines[index];
            if (line.Column != column || line.Cells.Count < 2)
                break;

            if (rows.Count > 0)
            {
                double verticalGap = rows[^1].Bounds.Bottom - line.Bounds.Top;
                if (verticalGap > Math.Max(12, bodyFontSize * 1.6))
                    break;
            }

            rows.Add(line);
        }

        if (rows.Count < 2)
            return false;

        double tolerance = Math.Max(6, bodyFontSize * 0.7);
        var clusters = new List<ColumnCluster>();
        foreach (TextLayoutLine row in rows)
        {
            foreach (TextLayoutCell cell in row.Cells)
            {
                ColumnCluster? cluster = clusters.FirstOrDefault(candidate =>
                    Math.Abs(candidate.Position - cell.Bounds.Left) <= tolerance);
                if (cluster is null)
                {
                    clusters.Add(new ColumnCluster(cell.Bounds.Left));
                }
                else
                {
                    cluster.Add(cell.Bounds.Left);
                }
            }
        }

        ColumnCluster[] columns = clusters
            .Where(cluster => cluster.Count >= 2)
            .OrderBy(cluster => cluster.Position)
            .ToArray();
        if (columns.Length is < 2 or > 12)
            return false;

        string[][] grid = new string[rows.Count][];
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            grid[rowIndex] = new string[columns.Length];
            foreach (TextLayoutCell cell in rows[rowIndex].Cells)
            {
                int nearest = NearestColumn(columns, cell.Bounds.Left);
                if (Math.Abs(columns[nearest].Position - cell.Bounds.Left) > tolerance * 1.5)
                    return false;

                string value = cell.Text.Trim();
                grid[rowIndex][nearest] = string.IsNullOrEmpty(grid[rowIndex][nearest])
                    ? value
                    : grid[rowIndex][nearest] + " " + value;
            }
        }

        if (options.Flavor == MarkdownFlavor.CommonMark)
        {
            consumed = rows.Count;
            markdown = HtmlTable(grid);
            diagnostics.Add(
                MarkdownExportWarningKind.HtmlFallbackUsed,
                "An inferred table was emitted as HTML because CommonMark has no table syntax.",
                "Table");
            return true;
        }

        var builder = new StringBuilder();
        AppendTableRow(builder, grid[0]);
        builder.Append('\n');
        builder.Append('|');
        for (int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
            builder.Append(" --- |");
        for (int rowIndex = 1; rowIndex < grid.Length; rowIndex++)
        {
            builder.Append('\n');
            AppendTableRow(builder, grid[rowIndex]);
        }

        consumed = rows.Count;
        markdown = builder.ToString();
        return true;
    }

    private int HeadingLevel(TextLayoutLine line)
    {
        string text = line.Text.Trim();
        if (text.Length is 0 or > 160 || line.Cells.Count > 1 || TryListItem(text, out _, out _))
            return 0;

        double size = RoundFontSize(line.FontSize);
        for (int i = 0; i < headingSizes.Length; i++)
        {
            if (Math.Abs(headingSizes[i] - size) <= 0.26)
                return Math.Min(4, i + 1);
        }

        if (line.IsBold && line.FontSize >= bodyFontSize * 1.03 && text.Length <= 100 &&
            text.Any(char.IsLetter))
        {
            return Math.Min(4, headingSizes.Length + 1);
        }

        return 0;
    }

    private bool CanContinueParagraph(TextLayoutLine previous, TextLayoutLine next)
    {
        if (previous.Column != next.Column)
            return false;

        double gap = previous.Bounds.Bottom - next.Bounds.Top;
        return gap <= Math.Max(9, bodyFontSize * 0.9);
    }

    private static void AppendContinuedLine(StringBuilder paragraph, string line)
    {
        if (line.Length == 0)
            return;

        if (paragraph.Length > 1 && paragraph[^1] == '-' && char.IsLower(line[0]))
        {
            paragraph.Length--;
            paragraph.Append(line);
            return;
        }

        if (paragraph.Length > 0 && !char.IsWhiteSpace(paragraph[^1]))
            paragraph.Append(' ');
        paragraph.Append(line);
    }

    private static void AppendBlock(StringBuilder output, string block)
    {
        if (string.IsNullOrWhiteSpace(block))
            return;

        if (output.Length > 0)
            output.Append("\n\n");
        output.Append(block.Trim());
    }

    private static void AppendTableRow(StringBuilder builder, IReadOnlyList<string> cells)
    {
        builder.Append('|');
        foreach (string? cell in cells)
        {
            builder.Append(' ');
            builder.Append(MarkdownText.Escape(cell ?? string.Empty, tableCell: true));
            builder.Append(" |");
        }
    }

    private static string HtmlTable(IReadOnlyList<string[]> rows)
    {
        var html = new StringBuilder("<table>");
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            string element = rowIndex == 0 ? "th" : "td";
            html.Append("\n  <tr>");
            foreach (string? cell in rows[rowIndex])
            {
                html.Append("\n    <");
                html.Append(element);
                html.Append('>');
                html.Append(MarkdownText.HtmlText(cell));
                html.Append("</");
                html.Append(element);
                html.Append('>');
            }
            html.Append("\n  </tr>");
        }
        html.Append("\n</table>");
        return html.ToString();
    }

    private static int NearestColumn(IReadOnlyList<ColumnCluster> columns, double position)
    {
        int nearest = 0;
        double distance = Math.Abs(columns[0].Position - position);
        for (int i = 1; i < columns.Count; i++)
        {
            double candidate = Math.Abs(columns[i].Position - position);
            if (candidate < distance)
            {
                distance = candidate;
                nearest = i;
            }
        }

        return nearest;
    }

    private static double FindBodyFontSize(IReadOnlyList<TextLayoutLine> lines)
    {
        var weights = new Dictionary<double, int>();
        foreach (TextLayoutLine line in lines)
        {
            double size = RoundFontSize(line.FontSize);
            if (size <= 0)
                continue;
            weights[size] = weights.GetValueOrDefault(size) + Math.Clamp(line.Text.Length, 1, 500);
        }

        return weights.Count == 0
            ? 12
            : weights.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
    }

    private static double[] FindHeadingSizes(IReadOnlyList<TextLayoutLine> lines, double bodySize) => lines
        .Where(line => line.Cells.Count == 1 && line.Text.Length <= 160 && line.FontSize >= bodySize * 1.12 &&
                       line.Text.Any(char.IsLetter))
        .Select(line => RoundFontSize(line.FontSize))
        .Distinct()
        .OrderByDescending(size => size)
        .Take(4)
        .ToArray();

    private static double RoundFontSize(double value) => Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;

    private static bool TryListItem(string text, out string marker, out string item)
    {
        Match bullet = BulletPattern().Match(text);
        if (bullet.Success)
        {
            marker = "- ";
            item = text[bullet.Length..].TrimStart();
            return item.Length > 0;
        }

        Match numbered = NumberedPattern().Match(text);
        if (numbered.Success)
        {
            marker = numbered.Groups[1].Value + ". ";
            item = text[numbered.Length..].TrimStart();
            return item.Length > 0;
        }

        Match lettered = LetteredPattern().Match(text);
        if (lettered.Success)
        {
            marker = "- " + lettered.Groups[1].Value + ". ";
            item = text[lettered.Length..].TrimStart();
            return item.Length > 0;
        }

        marker = string.Empty;
        item = string.Empty;
        return false;
    }

    [GeneratedRegex(@"^\s*[•●◦▪‣∙–—-]\s+")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^\s*(\d{1,9})[.)]\s+")]
    private static partial Regex NumberedPattern();

    [GeneratedRegex(@"^\s*\(?([A-Za-z])[.)]\s+")]
    private static partial Regex LetteredPattern();

    private sealed record PageLayout(int PageIndex, TextPageLayout Layout);

    private sealed class ColumnCluster(double position)
    {
        private double total = position;
        internal int Count { get; private set; } = 1;
        internal double Position => total / Count;

        internal void Add(double value)
        {
            total += value;
            Count++;
        }
    }
}
