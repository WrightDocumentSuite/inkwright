using System.Collections;
using Inkwright.Cos;
using Inkwright.Tagging;

namespace Inkwright.Annotations;

/// <summary>
/// The annotations on a page: the comments, marks and shapes a reader shows on top of the content.
/// Adding one writes its appearance straight away, so it looks the same in every reader rather than
/// only in the one that happens to draw comments itself.
/// </summary>
public sealed class PdfAnnotationCollection : IReadOnlyList<PdfAnnotation>
{
    private readonly PdfPage _page;
    private List<PdfAnnotation>? _annotations;
    private int _seenCount = -1;

    internal PdfAnnotationCollection(PdfPage page) => _page = page;

    /// <inheritdoc />
    public int Count => Items.Count;

    /// <inheritdoc />
    public PdfAnnotation this[int index] => Items[index];

    /// <summary>The raw <c>Annots</c> array, for anything the typed model does not cover.</summary>
    public PdfArray RawArray
    {
        get
        {
            if (_page.Dictionary.GetArray(AnnotationNames.Annots) is { } existing)
                return existing;

            var array = new PdfArray();
            _page.Document.Attach(PdfValue.Array(array));
            _page.Dictionary.Set(AnnotationNames.Annots, PdfValue.Array(array));
            _page.MarkChanged();
            return array;
        }
    }

    /// <summary>
    /// The comments on the page: markup annotations that start a thread. Replies and the pop-up
    /// windows that carry them are left out, because a reader lists them under their parent.
    /// </summary>
    public IEnumerable<PdfMarkupAnnotation> Comments
    {
        get
        {
            foreach (PdfAnnotation annotation in Items)
            {
                if (annotation is PdfMarkupAnnotation markup && !markup.IsReply)
                    yield return markup;
            }
        }
    }

    /// <summary>Adds a sticky note anchored at a point.</summary>
    /// <param name="at">Where the icon's lower-left corner sits.</param>
    /// <param name="text">What the comment says.</param>
    /// <param name="author">Who wrote it.</param>
    /// <param name="icon">Which icon the reader draws.</param>
    /// <param name="color">The icon colour.</param>
    /// <param name="withIcon">Whether to draw an icon at all; replies carry none.</param>
    public PdfTextAnnotation AddNote(
        PdfPoint at,
        string text,
        string? author = null,
        PdfNoteIcon icon = PdfNoteIcon.Note,
        PdfColor? color = null,
        bool withIcon = true)
    {
        ArgumentNullException.ThrowIfNull(text);

        const double Size = 22;
        var dictionary = NewMarkup("Text", PdfRectangle.FromSize(at.X, at.Y - Size, Size, Size), text, author, color);
        dictionary.Set(AnnotationNames.Name, PdfValue.Name(PdfName.Get(icon.ToString())));

        // Notes are specified to behave as if these flags were always set, and saying so keeps a
        // reader from scaling the icon with the page.
        dictionary.Set(AnnotationNames.F, PdfValue.Integer(
            (long)(PdfAnnotationFlags.Print | PdfAnnotationFlags.NoZoom | PdfAnnotationFlags.NoRotate)));

        // A note with no icon is a reply, which a reader shows inside its thread; drawing one would
        // put an orphan marker in the margin.
        return (PdfTextAnnotation)Attach(dictionary, withPopup: withIcon, drawNow: withIcon);
    }

    /// <summary>Marks areas of the page with a coloured wash.</summary>
    /// <param name="areas">The areas to mark, usually one per line of text.</param>
    /// <param name="text">An optional comment attached to the mark.</param>
    /// <param name="author">Who made the mark.</param>
    /// <param name="color">The wash colour.</param>
    public PdfTextMarkupAnnotation AddHighlight(
        IEnumerable<PdfRectangle> areas, string? text = null, string? author = null, PdfColor? color = null) =>
        AddTextMarkup("Highlight", areas, text, author, color);

    /// <summary>Underlines areas of the page.</summary>
    /// <param name="areas">The areas to underline.</param>
    /// <param name="text">An optional comment attached to the mark.</param>
    /// <param name="author">Who made the mark.</param>
    /// <param name="color">The line colour.</param>
    public PdfTextMarkupAnnotation AddUnderline(
        IEnumerable<PdfRectangle> areas, string? text = null, string? author = null, PdfColor? color = null) =>
        AddTextMarkup("Underline", areas, text, author, color);

