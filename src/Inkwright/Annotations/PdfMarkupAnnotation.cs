using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>
/// An annotation that carries a comment: it has an author, a creation time and a pop-up window, and
/// other annotations can be written in reply to it. These are the ones a reader lists in its
/// comments pane (ISO 32000-1 §12.5.6.2).
/// </summary>
public class PdfMarkupAnnotation : PdfAnnotation
{
    private protected PdfMarkupAnnotation(PdfPage page, PdfObjectId id, PdfDictionary dictionary)
        : base(page, id, dictionary)
    {
    }

    /// <summary>Who wrote the comment.</summary>
    public string? Author
    {
        get => Dictionary.GetTextString(AnnotationNames.T);
        set
        {
            SetText(AnnotationNames.T, value);
            Touch();
        }
    }

    /// <summary>A short description of what the comment is about.</summary>
    public string? Subject
    {
        get => Dictionary.GetTextString(AnnotationNames.Subj);
        set
        {
            SetText(AnnotationNames.Subj, value);
            Touch();
        }
    }

    /// <summary>How opaque the annotation is painted, from zero to one.</summary>
    public double Opacity
    {
        get => Dictionary.GetNumber(AnnotationNames.CA, 1);
        set
        {
            Dictionary.Set(AnnotationNames.CA, PdfValue.Number(Math.Clamp(value, 0, 1)));
            Touch();
            RefreshAppearance();
        }
    }

    /// <summary>When the comment was written.</summary>
    public DateTimeOffset? CreatedAt
    {
        get => PdfDate.TryParse(Dictionary.GetTextString(AnnotationNames.CreationDate), out DateTimeOffset value)
            ? value
            : null;
        set => SetDate(AnnotationNames.CreationDate, value);
    }

    /// <summary>Whether the pop-up window starts open.</summary>
    public bool IsOpen
    {
        get => PopupAnnotation?.GetBoolean(AnnotationNames.Open) ?? Dictionary.GetBoolean(AnnotationNames.Open);
        set
        {
            Dictionary.Set(AnnotationNames.Open, PdfValue.Boolean(value));

            if (PopupAnnotation is { } popup)
                popup.Set(AnnotationNames.Open, PdfValue.Boolean(value));

            Touch();
        }
    }

    /// <summary>The annotation this one replies to, or <see langword="null"/> when it starts a thread.</summary>
    public PdfMarkupAnnotation? InReplyTo
    {
        get
        {
            if (!Dictionary.TryGetRaw(AnnotationNames.IRT, out PdfValue reference) || !reference.IsReference)
                return null;

            return Page.Annotations.Find(reference.AsReference) as PdfMarkupAnnotation;
        }
    }

    /// <summary>Whether this annotation is itself a reply to another.</summary>
    public bool IsReply => Dictionary.ContainsKey(AnnotationNames.IRT);

    /// <summary>
    /// The replies written to this comment, oldest first. Readers show these as a thread rather than
    /// as separate comments.
    /// </summary>
    public IReadOnlyList<PdfMarkupAnnotation> Replies
    {
        get
        {
            List<PdfMarkupAnnotation> replies = [];

            foreach (PdfAnnotation annotation in Page.Annotations)
            {
                if (annotation is not PdfMarkupAnnotation markup || !markup.RepliesTo(Id))
                    continue;

                // A "Group" relationship means the two move together, not that one answers the other.
                if (markup.Dictionary.GetName(AnnotationNames.RT) == AnnotationNames.Group)
                    continue;

                replies.Add(markup);
            }

            replies.Sort((a, b) => Nullable.Compare(a.CreatedAt, b.CreatedAt));
            return replies;
        }
    }

    /// <summary>The whole thread: this comment followed by every reply beneath it, in order.</summary>
    public IReadOnlyList<PdfMarkupAnnotation> Thread
    {
        get
        {
            List<PdfMarkupAnnotation> thread = [this];
            Collect(this, thread, 0);
            return thread;
        }
    }

    /// <summary>
    /// The review state the last reply recorded, or <see cref="PdfReviewState.None"/> when nobody has
    /// said anything. The state of an annotation is not stored on it: it lives in a reply that names
    /// the reviewer, so two people can disagree without overwriting each other (§12.5.6.3).
    /// </summary>
    public PdfReviewState ReviewState => ReviewStateBy(null);

