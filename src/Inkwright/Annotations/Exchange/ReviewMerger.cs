using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>
/// One annotation as a round of review delivered it. Three things cannot travel inside the
/// dictionary: which page it belongs on, which comment it answers, and the window that shows its
/// text. All three are references, and a reference only means something once the annotation has
/// found its place in the document that is receiving it.
/// </summary>
internal sealed record IncomingAnnotation
{
    /// <summary>Which page it belongs on, counting from zero.</summary>
    internal required int PageIndex { get; init; }

    /// <summary>The annotation dictionary, already belonging to the receiving document.</summary>
    internal required PdfDictionary Dictionary { get; init; }

    /// <summary>The name of the comment this one answers, or nothing when it starts a thread.</summary>
    internal string? InReplyTo { get; init; }

    /// <summary>Whether the tie to the parent is a grouping rather than an answer.</summary>
    internal bool IsGroupedWithParent { get; init; }

    /// <summary>Whether the payload asked for a pop-up window.</summary>
    internal bool WantsPopup { get; init; }

    /// <summary>Where that window sits, or nothing to place it beside the comment.</summary>
    internal PdfRectangle? PopupBounds { get; init; }

    /// <summary>Whether the window starts open.</summary>
    internal bool PopupOpen { get; init; }
}

/// <summary>
/// Puts a round of review into a document. Both exchange formats end here, because the hard part is
/// the same either way: a comment that is already in the document must be replaced rather than
/// duplicated, and a reply is only a reply once the comment it answers has a place of its own.
/// </summary>
internal static class ReviewMerger
{
    internal static PdfReviewImportResult Merge(
        PdfDocument document, IReadOnlyList<IncomingAnnotation> incoming, List<string> warnings)
    {
        Dictionary<string, PdfAnnotation> known = Index(document);
        List<(PdfAnnotation Annotation, IncomingAnnotation Source)> placed = new(incoming.Count);

        int added = 0;
        int updated = 0;
        int skipped = 0;

        foreach (IncomingAnnotation entry in incoming)
        {
            if (entry.PageIndex < 0 || entry.PageIndex >= document.Pages.Count)
            {
                warnings.Add(
                    $"A comment belongs on page {entry.PageIndex + 1}, and the document has " +
                    $"{document.Pages.Count}; it was left out.");

                skipped++;
                continue;
            }

            PdfPage page = document.Pages[entry.PageIndex];
            string name = NameOf(entry.Dictionary);

            if (known.TryGetValue(name, out PdfAnnotation? existing) && !existing.IsDeleted)
            {
                if (!ReferenceEquals(existing.Page.Dictionary, page.Dictionary))
                {
                    warnings.Add(
                        $"The comment '{name}' now says it is on page {entry.PageIndex + 1}; " +
                        "it was rewritten where it already sits.");
                }

                Overwrite(document, existing, entry.Dictionary);
                placed.Add((existing, entry));
                updated++;
                continue;
            }

            PdfAnnotation annotation = page.Annotations.Adopt(entry.Dictionary);
            known[name] = annotation;
            placed.Add((annotation, entry));
            added++;
        }

        Thread(document, known, placed, warnings);
        Finish(placed);

        return new PdfReviewImportResult(added, updated, skipped, warnings);
    }

    /// <summary>Every annotation already in the document, by the name it goes under.</summary>
    private static Dictionary<string, PdfAnnotation> Index(PdfDocument document)
    {
        Dictionary<string, PdfAnnotation> known = new(StringComparer.Ordinal);

        foreach (PdfPage page in document.Pages)
        {
            foreach (PdfAnnotation annotation in page.Annotations)
            {
                if (annotation.Name is { Length: > 0 } name)
                    known.TryAdd(name, annotation);
            }
        }

        return known;
    }

    /// <summary>
    /// The name the comment goes under. A payload that names nothing is given a name here rather
    /// than left nameless: without one the next round of review has no way to recognise the same
    /// comment again, and every exchange would pile up another copy.
    /// </summary>
    private static string NameOf(PdfDictionary dictionary)
    {
        if (dictionary.GetTextString(AnnotationNames.NM) is { Length: > 0 } name)
            return name;

        string minted = Guid.NewGuid().ToString("N");
        dictionary.Set(AnnotationNames.NM, PdfValue.TextString(minted));
        return minted;
    }

    /// <summary>
    /// Replaces what a comment says with what the round of review says. Three entries survive
    /// because they belong to the document rather than to the payload: the page the annotation sits
    /// on, the window that shows it, and the comment it answers.
    /// </summary>
    private static void Overwrite(PdfDocument document, PdfAnnotation target, PdfDictionary source)
    {
        PdfDictionary dictionary = target.Dictionary;

        foreach (PdfName key in dictionary.Keys.ToArray())
        {
            if (!Preserved(key) && !source.ContainsKey(key))
                dictionary.Remove(key);
        }

        foreach ((PdfName key, PdfValue value) in source)
        {
            if (!Preserved(key))
                dictionary.Set(key, value);
        }

        if (target.Id.IsValid)
            document.MarkChanged(target.Id);
        else
            target.Page.MarkChanged();
    }

    private static bool Preserved(PdfName key) =>
        key == AnnotationNames.P || key == AnnotationNames.Popup || key == AnnotationNames.IRT;

    /// <summary>
    /// Ties the replies to the comments they answer. The payload names the parent, because an
    /// object number from the sender's document means nothing here; the name does.
    /// </summary>
    private static void Thread(
        PdfDocument document,
        Dictionary<string, PdfAnnotation> known,
        List<(PdfAnnotation Annotation, IncomingAnnotation Source)> placed,
        List<string> warnings)
    {
        foreach ((PdfAnnotation annotation, IncomingAnnotation source) in placed)
        {
            if (source.InReplyTo is not { Length: > 0 } parent)
                continue;

            if (!known.TryGetValue(parent, out PdfAnnotation? answered) || !answered.Id.IsValid)
            {
                warnings.Add($"A reply answers the comment named '{parent}', which is not in the document.");
                continue;
            }

            annotation.Dictionary.Set(AnnotationNames.IRT, PdfValue.Reference(answered.Id));
            annotation.Dictionary.Set(
                AnnotationNames.RT,
                PdfValue.Name(source.IsGroupedWithParent ? AnnotationNames.Group : AnnotationNames.R));

            document.MarkChanged(annotation.Id);
        }
    }

    /// <summary>
    /// Gives every comment a window to hold its text and a drawing to be seen by. This has to come
    /// after the threading: a reply draws nothing at all, and it only knows it is a reply once the
    /// tie to its parent is in place.
    /// </summary>
    private static void Finish(List<(PdfAnnotation Annotation, IncomingAnnotation Source)> placed)
    {
        foreach ((PdfAnnotation annotation, IncomingAnnotation source) in placed)
        {
            if (annotation is PdfMarkupAnnotation markup && source.WantsPopup &&
                !markup.PopupId.IsValid && !markup.IsReply)
            {
                markup.Page.Annotations.AttachPopup(markup, source.PopupBounds, source.PopupOpen);
            }

            // A payload that brought its own drawing keeps it; one that brought none gets ours,
            // because the alternative is a comment that only some readers bother to draw.
            if (!annotation.Dictionary.ContainsKey(AnnotationNames.AP))
                annotation.RegenerateAppearance();
        }
    }
}
