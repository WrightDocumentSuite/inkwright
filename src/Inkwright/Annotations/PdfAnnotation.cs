using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>
/// An annotation on a page. The class is a typed façade over the annotation dictionary: everything
/// it exposes lives in that dictionary, and anything it does not model stays reachable through
/// <see cref="Dictionary"/>.
/// </summary>
public class PdfAnnotation
{
    private protected PdfAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
    {
        Page = page;
        Id = id;
        Dictionary = dictionary;
    }

    /// <summary>The page the annotation sits on.</summary>
    public PdfPage Page { get; internal set; }

    /// <summary>The identity of the annotation object.</summary>
    public PdfObjectId Id { get; }

    /// <summary>The annotation dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The document the annotation belongs to.</summary>
    public PdfDocument Document => Page.Document;

    /// <summary>Which kind of annotation this is.</summary>
    public PdfAnnotationType Type => Classify(Dictionary.GetName(AnnotationNames.Subtype)?.Value);

    /// <summary>The subtype exactly as the file spells it, including ones the library does not model.</summary>
    public string Subtype => Dictionary.GetName(AnnotationNames.Subtype)?.Value ?? string.Empty;

    /// <summary>Whether the annotation has been removed from its page.</summary>
    public bool IsDeleted { get; internal set; }

    /// <summary>Where the annotation sits on the page, in default user space.</summary>
    public PdfRectangle Rectangle
    {
        get => PdfRectangle.FromArray(Dictionary.GetArray(AnnotationNames.Rect));
        set
        {
            Dictionary.Set(AnnotationNames.Rect, PdfValue.Array(value.ToArray()));
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>
    /// The text of the annotation. For a comment this is what the reader shows in the pop-up and in
    /// the comments pane; for types that draw no text it is the description an assistive tool reads.
    /// Paragraphs are separated by line feeds here and by carriage returns in the file, which is
    /// what the specification asks for and what readers actually honour.
    /// </summary>
    public string? Contents
    {
        get => Dictionary.GetTextString(AnnotationNames.Contents) is { } text ? AnnotationText.FromPdf(text) : null;
        set
        {
            SetText(AnnotationNames.Contents, value is null ? null : AnnotationText.ToPdf(value));

            // A rich-text copy that no longer matches the plain text would be shown instead of it.
            Dictionary.Remove(AnnotationNames.RC);
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>A name identifying the annotation among the others on its page.</summary>
    public string? Name
    {
        get => Dictionary.GetTextString(AnnotationNames.NM);
        set => SetText(AnnotationNames.NM, value);
    }

    /// <summary>When the annotation was last changed.</summary>
    public DateTimeOffset? ModifiedAt
    {
        get => PdfDate.TryParse(Dictionary.GetTextString(AnnotationNames.M), out DateTimeOffset value) ? value : null;
        set => SetDate(AnnotationNames.M, value);
    }

    /// <summary>What the reader may do with the annotation.</summary>
    public PdfAnnotationFlags Flags
    {
        get => (PdfAnnotationFlags)Dictionary.GetInteger(AnnotationNames.F);
        set
        {
            Dictionary.Set(AnnotationNames.F, PdfValue.Integer((long)value));
            Touch();
        }
    }

    /// <summary>
    /// The annotation's colour: the icon background of a note, the wash of a highlight, the border of
    /// a link. <see langword="null"/> means the file names no colour.
    /// </summary>
    public PdfColor? Color
    {
        get => ReadColor(AnnotationNames.C);
        set
        {
            WriteColor(AnnotationNames.C, value);
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>Whether the annotation is printed along with the page.</summary>
    public bool IsPrinted
    {
        get => Flags.HasFlag(PdfAnnotationFlags.Print);
        set => Flags = value ? Flags | PdfAnnotationFlags.Print : Flags & ~PdfAnnotationFlags.Print;
    }

    /// <summary>Whether the annotation is hidden from both screen and printer.</summary>
    public bool IsHidden
    {
        get => Flags.HasFlag(PdfAnnotationFlags.Hidden);
        set => Flags = value ? Flags | PdfAnnotationFlags.Hidden : Flags & ~PdfAnnotationFlags.Hidden;
    }

    /// <summary>
    /// Removes the annotation from its page, along with its pop-up window and everything written in
    /// reply to it. Deleting a comment and leaving its thread behind produces replies that hang off
    /// nothing, which readers show as orphaned notes.
    /// </summary>
    public void Delete()
    {
        if (!IsDeleted)
            Page.Annotations.Detach(this);
    }

    /// <summary>
    /// Rebuilds the drawing the reader shows for this annotation, replacing whatever is there. Use
    /// it to take over an appearance another producer wrote.
    /// </summary>
    public void RegenerateAppearance() => RebuildAppearance();

    /// <summary>Draws the annotation's appearance stream, if this type has one to draw.</summary>
    private protected virtual void RebuildAppearance()
    {
    }

    /// <summary>
    /// Redraws the appearance after a change, but only when doing so destroys nothing. An annotation
    /// that arrived with a drawing of its own — an image stamp, a signature, a shape some other tool
    /// rendered — keeps it, because a comment edit is no reason to throw away artwork we cannot
    /// reproduce.
    /// </summary>
    private protected void RefreshAppearance()
    {
        if (AppearanceId.IsValid || !Dictionary.ContainsKey(AnnotationNames.AP))
            RebuildAppearance();
    }

    /// <summary>The appearance stream this library generated, when it generated one.</summary>
    internal PdfObjectId AppearanceId { get; set; } = PdfObjectId.None;

    /// <summary>Records that the annotation changed and stamps it with the current time.</summary>
    private protected void Touch()
    {
        Dictionary.Set(AnnotationNames.M, PdfValue.TextString(PdfDate.Format(DateTimeOffset.Now)));
        MarkDirty();
    }

    /// <summary>
    /// Notes that the annotation has to be written out again. A few files hold the dictionary inline
    /// in the page rather than as an object of its own, and then it is the page that changed.
    /// </summary>
    private protected void MarkDirty()
    {
        if (Id.IsValid)
            Document.MarkChanged(Id);
        else
            Page.MarkChanged();
    }

    private protected void SetText(PdfName key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            Dictionary.Remove(key);
        else
            Dictionary.Set(key, PdfValue.TextString(value));
    }

    private protected void SetDate(PdfName key, DateTimeOffset? value)
    {
        if (value is null)
            Dictionary.Remove(key);
        else
            Dictionary.Set(key, PdfValue.TextString(PdfDate.Format(value.Value)));

        MarkDirty();
    }

    private protected PdfColor? ReadColor(PdfName key)
    {
        if (Dictionary.GetArray(key) is not { } components)
            return null;

        // The component count names the colour space: nothing, grey, RGB or CMYK (table 164).
        return components.Count switch
        {
            1 => PdfColor.Gray((float)components.GetNumber(0)),
            3 => PdfColor.Rgb((float)components.GetNumber(0), (float)components.GetNumber(1), (float)components.GetNumber(2)),
            4 => PdfColor.Cmyk(
                (float)components.GetNumber(0), (float)components.GetNumber(1),
                (float)components.GetNumber(2), (float)components.GetNumber(3)),
            _ => null,
        };
    }

    private protected void WriteColor(PdfName key, PdfColor? color)
    {
        if (color is not { } value)
        {
            Dictionary.Set(key, PdfValue.Array(new PdfArray()));
            return;
        }

        PdfArray components = value.Kind switch
        {
            PdfColorKind.Gray => PdfArray.OfNumbers(value.Component0),
            PdfColorKind.Rgb => PdfArray.OfNumbers(value.Component0, value.Component1, value.Component2),
            _ => PdfArray.OfNumbers(value.Component0, value.Component1, value.Component2, value.Component3),
        };

        Dictionary.Set(key, PdfValue.Array(components));
    }

    internal static PdfAnnotationType Classify(string? subtype) => subtype switch
    {
        "Text" => PdfAnnotationType.Text,
        "Link" => PdfAnnotationType.Link,
        "FreeText" => PdfAnnotationType.FreeText,
        "Line" => PdfAnnotationType.Line,
        "Square" => PdfAnnotationType.Square,
        "Circle" => PdfAnnotationType.Circle,
        "Polygon" => PdfAnnotationType.Polygon,
        "PolyLine" => PdfAnnotationType.PolyLine,
        "Highlight" => PdfAnnotationType.Highlight,
        "Underline" => PdfAnnotationType.Underline,
        "Squiggly" => PdfAnnotationType.Squiggly,
        "StrikeOut" => PdfAnnotationType.StrikeOut,
        "Stamp" => PdfAnnotationType.Stamp,
        "Caret" => PdfAnnotationType.Caret,
        "Ink" => PdfAnnotationType.Ink,
        "Popup" => PdfAnnotationType.Popup,
        "FileAttachment" => PdfAnnotationType.FileAttachment,
        "Widget" => PdfAnnotationType.Widget,
        "Redact" => PdfAnnotationType.Redact,
        _ => PdfAnnotationType.Unknown,
    };

    /// <inheritdoc />
    public override string ToString() =>
        Contents is { Length: > 0 } text ? $"{Subtype}: {text}" : $"{Subtype} at {Rectangle}";
}
