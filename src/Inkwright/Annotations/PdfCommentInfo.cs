using Inkwright.Text;

namespace Inkwright.Annotations;

/// <summary>
/// A comment reduced to the facts a review report needs: who said what, about which words, when, and
/// what was decided. The shape is deliberately plain so it serialises straight to JSON or a table.
/// </summary>
/// <param name="PageIndex">Which page the comment sits on, counting from zero.</param>
/// <param name="Type">The kind of annotation.</param>
/// <param name="Author">Who wrote it.</param>
/// <param name="Contents">What it says.</param>
/// <param name="Subject">The short description of what it is about.</param>
/// <param name="QuotedText">The page text the comment covers, for marks that cover text.</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="ModifiedAt">When it was last changed.</param>
/// <param name="State">The review decision recorded for it.</param>
/// <param name="Bounds">Where it sits on the page.</param>
/// <param name="Replies">The answers to it, in order.</param>
public sealed record PdfCommentInfo(
    int PageIndex,
    PdfAnnotationType Type,
    string? Author,
    string? Contents,
    string? Subject,
    string? QuotedText,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt,
    PdfReviewState State,
    PdfRectangle Bounds,
    IReadOnlyList<PdfCommentInfo> Replies)
{
    /// <inheritdoc />
    public override string ToString()
    {
        string who = Author is { Length: > 0 } author ? author : "anonymous";
        string about = QuotedText is { Length: > 0 } quoted ? $" on \"{quoted}\"" : string.Empty;
        return $"p{PageIndex + 1} {Type} by {who}{about}: {Contents}";
    }
}

/// <summary>Reads the page text a comment refers to, and turns threads into plain records.</summary>
internal static class CommentReport
{
    /// <summary>Builds a record for a comment and everything written in reply to it.</summary>
    /// <param name="comment">The comment to describe.</param>
    /// <param name="pageIndex">Which page it is on.</param>
    /// <param name="letters">The glyphs of that page, or nothing to skip the quoted text.</param>
    internal static PdfCommentInfo Describe(
        PdfMarkupAnnotation comment, int pageIndex, IReadOnlyList<PdfLetter>? letters)
    {
        List<PdfCommentInfo> replies = [];
        foreach (PdfMarkupAnnotation reply in comment.Replies)
            replies.Add(Describe(reply, pageIndex, letters));

        return new PdfCommentInfo(
            pageIndex,
            comment.Type,
            comment.Author,
            comment.Contents,
            comment.Subject,
            letters is null ? null : Quote(comment, letters),
            comment.CreatedAt,
            comment.ModifiedAt,
            DeclaredState(comment) ?? comment.ReviewState,
            comment.Rectangle,
            replies);
    }

    /// <summary>The page text a comment covers, or nothing when it covers no text.</summary>
    /// <param name="comment">The comment to read under.</param>
    /// <param name="letters">The glyphs of the page the comment is on.</param>
    internal static string? Quote(PdfMarkupAnnotation comment, IReadOnlyList<PdfLetter> letters)
    {
        if (!QuotesText(comment.Type))
            return null;

        IReadOnlyList<PdfRectangle> areas = comment is PdfTextMarkupAnnotation markup && markup.Areas.Count > 0
            ? markup.Areas
            : [comment.Rectangle];

        List<PdfLetter> covered = [];
        foreach (PdfLetter letter in letters)
        {
            PdfPoint centre = TextSearch.BoundsOf(letter).Center;
            foreach (PdfRectangle area in areas)
            {
                if (!area.Contains(centre))
                    continue;

                covered.Add(letter);
                break;
            }
        }

        return covered.Count == 0 ? null : TextSearch.Compose(covered).Trim();
    }

    /// <summary>Whether any comment on the page needs the page text read.</summary>
    /// <param name="comments">The comments to inspect.</param>
    internal static bool NeedsText(IEnumerable<PdfMarkupAnnotation> comments)
    {
        foreach (PdfMarkupAnnotation comment in comments)
        {
            if (QuotesText(comment.Type))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether this kind of annotation is placed over the words it is about. A sticky note is
    /// anchored beside the text, a note written on the page carries its own words and a stamp lands
    /// on top of whatever is there — quoting from under any of those reports noise as evidence.
    /// </summary>
    private static bool QuotesText(PdfAnnotationType type) => type is
        PdfAnnotationType.Highlight or PdfAnnotationType.Underline or
        PdfAnnotationType.StrikeOut or PdfAnnotationType.Squiggly or
        PdfAnnotationType.Square or PdfAnnotationType.Circle or
        PdfAnnotationType.Polygon or PdfAnnotationType.PolyLine or
        PdfAnnotationType.Ink or PdfAnnotationType.Line;

    /// <summary>
    /// The decision a reply itself records, as opposed to the one its thread arrived at. Without
    /// this a review decision would be reported as an empty reply that decided nothing.
    /// </summary>
    private static PdfReviewState? DeclaredState(PdfMarkupAnnotation comment) =>
        comment is PdfTextAnnotation { IsStateReply: true } reply
            ? PdfMarkupAnnotation.ParseState(reply.Dictionary.GetTextString(AnnotationNames.State) ?? string.Empty)
            : null;
}
