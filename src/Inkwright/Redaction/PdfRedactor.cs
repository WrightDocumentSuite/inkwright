using Inkwright.Annotations;
using Inkwright.Content;
using Inkwright.Cos;

namespace Inkwright.Redaction;

/// <summary>
/// Physically removes text glyphs, inline images, and image XObjects whose painted bounds intersect
/// redaction regions, then paints opaque redaction overlays in their place.
/// </summary>
internal static partial class PdfRedactor
{
    internal static PdfRedactionResult Redact(
        PdfPage page,
        IEnumerable<PdfRectangle> regions,
        PdfRedactionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(regions);

        options ??= PdfRedactionOptions.Default;
        PdfRectangle[] targets = [.. regions.Where(r => !r.IsEmpty)];
        if (targets.Length == 0)
            return default;

        OverlaySpec[] specs = [.. targets.Select(r => new OverlaySpec(r, options.FillColor, options.OverlayText))];
        return Execute(page, targets, specs, options, extraAnnotationsRemoved: 0);
    }

    internal static PdfRedactionResult ApplyAnnotationRedactions(PdfPage page, PdfRedactionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(page);
        options ??= PdfRedactionOptions.Default;

        List<PdfAnnotation> redactions = [.. page.Annotations.Where(a => a.Type == PdfAnnotationType.Redact)];
        if (redactions.Count == 0)
            return default;

        List<PdfRectangle> targets = [];
        List<OverlaySpec> specs = [];
        int removedAnnotations = 0;

        foreach (PdfAnnotation annotation in redactions)
        {
            CollectAnnotationSpecs(annotation, options, targets, specs);
            if (page.Annotations.Remove(annotation))
                removedAnnotations++;
        }

        if (targets.Count == 0)
            return new PdfRedactionResult(0, 0, 0, removedAnnotations);

        return Execute(page, [.. targets], [.. specs], options, removedAnnotations);
    }

    private static void CollectAnnotationSpecs(
        PdfAnnotation annotation,
        PdfRedactionOptions options,
        List<PdfRectangle> targets,
        List<OverlaySpec> specs)
    {
        PdfColor? fill = ReadInteriorColor(annotation.Dictionary) ?? options.FillColor;
        string? overlay = annotation.Dictionary.GetTextString(PdfName.Get("OverlayText")) ?? options.OverlayText;
        IReadOnlyList<PdfRectangle> quads = ReadQuadRectangles(annotation.Dictionary);

        if (quads.Count > 0)
        {
            foreach (PdfRectangle quad in quads)
            {
                if (!quad.IsEmpty)
                {
                    targets.Add(quad);
                    specs.Add(new OverlaySpec(quad, fill, overlay));
                }
            }
            return;
        }

        if (!annotation.Rectangle.IsEmpty)
        {
            targets.Add(annotation.Rectangle);
            specs.Add(new OverlaySpec(annotation.Rectangle, fill, overlay));
        }
    }

    private static PdfRedactionResult Execute(
        PdfPage page,
        PdfRectangle[] targets,
        OverlaySpec[] specs,
        PdfRedactionOptions options,
        int extraAnnotationsRemoved)
    {
        var rewriter = new StreamRewriter(page.Document, targets, options.RemoveImages);
        byte[] rewritten = rewriter.Rewrite(page.GetContent(), page.Resources, PdfMatrix.Identity, depth: 0);
        page.SetContent(rewritten);

        int removedAnnotations = extraAnnotationsRemoved;
        if (options.RemoveAnnotations)
        {
            removedAnnotations += page.Annotations.RemoveAll(
                annotation => IntersectsAny(annotation.Rectangle, targets));
        }

        PaintOverlays(page, specs, options);
        return new PdfRedactionResult(
            targets.Length,
            rewriter.GlyphsRemoved,
            rewriter.ImagesRemoved,
            removedAnnotations);
    }

