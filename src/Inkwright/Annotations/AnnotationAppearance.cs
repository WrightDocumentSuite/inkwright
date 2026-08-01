using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Fonts;

namespace Inkwright.Annotations;

/// <summary>
/// Draws what a reader shows for an annotation. The specification lets a reader invent its own
/// appearance for a comment, and they all invent different ones; generating the stream ourselves is
/// the only way a highlight lands on the same words and a note shows the same icon everywhere.
/// </summary>
internal static class AnnotationAppearance
{
    internal static void DrawNote(PdfTextAnnotation note)
    {
        PdfRectangle box = note.Rectangle;
        if (box.IsEmpty)
            return;

        PdfColor fill = note.Color ?? PdfColor.Parse("#FFD34E");

        Build(note, box, canvas =>
        {
            double size = Math.Min(box.Width, box.Height);
            double x = box.Left;
            double y = box.Bottom;

            canvas.FillColor(fill).StrokeColor(Darken(fill)).LineWidth(Math.Max(0.6, size / 20));

            switch (note.Icon)
            {
                case PdfNoteIcon.Help:
                    canvas.Ellipse(new PdfRectangle(x, y, x + size, y + size)).FillStroke();
                    Glyph(canvas, note, "?", box, size * 0.62);
                    break;

                case PdfNoteIcon.Key:
                    canvas.Ellipse(new PdfRectangle(x + (size * 0.08), y + (size * 0.5), x + (size * 0.52), y + (size * 0.94)))
                          .FillStroke();
                    canvas.Rectangle(x + (size * 0.42), y + (size * 0.1), size * 0.16, size * 0.5).FillStroke();
                    canvas.Rectangle(x + (size * 0.58), y + (size * 0.1), size * 0.22, size * 0.14).FillStroke();
                    break;

                case PdfNoteIcon.Insert:
                    canvas.MoveTo(x + (size / 2), y + size)
                          .LineTo(x + size, y)
                          .LineTo(x, y)
                          .ClosePath()
                          .FillStroke();
                    break;

                case PdfNoteIcon.Paragraph:
                case PdfNoteIcon.NewParagraph:
                    Bubble(canvas, box, size);
                    canvas.FillStroke();
                    Glyph(canvas, note, "\u00B6", box, size * 0.6);
                    break;

                default:
                    Bubble(canvas, box, size);
                    canvas.FillStroke();
                    Lines(canvas, box, size);
                    break;
            }
        });
    }

    internal static void DrawNothing(PdfAnnotation annotation)
    {
        PdfRectangle box = annotation.Rectangle;
        Build(annotation, box.IsEmpty ? PdfRectangle.FromSize(box.Left, box.Bottom, 1, 1) : box, static _ => { });
    }

    internal static void DrawTextMarkup(PdfTextMarkupAnnotation markup)
    {
        IReadOnlyList<PdfRectangle> areas = markup.Areas;
        if (areas.Count == 0)
            return;

        PdfColor color = markup.Color ?? DefaultMarkupColor(markup.Type);
        bool highlight = markup.Type == PdfAnnotationType.Highlight;

        Build(markup, markup.Rectangle, canvas =>
        {
            foreach (PdfRectangle area in areas)
            {
                double thickness = Math.Max(0.6, area.Height * 0.06);

                switch (markup.Type)
                {
                    case PdfAnnotationType.Highlight:
                        canvas.FillColor(color).Rectangle(area).Fill();
                        break;

                    case PdfAnnotationType.Underline:
                        Rule(canvas, color, area.Left, area.Right, area.Bottom + (area.Height * 0.06), thickness);
                        break;

                    case PdfAnnotationType.StrikeOut:
                        Rule(canvas, color, area.Left, area.Right, area.Bottom + (area.Height * 0.42), thickness);
                        break;

                    default:
                        Squiggle(canvas, color, area, thickness);
                        break;
                }
            }
        },
        // A wash painted over words has to multiply, or it hides the very text it marks.
        blendMultiply: highlight);
    }

