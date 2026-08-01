using System.Collections.Frozen;
using System.Text;

namespace Inkwright.Content;

/// <summary>
/// The content stream operators of ISO 32000-1 Annex A. Anything the table does not name becomes
/// <see cref="Unknown"/>, which keeps a damaged or extended stream walkable instead of fatal.
/// </summary>
public enum PdfOperator : byte
{
    /// <summary>An operator the library does not know.</summary>
    Unknown = 0,

    /// <summary><c>w</c>: set line width.</summary>
    SetLineWidth,

    /// <summary><c>J</c>: set line cap style.</summary>
    SetLineCap,

    /// <summary><c>j</c>: set line join style.</summary>
    SetLineJoin,

    /// <summary><c>M</c>: set miter limit.</summary>
    SetMiterLimit,

    /// <summary><c>d</c>: set dash pattern.</summary>
    SetDash,

    /// <summary><c>ri</c>: set rendering intent.</summary>
    SetRenderingIntent,

    /// <summary><c>i</c>: set flatness tolerance.</summary>
    SetFlatness,

    /// <summary><c>gs</c>: apply a graphics state parameter dictionary.</summary>
    SetExtGState,

    /// <summary><c>q</c>: save the graphics state.</summary>
    SaveState,

    /// <summary><c>Q</c>: restore the graphics state.</summary>
    RestoreState,

    /// <summary><c>cm</c>: concatenate a matrix onto the current transformation.</summary>
    ConcatMatrix,

    /// <summary><c>m</c>: begin a new subpath.</summary>
    MoveTo,

    /// <summary><c>l</c>: append a straight segment.</summary>
    LineTo,

    /// <summary><c>c</c>: append a cubic curve with both control points given.</summary>
    CurveTo,

    /// <summary><c>v</c>: append a cubic curve whose first control point is the current point.</summary>
    CurveToV,

    /// <summary><c>y</c>: append a cubic curve whose second control point is the end point.</summary>
    CurveToY,

    /// <summary><c>h</c>: close the current subpath.</summary>
    ClosePath,

    /// <summary><c>re</c>: append a rectangle as a complete subpath.</summary>
    Rectangle,

    /// <summary><c>S</c>: stroke the path.</summary>
    Stroke,

    /// <summary><c>s</c>: close and stroke the path.</summary>
    CloseStroke,

    /// <summary><c>f</c> or <c>F</c>: fill the path with the nonzero winding rule.</summary>
    Fill,

    /// <summary><c>f*</c>: fill the path with the even-odd rule.</summary>
    FillEvenOdd,

    /// <summary><c>B</c>: fill then stroke.</summary>
    FillStroke,

    /// <summary><c>B*</c>: fill with the even-odd rule then stroke.</summary>
    FillStrokeEvenOdd,

    /// <summary><c>b</c>: close, fill and stroke.</summary>
    CloseFillStroke,

    /// <summary><c>b*</c>: close, fill with the even-odd rule and stroke.</summary>
    CloseFillStrokeEvenOdd,

    /// <summary><c>n</c>: end the path without painting.</summary>
    EndPath,

    /// <summary><c>W</c>: use the path as a clip with the nonzero winding rule.</summary>
    Clip,

    /// <summary><c>W*</c>: use the path as a clip with the even-odd rule.</summary>
    ClipEvenOdd,

    /// <summary><c>BT</c>: begin a text object.</summary>
    BeginText,

    /// <summary><c>ET</c>: end a text object.</summary>
    EndText,

    /// <summary><c>Tc</c>: set character spacing.</summary>
    SetCharSpacing,

    /// <summary><c>Tw</c>: set word spacing.</summary>
    SetWordSpacing,

    /// <summary><c>Tz</c>: set horizontal scaling.</summary>
    SetHorizontalScale,

    /// <summary><c>TL</c>: set leading.</summary>
    SetLeading,

    /// <summary><c>Tf</c>: set font and size.</summary>
    SetFont,

    /// <summary><c>Tr</c>: set the text rendering mode.</summary>
    SetTextRenderMode,