    /// <summary>Strikes through areas of the page.</summary>
    /// <param name="areas">The areas to strike through.</param>
    /// <param name="text">An optional comment attached to the mark.</param>
    /// <param name="author">Who made the mark.</param>
    /// <param name="color">The line colour.</param>
    public PdfTextMarkupAnnotation AddStrikeOut(
        IEnumerable<PdfRectangle> areas, string? text = null, string? author = null, PdfColor? color = null) =>
        AddTextMarkup("StrikeOut", areas, text, author, color);

    /// <summary>Draws a wavy underline beneath areas of the page.</summary>
    /// <param name="areas">The areas to mark.</param>
    /// <param name="text">An optional comment attached to the mark.</param>
    /// <param name="author">Who made the mark.</param>
    /// <param name="color">The line colour.</param>
    public PdfTextMarkupAnnotation AddSquiggly(
        IEnumerable<PdfRectangle> areas, string? text = null, string? author = null, PdfColor? color = null) =>
        AddTextMarkup("Squiggly", areas, text, author, color);

    /// <summary>
    /// Finds a phrase on the page and marks every occurrence. This is the usual way to comment on
    /// something: the caller says what to mark rather than working out where the words landed.
    /// </summary>
    /// <param name="phrase">The text to mark.</param>
    /// <param name="kind">Which mark to make: highlight, underline, strikeout or squiggly.</param>
    /// <param name="text">An optional comment attached to each mark.</param>
    /// <param name="author">Who made the marks.</param>
    /// <param name="color">The mark colour.</param>
    /// <param name="comparison">How to compare; case-insensitive by default.</param>
    /// <returns>One annotation per occurrence, empty when the phrase is not on the page.</returns>
    public IReadOnlyList<PdfTextMarkupAnnotation> MarkText(
        string phrase,
        PdfAnnotationType kind = PdfAnnotationType.Highlight,
        string? text = null,
        string? author = null,
        PdfColor? color = null,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phrase);

        string subtype = kind switch
        {
            PdfAnnotationType.Highlight => "Highlight",
            PdfAnnotationType.Underline => "Underline",
            PdfAnnotationType.StrikeOut => "StrikeOut",
            PdfAnnotationType.Squiggly => "Squiggly",
            _ => throw new ArgumentException("Only the four text markup kinds can mark text.", nameof(kind)),
        };

        List<PdfTextMarkupAnnotation> marks = [];
        foreach (Text.PdfTextMatch match in _page.FindText(phrase, comparison))
            marks.Add(AddTextMarkup(subtype, match.Areas, text, author, color));

