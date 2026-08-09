using Inkwright.Content;
using Inkwright.Cos;

namespace Inkwright.Graphics;

/// <summary>A straight vector segment painted on a PDF page.</summary>
/// <param name="Start">Start point in points from the visible page's top-left corner.</param>
/// <param name="End">End point in the same coordinate system.</param>
/// <param name="Width">Effective stroke width in points.</param>
public readonly record struct PdfLineSegment(PdfPoint Start, PdfPoint End, double Width);

/// <summary>
/// Finds stroked straight segments and dark, thin filled rectangles in page and Form XObject
/// content streams. PDF producers commonly paint table rules as filled slivers rather than paths.
/// </summary>
internal static class PageLineFinder
{
    private const int MaxDepth = 12;
    private const double MaximumFilledRuleWidth = 3;
    private const double MaximumRuleLuminance = 0.75;

    internal static IReadOnlyList<PdfLineSegment> Find(PdfPage page)
    {
        List<PdfLineSegment> raw = [];
        Run(page.GetContent(), page.Resources, page.Document, PdfMatrix.Identity, 1, raw, 0);

        PdfMatrix visible = page.TopLeftTransform();
        return [.. raw.Select(line => line with
        {
            Start = visible.Transform(line.Start),
            End = visible.Transform(line.End),
        })];
    }

    private static void Run(
        ReadOnlySpan<byte> content,
        PdfDictionary resources,
        PdfDocument document,
        PdfMatrix initialTransform,
        double initialLineWidth,
        List<PdfLineSegment> output,
        int depth)
    {
        if (depth > MaxDepth)
            return;

        ContentOperations operations = ContentOperations.Parse(content, document);
        var states = new Stack<GraphicsState>();
        List<(PdfPoint Start, PdfPoint End)> path = [];
        List<FilledRectangle> rectangles = [];
        PdfMatrix transform = initialTransform;
        double lineWidth = initialLineWidth;
        double fillLuminance = 0;
        PdfPoint current = default;
        PdfPoint subpathStart = default;
        bool hasCurrent = false;

        for (int index = 0; index < operations.Count; index++)
        {
            ReadOnlySpan<PdfValue> operands = operations.GetOperands(index);
            PdfOperator op = operations.GetOperator(index);

            switch (op)
            {
                case PdfOperator.SaveState:
                    states.Push(new GraphicsState(transform, lineWidth, fillLuminance));
                    break;

                case PdfOperator.RestoreState:
                    if (states.Count > 0)
                    {
                        GraphicsState state = states.Pop();
                        transform = state.Transform;
                        lineWidth = state.LineWidth;
                        fillLuminance = state.FillLuminance;
                    }
                    break;

                case PdfOperator.ConcatMatrix when operands.Length >= 6:
                    transform = Matrix(operands) * transform;
                    break;

                case PdfOperator.SetLineWidth when operands.Length >= 1:
                    lineWidth = Math.Max(0, operands[0].AsNumber);
                    break;

                case PdfOperator.SetFillGray when operands.Length >= 1:
                    fillLuminance = Unit(operands[0].AsNumber);
                    break;

                case PdfOperator.SetFillRgb when operands.Length >= 3:
                    fillLuminance = RgbLuminance(
                        operands[0].AsNumber, operands[1].AsNumber, operands[2].AsNumber);
                    break;

                case PdfOperator.SetFillCmyk when operands.Length >= 4:
                    fillLuminance = CmykLuminance(
                        operands[0].AsNumber, operands[1].AsNumber,
                        operands[2].AsNumber, operands[3].AsNumber);
                    break;

                case PdfOperator.MoveTo when operands.Length >= 2:
                    current = transform.Transform(new PdfPoint(operands[0].AsNumber, operands[1].AsNumber));
                    subpathStart = current;
                    hasCurrent = true;
                    break;

                case PdfOperator.LineTo when operands.Length >= 2:
                {
                    PdfPoint next = transform.Transform(new PdfPoint(operands[0].AsNumber, operands[1].AsNumber));
                    if (hasCurrent)
                        path.Add((current, next));
                    current = next;
                    hasCurrent = true;
                    break;
                }

                case PdfOperator.Rectangle when operands.Length >= 4:
                {
                    double x = operands[0].AsNumber;
                    double y = operands[1].AsNumber;
                    double width = operands[2].AsNumber;
                    double height = operands[3].AsNumber;
                    PdfPoint a = transform.Transform(new PdfPoint(x, y));
                    PdfPoint b = transform.Transform(new PdfPoint(x + width, y));
                    PdfPoint c = transform.Transform(new PdfPoint(x + width, y + height));
                    PdfPoint d = transform.Transform(new PdfPoint(x, y + height));
                    path.Add((a, b));
                    path.Add((b, c));
                    path.Add((c, d));
                    path.Add((d, a));
                    rectangles.Add(new FilledRectangle(a, b, c, d));
                    current = a;
                    subpathStart = a;
                    hasCurrent = true;
                    break;
                }

                case PdfOperator.ClosePath:
                    ClosePath(path, ref current, subpathStart, hasCurrent);
                    break;

                case PdfOperator.Stroke:
                case PdfOperator.FillStroke:
                case PdfOperator.FillStrokeEvenOdd:
                    Stroke(path, lineWidth, transform, output);
                    Reset(path, rectangles, ref hasCurrent);
                    break;

                case PdfOperator.CloseStroke:
                case PdfOperator.CloseFillStroke:
                case PdfOperator.CloseFillStrokeEvenOdd:
                    ClosePath(path, ref current, subpathStart, hasCurrent);
                    Stroke(path, lineWidth, transform, output);
                    Reset(path, rectangles, ref hasCurrent);
                    break;

                case PdfOperator.Fill:
                case PdfOperator.FillEvenOdd:
                    FillThinRectangles(rectangles, fillLuminance, output);
                    Reset(path, rectangles, ref hasCurrent);
                    break;

                case PdfOperator.EndPath:
                    Reset(path, rectangles, ref hasCurrent);
                    break;

                case PdfOperator.DrawXObject when operands.Length >= 1 && operands[0].AsName is { } name:
                    DrawForm(name, resources, document, transform, lineWidth, output, depth);
                    break;
            }
        }
    }

