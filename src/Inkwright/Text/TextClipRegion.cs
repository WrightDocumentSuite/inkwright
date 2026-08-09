namespace Inkwright.Text;

/// <summary>
/// The part of page space in which painted text can still be visible. PDF clipping paths are
/// cumulative intersections, so the region keeps the cheap rectangular intersection separately
/// and evaluates uncommon non-rectangular paths only after that succeeds.
/// </summary>
internal sealed class TextClipRegion
{
    private readonly TextClipPath[] paths;

    private TextClipRegion(PdfRectangle bounds, TextClipPath[] paths)
    {
        Bounds = bounds;
        this.paths = paths;
    }

    internal PdfRectangle Bounds { get; }

    internal static TextClipRegion FromRectangle(PdfRectangle bounds) => new(bounds, []);

    internal TextClipRegion Intersect(TextPath path, bool evenOdd)
    {
        if (Bounds.IsEmpty || path.IsEmpty)
            return new TextClipRegion(default, paths);

        if (path.TryGetAxisAlignedRectangle(out PdfRectangle rectangle))
            return new TextClipRegion(Bounds.Intersect(rectangle), paths);

        PdfRectangle intersection = Bounds.Intersect(path.Bounds);
        if (intersection.IsEmpty)
            return new TextClipRegion(default, paths);

        var updated = new TextClipPath[paths.Length + 1];
        paths.CopyTo(updated, 0);
        updated[^1] = new TextClipPath(path.Subpaths, evenOdd);
        return new TextClipRegion(intersection, updated);
    }

    internal bool Intersects(PdfPoint origin, PdfPoint advance, PdfPoint up)
    {
        if (Bounds.IsEmpty)
            return false;

        PdfPoint offset = up - origin;
        PdfPoint[] glyph = [origin, advance, advance + offset, up];
        PdfRectangle glyphBounds = BoundsOf(glyph);
        if (!Bounds.IntersectsWith(glyphBounds))
            return false;

        foreach (TextClipPath path in paths)
        {
            if (!path.Intersects(glyph, glyphBounds))
                return false;
        }

        return true;
    }

    private static PdfRectangle BoundsOf(IReadOnlyList<PdfPoint> points)
    {
        double left = points[0].X;
        double right = left;
        double bottom = points[0].Y;
        double top = bottom;
        for (int i = 1; i < points.Count; i++)
        {
            left = Math.Min(left, points[i].X);
            right = Math.Max(right, points[i].X);
            bottom = Math.Min(bottom, points[i].Y);
            top = Math.Max(top, points[i].Y);
        }

        return new PdfRectangle(left, bottom, right, top);
    }
}

/// <summary>A completed path in page space, with open subpaths implicitly closed for clipping.</summary>
internal sealed class TextPath(IReadOnlyList<PdfPoint[]> subpaths)
{
    internal IReadOnlyList<PdfPoint[]> Subpaths { get; } = subpaths;

    internal bool IsEmpty => Subpaths.Count == 0;

    internal PdfRectangle Bounds
    {
        get
        {
            PdfRectangle bounds = default;
            bool first = true;
            foreach (PdfPoint[] subpath in Subpaths)
            {
                foreach (PdfPoint point in subpath)
                {
                    var single = new PdfRectangle(point.X, point.Y, point.X, point.Y);
                    bounds = first ? single : bounds.Union(single);
                    first = false;
                }
            }

            return first ? default : bounds;
        }
    }

    internal bool TryGetAxisAlignedRectangle(out PdfRectangle rectangle)
    {
        rectangle = default;
        if (Subpaths.Count != 1 || Subpaths[0].Length is not (4 or 5))
            return false;

        PdfPoint[] points = Subpaths[0];
        int count = points.Length == 5 && points[0] == points[^1] ? 4 : points.Length;
        if (count != 4)
            return false;

        rectangle = new PdfRectangle(
            points.Min(point => point.X),
            points.Min(point => point.Y),
            points.Max(point => point.X),
            points.Max(point => point.Y));
        if (rectangle.IsEmpty)
            return false;

        for (int i = 0; i < count; i++)
        {
            PdfPoint current = points[i];
            PdfPoint next = points[(i + 1) % count];
            if (Math.Abs(current.X - next.X) > 1e-7 && Math.Abs(current.Y - next.Y) > 1e-7)
                return false;
        }

        return true;
    }
}

/// <summary>Accumulates one PDF current path while applying the CTM at construction time.</summary>
internal sealed class TextPathBuilder
{
    private const int CurveSegments = 12;
    private readonly List<List<PdfPoint>> subpaths = [];
    private List<PdfPoint>? current;

    internal bool IsEmpty => subpaths.Count == 0;

    internal void MoveTo(PdfPoint point)
    {
        current = [point];
        subpaths.Add(current);
    }

    internal void LineTo(PdfPoint point)
    {
        EnsureCurrent(point);
        current!.Add(point);
    }

    internal void CurveTo(PdfPoint control1, PdfPoint control2, PdfPoint end)
    {
        EnsureCurrent(end);
        PdfPoint start = current![^1];
        for (int i = 1; i <= CurveSegments; i++)
        {
            double t = (double)i / CurveSegments;
            double u = 1 - t;
            current.Add(new PdfPoint(
                (u * u * u * start.X) + (3 * u * u * t * control1.X) +
                (3 * u * t * t * control2.X) + (t * t * t * end.X),
                (u * u * u * start.Y) + (3 * u * u * t * control1.Y) +
                (3 * u * t * t * control2.Y) + (t * t * t * end.Y)));
        }
    }

