using System.Text;

namespace Inkwright.Text;

/// <summary>
/// Reconstructs physical lines and a conservative two-column reading order from positioned glyphs.
/// It deliberately exposes geometry rather than Markdown concepts so text extraction and semantic
/// inference share one ordering decision.
/// </summary>
internal static class TextLayoutAnalyzer
{
    internal static TextPageLayout Analyze(PdfRectangle pageBounds, IReadOnlyList<PdfLetter> letters)
    {
        if (letters.Count == 0)
            return new TextPageLayout([], null);

        List<PhysicalRow> rows = BuildRows(letters);
        TextGutter? gutter = DetectGutter(pageBounds, rows);
        List<TextLayoutLine> lines = BuildLines(rows, gutter);
        return new TextPageLayout(Order(lines, gutter), gutter);
    }

    internal static string Compose(
        PdfRectangle pageBounds,
        IReadOnlyList<PdfLetter> letters,
        bool resolveBidirectional)
    {
        TextPageLayout layout = Analyze(pageBounds, letters);
        var text = new StringBuilder(letters.Count + layout.Lines.Count);
        foreach (TextLayoutLine line in layout.Lines)
        {
            string value = line.Text;
            text.Append(resolveBidirectional ? Bidi.Resolve(value).Text : value);
            text.Append('\n');
        }

        return text.ToString();
    }

    internal static List<PdfLetter> Reorder(PdfRectangle pageBounds, IReadOnlyList<PdfLetter> letters)
    {
        TextPageLayout layout = Analyze(pageBounds, letters);
        var ordered = new List<PdfLetter>(letters.Count);
        foreach (TextLayoutLine line in layout.Lines)
        {
            foreach (TextLayoutCell cell in line.Cells)
                ordered.AddRange(cell.Letters);
        }

        return ordered;
    }

    private static List<PhysicalRow> BuildRows(IReadOnlyList<PdfLetter> letters)
    {
        PdfLetter[] sorted = letters
            .OrderByDescending(letter => letter.Origin.Y)
            .ThenBy(letter => letter.Origin.X)
            .ToArray();
        var rows = new List<PhysicalRow>();

        foreach (PdfLetter letter in sorted)
        {
            double tolerance = Math.Max(1, letter.FontSize * 0.32);
            PhysicalRow? row = null;
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                double difference = Math.Abs(rows[i].Baseline - letter.Origin.Y);
                if (difference <= tolerance)
                {
                    row = rows[i];
                    break;
                }

                if (rows[i].Baseline - letter.Origin.Y > tolerance * 2)
                    break;
            }

            if (row is null)
            {
                row = new PhysicalRow(letter.Origin.Y);
                rows.Add(row);
            }

            row.Letters.Add(letter);
        }

        foreach (PhysicalRow row in rows)
        {
            row.Letters.Sort((left, right) => left.Origin.X.CompareTo(right.Origin.X));
            row.Cells.AddRange(SplitCells(row.Letters));
        }