    private static void DrawForm(
        PdfName name,
        PdfDictionary resources,
        PdfDocument document,
        PdfMatrix transform,
        double lineWidth,
        List<PdfLineSegment> output,
        int depth)
    {
        if (resources.GetDictionary(PdfNames.XObject)?.GetStream(name) is not { } stream ||
            stream.Dictionary.GetName(PdfNames.Subtype) != PdfNames.Form)
        {
            return;
        }

        PdfDictionary formResources = stream.Dictionary.GetDictionary(PdfNames.Resources) ?? resources;
        PdfMatrix formTransform = PdfMatrix.FromArray(stream.Dictionary.GetArray(PdfNames.Matrix)) * transform;
        Run(stream.DecodedSpan(), formResources, document, formTransform, lineWidth, output, depth + 1);
    }

    private static void Stroke(
        List<(PdfPoint Start, PdfPoint End)> path,
        double lineWidth,
        PdfMatrix transform,
        List<PdfLineSegment> output)
    {
        double width = Math.Max(0.1, lineWidth * transform.ScaleFactor);
        foreach ((PdfPoint start, PdfPoint end) in path)
        {
            if (Math.Abs(start.X - end.X) + Math.Abs(start.Y - end.Y) > 0.01)
                output.Add(new PdfLineSegment(start, end, width));
        }
    }

    private static void FillThinRectangles(
        IReadOnlyList<FilledRectangle> rectangles,
        double luminance,
        ICollection<PdfLineSegment> output)
    {
        if (luminance > MaximumRuleLuminance)
            return;

        foreach (FilledRectangle rectangle in rectangles)
        {
            double first = rectangle.A.DistanceTo(rectangle.B);
            double second = rectangle.B.DistanceTo(rectangle.C);
            double thickness = Math.Min(first, second);
            double length = Math.Max(first, second);
            if (thickness <= 0 || thickness > MaximumFilledRuleWidth || length <= thickness * 1.5)
                continue;

            PdfPoint start;
            PdfPoint end;
            if (first >= second)
            {
                start = Midpoint(rectangle.A, rectangle.D);
                end = Midpoint(rectangle.B, rectangle.C);
            }
            else
            {
                start = Midpoint(rectangle.A, rectangle.B);
                end = Midpoint(rectangle.D, rectangle.C);
            }

            output.Add(new PdfLineSegment(start, end, Math.Max(0.1, thickness)));
        }
    }

    private static void ClosePath(
        List<(PdfPoint Start, PdfPoint End)> path,
        ref PdfPoint current,
        PdfPoint start,
        bool hasCurrent)
    {
        if (!hasCurrent || current == start)
            return;

        path.Add((current, start));
        current = start;
    }

    private static void Reset(
        List<(PdfPoint Start, PdfPoint End)> path,
        List<FilledRectangle> rectangles,
        ref bool hasCurrent)
    {
        path.Clear();
        rectangles.Clear();
        hasCurrent = false;
    }

    private static PdfPoint Midpoint(PdfPoint first, PdfPoint second) =>
        new((first.X + second.X) / 2, (first.Y + second.Y) / 2);

    private static double Unit(double value) => Math.Clamp(value, 0, 1);

    private static double RgbLuminance(double red, double green, double blue) =>
        0.2126 * Unit(red) + 0.7152 * Unit(green) + 0.0722 * Unit(blue);

    private static double CmykLuminance(double cyan, double magenta, double yellow, double black) =>
        RgbLuminance(
            1 - Math.Min(1, Unit(cyan) + Unit(black)),
            1 - Math.Min(1, Unit(magenta) + Unit(black)),
            1 - Math.Min(1, Unit(yellow) + Unit(black)));

    private static PdfMatrix Matrix(ReadOnlySpan<PdfValue> values) => new(
        values[0].AsNumber,
        values[1].AsNumber,
        values[2].AsNumber,
        values[3].AsNumber,
        values[4].AsNumber,
        values[5].AsNumber);

    private readonly record struct GraphicsState(
        PdfMatrix Transform,
        double LineWidth,
        double FillLuminance);

    private readonly record struct FilledRectangle(PdfPoint A, PdfPoint B, PdfPoint C, PdfPoint D);
}
