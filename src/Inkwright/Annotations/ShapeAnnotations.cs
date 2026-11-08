using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>A comment drawn as a shape: it has a line width and, where it encloses an area, a fill.</summary>
public abstract class PdfShapeAnnotation : PdfMarkupAnnotation
{
    private protected PdfShapeAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>The width of the stroked line.</summary>
    public double BorderWidth
    {
        get => Dictionary.GetDictionary(AnnotationNames.BS)?.GetNumber(AnnotationNames.W, 1) ?? 1;
        set
        {
            var style = new PdfDictionary(2);
            style.Set(AnnotationNames.W, PdfValue.Number(Math.Max(0, value)));
            style.Set(AnnotationNames.S, PdfValue.Name(PdfName.Get("S")));
            Dictionary.Set(AnnotationNames.BS, PdfValue.Dictionary(style));
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>The colour inside the shape, or <see langword="null"/> to leave it unfilled.</summary>
    public PdfColor? InteriorColor
    {
        get => ReadColor(AnnotationNames.IC);
        set
        {
            WriteColor(AnnotationNames.IC, value);
            Touch();
            RefreshAppearance();
        }
    }
}

/// <summary>A rectangle drawn on the page.</summary>
public sealed class PdfSquareAnnotation : PdfShapeAnnotation
{
    internal PdfSquareAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawSquareOrCircle(this, ellipse: false);
}

/// <summary>An ellipse drawn on the page.</summary>
public sealed class PdfCircleAnnotation : PdfShapeAnnotation
{
    internal PdfCircleAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawSquareOrCircle(this, ellipse: true);
}

/// <summary>A straight line between two points.</summary>
public sealed class PdfLineAnnotation : PdfShapeAnnotation
{
    internal PdfLineAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>Where the line starts.</summary>
    public PdfPoint Start => Endpoints.Start;

    /// <summary>Where the line ends.</summary>
    public PdfPoint End => Endpoints.End;

    /// <summary>What the line wears at its start.</summary>
    public PdfLineEnding StartEnding
    {
        get => LineEndings.Parse(Dictionary.GetArray(AnnotationNames.LE)?.GetName(0)?.Value);
        set => SetEndings(value, EndEnding);
    }

    /// <summary>What the line wears at its end.</summary>
    public PdfLineEnding EndEnding
    {
        get => LineEndings.Parse(Dictionary.GetArray(AnnotationNames.LE)?.GetName(1)?.Value);
        set => SetEndings(StartEnding, value);
    }

    /// <summary>Puts decorations on both ends at once.</summary>
    /// <param name="start">What the line wears at its start.</param>
    /// <param name="end">What the line wears at its end.</param>
    public void SetEndings(PdfLineEnding start, PdfLineEnding end)
    {
        var endings = new PdfArray(2);
        endings.Add(PdfValue.Name(PdfName.Get(LineEndings.Name(start))));
        endings.Add(PdfValue.Name(PdfName.Get(LineEndings.Name(end))));

        Dictionary.Set(AnnotationNames.LE, PdfValue.Array(endings));
        Touch();
        RefreshAppearance();
    }

    /// <summary>Moves the line.</summary>
    /// <param name="start">The new start point.</param>
    /// <param name="end">The new end point.</param>
    public void SetEndpoints(PdfPoint start, PdfPoint end)
    {
        Dictionary.Set(AnnotationNames.L, PdfValue.Array(PdfArray.OfNumbers(start.X, start.Y, end.X, end.Y)));
        Dictionary.Set(AnnotationNames.Rect, PdfValue.Array(
            new PdfRectangle(start.X, start.Y, end.X, end.Y).Inflate(Math.Max(4, BorderWidth * 2)).ToArray()));

        Touch();
        RefreshAppearance();
    }

    internal (PdfPoint Start, PdfPoint End) Endpoints
    {
        get
        {
            if (Dictionary.GetArray(AnnotationNames.L) is not { Count: >= 4 } line)
                return (PdfPoint.Origin, PdfPoint.Origin);

            return (new PdfPoint(line.GetNumber(0), line.GetNumber(1)),
                    new PdfPoint(line.GetNumber(2), line.GetNumber(3)));
        }
    }

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawLine(this);
}

/// <summary>Freehand strokes, one path per continuous movement of the pen.</summary>
public sealed class PdfInkAnnotation : PdfShapeAnnotation
{
    internal PdfInkAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>The strokes, each a run of points joined in order.</summary>
    public IReadOnlyList<IReadOnlyList<PdfPoint>> Strokes
    {
        get
        {
            List<IReadOnlyList<PdfPoint>> strokes = [];
            if (Dictionary.GetArray(AnnotationNames.InkList) is not { } list)
                return strokes;

            for (int i = 0; i < list.Count; i++)
            {
                if (list.GetArray(i) is not { } path)
                    continue;

                List<PdfPoint> points = [];
                for (int p = 0; p + 1 < path.Count; p += 2)
                    points.Add(new PdfPoint(path.GetNumber(p), path.GetNumber(p + 1)));

                strokes.Add(points);
            }

            return strokes;
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            var list = new PdfArray(value.Count);
            double left = double.MaxValue, bottom = double.MaxValue;
            double right = double.MinValue, top = double.MinValue;

            foreach (IReadOnlyList<PdfPoint> stroke in value)
            {
                var path = new PdfArray(stroke.Count * 2);
                foreach (PdfPoint point in stroke)
                {
                    path.Add(PdfValue.Number(point.X));
                    path.Add(PdfValue.Number(point.Y));
                    left = Math.Min(left, point.X);
                    right = Math.Max(right, point.X);
                    bottom = Math.Min(bottom, point.Y);
                    top = Math.Max(top, point.Y);
                }

                list.Add(PdfValue.Array(path));
            }

            Dictionary.Set(AnnotationNames.InkList, PdfValue.Array(list));

            if (left <= right)
            {
                double margin = Math.Max(2, BorderWidth * 2);
                Dictionary.Set(AnnotationNames.Rect, PdfValue.Array(
                    new PdfRectangle(left - margin, bottom - margin, right + margin, top + margin).ToArray()));
            }

            Touch();
            RefreshAppearance();
        }
    }

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawInk(this);
}

/// <summary>
/// A run of straight segments: closed as a polygon, open as a polyline. Reviewers use these to ring
/// an irregular area or to trace a path through a drawing.
/// </summary>
public sealed class PdfPolygonAnnotation : PdfShapeAnnotation
{
    internal PdfPolygonAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>Whether the last vertex joins back to the first.</summary>
    public bool IsClosed => Type == PdfAnnotationType.Polygon;

    /// <summary>The corners, in order.</summary>
    public IReadOnlyList<PdfPoint> Vertices
    {
        get
        {
            List<PdfPoint> points = [];
            if (Dictionary.GetArray(AnnotationNames.Vertices) is not { } values)
                return points;

            for (int i = 0; i + 1 < values.Count; i += 2)
                points.Add(new PdfPoint(values.GetNumber(i), values.GetNumber(i + 1)));

            return points;
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            var values = new PdfArray(value.Count * 2);
            double left = double.MaxValue, bottom = double.MaxValue;
            double right = double.MinValue, top = double.MinValue;

            foreach (PdfPoint point in value)
            {
                values.Add(PdfValue.Number(point.X));
                values.Add(PdfValue.Number(point.Y));
                left = Math.Min(left, point.X);
                right = Math.Max(right, point.X);
                bottom = Math.Min(bottom, point.Y);
                top = Math.Max(top, point.Y);
            }

            Dictionary.Set(AnnotationNames.Vertices, PdfValue.Array(values));

            if (left <= right)
            {
                double margin = Math.Max(2, BorderWidth * 2);
                Dictionary.Set(AnnotationNames.Rect, PdfValue.Array(
                    new PdfRectangle(left - margin, bottom - margin, right + margin, top + margin).ToArray()));
            }

            Touch();
            RefreshAppearance();
        }
    }

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawPolygon(this);
}

/// <summary>A caret marking where text was inserted or should be.</summary>
public sealed class PdfCaretAnnotation : PdfMarkupAnnotation
{
    internal PdfCaretAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>Whether the caret carries a paragraph sign beside it.</summary>
    public bool IsParagraph
    {
        get => Dictionary.GetName(PdfName.Get("Sy"))?.Value == "P";
        set
        {
            Dictionary.Set(PdfName.Get("Sy"), PdfValue.Name(PdfName.Get(value ? "P" : "None")));
            Touch();
            RefreshAppearance();
        }
    }

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawCaret(this);
}

/// <summary>The names ISO 32000-1 gives to line decorations, and the enum they map to.</summary>
internal static class LineEndings
{
    internal static string Name(PdfLineEnding ending) => ending switch
    {
        PdfLineEnding.ClosedArrow => "ClosedArrow",
        PdfLineEnding.OpenArrow => "OpenArrow",
        PdfLineEnding.Circle => "Circle",
        PdfLineEnding.Square => "Square",
        PdfLineEnding.Diamond => "Diamond",
        PdfLineEnding.Butt => "Butt",
        PdfLineEnding.ReverseClosedArrow => "RClosedArrow",
        PdfLineEnding.ReverseOpenArrow => "ROpenArrow",
        PdfLineEnding.Slash => "Slash",
        _ => "None",
    };

    internal static PdfLineEnding Parse(string? name) => name switch
    {
        "ClosedArrow" => PdfLineEnding.ClosedArrow,
        "OpenArrow" => PdfLineEnding.OpenArrow,
        "Circle" => PdfLineEnding.Circle,
        "Square" => PdfLineEnding.Square,
        "Diamond" => PdfLineEnding.Diamond,
        "Butt" => PdfLineEnding.Butt,
        "RClosedArrow" => PdfLineEnding.ReverseClosedArrow,
        "ROpenArrow" => PdfLineEnding.ReverseOpenArrow,
        "Slash" => PdfLineEnding.Slash,
        _ => PdfLineEnding.None,
    };
}

/// <summary>A hyperlink or an action attached to a rectangle. Links carry no comment of their own.</summary>
public sealed class PdfLinkAnnotation : PdfAnnotation
{
    internal PdfLinkAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>The address the link opens, or <see langword="null"/> when it points inside the document.</summary>
    public string? Uri
    {
        get => Dictionary.GetDictionary(AnnotationNames.A)?.GetTextString(AnnotationNames.URI);
        set
        {
            if (value is null)
            {
                Dictionary.Remove(AnnotationNames.A);
            }
            else
            {
                var action = new PdfDictionary(3);
                action.Set(AnnotationNames.Type, PdfValue.Name(AnnotationNames.Action));
                action.Set(AnnotationNames.S, PdfValue.Name(AnnotationNames.URI));
                action.Set(AnnotationNames.URI, PdfValue.TextString(value));
                Dictionary.Set(AnnotationNames.A, PdfValue.Dictionary(action));
            }

            Touch();
        }
    }

    /// <summary>The page the link jumps to, when it points inside the document.</summary>
    public int? DestinationPageIndex
    {
        get
        {
            PdfArray? array = Dictionary.Get(AnnotationNames.Dest).AsArray;
            if (array is null &&
                Dictionary.GetDictionary(AnnotationNames.A) is { } action &&
                action.GetName(AnnotationNames.S)?.Value == "GoTo")
            {
                array = action.Get(PdfName.Get("D")).AsArray;
            }

            if (array is not { Count: > 0 } || !array.AsSpan()[0].IsReference)
                return null;

            PdfObjectId target = array[0].AsReference;
            for (int i = 0; i < Document.Pages.Count; i++)
            {
                if (Document.Pages[i].Id == target)
                    return i;
            }

            return null;
        }
    }

    /// <summary>Points the link at a page of this document.</summary>
    /// <param name="pageIndex">Which page to jump to.</param>
    public void LinkTo(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, Document.Pages.Count);

        var destination = new PdfArray(2);
        destination.Add(PdfValue.Reference(Document.Pages[pageIndex].Id));
        destination.Add(PdfValue.Name(PdfName.Get("Fit")));

        Dictionary.Remove(AnnotationNames.A);
        Dictionary.Set(AnnotationNames.Dest, PdfValue.Array(destination));
        Touch();
    }
}