        rows.Sort((left, right) => right.Baseline.CompareTo(left.Baseline));
        return rows;
    }

    private static IEnumerable<TextLayoutCell> SplitCells(IReadOnlyList<PdfLetter> letters)
    {
        if (letters.Count == 0)
            yield break;

        var current = new List<PdfLetter>();
        double previousEnd = double.NaN;
        double representativeSize = Median(letters.Where(letter => !letter.IsWhiteSpace).Select(letter => letter.FontSize));
        // Explicit PDF space glyphs already account for ordinary word separation. A remaining
        // positioning gap of roughly one em is therefore strong evidence of a cell or column
        // boundary; real two-column publications often leave only 12-18 pt between columns.
        double splitGap = Math.Max(6, representativeSize * 0.75);

        foreach (PdfLetter letter in letters)
        {
            double gap = double.IsNaN(previousEnd) ? 0 : letter.Origin.X - previousEnd;
            if (current.Count > 0 && gap > splitGap)
            {
                yield return new TextLayoutCell(current);
                current = [];
            }

            current.Add(letter);
            double end = letter.Origin.X + letter.Width;
            previousEnd = double.IsNaN(previousEnd) ? end : Math.Max(previousEnd, end);
        }

        if (current.Count > 0)
            yield return new TextLayoutCell(current);
    }

    private static TextGutter? DetectGutter(PdfRectangle pageBounds, IReadOnlyList<PhysicalRow> rows)
    {
        if (pageBounds.IsEmpty || rows.Count < 6)
            return null;

        const int BinCount = 192;
        int[] occupiedRows = new int[BinCount];
        double binWidth = pageBounds.Width / BinCount;
        if (binWidth <= 0)
            return null;

        foreach (PhysicalRow row in rows)
        {
            bool[] occupied = new bool[BinCount];
            foreach (TextLayoutCell cell in row.Cells)
            {
                int first = Math.Clamp((int)((cell.Bounds.Left - pageBounds.Left) / binWidth), 0, BinCount - 1);
                int last = Math.Clamp((int)((cell.Bounds.Right - pageBounds.Left) / binWidth), 0, BinCount - 1);
                for (int bin = first; bin <= last; bin++)
                    occupied[bin] = true;
            }

            for (int bin = 0; bin < BinCount; bin++)
            {
                if (occupied[bin])
                    occupiedRows[bin]++;
            }
        }

        int maximumOccupancy = Math.Max(1, (int)Math.Floor(rows.Count * 0.18));
        int marginBins = (int)Math.Ceiling(BinCount * 0.12);
        double minimumWidth = Math.Max(6, pageBounds.Width * 0.01);
        TextGutter? best = null;
        double bestScore = 0;

        for (int start = marginBins; start < BinCount - marginBins; start++)
        {
            if (occupiedRows[start] > maximumOccupancy)
                continue;

            int end = start;
            int peakOccupancy = occupiedRows[start];
            while (end + 1 < BinCount - marginBins && occupiedRows[end + 1] <= maximumOccupancy)
            {
                end++;
                peakOccupancy = Math.Max(peakOccupancy, occupiedRows[end]);
            }

            double left = pageBounds.Left + (start * binWidth);
            double right = pageBounds.Left + ((end + 1) * binWidth);
            start = end;
            if (right - left < minimumWidth)
                continue;

            double wide = pageBounds.Width * 0.12;
            int leftSupport = rows.Count(row => row.Cells.Any(cell =>
                cell.Bounds.Right <= right && cell.Bounds.Width >= wide));
            int rightSupport = rows.Count(row => row.Cells.Any(cell =>
                cell.Bounds.Left >= left && cell.Bounds.Width >= wide));
            if (leftSupport < 3 || rightSupport < 3)
                continue;

            double score = (right - left) * Math.Min(leftSupport, rightSupport) / (peakOccupancy + 1);
            if (score > bestScore)
            {
                bestScore = score;
                best = new TextGutter(left, right);
            }
        }

        return best;
    }

    private static List<TextLayoutLine> BuildLines(
        IReadOnlyList<PhysicalRow> rows,
        TextGutter? gutter)
    {
        var lines = new List<TextLayoutLine>(rows.Count * 2);
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            PhysicalRow row = rows[rowIndex];
            if (gutter is null)
            {
                lines.Add(new TextLayoutLine(row.Cells, column: 0));
                continue;
            }

            List<TextLayoutCell> crossing = row.Cells
                .Where(cell => cell.Bounds.Left < gutter.Value.Right && cell.Bounds.Right > gutter.Value.Left)
                .ToList();
            if (crossing.Count > 0)
            {
                lines.Add(new TextLayoutLine(row.Cells, column: -1));
                continue;
            }

            List<TextLayoutCell> left = row.Cells
                .Where(cell => cell.Bounds.Center.X < gutter.Value.Center)
                .ToList();
            List<TextLayoutCell> right = row.Cells
                .Where(cell => cell.Bounds.Center.X >= gutter.Value.Center)
                .ToList();
            double fontSize = Median(row.Letters.Where(letter => !letter.IsWhiteSpace).Select(letter => letter.FontSize));
            double previousGap = rowIndex == 0
                ? double.PositiveInfinity
                : rows[rowIndex - 1].Baseline - row.Baseline;
            double nextGap = rowIndex == rows.Count - 1
                ? double.PositiveInfinity
                : row.Baseline - rows[rowIndex + 1].Baseline;
            double furnitureGap = Math.Max(24, fontSize * 2.5);
            bool edgeFurniture = rowIndex < 2 && nextGap > furnitureGap ||
                                 rowIndex >= rows.Count - 2 && previousGap > furnitureGap;
            if (left.Count > 0 && right.Count > 0 && edgeFurniture)
            {
                lines.Add(new TextLayoutLine(row.Cells, column: -1));
                continue;
            }

            if (left.Count > 0)
                lines.Add(new TextLayoutLine(left, column: 0));
            if (right.Count > 0)
                lines.Add(new TextLayoutLine(right, column: 1));
        }

        return lines;
    }

    private static IReadOnlyList<TextLayoutLine> Order(
        IReadOnlyList<TextLayoutLine> lines,
        TextGutter? gutter)
    {
        if (gutter is null)
        {
            return lines
                .OrderByDescending(line => line.Bounds.Top)
                .ThenBy(line => line.Bounds.Left)
                .ToArray();
        }

        TextLayoutLine[] spanning = lines
            .Where(line => line.Column < 0)
            .OrderByDescending(line => line.Bounds.Top)
            .ToArray();
        List<TextLayoutLine> columns = lines.Where(line => line.Column >= 0).ToList();
        var ordered = new List<TextLayoutLine>(lines.Count);
        double upperBoundary = double.PositiveInfinity;

        foreach (TextLayoutLine span in spanning)
        {
            double boundary = span.Bounds.Center.Y;
            AppendBand(columns, ordered, upperBoundary, boundary);
            ordered.Add(span);
            upperBoundary = boundary;
        }

        AppendBand(columns, ordered, upperBoundary, double.NegativeInfinity);
        return ordered;
    }

    private static void AppendBand(
        List<TextLayoutLine> source,
        List<TextLayoutLine> destination,
        double upperBoundary,
        double lowerBoundary)
    {
        TextLayoutLine[] band = source
            .Where(line => line.Bounds.Center.Y < upperBoundary && line.Bounds.Center.Y >= lowerBoundary)
            .OrderBy(line => line.Column)
            .ThenByDescending(line => line.Bounds.Top)
            .ThenBy(line => line.Bounds.Left)
            .ToArray();
        destination.AddRange(band);
        foreach (TextLayoutLine line in band)
            source.Remove(line);
    }

    private static double Median(IEnumerable<double> source)
    {
        double[] values = source.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (values.Length == 0)
            return 0;

        int middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }

    private sealed class PhysicalRow(double baseline)
    {
        internal double Baseline { get; } = baseline;
        internal List<PdfLetter> Letters { get; } = [];
        internal List<TextLayoutCell> Cells { get; } = [];
    }
}