    /// <summary><c>Ts</c>: set the text rise.</summary>
    SetTextRise,

    /// <summary><c>Td</c>: move to the start of the next line by an offset.</summary>
    TextMove,

    /// <summary><c>TD</c>: move to the next line and set the leading.</summary>
    TextMoveSetLeading,

    /// <summary><c>Tm</c>: set the text matrix.</summary>
    SetTextMatrix,

    /// <summary><c>T*</c>: move to the start of the next line.</summary>
    TextNextLine,

    /// <summary><c>Tj</c>: show a string.</summary>
    ShowText,

    /// <summary><c>TJ</c>: show an array of strings and position adjustments.</summary>
    ShowTextAdjusted,

    /// <summary><c>'</c>: move to the next line and show a string.</summary>
    NextLineShowText,

    /// <summary><c>"</c>: set spacing, move to the next line and show a string.</summary>
    NextLineShowTextSpacing,

    /// <summary><c>d0</c>: declare the width of a Type 3 glyph.</summary>
    Type3Width,

    /// <summary><c>d1</c>: declare the width and bounding box of a Type 3 glyph.</summary>
    Type3WidthBBox,

    /// <summary><c>CS</c>: set the stroking colour space.</summary>
    SetStrokeColorSpace,

    /// <summary><c>cs</c>: set the non-stroking colour space.</summary>
    SetFillColorSpace,

    /// <summary><c>SC</c> or <c>SCN</c>: set the stroking colour.</summary>
    SetStrokeColor,

    /// <summary><c>sc</c> or <c>scn</c>: set the non-stroking colour.</summary>
    SetFillColor,

    /// <summary><c>G</c>: set a grey stroking colour.</summary>
    SetStrokeGray,

    /// <summary><c>g</c>: set a grey non-stroking colour.</summary>
    SetFillGray,

    /// <summary><c>RG</c>: set an RGB stroking colour.</summary>
    SetStrokeRgb,

    /// <summary><c>rg</c>: set an RGB non-stroking colour.</summary>
    SetFillRgb,

    /// <summary><c>K</c>: set a CMYK stroking colour.</summary>
    SetStrokeCmyk,

    /// <summary><c>k</c>: set a CMYK non-stroking colour.</summary>
    SetFillCmyk,

    /// <summary><c>sh</c>: paint a shading.</summary>
    PaintShading,

    /// <summary><c>Do</c>: paint an external object.</summary>
    DrawXObject,

    /// <summary><c>BI</c>: begin an inline image.</summary>
    InlineImage,

    /// <summary><c>MP</c>: mark a point.</summary>
    MarkPoint,

    /// <summary><c>DP</c>: mark a point with a property list.</summary>
    MarkPointProperties,

    /// <summary><c>BMC</c>: begin a marked-content sequence.</summary>
    BeginMarkedContent,

    /// <summary><c>BDC</c>: begin a marked-content sequence with a property list.</summary>
    BeginMarkedContentProperties,

    /// <summary><c>EMC</c>: end a marked-content sequence.</summary>
    EndMarkedContent,

    /// <summary><c>BX</c>: begin a compatibility section.</summary>
    BeginCompatibility,

    /// <summary><c>EX</c>: end a compatibility section.</summary>
    EndCompatibility,
}