    internal void Close()
    {
        if (current is { Count: > 1 } && current[0] != current[^1])
            current.Add(current[0]);
    }

    internal void Rectangle(PdfRectangle rectangle, PdfMatrix transform)
    {
        PdfPoint bottomLeft = transform.Transform(rectangle.BottomLeft);
        PdfPoint bottomRight = transform.Transform(new PdfPoint(rectangle.Right, rectangle.Bottom));
        PdfPoint topRight = transform.Transform(rectangle.TopRight);
        PdfPoint topLeft = transform.Transform(new PdfPoint(rectangle.Left, rectangle.Top));
        current = [bottomLeft, bottomRight, topRight, topLeft, bottomLeft];
        subpaths.Add(current);
    }

    internal void Polygon(params PdfPoint[] points)
    {
        if (points.Length < 3)
            return;

        current = [.. points];
        if (current[0] != current[^1])
            current.Add(current[0]);
        subpaths.Add(current);
    }

    internal TextPath BuildAndReset()
    {
        PdfPoint[][] completed = subpaths
            .Where(path => path.Count >= 2)
            .Select(path => path.ToArray())
            .ToArray();
        Reset();
        return new TextPath(completed);
    }

    internal void Load(TextPath path)
    {
        Reset();
        foreach (PdfPoint[] subpath in path.Subpaths)
        {
            current = [.. subpath];
            subpaths.Add(current);
        }
    }

    internal void Reset()
    {
        subpaths.Clear();
        current = null;
    }

    internal PdfPoint CurrentPoint => current is { Count: > 0 } ? current[^1] : PdfPoint.Origin;

    private void EnsureCurrent(PdfPoint point)
    {
        if (current is null)
            MoveTo(point);
    }
}

/// <summary>A non-rectangular clipping path evaluated with the PDF winding rule.</summary>
internal sealed class TextClipPath(IReadOnlyList<PdfPoint[]> subpaths, bool evenOdd)
{
    private const double Epsilon = 1e-7;

    internal bool Intersects(PdfPoint[] polygon, PdfRectangle polygonBounds)
    {
        foreach (PdfPoint point in polygon)
        {
            if (Contains(point))
                return true;
        }

        PdfPoint center = new(
            polygon.Average(point => point.X),
            polygon.Average(point => point.Y));
        if (Contains(center))
            return true;

        foreach (PdfPoint[] subpath in subpaths)
        {
            for (int i = 0; i < subpath.Length; i++)
            {
                PdfPoint point = subpath[i];
                if (polygonBounds.Contains(point))
                    return true;

                PdfPoint next = subpath[(i + 1) % subpath.Length];
                for (int edge = 0; edge < polygon.Length; edge++)
                {
                    if (SegmentsIntersect(point, next, polygon[edge], polygon[(edge + 1) % polygon.Length]))
                        return true;
                }
            }
        }

        return false;
    }

    private bool Contains(PdfPoint point)
    {
        int winding = 0;
        bool inside = false;
        foreach (PdfPoint[] subpath in subpaths)
        {
            for (int i = 0; i < subpath.Length; i++)
            {
                PdfPoint first = subpath[i];
                PdfPoint second = subpath[(i + 1) % subpath.Length];
                if (OnSegment(first, second, point))
                    return true;

                bool crosses = (first.Y > point.Y) != (second.Y > point.Y);
                if (crosses)
                {
                    double x = first.X + ((point.Y - first.Y) * (second.X - first.X) / (second.Y - first.Y));
                    if (x > point.X)
                        inside = !inside;
                }

                if (first.Y <= point.Y)
                {
                    if (second.Y > point.Y && Cross(first, second, point) > Epsilon)
                        winding++;
                }
                else if (second.Y <= point.Y && Cross(first, second, point) < -Epsilon)
                {
                    winding--;
                }
            }
        }

        return evenOdd ? inside : winding != 0;
    }

    private static bool SegmentsIntersect(PdfPoint a, PdfPoint b, PdfPoint c, PdfPoint d)
    {
        double abC = Cross(a, b, c);
        double abD = Cross(a, b, d);
        double cdA = Cross(c, d, a);
        double cdB = Cross(c, d, b);
        if (((abC > Epsilon && abD < -Epsilon) || (abC < -Epsilon && abD > Epsilon)) &&
            ((cdA > Epsilon && cdB < -Epsilon) || (cdA < -Epsilon && cdB > Epsilon)))
        {
            return true;
        }

        return Math.Abs(abC) <= Epsilon && OnSegment(a, b, c) ||
               Math.Abs(abD) <= Epsilon && OnSegment(a, b, d) ||
               Math.Abs(cdA) <= Epsilon && OnSegment(c, d, a) ||
               Math.Abs(cdB) <= Epsilon && OnSegment(c, d, b);
    }

    private static bool OnSegment(PdfPoint a, PdfPoint b, PdfPoint point) =>
        Math.Abs(Cross(a, b, point)) <= Epsilon &&
        point.X >= Math.Min(a.X, b.X) - Epsilon && point.X <= Math.Max(a.X, b.X) + Epsilon &&
        point.Y >= Math.Min(a.Y, b.Y) - Epsilon && point.Y <= Math.Max(a.Y, b.Y) + Epsilon;

    private static double Cross(PdfPoint a, PdfPoint b, PdfPoint point) =>
        ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));
}