    /// <summary>The review state a particular reviewer last recorded.</summary>
    /// <param name="author">The reviewer's name, or <see langword="null"/> for the most recent state from anyone.</param>
    public PdfReviewState ReviewStateBy(string? author)
    {
        PdfReviewState state = PdfReviewState.None;
        DateTimeOffset newest = DateTimeOffset.MinValue;

        foreach (PdfMarkupAnnotation reply in Replies)
        {
            if (reply.Dictionary.GetTextString(AnnotationNames.State) is not { } text)
                continue;

            if (author is not null && !string.Equals(reply.Author, author, StringComparison.Ordinal))
                continue;

            DateTimeOffset moment = reply.CreatedAt ?? reply.ModifiedAt ?? DateTimeOffset.MinValue;
            if (moment < newest)
                continue;

            newest = moment;
            state = ParseState(text);
        }

        return state;
    }

    /// <summary>
    /// The page text the comment covers: the words under a highlight, inside a drawn rectangle, and
    /// so on. Sticky notes return nothing, because they sit beside the text rather than over it.
    /// </summary>
    /// <remarks>
    /// Every call reads the page. Use <see cref="PdfAnnotationCollection.ExportComments"/> when
    /// several comments on the same page are wanted.
    /// </remarks>
    public string? GetQuotedText() => CommentReport.Quote(this, Page.ExtractLetters());

    /// <summary>Writes a reply to this comment.</summary>
    /// <param name="text">What the reply says.</param>
    /// <param name="author">Who wrote it.</param>
    /// <returns>The reply, which can itself be replied to.</returns>
    public PdfTextAnnotation Reply(string text, string? author = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        // A reply sits where its parent sits and shows no icon of its own; readers place it in the
        // thread rather than on the page.
        PdfTextAnnotation reply = Page.Annotations.AddNote(
            Rectangle.BottomLeft, text, author ?? Author, icon: PdfNoteIcon.Comment, withIcon: false);

        reply.Dictionary.Set(AnnotationNames.IRT, PdfValue.Reference(Id));
        reply.Dictionary.Set(AnnotationNames.RT, PdfValue.Name(AnnotationNames.R));
        reply.Dictionary.Set(AnnotationNames.Rect, PdfValue.Array(Rectangle.ToArray()));

        // Only now does the note know it is a reply, so this is where its appearance is settled.
        reply.RegenerateAppearance();
        Document.MarkChanged(reply.Id);
        return reply;
    }

    /// <summary>Records a review decision as a reply, the way the specification asks for it.</summary>
    /// <param name="state">The decision.</param>
    /// <param name="author">Who made it.</param>
    /// <param name="comment">An optional note explaining the decision.</param>
    public PdfTextAnnotation SetReviewState(PdfReviewState state, string? author = null, string? comment = null)
    {
        PdfTextAnnotation reply = Reply(comment ?? string.Empty, author);

        (string model, string name) = state switch
        {
            PdfReviewState.Marked => ("Marked", "Marked"),
            PdfReviewState.Unmarked => ("Marked", "Unmarked"),
            _ => ("Review", state.ToString()),
        };

        reply.Dictionary.Set(AnnotationNames.State, PdfValue.TextString(name));
        reply.Dictionary.Set(AnnotationNames.StateModel, PdfValue.TextString(model));
        Document.MarkChanged(reply.Id);
        return reply;
    }

    internal bool RepliesTo(PdfObjectId id) =>
        Dictionary.TryGetRaw(AnnotationNames.IRT, out PdfValue reference) &&
        reference.IsReference &&
        reference.AsReference.Number == id.Number;

    internal PdfDictionary? PopupAnnotation => Dictionary.GetDictionary(AnnotationNames.Popup);

    internal PdfObjectId PopupId =>
        Dictionary.TryGetRaw(AnnotationNames.Popup, out PdfValue reference) && reference.IsReference
            ? reference.AsReference
            : PdfObjectId.None;

    private static void Collect(PdfMarkupAnnotation annotation, List<PdfMarkupAnnotation> thread, int depth)
    {
        if (depth > 32)
            return;

        foreach (PdfMarkupAnnotation reply in annotation.Replies)
        {
            thread.Add(reply);
            Collect(reply, thread, depth + 1);
        }
    }

    internal static PdfReviewState ParseState(string text) => text switch
    {
        "Accepted" => PdfReviewState.Accepted,
        "Rejected" => PdfReviewState.Rejected,
        "Cancelled" => PdfReviewState.Cancelled,
        "Completed" => PdfReviewState.Completed,
        "Marked" => PdfReviewState.Marked,
        "Unmarked" => PdfReviewState.Unmarked,
        _ => PdfReviewState.None,
    };

    /// <inheritdoc />
    public override string ToString()
    {
        string who = Author is { Length: > 0 } author ? author : "anonymous";
        return $"{Subtype} by {who}: {Contents}";
    }
}