/// <summary>Maps operator spellings to and from <see cref="PdfOperator"/>.</summary>
internal static class PdfOperators
{
    private static readonly (string Text, PdfOperator Op)[] Table =
    [
        ("w", PdfOperator.SetLineWidth), ("J", PdfOperator.SetLineCap), ("j", PdfOperator.SetLineJoin),
        ("M", PdfOperator.SetMiterLimit), ("d", PdfOperator.SetDash), ("ri", PdfOperator.SetRenderingIntent),
        ("i", PdfOperator.SetFlatness), ("gs", PdfOperator.SetExtGState), ("q", PdfOperator.SaveState),
        ("Q", PdfOperator.RestoreState), ("cm", PdfOperator.ConcatMatrix),
        ("m", PdfOperator.MoveTo), ("l", PdfOperator.LineTo), ("c", PdfOperator.CurveTo),
        ("v", PdfOperator.CurveToV), ("y", PdfOperator.CurveToY), ("h", PdfOperator.ClosePath),
        ("re", PdfOperator.Rectangle),
        ("S", PdfOperator.Stroke), ("s", PdfOperator.CloseStroke), ("f", PdfOperator.Fill),
        ("F", PdfOperator.Fill), ("f*", PdfOperator.FillEvenOdd), ("B", PdfOperator.FillStroke),
        ("B*", PdfOperator.FillStrokeEvenOdd), ("b", PdfOperator.CloseFillStroke),
        ("b*", PdfOperator.CloseFillStrokeEvenOdd), ("n", PdfOperator.EndPath),
        ("W", PdfOperator.Clip), ("W*", PdfOperator.ClipEvenOdd),
        ("BT", PdfOperator.BeginText), ("ET", PdfOperator.EndText),
        ("Tc", PdfOperator.SetCharSpacing), ("Tw", PdfOperator.SetWordSpacing),
        ("Tz", PdfOperator.SetHorizontalScale), ("TL", PdfOperator.SetLeading), ("Tf", PdfOperator.SetFont),
        ("Tr", PdfOperator.SetTextRenderMode), ("Ts", PdfOperator.SetTextRise),
        ("Td", PdfOperator.TextMove), ("TD", PdfOperator.TextMoveSetLeading), ("Tm", PdfOperator.SetTextMatrix),
        ("T*", PdfOperator.TextNextLine), ("Tj", PdfOperator.ShowText), ("TJ", PdfOperator.ShowTextAdjusted),
        ("'", PdfOperator.NextLineShowText), ("\"", PdfOperator.NextLineShowTextSpacing),
        ("d0", PdfOperator.Type3Width), ("d1", PdfOperator.Type3WidthBBox),
        ("CS", PdfOperator.SetStrokeColorSpace), ("cs", PdfOperator.SetFillColorSpace),
        ("SC", PdfOperator.SetStrokeColor), ("SCN", PdfOperator.SetStrokeColor),
        ("sc", PdfOperator.SetFillColor), ("scn", PdfOperator.SetFillColor),
        ("G", PdfOperator.SetStrokeGray), ("g", PdfOperator.SetFillGray),
        ("RG", PdfOperator.SetStrokeRgb), ("rg", PdfOperator.SetFillRgb),
        ("K", PdfOperator.SetStrokeCmyk), ("k", PdfOperator.SetFillCmyk),
        ("sh", PdfOperator.PaintShading), ("Do", PdfOperator.DrawXObject), ("BI", PdfOperator.InlineImage),
        ("MP", PdfOperator.MarkPoint), ("DP", PdfOperator.MarkPointProperties),
        ("BMC", PdfOperator.BeginMarkedContent), ("BDC", PdfOperator.BeginMarkedContentProperties),
        ("EMC", PdfOperator.EndMarkedContent),
        ("BX", PdfOperator.BeginCompatibility), ("EX", PdfOperator.EndCompatibility),
    ];

    private static readonly FrozenDictionary<string, PdfOperator> ByText =
        Table.ToFrozenDictionary(e => e.Text, e => e.Op, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, PdfOperator>.AlternateLookup<ReadOnlySpan<char>> BySpan =
        ByText.GetAlternateLookup<ReadOnlySpan<char>>();

    internal static PdfOperator Parse(ReadOnlySpan<byte> text)
    {
        if (text.Length is 0 or > 3)
            return PdfOperator.Unknown;

        Span<char> chars = stackalloc char[text.Length];
        for (int i = 0; i < text.Length; i++)
            chars[i] = (char)text[i];

        return BySpan.TryGetValue(chars, out PdfOperator op) ? op : PdfOperator.Unknown;
    }

    internal static string Text(PdfOperator op)
    {
        foreach ((string text, PdfOperator candidate) in Table)
        {
            if (candidate == op)
                return text;
        }

        return string.Empty;
    }

    internal static byte[] Bytes(PdfOperator op) => Encoding.ASCII.GetBytes(Text(op));
}