    internal static void DrawFreeText(PdfFreeTextAnnotation annotation)
    {
        PdfRectangle bounds = annotation.Rectangle;
        PdfRectangle box = annotation.TextBox;
        if (box.IsEmpty)
            return;

        DefaultAppearance appearance = DefaultAppearance.Read(annotation.Dictionary);
        double size = appearance.Size <= 0 ? 11 : appearance.Size;
        double border = annotation.BorderWidth;
        PdfColor? background = annotation.Color;
        IReadOnlyList<PdfPoint> callout = annotation.CalloutLine;

        Build(annotation, bounds, canvas =>
        {
            if (callout.Count >= 2)
            {
                canvas.StrokeColor(appearance.Color).LineWidth(Math.Max(0.5, border));
                canvas.MoveTo(callout[^1].X, callout[^1].Y);
                for (int i = callout.Count - 2; i >= 0; i--)
                    canvas.LineTo(callout[i].X, callout[i].Y);

                canvas.Stroke();
                Ending(canvas, annotation.CalloutEnding, callout[1], callout[0], Math.Max(0.5, border), appearance.Color);
            }

            if (background is { } fill)
                canvas.FillColor(fill).Rectangle(box).Fill();

            if (border > 0)
            {
                canvas.StrokeColor(appearance.Color)
                      .LineWidth(border)
                      .Rectangle(box.Deflate(border / 2))
                      .Stroke();
            }

            PdfFont font = annotation.Document.Fonts.Helvetica;
            double padding = border + 2;
            double width = box.Width - (padding * 2);
            if (width <= 0)
                return;

            double y = box.Top - padding - size;
            foreach (string line in Wrap(annotation.Contents ?? string.Empty, font, size, width))
            {
                if (y < box.Bottom)
                    break;

                double x = box.Left + padding;
                double free = width - font.Measure(line, size);
                x += annotation.Alignment switch { 1 => Math.Max(0, free / 2), 2 => Math.Max(0, free), _ => 0 };

                canvas.DrawText(line, x, y, font, size, appearance.Color);
                y -= size * 1.2;
            }
        });
    }

    internal static void DrawSquareOrCircle(PdfShapeAnnotation annotation, bool ellipse)
    {
        PdfRectangle box = annotation.Rectangle;
        if (box.IsEmpty)
            return;

        double width = annotation.BorderWidth;
        PdfColor stroke = annotation.Color ?? PdfColor.Red;
        PdfColor? fill = annotation.InteriorColor;
        PdfRectangle shape = box.Deflate(width / 2);

        Build(annotation, box, canvas =>
        {
            canvas.StrokeColor(stroke).LineWidth(width);
            if (fill is { } interior)
                canvas.FillColor(interior);

            if (ellipse)
                canvas.Ellipse(shape);
            else
                canvas.Rectangle(shape);

            Paint(canvas, fill is not null, width > 0);
        });
    }

    internal static void DrawLine(PdfLineAnnotation annotation)
    {
        (PdfPoint start, PdfPoint end) = annotation.Endpoints;
        if (start == end)
            return;

        double width = Math.Max(0.4, annotation.BorderWidth);
        PdfColor stroke = annotation.Color ?? PdfColor.Red;

        Build(annotation, annotation.Rectangle, canvas =>
        {
            canvas.StrokeColor(stroke)
                  .LineWidth(width)
                  .MoveTo(start.X, start.Y)
                  .LineTo(end.X, end.Y)
                  .Stroke();

            Ending(canvas, annotation.StartEnding, end, start, width, stroke);
            Ending(canvas, annotation.EndEnding, start, end, width, stroke);
        });
    }