internal sealed class TextPageLayout(IReadOnlyList<TextLayoutLine> lines, TextGutter? gutter)
{
    internal IReadOnlyList<TextLayoutLine> Lines { get; } = lines;
    internal TextGutter? Gutter { get; } = gutter;
}

internal sealed class TextLayoutLine(IReadOnlyList<TextLayoutCell> cells, int column)
{
    internal IReadOnlyList<TextLayoutCell> Cells { get; } = cells;
    internal int Column { get; } = column;
    internal PdfRectangle Bounds { get; } = cells.Select(cell => cell.Bounds).Aggregate((left, right) => left.Union(right));
    internal string Text { get; } = string.Join(" ", cells.Select(cell => cell.Text));
    internal double FontSize { get; } = WeightedFontSize(cells);
    internal string FontName { get; } = DominantFont(cells);
    internal bool IsBold => FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
                            FontName.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
                            FontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase) ||
                            FontName.Contains("Semibold", StringComparison.OrdinalIgnoreCase);

    private static double WeightedFontSize(IReadOnlyList<TextLayoutCell> source)
    {
        double total = 0;
        int count = 0;
        foreach (TextLayoutCell cell in source)
        {
            foreach (PdfLetter letter in cell.Letters)
            {
                if (letter.IsWhiteSpace)
                    continue;
                total += letter.FontSize;
                count++;
            }
        }

        return count == 0 ? 0 : total / count;
    }

    private static string DominantFont(IReadOnlyList<TextLayoutCell> source) => source
        .SelectMany(cell => cell.Letters)
        .Where(letter => !letter.IsWhiteSpace)
        .GroupBy(letter => letter.FontName, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count())
        .Select(group => group.Key)
        .FirstOrDefault() ?? string.Empty;
}

internal sealed class TextLayoutCell
{
    internal TextLayoutCell(IReadOnlyList<PdfLetter> letters)
    {
        Letters = letters;
        Bounds = letters.Select(letter => letter.Bounds).Aggregate((left, right) => left.Union(right));
        Text = Compose(letters);
    }

    internal IReadOnlyList<PdfLetter> Letters { get; }
    internal PdfRectangle Bounds { get; }
    internal string Text { get; }

    private static string Compose(IReadOnlyList<PdfLetter> letters)
    {
        var text = new StringBuilder(letters.Count);
        double previousEnd = double.NaN;
        foreach (PdfLetter letter in letters)
        {
            if (!double.IsNaN(previousEnd))
            {
                double gap = letter.Origin.X - previousEnd;
                if (gap > letter.FontSize * 0.18 && !letter.IsWhiteSpace && text.Length > 0 && text[^1] != ' ')
                    text.Append(' ');
            }

            text.Append(letter.Text);
            double end = letter.Origin.X + letter.Width;
            previousEnd = double.IsNaN(previousEnd) ? end : Math.Max(previousEnd, end);
        }

        return text.ToString().Trim();
    }
}

internal readonly record struct TextGutter(double Left, double Right)
{
    internal double Center => (Left + Right) / 2;
}
