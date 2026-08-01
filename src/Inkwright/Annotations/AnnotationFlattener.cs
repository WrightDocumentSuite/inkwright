using Inkwright.Content;
using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>
/// Burns annotations into the page they sit on. What was a comment a reader could open, move or
/// delete becomes ordinary page content: the mark still shows, the conversation behind it is gone.
/// </summary>
internal static class AnnotationFlattener
{
    internal static List<PdfAnnotation> Flatten(PdfPage page, IReadOnlyList<PdfAnnotation> candidates)
    {
        List<(PdfAnnotation Annotation, PdfValue Form, PdfMatrix Placement)> drawable = [];

        foreach (PdfAnnotation annotation in candidates)
        {
            // A pop-up window is a piece of reader furniture, not something the page ever shows.
            if (annotation.Type == PdfAnnotationType.Popup)
                continue;

            PdfAnnotationFlags flags = annotation.Flags;
            if (flags.HasFlag(PdfAnnotationFlags.Hidden) || flags.HasFlag(PdfAnnotationFlags.NoView))
                continue;

            if (Appearance(annotation) is not { } appearance)
                continue;

            drawable.Add((annotation, appearance.Reference, Placement(appearance.Stream, annotation.Rectangle)));
        }

        if (drawable.Count == 0)
            return [];

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            foreach ((PdfAnnotation _, PdfValue form, PdfMatrix placement) in drawable)
            {
                PdfName name = canvas.Resources.AddXObject(form);
                canvas.Save().Transform(placement).DrawXObject(name).Restore();
            }

            canvas.Commit();
        }

        return [.. drawable.Select(entry => entry.Annotation)];
    }

    /// <summary>
    /// The drawing a reader would show, and a reference to it. An appearance may be a single stream
    /// or a set of states, in which case <c>AS</c> says which one is current.
    /// </summary>
    private static (PdfValue Reference, PdfStream Stream)? Appearance(PdfAnnotation annotation)
    {
        if (annotation.Dictionary.GetDictionary(AnnotationNames.AP) is not { } appearance)
            return null;

        if (appearance.GetStream(AnnotationNames.N) is { } single)
            return (Reference(annotation, appearance, AnnotationNames.N, single), single);

        if (appearance.GetDictionary(AnnotationNames.N) is not { } states)
            return null;

        PdfName? state = annotation.Dictionary.GetName(PdfName.Get("AS"));
        if (state is null && states.Count == 1)
        {
            foreach (PdfName only in states.Keys)
                state = only;
        }

        if (state is null || states.GetStream(state) is not { } chosen)
            return null;

        return (Reference(annotation, states, state, chosen), chosen);
    }

    /// <summary>
    /// A resource entry has to be indirect, so a stream written straight into the appearance
    /// dictionary is promoted to an object of its own before it can be painted.
    /// </summary>
    private static PdfValue Reference(PdfAnnotation annotation, PdfDictionary parent, PdfName key, PdfStream stream)
    {
        if (parent.TryGetRaw(key, out PdfValue raw) && raw.IsReference)
            return raw;

        return PdfValue.Reference(annotation.Document.AddObject(PdfValue.Stream(stream)));
    }

    /// <summary>
    /// The transformation of ISO 32000-1 §12.5.5: the appearance's bounding box, once its own matrix
    /// has been applied, is scaled and shifted onto the annotation rectangle. Only that second step
    /// is emitted, because painting the form applies its matrix again.
    /// </summary>
    private static PdfMatrix Placement(PdfStream form, PdfRectangle rectangle)
    {
        PdfRectangle box = PdfRectangle.FromArray(form.Dictionary.GetArray(AnnotationNames.BBox));
        if (box.IsEmpty || rectangle.IsEmpty)
            return PdfMatrix.Identity;

        PdfMatrix matrix = Matrix(form);
        PdfRectangle transformed = box.Transform(matrix);
        if (transformed.Width <= 0 || transformed.Height <= 0)
            return PdfMatrix.Identity;

        double scaleX = rectangle.Width / transformed.Width;
        double scaleY = rectangle.Height / transformed.Height;

        return new PdfMatrix(
            scaleX,
            0,
            0,
            scaleY,
            rectangle.Left - (transformed.Left * scaleX),
            rectangle.Bottom - (transformed.Bottom * scaleY));
    }

    private static PdfMatrix Matrix(PdfStream form)
    {
        if (form.Dictionary.GetArray(AnnotationNames.Matrix) is not { Count: 6 } values)
            return PdfMatrix.Identity;

        return new PdfMatrix(
            values.GetNumber(0), values.GetNumber(1), values.GetNumber(2),
            values.GetNumber(3), values.GetNumber(4), values.GetNumber(5));
    }
}