    internal static void DrawPolygon(PdfPolygonAnnotation annotation)
    {
        IReadOnlyList<PdfPoint> vertices = annotation.Vertices;
        if (vertices.Count < 2)
            return;

        double width = Math.Max(0.4, annotation.BorderWidth);
        PdfColor stroke = annotation.Color ?? PdfColor.Red;
        PdfColor? fill = annotation.IsClosed ? annotation.InteriorColor : null;

        Build(annotation, annotation.Rectangle, canvas =>
        {
            canvas.StrokeColor(stroke).LineWidth(width).LineJoin(PdfLineJoin.Round);
            if (fill is { } interior)
                canvas.FillColor(interior);

            canvas.MoveTo(vertices[0].X, vertices[0].Y);
            for (int i = 1; i < vertices.Count; i++)
                canvas.LineTo(vertices[i].X, vertices[i].Y);

            if (annotation.IsClosed)
                canvas.ClosePath();

            Paint(canvas, fill is not null, stroke: true);
        });
    }

    internal static void DrawCaret(PdfCaretAnnotation annotation)
    {
        PdfRectangle box = annotation.Rectangle;
        if (box.IsEmpty)
            return;

        PdfColor color = annotation.Color ?? PdfColor.Parse("#1565C0");

        Build(annotation, box, canvas =>
        {
            // A caret is drawn as a wedge standing on the baseline, the way a proof-reader marks an
            // insertion point.
            double half = box.Width / 2;
            canvas.FillColor(color)
                  .MoveTo(box.Left, box.Bottom)
                  .CurveTo(
                      box.Left + (half * 0.5), box.Bottom, box.Left + (half * 0.7), box.Top - (box.Height * 0.2),
                      box.Left + half, box.Top)
                  .CurveTo(
                      box.Right - (half * 0.7), box.Top - (box.Height * 0.2), box.Right - (half * 0.5), box.Bottom,
                      box.Right, box.Bottom)
                  .ClosePath()
                  .Fill();
        });
    }

    /// <summary>Draws a line decoration at <paramref name="tip"/>, pointing away from <paramref name="from"/>.</summary>
    private static void Ending(
        ContentCanvas canvas, PdfLineEnding ending, PdfPoint from, PdfPoint tip, double width, PdfColor color)
    {
        if (ending == PdfLineEnding.None)
            return;

        double dx = tip.X - from.X;
        double dy = tip.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 0.001)
            return;

        double ux = dx / length;
        double uy = dy / length;
        double size = Math.Max(4, width * 4);

        canvas.FillColor(color).StrokeColor(color).LineWidth(width);

