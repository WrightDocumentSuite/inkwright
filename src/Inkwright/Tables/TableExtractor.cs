using System.Text;
using Inkwright.Graphics;
using Inkwright.Text;

namespace Inkwright.Tables;

internal static class TableExtractor
{
    internal static IReadOnlyList<PdfTable> Extract(PdfPage page, TableExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        List<Rule> rules = Normalize(page.ExtractLines(), options);
        if (rules.Count == 0)
            return [];

        IReadOnlyList<List<Rule>> components = Components(rules, options.SnapTolerance);
        IReadOnlyList<VisibleLetter> letters = VisibleLetters(page);
        var tables = new List<PdfTable>();
        foreach (List<Rule> component in components)
        {
            if (TryBuild(component, letters, options, out PdfTable? table) && table is not null)
                tables.Add(table);
        }

        return [.. tables.OrderBy(table => table.Bounds.Bottom).ThenBy(table => table.Bounds.Left)];
    }

    private static void Validate(TableExtractionOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SnapTolerance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.JoinTolerance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MinimumCellSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MinimumCells);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumEdgesPerAxis, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRules, 4);
    }

    private static List<Rule> Normalize(
        IReadOnlyList<PdfLineSegment> source,
        TableExtractionOptions options)
    {
        var raw = new List<Rule>();
        foreach (PdfLineSegment line in source)
        {
            double dx = Math.Abs(line.End.X - line.Start.X);
            double dy = Math.Abs(line.End.Y - line.Start.Y);
            double axisTolerance = Math.Max(options.SnapTolerance, line.Width * 2);
            if (dy <= axisTolerance && dx >= options.MinimumCellSize)
            {
                raw.Add(new Rule(
                    Horizontal: true,
                    Fixed: (line.Start.Y + line.End.Y) / 2,
                    Start: Math.Min(line.Start.X, line.End.X),
                    End: Math.Max(line.Start.X, line.End.X),
                    line.Width));
            }
            else if (dx <= axisTolerance && dy >= options.MinimumCellSize)
            {
                raw.Add(new Rule(
                    Horizontal: false,
                    Fixed: (line.Start.X + line.End.X) / 2,
                    Start: Math.Min(line.Start.Y, line.End.Y),
                    End: Math.Max(line.Start.Y, line.End.Y),
                    line.Width));
            }
        }

        if (raw.Count == 0)
            return [];
        if (raw.Count > options.MaximumRules)
            return [];

        double[] horizontalCoordinates = Clusters(raw.Where(rule => rule.Horizontal).Select(rule => rule.Fixed), options.SnapTolerance);
        double[] verticalCoordinates = Clusters(raw.Where(rule => !rule.Horizontal).Select(rule => rule.Fixed), options.SnapTolerance);
        for (int index = 0; index < raw.Count; index++)
        {
            Rule rule = raw[index];
            double[] fixedCoordinates = rule.Horizontal ? horizontalCoordinates : verticalCoordinates;
            double[] endpoints = rule.Horizontal ? verticalCoordinates : horizontalCoordinates;
            raw[index] = rule with
            {
                Fixed = Nearest(rule.Fixed, fixedCoordinates, options.SnapTolerance),
                Start = Nearest(rule.Start, endpoints, options.JoinTolerance),
                End = Nearest(rule.End, endpoints, options.JoinTolerance),
            };
        }

        var merged = new List<Rule>();
        foreach (IGrouping<(bool Horizontal, double Fixed), Rule> group in raw
                     .GroupBy(rule => (rule.Horizontal, rule.Fixed)))
        {
            Rule[] ordered = [.. group.OrderBy(rule => rule.Start).ThenBy(rule => rule.End)];
            Rule current = ordered[0];
            for (int index = 1; index < ordered.Length; index++)
            {
                Rule next = ordered[index];
                if (next.Start <= current.End + options.JoinTolerance)
                {
                    current = current with
                    {
                        End = Math.Max(current.End, next.End),
                        Width = Math.Max(current.Width, next.Width),
                    };
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }
            merged.Add(current);
        }

        return merged;
    }

    private static IReadOnlyList<List<Rule>> Components(IReadOnlyList<Rule> rules, double tolerance)
    {
        var union = new DisjointSet(rules.Count);
        for (int left = 0; left < rules.Count; left++)
        {
            for (int right = left + 1; right < rules.Count; right++)
            {
                if (rules[left].Horizontal == rules[right].Horizontal)
                    continue;

                Rule horizontal = rules[left].Horizontal ? rules[left] : rules[right];
                Rule vertical = rules[left].Horizontal ? rules[right] : rules[left];
                if (vertical.Fixed >= horizontal.Start - tolerance &&
                    vertical.Fixed <= horizontal.End + tolerance &&
                    horizontal.Fixed >= vertical.Start - tolerance &&
                    horizontal.Fixed <= vertical.End + tolerance)
                {
                    union.Join(left, right);
                }
            }
        }

        return [.. Enumerable.Range(0, rules.Count)
            .GroupBy(union.Find)
            .Select(group => group.Select(index => rules[index]).ToList())];
    }

    private static bool TryBuild(
        IReadOnlyList<Rule> rules,
        IReadOnlyList<VisibleLetter> letters,
        TableExtractionOptions options,
        out PdfTable? table)
    {
        table = null;
        Rule[] horizontal = [.. rules.Where(rule => rule.Horizontal)];
        Rule[] vertical = [.. rules.Where(rule => !rule.Horizontal)];
        double[] rows = DistinctCoordinates(horizontal.Select(rule => rule.Fixed), options.MinimumCellSize);
        double[] columns = DistinctCoordinates(vertical.Select(rule => rule.Fixed), options.MinimumCellSize);
        if (rows.Length is < 2 || columns.Length is < 2 ||
            rows.Length > options.MaximumEdgesPerAxis || columns.Length > options.MaximumEdgesPerAxis ||
            (rows.Length - 1) * (columns.Length - 1) < options.MinimumCells)
        {
            return false;
        }

        double top = rows[0];
        double bottom = rows[^1];
        double left = columns[0];
        double right = columns[^1];
        if (!Covered(horizontal, top, left, right) || !Covered(horizontal, bottom, left, right) ||
            !Covered(vertical, left, top, bottom) || !Covered(vertical, right, top, bottom))
        {
            return false;
        }

        int rowCount = rows.Length - 1;
        int columnCount = columns.Length - 1;
        var union = new DisjointSet(rowCount * columnCount);
        for (int row = 0; row < rowCount; row++)
        {
            for (int column = 0; column < columnCount; column++)
            {
                int index = Index(row, column, columnCount);
                if (column + 1 < columnCount &&
                    !Covered(vertical, columns[column + 1], rows[row], rows[row + 1]))
                {
                    union.Join(index, Index(row, column + 1, columnCount));
                }

                if (row + 1 < rowCount &&
                    !Covered(horizontal, rows[row + 1], columns[column], columns[column + 1]))
                {
                    union.Join(index, Index(row + 1, column, columnCount));
                }
            }
        }

        PdfRectangle bounds = new(left, top, right, bottom);
        VisibleLetter[] inside = [.. letters.Where(letter => bounds.Contains(letter.Bounds.Center))];
        var cells = new List<PdfTableCell>();
        foreach (IGrouping<int, int> group in Enumerable.Range(0, rowCount * columnCount).GroupBy(union.Find))
        {
            int[] indexes = [.. group];
            int minRow = indexes.Min(index => index / columnCount);
            int maxRow = indexes.Max(index => index / columnCount);
            int minColumn = indexes.Min(index => index % columnCount);
            int maxColumn = indexes.Max(index => index % columnCount);
            if (indexes.Length != (maxRow - minRow + 1) * (maxColumn - minColumn + 1))
            {
                foreach (int index in indexes)
                    cells.Add(Cell(index / columnCount, index % columnCount, 1, 1));
                continue;
            }

            cells.Add(Cell(
                minRow,
                minColumn,
                maxRow - minRow + 1,
                maxColumn - minColumn + 1));
        }

        table = new PdfTable(
            bounds,
            rows,
            columns,
            [.. cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column)]);
        return true;

        PdfTableCell Cell(int row, int column, int rowSpan, int columnSpan)
        {
            PdfRectangle cellBounds = new(
                columns[column], rows[row],
                columns[column + columnSpan], rows[row + rowSpan]);
            VisibleLetter[] cellLetters = [.. inside.Where(letter => cellBounds.Contains(letter.Bounds.Center))];
            PdfLetter[] sourceLetters = [.. cellLetters.Select(letter => letter.Source)];
            PdfLetter[] visibleGlyphs = [.. sourceLetters.Where(letter => !letter.IsWhiteSpace)];
            bool bold = visibleGlyphs.Length > 0 && visibleGlyphs.Count(letter => IsBold(letter.FontName)) * 2 >= visibleGlyphs.Length;
            double fontSize = visibleGlyphs.Length == 0 ? 0 : visibleGlyphs.Average(letter => letter.FontSize);
            return new PdfTableCell(
                row, column, rowSpan, columnSpan,
                Compose(cellLetters), cellBounds, sourceLetters, bold, fontSize);
        }
    }

    private static IReadOnlyList<VisibleLetter> VisibleLetters(PdfPage page)
    {
        var extraction = new TextExtractionOptions
        {
            ResolveBidirectional = true,
            SkipHiddenLayers = true,
            UseActualText = true,
        };
        PdfMatrix transform = page.TopLeftTransform();
        return [.. page.ExtractLetters(extraction).Select(letter =>
            new VisibleLetter(letter, letter.Bounds.Transform(transform)))];
    }

    private static string Compose(IReadOnlyList<VisibleLetter> letters)
    {
        VisibleLetter[] visible = [.. letters.Where(letter => !string.IsNullOrEmpty(letter.Source.Text))
            .OrderBy(letter => letter.Bounds.Center.Y)
            .ThenBy(letter => letter.Bounds.Left)];
        if (visible.Length == 0)
            return string.Empty;

        var lines = new List<List<VisibleLetter>>();
        foreach (VisibleLetter letter in visible)
        {
            double tolerance = Math.Max(1, letter.Source.FontSize * 0.35);
            List<VisibleLetter>? line = lines.LastOrDefault(candidate =>
                Math.Abs(candidate.Average(item => item.Bounds.Center.Y) - letter.Bounds.Center.Y) <= tolerance);
            if (line is null)
            {
                line = [];
                lines.Add(line);
            }
            line.Add(letter);
        }

        var result = new StringBuilder();
        foreach (List<VisibleLetter> line in lines.OrderBy(items => items.Average(item => item.Bounds.Center.Y)))
        {
            if (result.Length > 0)
                result.Append('\n');

            double previousEnd = double.NaN;
            foreach (VisibleLetter letter in line.OrderBy(item => item.Bounds.Left))
            {
                if (!double.IsNaN(previousEnd))
                {
                    double gap = letter.Bounds.Left - previousEnd;
                    if (gap > letter.Source.FontSize * 0.18 && !letter.Source.IsWhiteSpace &&
                        result.Length > 0 && result[^1] is not (' ' or '\n'))
                    {
                        result.Append(' ');
                    }
                }

                result.Append(letter.Source.Text);
                previousEnd = double.IsNaN(previousEnd)
                    ? letter.Bounds.Right
                    : Math.Max(previousEnd, letter.Bounds.Right);
            }
        }

        return result.ToString().Trim();
    }

    private static bool Covered(IReadOnlyList<Rule> rules, double fixedCoordinate, double start, double end)
    {
        double length = end - start;
        if (length <= 0)
            return false;

        double covered = rules
            .Where(rule => Math.Abs(rule.Fixed - fixedCoordinate) <= 0.01)
            .Sum(rule => Math.Max(0, Math.Min(end, rule.End) - Math.Max(start, rule.Start)));
        return covered >= length * 0.72;
    }

    private static double[] Clusters(IEnumerable<double> values, double tolerance)
    {
        double[] sorted = [.. values.OrderBy(value => value)];
        if (sorted.Length == 0)
            return [];

        var clusters = new List<List<double>> { new() { sorted[0] } };
        for (int index = 1; index < sorted.Length; index++)
        {
            List<double> current = clusters[^1];
            if (Math.Abs(sorted[index] - current.Average()) <= tolerance)
                current.Add(sorted[index]);
            else
                clusters.Add([sorted[index]]);
        }
        return [.. clusters.Select(cluster => cluster.Average())];
    }

    private static double[] DistinctCoordinates(IEnumerable<double> values, double minimumGap)
    {
        double[] sorted = [.. values.Distinct().OrderBy(value => value)];
        var result = new List<double>();
        foreach (double value in sorted)
        {
            if (result.Count == 0 || value - result[^1] >= minimumGap)
                result.Add(value);
        }
        return [.. result];
    }

    private static double Nearest(double value, IReadOnlyList<double> candidates, double tolerance)
    {
        if (candidates.Count == 0)
            return value;
        double nearest = candidates[0];
        double distance = Math.Abs(value - nearest);
        for (int index = 1; index < candidates.Count; index++)
        {
            double candidate = Math.Abs(value - candidates[index]);
            if (candidate < distance)
            {
                distance = candidate;
                nearest = candidates[index];
            }
        }
        return distance <= tolerance ? nearest : value;
    }

    private static bool IsBold(string fontName) =>
        fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Semibold", StringComparison.OrdinalIgnoreCase);

    private static int Index(int row, int column, int columnCount) => row * columnCount + column;

    private readonly record struct Rule(bool Horizontal, double Fixed, double Start, double End, double Width);
    private readonly record struct VisibleLetter(PdfLetter Source, PdfRectangle Bounds);

    private sealed class DisjointSet
    {
        private readonly int[] parent;
        private readonly byte[] rank;

        internal DisjointSet(int count)
        {
            parent = Enumerable.Range(0, count).ToArray();
            rank = new byte[count];
        }

        internal int Find(int item)
        {
            while (parent[item] != item)
            {
                parent[item] = parent[parent[item]];
                item = parent[item];
            }
            return item;
        }

        internal void Join(int left, int right)
        {
            int a = Find(left);
            int b = Find(right);
            if (a == b)
                return;
            if (rank[a] < rank[b])
                parent[a] = b;
            else if (rank[a] > rank[b])
                parent[b] = a;
            else
            {
                parent[b] = a;
                rank[a]++;
            }
        }
    }
}
