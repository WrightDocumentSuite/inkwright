using Inkwright.Text;

namespace Inkwright.Tables;

/// <summary>Controls conservative table reconstruction from painted page rules.</summary>
public sealed record TableExtractionOptions
{
    /// <summary>Maximum coordinate difference, in points, treated as the same painted rule.</summary>
    public double SnapTolerance { get; init; } = 1.25;

    /// <summary>Maximum gap joined between collinear pieces of the same rule.</summary>
    public double JoinTolerance { get; init; } = 4;

    /// <summary>Smallest accepted atomic row height or column width, in points.</summary>
    public double MinimumCellSize { get; init; } = 3;

    /// <summary>Minimum number of atomic grid cells required before a rectangle is called a table.</summary>
    public int MinimumCells { get; init; } = 2;

    /// <summary>Safety limit for distinct row or column edges in one connected grid.</summary>
    public int MaximumEdgesPerAxis { get; init; } = 512;

    /// <summary>Safety limit for straight painted rules considered on one page.</summary>
    public int MaximumRules { get; init; } = 8_192;
}

/// <summary>A table reconstructed from straight painted rules and positioned glyphs.</summary>
public sealed class PdfTable
{
    internal PdfTable(
        PdfRectangle bounds,
        IReadOnlyList<double> rowEdges,
        IReadOnlyList<double> columnEdges,
        IReadOnlyList<PdfTableCell> cells)
    {
        Bounds = bounds;
        RowEdges = rowEdges;
        ColumnEdges = columnEdges;
        Cells = cells;
    }

    /// <summary>Table bounds in rotation-normalised coordinates from the page's top-left corner.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>Top-to-bottom row boundary coordinates, in points from the visible page top.</summary>
    public IReadOnlyList<double> RowEdges { get; }

    /// <summary>Left-to-right column boundary coordinates, in points from the visible page left.</summary>
    public IReadOnlyList<double> ColumnEdges { get; }

    /// <summary>Visible cells in row-major order. Covered positions of a spanning cell are omitted.</summary>
    public IReadOnlyList<PdfTableCell> Cells { get; }

    /// <summary>Number of atomic rows in the reconstructed grid.</summary>
    public int RowCount => Math.Max(0, RowEdges.Count - 1);

    /// <summary>Number of atomic columns in the reconstructed grid.</summary>
    public int ColumnCount => Math.Max(0, ColumnEdges.Count - 1);
}

/// <summary>One visible table cell, including spans inferred from absent internal rules.</summary>
public sealed class PdfTableCell
{
    internal PdfTableCell(
        int row,
        int column,
        int rowSpan,
        int columnSpan,
        string text,
        PdfRectangle bounds,
        IReadOnlyList<PdfLetter> letters,
        bool isBold,
        double fontSize)
    {
        Row = row;
        Column = column;
        RowSpan = rowSpan;
        ColumnSpan = columnSpan;
        Text = text;
        Bounds = bounds;
        Letters = letters;
        IsBold = isBold;
        FontSize = fontSize;
    }

    /// <summary>Zero-based atomic row containing the top-left of the cell.</summary>
    public int Row { get; }

    /// <summary>Zero-based atomic column containing the top-left of the cell.</summary>
    public int Column { get; }

    /// <summary>Number of atomic rows covered by the cell.</summary>
    public int RowSpan { get; }

    /// <summary>Number of atomic columns covered by the cell.</summary>
    public int ColumnSpan { get; }

    /// <summary>Visible text in physical line order. Line breaks inside the cell are retained.</summary>
    public string Text { get; }

    /// <summary>Cell bounds in rotation-normalised coordinates from the page's top-left corner.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>Source glyphs assigned to the cell, in content-stream order.</summary>
    public IReadOnlyList<PdfLetter> Letters { get; }

    /// <summary>Whether most visible glyphs use a bold-family font.</summary>
    public bool IsBold { get; }

    /// <summary>Average visible glyph size, in points.</summary>
    public double FontSize { get; }
}