    private static void PaintOverlays(PdfPage page, OverlaySpec[] specs, PdfRedactionOptions options)
    {
        bool anyVisual = specs.Any(s => s.FillColor is not null || !string.IsNullOrEmpty(s.OverlayText));
        if (!anyVisual)
            return;

        using ContentCanvas canvas = ContentCanvas.ForPage(page);
        canvas.BeginMarkedContent(PdfNames.Artifact);

        foreach (OverlaySpec spec in specs)
            PaintSingleOverlay(canvas, page, spec, options);

        canvas.EndMarkedContent();
        canvas.Commit(append: true);
    }

    private static void PaintSingleOverlay(
        ContentCanvas canvas,
        PdfPage page,
        OverlaySpec spec,
        PdfRedactionOptions options)
    {
        if (spec.FillColor is null && string.IsNullOrEmpty(spec.OverlayText))
            return;

        canvas.Save().ClipTo(spec.Region);
        if (spec.FillColor is { } fill)
            canvas.FillColor(fill).Rectangle(spec.Region).Fill();

        if (!string.IsNullOrEmpty(spec.OverlayText))
        {
            Fonts.PdfFont font = page.Document.Fonts.Helvetica;
            double size = options.OverlayFontSize ?? FitFontSize(spec.Region, spec.OverlayText);
            double textWidth = font.Measure(spec.OverlayText, size);
            double x = spec.Region.Left + Math.Max(1, (spec.Region.Width - textWidth) / 2);
            double y = spec.Region.Bottom + Math.Max(1, (spec.Region.Height - (size * 0.72)) / 2);
            canvas.DrawText(spec.OverlayText, x, y, font, size, options.OverlayTextColor);
        }

        canvas.Restore();
    }

    private static double FitFontSize(PdfRectangle region, string text)
    {
        double byHeight = region.Height * 0.65;
        double byWidth = region.Width / Math.Max(1.0, text.Length * 0.55);
        return Math.Clamp(Math.Min(byHeight, byWidth), 4.0, 24.0);
    }

    private static PdfColor? ReadInteriorColor(PdfDictionary dictionary)
    {
        if (dictionary.GetArray(PdfName.Get("IC")) is not { } array)
            return null;

        return array.Count switch
        {
            1 => PdfColor.Gray((float)array.GetNumber(0)),
            3 => PdfColor.Rgb((float)array.GetNumber(0), (float)array.GetNumber(1), (float)array.GetNumber(2)),
            4 => PdfColor.Cmyk(
                (float)array.GetNumber(0),
                (float)array.GetNumber(1),
                (float)array.GetNumber(2),
                (float)array.GetNumber(3)),
            _ => null,
        };
    }

    private static List<PdfRectangle> ReadQuadRectangles(PdfDictionary dictionary)
    {
        List<PdfRectangle> areas = [];
        if (dictionary.GetArray(PdfName.Get("QuadPoints")) is not { } quads)
            return areas;

        for (int i = 0; i + 7 < quads.Count; i += 8)
        {
            double left = double.MaxValue, bottom = double.MaxValue;
            double right = double.MinValue, top = double.MinValue;
            for (int corner = 0; corner < 4; corner++)
            {
                double x = quads.GetNumber(i + (corner * 2));
                double y = quads.GetNumber(i + (corner * 2) + 1);
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                bottom = Math.Min(bottom, y);
                top = Math.Max(top, y);
            }

            areas.Add(new PdfRectangle(left, bottom, right, top));
        }

        return areas;
    }

    internal static bool IntersectsAny(PdfRectangle candidate, ReadOnlySpan<PdfRectangle> targets)
    {
        foreach (PdfRectangle target in targets)
        {
            if (OverlapsSubstantially(candidate, target))
                return true;
        }

        return false;
    }

    private static bool OverlapsSubstantially(PdfRectangle candidate, PdfRectangle target)
    {
        if (candidate.IsEmpty)
            return target.Contains(candidate.Center);

        PdfRectangle overlap = candidate.Intersect(target);
        if (overlap.IsEmpty)
            return false;

        double minWidth = Math.Min(0.5, candidate.Width * 0.15);
        double minHeight = Math.Min(0.5, candidate.Height * 0.15);
        return overlap.Width > minWidth && overlap.Height > minHeight;
    }

    private readonly record struct OverlaySpec(
        PdfRectangle Region,
        PdfColor? FillColor,
        string? OverlayText);
}