        switch (ending)
        {
            case PdfLineEnding.Circle:
                canvas.Ellipse(new PdfRectangle(tip.X - size / 2, tip.Y - size / 2, tip.X + size / 2, tip.Y + size / 2))
                      .Fill();
                break;

            case PdfLineEnding.Square:
                canvas.Rectangle(tip.X - size / 2, tip.Y - size / 2, size, size).Fill();
                break;

            case PdfLineEnding.Diamond:
                canvas.MoveTo(tip.X, tip.Y + (size / 2))
                      .LineTo(tip.X + (size / 2), tip.Y)
                      .LineTo(tip.X, tip.Y - (size / 2))
                      .LineTo(tip.X - (size / 2), tip.Y)
                      .ClosePath()
                      .Fill();
                break;

            case PdfLineEnding.Butt:
                canvas.MoveTo(tip.X - (uy * size / 2), tip.Y + (ux * size / 2))
                      .LineTo(tip.X + (uy * size / 2), tip.Y - (ux * size / 2))
                      .Stroke();
                break;

            case PdfLineEnding.Slash:
                Slash(canvas, tip, ux, uy, size);
                break;

            default:
                Arrow(canvas, ending, tip, ux, uy, size);
                break;
        }
    }

    private static void Slash(ContentCanvas canvas, PdfPoint tip, double ux, double uy, double size)
    {
        // A slash sits at sixty degrees to the line, per the shape the specification names.
        const double Cos = 0.5;
        const double Sin = 0.866;
        double sx = (ux * Cos) - (uy * Sin);
        double sy = (ux * Sin) + (uy * Cos);

        canvas.MoveTo(tip.X - (sx * size / 2), tip.Y - (sy * size / 2))
              .LineTo(tip.X + (sx * size / 2), tip.Y + (sy * size / 2))
              .Stroke();
    }

    private static void Arrow(ContentCanvas canvas, PdfLineEnding ending, PdfPoint tip, double ux, double uy, double size)
    {
        bool reversed = ending is PdfLineEnding.ReverseClosedArrow or PdfLineEnding.ReverseOpenArrow;
        if (reversed)
        {
            ux = -ux;
            uy = -uy;
        }

        double spread = size * 0.42;
        double baseX = tip.X - (ux * size);
        double baseY = tip.Y - (uy * size);

        canvas.MoveTo(baseX - (uy * spread), baseY + (ux * spread))
              .LineTo(tip.X, tip.Y)
              .LineTo(baseX + (uy * spread), baseY - (ux * spread));

        if (ending is PdfLineEnding.ClosedArrow or PdfLineEnding.ReverseClosedArrow)
            canvas.ClosePath().Fill();
        else
            canvas.Stroke();
    }

    internal static void DrawInk(PdfInkAnnotation annotation)
    {
        IReadOnlyList<IReadOnlyList<PdfPoint>> strokes = annotation.Strokes;
        if (strokes.Count == 0)
            return;

        double width = Math.Max(0.4, annotation.BorderWidth);
        PdfColor color = annotation.Color ?? PdfColor.Red;

        Build(annotation, annotation.Rectangle, canvas =>
        {
            canvas.StrokeColor(color).LineWidth(width).LineCap(PdfLineCap.Round).LineJoin(PdfLineJoin.Round);

            foreach (IReadOnlyList<PdfPoint> stroke in strokes)
            {
                if (stroke.Count == 0)
                    continue;

                canvas.MoveTo(stroke[0].X, stroke[0].Y);
                for (int i = 1; i < stroke.Count; i++)
                    canvas.LineTo(stroke[i].X, stroke[i].Y);

                // A single tap has no length to stroke, so it is drawn as a dot.
                if (stroke.Count == 1)
                    canvas.LineTo(stroke[0].X + 0.01, stroke[0].Y);

                canvas.Stroke();
            }
        });
    }

    internal static void DrawStamp(PdfStampAnnotation annotation)
    {
        PdfRectangle box = annotation.Rectangle;
        if (box.IsEmpty)
            return;

        string text = Spaced(annotation.StampName);
        PdfColor color = annotation.Color ?? PdfColor.Parse("#B3261E");
        PdfFont font = annotation.Document.Fonts.HelveticaBold;

        double border = Math.Max(1, box.Height / 18);
        double padding = border + (box.Width * 0.06);

        // Stamp names differ in length by a factor of four, so the size has to come from measuring
        // the text rather than from the box alone: "Sold" and "NotForPublicRelease" both have to fit.
        double unit = Math.Max(0.001, font.Measure(text, 1));
        double size = Math.Clamp(Math.Min(box.Height * 0.5, (box.Width - (padding * 2)) / unit), 4, 48);

        Build(annotation, box, canvas =>
        {
            canvas.StrokeColor(color)
                  .LineWidth(border)
                  .RoundedRectangle(box.Deflate(border), box.Height / 6)
                  .Stroke();

            double width = font.Measure(text, size);
            canvas.DrawText(
                text,
                box.Left + ((box.Width - width) / 2),
                box.Bottom + ((box.Height - size) / 2) + (size * 0.24),
                font,
                size,
                color);
        });
    }

    internal static void DrawAttachment(PdfFileAttachmentAnnotation annotation)
    {
        PdfRectangle box = annotation.Rectangle;
        if (box.IsEmpty)
            return;

        PdfColor color = annotation.Color ?? PdfColor.Parse("#4A5568");

        Build(annotation, box, canvas =>
        {
            double size = Math.Min(box.Width, box.Height);
            double x = box.Left + (size * 0.3);

            // A paperclip: two nested rounded loops.
            canvas.StrokeColor(color).LineWidth(Math.Max(0.8, size / 12)).LineCap(PdfLineCap.Round);
            canvas.MoveTo(x, box.Bottom + (size * 0.25))
                  .LineTo(x, box.Bottom + (size * 0.78))
                  .CurveTo(x, box.Bottom + size, x + (size * 0.42), box.Bottom + size, x + (size * 0.42), box.Bottom + (size * 0.78))
                  .LineTo(x + (size * 0.42), box.Bottom + (size * 0.16))
                  .CurveTo(x + (size * 0.42), box.Bottom, x - (size * 0.2), box.Bottom, x - (size * 0.2), box.Bottom + (size * 0.16))
                  .LineTo(x - (size * 0.2), box.Bottom + (size * 0.7))
                  .Stroke();
        });
    }

    private static void Build(
        PdfAnnotation annotation,
        PdfRectangle box,
        Action<ContentCanvas> draw,
        bool blendMultiply = false)
    {
        if (box.IsEmpty)
            return;

        PdfDocument document = annotation.Document;
        var resources = new PdfDictionary(2);
        document.Attach(PdfValue.Dictionary(resources));

        byte[] content;
        using (ContentCanvas canvas = ContentCanvas.ForStream(document, resources))
        {
            canvas.Save();

            double opacity = annotation is PdfMarkupAnnotation markup ? markup.Opacity : 1;
            if (opacity < 1 || blendMultiply)
                canvas.ExtGState(BuildState(opacity, blendMultiply));

            draw(canvas);
            canvas.Restore();
            content = canvas.ToArray();
        }

        var stream = PdfStream.FromData(content);
        PdfDictionary dictionary = stream.Dictionary;
        dictionary.Set(AnnotationNames.Type, PdfValue.Name(AnnotationNames.XObject));
        dictionary.Set(AnnotationNames.Subtype, PdfValue.Name(AnnotationNames.Form));
        dictionary.Set(AnnotationNames.FormType, PdfValue.Integer(1));

        // The bounding box is given in page space and the matrix left at identity, so the mapping of
        // §12.5.5 comes out as a no-op and the drawing lands exactly where the geometry says.
        dictionary.Set(AnnotationNames.BBox, PdfValue.Array(box.ToArray()));
        dictionary.Set(AnnotationNames.Resources, PdfValue.Dictionary(resources));

        // Editing a comment several times should not leave a trail of abandoned streams behind it,
        // so a drawing of ours is overwritten in place.
        PdfObjectId id = annotation.AppearanceId;
        if (id.IsValid)
            document.SetObject(id, PdfValue.Stream(stream));
        else
            id = document.AddObject(PdfValue.Stream(stream));

        annotation.AppearanceId = id;

        PdfDictionary appearance = annotation.Dictionary.GetDictionary(AnnotationNames.AP) ?? new PdfDictionary(1);
        appearance.Set(AnnotationNames.N, PdfValue.Reference(id));
        annotation.Dictionary.Set(AnnotationNames.AP, PdfValue.Dictionary(appearance));

        if (annotation.Id.IsValid)
            document.MarkChanged(annotation.Id);
        else
            annotation.Page.MarkChanged();
    }

    private static PdfDictionary BuildState(double opacity, bool multiply)
    {
        var state = new PdfDictionary(4);
        state.Set(AnnotationNames.Type, PdfValue.Name(AnnotationNames.ExtGState));
        state.Set(PdfName.Get("ca"), PdfValue.Number(opacity));
        state.Set(AnnotationNames.CA, PdfValue.Number(opacity));

        if (multiply)
            state.Set(AnnotationNames.BM, PdfValue.Name(AnnotationNames.Multiply));

        return state;
    }

    private static void Paint(ContentCanvas canvas, bool fill, bool stroke)
    {
        if (fill && stroke)
            canvas.FillStroke();
        else if (fill)
            canvas.Fill();
        else if (stroke)
            canvas.Stroke();
        else
            canvas.EndPath();
    }

    private static void Bubble(ContentCanvas canvas, PdfRectangle box, double size)
    {
        double x = box.Left;
        double y = box.Bottom;
        double bodyBottom = y + (size * 0.28);
        double radius = size * 0.14;

        canvas.RoundedRectangle(new PdfRectangle(x, bodyBottom, x + size, y + size), radius);
        canvas.MoveTo(x + (size * 0.24), bodyBottom + 0.4)
              .LineTo(x + (size * 0.2), y)
              .LineTo(x + (size * 0.52), bodyBottom + 0.4)
              .ClosePath();
    }

    private static void Lines(ContentCanvas canvas, PdfRectangle box, double size)
    {
        canvas.StrokeColor(PdfColor.Gray(0.25f)).LineWidth(Math.Max(0.5, size / 22));

        for (int i = 0; i < 3; i++)
        {
            double y = box.Bottom + (size * (0.72 - (i * 0.16)));
            canvas.MoveTo(box.Left + (size * 0.18), y).LineTo(box.Left + (size * 0.82), y).Stroke();
        }
    }

    private static void Glyph(ContentCanvas canvas, PdfAnnotation annotation, string text, PdfRectangle box, double size)
    {
        PdfFont font = annotation.Document.Fonts.HelveticaBold;
        double width = font.Measure(text, size);
        canvas.DrawText(
            text,
            box.Left + ((box.Width - width) / 2),
            box.Bottom + ((box.Height - size) / 2) + (size * 0.18),
            font,
            size,
            PdfColor.Gray(0.15f));
    }

    private static void Rule(ContentCanvas canvas, PdfColor color, double left, double right, double y, double thickness) =>
        canvas.StrokeColor(color).LineWidth(thickness).MoveTo(left, y).LineTo(right, y).Stroke();

    private static void Squiggle(ContentCanvas canvas, PdfColor color, PdfRectangle area, double thickness)
    {
        double amplitude = Math.Max(1, area.Height * 0.07);
        double step = amplitude * 2;
        double y = area.Bottom + amplitude;

        canvas.StrokeColor(color).LineWidth(thickness).MoveTo(area.Left, y);

        bool up = true;
        for (double x = area.Left + step; x < area.Right; x += step, up = !up)
            canvas.LineTo(x, up ? y + amplitude : y);

        canvas.Stroke();
    }

    private static PdfColor DefaultMarkupColor(PdfAnnotationType type) => type switch
    {
        PdfAnnotationType.Highlight => PdfColor.Parse("#FFE44D"),
        PdfAnnotationType.Underline => PdfColor.Parse("#1B7F3B"),
        PdfAnnotationType.StrikeOut => PdfColor.Parse("#C62828"),
        _ => PdfColor.Parse("#1565C0"),
    };

    private static PdfColor Darken(PdfColor color)
    {
        (float red, float green, float blue) = color.ToRgb();
        return PdfColor.Rgb(red * 0.55f, green * 0.55f, blue * 0.55f);
    }

    private static string Spaced(string name)
    {
        // Stamp names run words together; a stamp reads better with them apart.
        var text = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                text.Append(' ');

            text.Append(char.ToUpperInvariant(name[i]));
        }

        return text.ToString();
    }

    private static IEnumerable<string> Wrap(string text, PdfFont font, double size, double width)
    {
        foreach (string paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var line = new System.Text.StringBuilder();
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && font.Measure(candidate, size) > width)
                {
                    yield return line.ToString();
                    line.Clear().Append(word);
                    continue;
                }

                line.Clear().Append(candidate);
            }

            if (line.Length > 0)
                yield return line.ToString();
        }
    }
}