        return marks;
    }

    /// <summary>Writes text directly onto the page in a box.</summary>
    /// <param name="bounds">Where the box sits.</param>
    /// <param name="text">The text to write.</param>
    /// <param name="author">Who wrote it.</param>
    /// <param name="fontSize">The size to draw the text at.</param>
    /// <param name="textColor">The colour to draw the text in.</param>
    /// <param name="background">A colour to paint behind the text, or nothing for a transparent box.</param>
    public PdfFreeTextAnnotation AddFreeText(
        PdfRectangle bounds,
        string text,
        string? author = null,
        double fontSize = 11,
        PdfColor? textColor = null,
        PdfColor? background = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        PdfDictionary dictionary = NewMarkup("FreeText", bounds, text, author, background);
        DefaultAppearance.Write(dictionary, new DefaultAppearance("Helv", fontSize, textColor ?? PdfColor.Black));

        return (PdfFreeTextAnnotation)Attach(dictionary, withPopup: true);
    }

    /// <summary>Draws a rectangle on the page.</summary>
    /// <param name="bounds">Where the rectangle sits.</param>
    /// <param name="text">An optional comment attached to it.</param>
    /// <param name="author">Who drew it.</param>
    /// <param name="color">The line colour.</param>
    /// <param name="fill">A colour to fill the shape with, or nothing to leave it open.</param>
    public PdfSquareAnnotation AddRectangle(
        PdfRectangle bounds, string? text = null, string? author = null, PdfColor? color = null, PdfColor? fill = null) =>
        (PdfSquareAnnotation)AddShape("Square", bounds, text, author, color, fill);

    /// <summary>Draws an ellipse on the page.</summary>
    /// <param name="bounds">The box the ellipse fits into.</param>
    /// <param name="text">An optional comment attached to it.</param>
    /// <param name="author">Who drew it.</param>
    /// <param name="color">The line colour.</param>
    /// <param name="fill">A colour to fill the shape with, or nothing to leave it open.</param>
    public PdfCircleAnnotation AddEllipse(
        PdfRectangle bounds, string? text = null, string? author = null, PdfColor? color = null, PdfColor? fill = null) =>
        (PdfCircleAnnotation)AddShape("Circle", bounds, text, author, color, fill);

    /// <summary>Draws a line on the page.</summary>
    /// <param name="from">Where the line starts.</param>
    /// <param name="to">Where the line ends.</param>
    /// <param name="text">An optional comment attached to it.</param>
    /// <param name="author">Who drew it.</param>
    /// <param name="color">The line colour.</param>
    public PdfLineAnnotation AddLine(
        PdfPoint from, PdfPoint to, string? text = null, string? author = null, PdfColor? color = null)
    {
        PdfDictionary dictionary = NewMarkup(
            "Line", new PdfRectangle(from.X, from.Y, to.X, to.Y).Inflate(4), text, author, color ?? PdfColor.Red);

        dictionary.Set(AnnotationNames.L, PdfValue.Array(PdfArray.OfNumbers(from.X, from.Y, to.X, to.Y)));

        return (PdfLineAnnotation)Attach(dictionary, withPopup: true);
    }

    /// <summary>Rings an area with a closed polygon.</summary>
    /// <param name="vertices">The corners, in order; the last joins back to the first.</param>
    /// <param name="text">An optional comment attached to it.</param>
    /// <param name="author">Who drew it.</param>
    /// <param name="color">The line colour.</param>
    /// <param name="fill">A colour to fill the shape with, or nothing to leave it open.</param>
    public PdfPolygonAnnotation AddPolygon(
        IEnumerable<PdfPoint> vertices,
        string? text = null,
        string? author = null,
        PdfColor? color = null,
        PdfColor? fill = null) =>
        AddVertices("Polygon", vertices, text, author, color, fill);

    /// <summary>Traces a path with connected line segments.</summary>
    /// <param name="vertices">The points, in order.</param>
    /// <param name="text">An optional comment attached to it.</param>
    /// <param name="author">Who drew it.</param>
    /// <param name="color">The line colour.</param>
    public PdfPolygonAnnotation AddPolyLine(
        IEnumerable<PdfPoint> vertices, string? text = null, string? author = null, PdfColor? color = null) =>
        AddVertices("PolyLine", vertices, text, author, color, null);

    /// <summary>Marks a point where text should be inserted.</summary>
    /// <param name="at">Where the caret sits; the point is its lower-left corner.</param>
    /// <param name="text">What should go in.</param>
    /// <param name="author">Who marked it.</param>
    /// <param name="color">The caret colour.</param>
    /// <param name="height">How tall the caret is drawn.</param>
    public PdfCaretAnnotation AddCaret(
        PdfPoint at, string? text = null, string? author = null, PdfColor? color = null, double height = 10)
    {
        PdfDictionary dictionary = NewMarkup(
            "Caret", PdfRectangle.FromSize(at.X, at.Y, height * 0.8, height), text, author, color);

        return (PdfCaretAnnotation)Attach(dictionary, withPopup: true);
    }

    /// <summary>
    /// Writes a note on the page with a leader line to what it is about. The annotation rectangle
    /// has to cover both, so it is worked out from the two and the text inset recorded in <c>RD</c>.
    /// </summary>
    /// <param name="textBounds">Where the writing goes.</param>
    /// <param name="pointsAt">What the leader line points at.</param>
    /// <param name="text">The text to write.</param>
    /// <param name="author">Who wrote it.</param>
    /// <param name="fontSize">The size to draw the text at.</param>
    /// <param name="textColor">The colour of the text and the leader line.</param>
    /// <param name="background">A colour to paint behind the text, or nothing for a transparent box.</param>
    public PdfFreeTextAnnotation AddCallout(
        PdfRectangle textBounds,
        PdfPoint pointsAt,
        string text,
        string? author = null,
        double fontSize = 11,
        PdfColor? textColor = null,
        PdfColor? background = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        // The leader leaves the box from whichever side faces the target.
        PdfPoint centre = textBounds.Center;
        PdfPoint knee = pointsAt.X < centre.X
            ? new PdfPoint(textBounds.Left, centre.Y)
            : new PdfPoint(textBounds.Right, centre.Y);

        PdfRectangle bounds = textBounds
            .Union(new PdfRectangle(pointsAt.X, pointsAt.Y, pointsAt.X, pointsAt.Y))
            .Inflate(Math.Max(6, fontSize / 2));

        PdfDictionary dictionary = NewMarkup("FreeText", bounds, text, author, background);
        DefaultAppearance.Write(dictionary, new DefaultAppearance("Helv", fontSize, textColor ?? PdfColor.Black));

        dictionary.Set(AnnotationNames.IT, PdfValue.Name(PdfName.Get("FreeTextCallout")));
        dictionary.Set(AnnotationNames.LE, PdfValue.Name(PdfName.Get("OpenArrow")));
        dictionary.Set(PdfName.Get("CL"), PdfValue.Array(
            PdfArray.OfNumbers(pointsAt.X, pointsAt.Y, knee.X, knee.Y)));

        dictionary.Set(PdfName.Get("RD"), PdfValue.Array(PdfArray.OfNumbers(
            textBounds.Left - bounds.Left,
            bounds.Top - textBounds.Top,
            bounds.Right - textBounds.Right,
            textBounds.Bottom - bounds.Bottom)));

        return (PdfFreeTextAnnotation)Attach(dictionary, withPopup: true);
    }

    /// <summary>Draws freehand strokes on the page.</summary>
    /// <param name="strokes">The strokes, each a run of points joined in order.</param>
    /// <param name="text">An optional comment attached to them.</param>
    /// <param name="author">Who drew them.</param>
    /// <param name="color">The ink colour.</param>
    /// <param name="width">The pen width.</param>
    public PdfInkAnnotation AddInk(
        IEnumerable<IReadOnlyList<PdfPoint>> strokes,
        string? text = null,
        string? author = null,
        PdfColor? color = null,
        double width = 1.5)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        PdfDictionary dictionary = NewMarkup("Ink", default, text, author, color ?? PdfColor.Red);
        var style = new PdfDictionary(2);
        style.Set(AnnotationNames.W, PdfValue.Number(width));
        style.Set(AnnotationNames.S, PdfValue.Name(PdfName.Get("S")));
        dictionary.Set(AnnotationNames.BS, PdfValue.Dictionary(style));

        var ink = (PdfInkAnnotation)Attach(dictionary, withPopup: false, drawNow: false);
        ink.Strokes = [.. strokes];
        AttachPopup(ink);
        return ink;
    }

    /// <summary>Stamps the page.</summary>
    /// <param name="bounds">Where the stamp sits.</param>
    /// <param name="stampName">One of the standard stamp names, such as <c>Approved</c>.</param>
    /// <param name="text">An optional comment attached to it.</param>
    /// <param name="author">Who stamped it.</param>
    /// <param name="color">The stamp colour.</param>
    public PdfStampAnnotation AddStamp(
        PdfRectangle bounds,
        string stampName = "Draft",
        string? text = null,
        string? author = null,
        PdfColor? color = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(stampName);

        PdfDictionary dictionary = NewMarkup("Stamp", bounds, text, author, color);
        dictionary.Set(AnnotationNames.Name, PdfValue.Name(PdfName.Get(stampName)));

        return (PdfStampAnnotation)Attach(dictionary, withPopup: true);
    }

    /// <summary>Adds a hyperlink over an area of the page.</summary>
    /// <param name="bounds">The clickable area.</param>
    /// <param name="uri">Where the link goes.</param>
    /// <param name="border">Whether to draw the reader's link border.</param>
    public PdfLinkAnnotation AddLink(PdfRectangle bounds, string uri, bool border = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);

        var dictionary = new PdfDictionary(6);
        dictionary.Set(AnnotationNames.Type, PdfValue.Name(AnnotationNames.Annot));
        dictionary.Set(AnnotationNames.Subtype, PdfValue.Name(PdfName.Get("Link")));
        dictionary.Set(AnnotationNames.Rect, PdfValue.Array(bounds.ToArray()));
        dictionary.Set(AnnotationNames.Border, PdfValue.Array(PdfArray.OfNumbers(0, 0, border ? 1 : 0)));
        dictionary.Set(AnnotationNames.F, PdfValue.Integer((long)PdfAnnotationFlags.Print));

        var link = (PdfLinkAnnotation)Attach(dictionary, withPopup: false);
        link.Uri = uri;
        return link;
    }

    /// <summary>Attaches a file to a point on the page.</summary>
    /// <param name="at">Where the pin sits.</param>
    /// <param name="fileName">The name the file is listed under.</param>
    /// <param name="content">The file contents.</param>
    /// <param name="text">An optional comment describing it.</param>
    /// <param name="author">Who attached it.</param>
    public PdfFileAttachmentAnnotation AddAttachment(
        PdfPoint at, string fileName, ReadOnlyMemory<byte> content, string? text = null, string? author = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        const double Size = 20;
        PdfValue specification = _page.Document.Attachments.Add(
            fileName, content, description: text, relationship: AttachmentRelationship.Unspecified);

        PdfDictionary dictionary = NewMarkup(
            "FileAttachment", PdfRectangle.FromSize(at.X, at.Y - Size, Size, Size), text, author, null);

        dictionary.Set(AnnotationNames.FS, specification);
        dictionary.Set(AnnotationNames.Name, PdfValue.Name(PdfName.Get("Paperclip")));

        return (PdfFileAttachmentAnnotation)Attach(dictionary, withPopup: true);
    }

    /// <summary>
    /// Reduces the comments on the page to plain records, threads and quoted text included. The page
    /// text is read once for the whole page rather than once per comment.
    /// </summary>
    /// <param name="pageIndex">The page number to record; taken from the document when omitted.</param>
    public IReadOnlyList<PdfCommentInfo> ExportComments(int? pageIndex = null)
    {
        PdfMarkupAnnotation[] comments = [.. Comments];
        if (comments.Length == 0)
            return [];

        int index = pageIndex ?? IndexOfPage();
        IReadOnlyList<Text.PdfLetter>? letters = CommentReport.NeedsText(comments) ? _page.ExtractLetters() : null;

        var report = new List<PdfCommentInfo>(comments.Length);
        foreach (PdfMarkupAnnotation comment in comments)
            report.Add(CommentReport.Describe(comment, index, letters));

        return report;
    }

    /// <summary>Removes an annotation, along with its pop-up window and every reply beneath it.</summary>
    /// <param name="annotation">The annotation to remove.</param>
    /// <returns>Whether it was on this page.</returns>
    public bool Remove(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return Items.Contains(annotation) && Detach(annotation) > 0;
    }

    /// <summary>Removes the annotation at a position.</summary>
    /// <param name="index">Which annotation to remove.</param>
    public void RemoveAt(int index) => Remove(Items[index]);

    /// <summary>Removes every annotation the predicate accepts.</summary>
    /// <param name="predicate">Chooses which annotations go.</param>
    /// <returns>How many were removed, counting the pop-ups and replies that went with them.</returns>
    public int RemoveAll(Func<PdfAnnotation, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        int removed = 0;
        foreach (PdfAnnotation annotation in Items.ToArray())
        {
            if (!annotation.IsDeleted && predicate(annotation))
                removed += Detach(annotation);
        }

        return removed;
    }

    /// <summary>
    /// Draws annotations into the page content and then removes them, so the marks survive as
    /// ordinary graphics in a reader that shows no comments at all — and so nobody can open, move or
    /// edit them. The thread behind a flattened comment goes with it; the words it says do not
    /// appear on the page unless the annotation drew them there.
    /// </summary>
    /// <param name="predicate">Which annotations to burn in; comments by default.</param>
    /// <returns>How many annotations were flattened, replies and pop-ups excluded.</returns>
    public int Flatten(Func<PdfAnnotation, bool>? predicate = null)
    {
        // A reply draws nothing of its own and goes when the comment it answers goes.
        predicate ??= static annotation => annotation is PdfMarkupAnnotation { IsReply: false };

        List<PdfAnnotation> chosen = [];
        foreach (PdfAnnotation annotation in Items)
        {
            if (predicate(annotation))
                chosen.Add(annotation);
        }

        List<PdfAnnotation> flattened = AnnotationFlattener.Flatten(_page, chosen);
        foreach (PdfAnnotation annotation in flattened)
            Detach(annotation);

        if (flattened.Count > 0)
            _page.MarkChanged();

        return flattened.Count;
    }

    /// <summary>Removes every comment, leaving links, form widgets and anything else in place.</summary>
    public int RemoveComments() => RemoveAll(annotation => annotation is PdfMarkupAnnotation or PdfPopupAnnotation);

    /// <summary>Removes every annotation from the page.</summary>
    public void Clear()
    {
        foreach (PdfAnnotation annotation in Items.ToArray())
            Detach(annotation);

        RawArray.Clear();
        Items.Clear();
        Synced();
        _page.MarkChanged();
    }

    /// <inheritdoc />
    public IEnumerator<PdfAnnotation> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal PdfAnnotation? Find(PdfObjectId id)
    {
        foreach (PdfAnnotation annotation in Items)
        {
            if (annotation.Id.Number == id.Number)
                return annotation;
        }

        return null;
    }

    /// <summary>
    /// Puts an annotation dictionary from elsewhere — a round of review that arrived as FDF or
    /// XFDF — onto this page. Nothing is drawn and no window is made: the caller settles both once
    /// it knows whether the annotation turned out to be a reply.
    /// </summary>
    /// <param name="dictionary">The annotation dictionary, used as it stands.</param>
    internal PdfAnnotation Adopt(PdfDictionary dictionary) =>
        Attach(dictionary, withPopup: false, drawNow: false);

    /// <summary>Gives a comment the window a reader shows its text in.</summary>
    /// <param name="parent">The comment the window belongs to.</param>
    /// <param name="bounds">Where the window sits, or nothing to place it beside the comment.</param>
    /// <param name="open">Whether the window starts open.</param>
    internal void AttachPopup(PdfMarkupAnnotation parent, PdfRectangle? bounds = null, bool open = false)
    {
        List<PdfAnnotation> items = Items;
        PdfRectangle window = bounds ?? BesideComment(parent);

        var dictionary = new PdfDictionary(7);
        dictionary.Set(AnnotationNames.Type, PdfValue.Name(AnnotationNames.Annot));
        dictionary.Set(AnnotationNames.Subtype, PdfValue.Name(AnnotationNames.Popup));
        dictionary.Set(AnnotationNames.Rect, PdfValue.Array(window.ToArray()));
        dictionary.Set(AnnotationNames.Open, PdfValue.Boolean(open));
        dictionary.Set(AnnotationNames.Parent, PdfValue.Reference(parent.Id));
        dictionary.Set(AnnotationNames.P, PdfValue.Reference(_page.Id));

        // The window keeps its size and orientation whatever the page does, which is the flag set
        // Acrobat writes on its own pop-ups.
        dictionary.Set(AnnotationNames.F, PdfValue.Integer(
            (long)(PdfAnnotationFlags.Print | PdfAnnotationFlags.NoZoom | PdfAnnotationFlags.NoRotate)));

        _page.Document.Attach(PdfValue.Dictionary(dictionary));
        PdfObjectId id = _page.Document.AddObject(PdfValue.Dictionary(dictionary));

        parent.Dictionary.Set(AnnotationNames.Popup, PdfValue.Reference(id));
        RawArray.Add(PdfValue.Reference(id));
        items.Add(new PdfPopupAnnotation(_page, id, dictionary));
        Synced();
    }

    /// <summary>Removes an annotation and everything that hangs off it, and says how much went.</summary>
    internal int Detach(PdfAnnotation annotation)
    {
        int removed = RemoveOne(annotation) ? 1 : 0;
        annotation.IsDeleted = true;

        // Dropping the pin has to drop the file behind it, or a document stripped of its comments
        // still carries every attachment somebody pinned to a page.
        if (removed > 0 && annotation is PdfFileAttachmentAnnotation attachment &&
            attachment.Dictionary.TryGetRaw(AnnotationNames.FS, out PdfValue specification))
        {
            _page.Document.Attachments.Unlist(specification);
        }

        if (annotation is PdfMarkupAnnotation markup)
        {
            if (markup.PopupId.IsValid && Find(markup.PopupId) is { } popup && RemoveOne(popup))
            {
                popup.IsDeleted = true;
                removed++;
            }

            foreach (PdfMarkupAnnotation reply in markup.Replies)
                removed += Detach(reply);
        }

        if (removed > 0)
            _page.MarkChanged();

        return removed;
    }

    private bool RemoveOne(PdfAnnotation annotation)
    {
        Items.Remove(annotation);

        PdfArray array = RawArray;
        for (int i = 0; i < array.Count; i++)
        {
            PdfValue entry = array[i];
            bool matches = entry.IsReference
                ? entry.AsReference.Number == annotation.Id.Number
                : ReferenceEquals(entry.AsDictionary, annotation.Dictionary);

            if (!matches)
                continue;

            array.RemoveAt(i);
            AnnotationStructureLinks.Unlink(annotation);
            Synced();
            return true;
        }

        return false;
    }

    /// <summary>Records the array length the cached list matches, so the next read knows it is current.</summary>
    private void Synced() => _seenCount = _page.Dictionary.GetArray(AnnotationNames.Annots)?.Count ?? 0;

    private int IndexOfPage()
    {
        PdfPageCollection pages = _page.Document.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            if (ReferenceEquals(pages[i].Dictionary, _page.Dictionary))
                return i;
        }

        return 0;
    }

    private PdfTextMarkupAnnotation AddTextMarkup(
        string subtype, IEnumerable<PdfRectangle> areas, string? text, string? author, PdfColor? color)
    {
        ArgumentNullException.ThrowIfNull(areas);

        PdfRectangle[] list = [.. areas];
        if (list.Length == 0)
            throw new ArgumentException("A text mark needs at least one area to cover.", nameof(areas));

        PdfDictionary dictionary = NewMarkup(subtype, default, text, author, color);

        // The window is placed beside the annotation, so it has to wait until the marked areas have
        // settled where the annotation is: placing it first puts it beside an empty rectangle in
        // the corner of the page.
        var markup = (PdfTextMarkupAnnotation)Attach(dictionary, withPopup: false, drawNow: false);
        markup.Areas = list;
        AttachPopup(markup);
        return markup;
    }

    private PdfPolygonAnnotation AddVertices(
        string subtype,
        IEnumerable<PdfPoint> vertices,
        string? text,
        string? author,
        PdfColor? color,
        PdfColor? fill)
    {
        ArgumentNullException.ThrowIfNull(vertices);

        PdfPoint[] points = [.. vertices];
        if (points.Length < 2)
            throw new ArgumentException("A polygon needs at least two corners.", nameof(vertices));

        PdfDictionary dictionary = NewMarkup(subtype, default, text, author, color ?? PdfColor.Red);

        if (fill is { } interior)
            dictionary.Set(AnnotationNames.IC, PdfValue.Array(Components(interior)));

        var shape = (PdfPolygonAnnotation)Attach(dictionary, withPopup: false, drawNow: false);
        shape.Vertices = points;
        AttachPopup(shape);
        return shape;
    }

    private PdfShapeAnnotation AddShape(
        string subtype, PdfRectangle bounds, string? text, string? author, PdfColor? color, PdfColor? fill)
    {
        PdfDictionary dictionary = NewMarkup(subtype, bounds, text, author, color ?? PdfColor.Red);

        if (fill is { } interior)
            dictionary.Set(AnnotationNames.IC, PdfValue.Array(Components(interior)));

        return (PdfShapeAnnotation)Attach(dictionary, withPopup: true);
    }

    private PdfDictionary NewMarkup(
        string subtype, PdfRectangle bounds, string? text, string? author, PdfColor? color)
    {
        var dictionary = new PdfDictionary(12);
        dictionary.Set(AnnotationNames.Type, PdfValue.Name(AnnotationNames.Annot));
        dictionary.Set(AnnotationNames.Subtype, PdfValue.Name(PdfName.Get(subtype)));
        dictionary.Set(AnnotationNames.Rect, PdfValue.Array(bounds.ToArray()));
        dictionary.Set(AnnotationNames.F, PdfValue.Integer((long)PdfAnnotationFlags.Print));

        string now = PdfDate.Format(DateTimeOffset.Now);
        dictionary.Set(AnnotationNames.CreationDate, PdfValue.TextString(now));
        dictionary.Set(AnnotationNames.M, PdfValue.TextString(now));
        dictionary.Set(AnnotationNames.NM, PdfValue.TextString(Guid.NewGuid().ToString("N")));

        if (!string.IsNullOrEmpty(text))
            dictionary.Set(AnnotationNames.Contents, PdfValue.TextString(AnnotationText.ToPdf(text)));

        if (!string.IsNullOrEmpty(author))
            dictionary.Set(AnnotationNames.T, PdfValue.TextString(author));

        if (color is { } value)
            dictionary.Set(AnnotationNames.C, PdfValue.Array(Components(value)));

        return dictionary;
    }

    /// <summary>A colour as the array of components its space needs (table 164).</summary>
    private static PdfArray Components(PdfColor color) => color.Kind switch
    {
        PdfColorKind.Gray => PdfArray.OfNumbers(color.Component0),
        PdfColorKind.Rgb => PdfArray.OfNumbers(color.Component0, color.Component1, color.Component2),
        _ => PdfArray.OfNumbers(color.Component0, color.Component1, color.Component2, color.Component3),
    };

    private PdfAnnotation Attach(PdfDictionary dictionary, bool withPopup, bool drawNow = true)
    {
        // The list has to be read before the array grows, or the lazy read would pick up the entry
        // that is about to be added by hand and the annotation would appear twice.
        List<PdfAnnotation> items = Items;

        PdfDocument document = _page.Document;
        document.Attach(PdfValue.Dictionary(dictionary));
        dictionary.Set(AnnotationNames.P, PdfValue.Reference(_page.Id));

        PdfObjectId id = document.AddObject(PdfValue.Dictionary(dictionary));
        PdfAnnotation annotation = Create(_page, id, dictionary);

        RawArray.Add(PdfValue.Reference(id));
        items.Add(annotation);

        if (withPopup && annotation is PdfMarkupAnnotation markup)
            AttachPopup(markup);

        if (drawNow)
            annotation.RegenerateAppearance();

        Synced();
        _page.MarkChanged();
        return annotation;
    }

    private PdfRectangle BesideComment(PdfMarkupAnnotation parent)
    {
        PdfRectangle anchor = parent.Rectangle;
        PdfRectangle page = _page.CropBox;

        double left = Math.Min(anchor.Right + 4, Math.Max(page.Left, page.Right - 190));
        double top = Math.Min(anchor.Top, page.Top);

        return PdfRectangle.FromSize(left, top - 90, 180, 90);
    }

    /// <summary>
    /// The typed view of the array, rebuilt whenever the array has grown or shrunk behind our back.
    /// The forms and signing packages add widgets straight to <see cref="RawArray"/>, so a cached
    /// list that never rechecks would hide a field from whoever looks at the page afterwards.
    /// </summary>
    private List<PdfAnnotation> Items
    {
        get
        {
            int count = _page.Dictionary.GetArray(AnnotationNames.Annots)?.Count ?? 0;
            if (_annotations is null || count != _seenCount)
                _annotations = Read();

            return _annotations;
        }
    }

    private List<PdfAnnotation> Read()
    {
        List<PdfAnnotation> annotations = [];

        if (_page.Dictionary.GetArray(AnnotationNames.Annots) is not { } array)
        {
            _seenCount = 0;
            return annotations;
        }

        // Wrappers are reused so that a rebuild does not forget which appearance streams are ours.
        Dictionary<PdfDictionary, PdfAnnotation> known = new(ReferenceEqualityComparer.Instance);
        foreach (PdfAnnotation annotation in _annotations ?? [])
            known[annotation.Dictionary] = annotation;

        for (int i = 0; i < array.Count; i++)
        {
            PdfValue entry = array[i];
            if (array.Get(i).AsDictionary is not { } dictionary)
                continue;

            if (known.TryGetValue(dictionary, out PdfAnnotation? existing))
            {
                annotations.Add(existing);
                continue;
            }

            PdfObjectId id = entry.IsReference ? entry.AsReference : PdfObjectId.None;
            annotations.Add(Create(_page, id, dictionary));
        }

        _seenCount = array.Count;
        return annotations;
    }

    private static PdfAnnotation Create(PdfPage page, PdfObjectId id, PdfDictionary dictionary) =>
        PdfAnnotation.Classify(dictionary.GetName(AnnotationNames.Subtype)?.Value) switch
        {
            PdfAnnotationType.Text => new PdfTextAnnotation(page, id, dictionary),
            PdfAnnotationType.Highlight or PdfAnnotationType.Underline or
                PdfAnnotationType.StrikeOut or PdfAnnotationType.Squiggly =>
                new PdfTextMarkupAnnotation(page, id, dictionary),
            PdfAnnotationType.FreeText => new PdfFreeTextAnnotation(page, id, dictionary),
            PdfAnnotationType.Square => new PdfSquareAnnotation(page, id, dictionary),
            PdfAnnotationType.Circle => new PdfCircleAnnotation(page, id, dictionary),
            PdfAnnotationType.Line => new PdfLineAnnotation(page, id, dictionary),
            PdfAnnotationType.Polygon or PdfAnnotationType.PolyLine => new PdfPolygonAnnotation(page, id, dictionary),
            PdfAnnotationType.Caret => new PdfCaretAnnotation(page, id, dictionary),
            PdfAnnotationType.Ink => new PdfInkAnnotation(page, id, dictionary),
            PdfAnnotationType.Stamp => new PdfStampAnnotation(page, id, dictionary),
            PdfAnnotationType.FileAttachment => new PdfFileAttachmentAnnotation(page, id, dictionary),
            PdfAnnotationType.Popup => new PdfPopupAnnotation(page, id, dictionary),
            PdfAnnotationType.Link => new PdfLinkAnnotation(page, id, dictionary),
            _ => new PdfGenericAnnotation(page, id, dictionary),
        };
}
