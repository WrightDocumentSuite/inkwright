using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>
/// A sticky note: an icon anchored to a point on the page with a comment behind it. Notes never
/// scale or rotate with the page, so the icon stays the same size however far the reader zooms in.
/// </summary>
public sealed class PdfTextAnnotation : PdfMarkupAnnotation
{
    internal PdfTextAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>Which icon the reader draws for the closed note.</summary>
    public PdfNoteIcon Icon
    {
        get => Dictionary.GetName(AnnotationNames.Name)?.Value switch
        {
            "Comment" => PdfNoteIcon.Comment,
            "Key" => PdfNoteIcon.Key,
            "Help" => PdfNoteIcon.Help,
            "NewParagraph" => PdfNoteIcon.NewParagraph,
            "Paragraph" => PdfNoteIcon.Paragraph,
            "Insert" => PdfNoteIcon.Insert,
            _ => PdfNoteIcon.Note,
        };

        set
        {
            Dictionary.Set(AnnotationNames.Name, PdfValue.Name(PdfName.Get(value.ToString())));
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>Whether this note carries a review decision rather than a comment of its own.</summary>
    public bool IsStateReply => Dictionary.ContainsKey(AnnotationNames.State);

    private protected override void RebuildAppearance()
    {
        // A reply belongs in its thread, not on the page. Leaving it without an appearance is not
        // enough: a reader that draws its own icon for a bare text annotation — Chrome's does —
        // would stack one marker per reply on top of the parent. An empty appearance says
        // "draw nothing" in a way every reader honours.
        if (IsStateReply || IsReply)
            AnnotationAppearance.DrawNothing(this);
        else
            AnnotationAppearance.DrawNote(this);
    }
}

/// <summary>
/// Text marked with a wash, an underline, a wavy underline or a strikethrough. The marked words are
/// given as quadrilaterals, so a mark can follow text that wraps across several lines.
/// </summary>
public sealed class PdfTextMarkupAnnotation : PdfMarkupAnnotation
{
    internal PdfTextMarkupAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>
    /// The areas the mark covers, one per run of words. Reading tolerates either vertex order a
    /// producer might have used; writing follows the order Acrobat writes, which is what readers
    /// actually expect however the specification words it.
    /// </summary>
    public IReadOnlyList<PdfRectangle> Areas
    {
        get
        {
            List<PdfRectangle> areas = [];
            if (Dictionary.GetArray(AnnotationNames.QuadPoints) is not { } quads)
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

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            var quads = new PdfArray(value.Count * 8);
            PdfRectangle bounds = default;

            foreach (PdfRectangle area in value)
            {
                quads.Add(PdfValue.Number(area.Left));
                quads.Add(PdfValue.Number(area.Top));
                quads.Add(PdfValue.Number(area.Right));
                quads.Add(PdfValue.Number(area.Top));
                quads.Add(PdfValue.Number(area.Left));
                quads.Add(PdfValue.Number(area.Bottom));
                quads.Add(PdfValue.Number(area.Right));
                quads.Add(PdfValue.Number(area.Bottom));

                bounds = bounds.IsEmpty ? area : bounds.Union(area);
            }

            Dictionary.Set(AnnotationNames.QuadPoints, PdfValue.Array(quads));

            if (!bounds.IsEmpty)
                Dictionary.Set(AnnotationNames.Rect, PdfValue.Array(bounds.Inflate(2).ToArray()));

            Touch();
            RefreshAppearance();
        }
    }

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawTextMarkup(this);
}

/// <summary>Text drawn straight onto the page, in a box the reader can move and resize.</summary>
public sealed class PdfFreeTextAnnotation : PdfMarkupAnnotation
{
    internal PdfFreeTextAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>
    /// The size the text is drawn at. Like the colour, it is kept in the default appearance string
    /// rather than beside it, so a reader that rebuilds the appearance itself agrees with us.
    /// </summary>
    public double FontSize
    {
        get => DefaultAppearance.Read(Dictionary).Size;
        set
        {
            DefaultAppearance.Write(Dictionary, DefaultAppearance.Read(Dictionary) with { Size = Math.Max(1, value) });
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>The colour the text is drawn in.</summary>
    public PdfColor TextColor
    {
        get => DefaultAppearance.Read(Dictionary).Color;
        set
        {
            DefaultAppearance.Write(Dictionary, DefaultAppearance.Read(Dictionary) with { Color = value });
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>The width of the box's border; zero for no border.</summary>
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

    /// <summary>How the text is aligned inside the box: 0 left, 1 centred, 2 right.</summary>
    public int Alignment
    {
        get => (int)Dictionary.GetInteger(AnnotationNames.Q);
        set
        {
            Dictionary.Set(AnnotationNames.Q, PdfValue.Integer(Math.Clamp(value, 0, 2)));
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>
    /// The leader line from the text box to whatever it comments on: the tip first, then an optional
    /// knee, then the point where it meets the box. Empty when the note has no callout.
    /// </summary>
    public IReadOnlyList<PdfPoint> CalloutLine
    {
        get
        {
            List<PdfPoint> points = [];
            if (Dictionary.GetArray(PdfName.Get("CL")) is not { } line)
                return points;

            for (int i = 0; i + 1 < line.Count; i += 2)
                points.Add(new PdfPoint(line.GetNumber(i), line.GetNumber(i + 1)));

            return points;
        }
    }

    /// <summary>What the callout wears where it touches what it points at.</summary>
    public PdfLineEnding CalloutEnding
    {
        get => LineEndings.Parse(Dictionary.GetName(AnnotationNames.LE)?.Value);
        set
        {
            Dictionary.Set(AnnotationNames.LE, PdfValue.Name(PdfName.Get(LineEndings.Name(value))));
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>
    /// Where the text actually sits. With a callout the annotation rectangle has to cover the leader
    /// line as well, and <c>RD</c> says how far inside it the writing starts.
    /// </summary>
    public PdfRectangle TextBox
    {
        get
        {
            PdfRectangle box = Rectangle;
            if (Dictionary.GetArray(PdfName.Get("RD")) is not { Count: 4 } inset)
                return box;

            return new PdfRectangle(
                box.Left + inset.GetNumber(0),
                box.Bottom + inset.GetNumber(3),
                box.Right - inset.GetNumber(2),
                box.Top - inset.GetNumber(1));
        }
    }

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawFreeText(this);
}

/// <summary>A rubber stamp: one of the standard stamp names drawn over the page.</summary>
public sealed class PdfStampAnnotation : PdfMarkupAnnotation
{
    internal PdfStampAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>The stamp name, such as <c>Approved</c> or <c>Draft</c>.</summary>
    public string StampName
    {
        get => Dictionary.GetName(AnnotationNames.Name)?.Value ?? "Draft";
        set
        {
            Dictionary.Set(AnnotationNames.Name, PdfValue.Name(PdfName.Get(value)));
            Touch();
            RefreshAppearance();
        }
    }

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawStamp(this);
}

/// <summary>A file carried on the page, shown as a pin the reader can open.</summary>
public sealed class PdfFileAttachmentAnnotation : PdfMarkupAnnotation
{
    internal PdfFileAttachmentAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>The name of the attached file.</summary>
    public string? FileName => Dictionary.GetDictionary(AnnotationNames.FS)?.GetTextString(PdfName.Get("UF"))
                               ?? Dictionary.GetDictionary(AnnotationNames.FS)?.GetTextString(PdfName.Get("F"));

    private protected override void RebuildAppearance() => AnnotationAppearance.DrawAttachment(this);
}

/// <summary>The window a reader opens to show a comment's text.</summary>
public sealed class PdfPopupAnnotation : PdfAnnotation
{
    internal PdfPopupAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>The comment this window belongs to.</summary>
    public PdfObjectId ParentId =>
        Dictionary.TryGetRaw(AnnotationNames.Parent, out PdfValue reference) && reference.IsReference
            ? reference.AsReference
            : PdfObjectId.None;

    /// <summary>Whether the window starts open.</summary>
    public bool IsOpen
    {
        get => Dictionary.GetBoolean(AnnotationNames.Open);
        set
        {
            Dictionary.Set(AnnotationNames.Open, PdfValue.Boolean(value));
            Touch();
        }
    }
}

/// <summary>An annotation of a kind the library does not model, kept whole so it survives a rewrite.</summary>
public sealed class PdfGenericAnnotation : PdfAnnotation
{
    internal PdfGenericAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }
}
