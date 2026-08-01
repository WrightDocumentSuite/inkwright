namespace Inkwright.Annotations;

/// <summary>
/// Document-wide views over the comments in a file, for the common jobs that span pages: listing
/// every thread, gathering one reviewer's remarks, or stripping the lot before publishing.
/// </summary>
public static class PdfCommentExtensions
{
    extension(PdfDocument document)
    {
        /// <summary>Every comment in the document, page by page, replies excluded.</summary>
        public IEnumerable<PdfMarkupAnnotation> Comments
        {
            get
            {
                foreach (PdfPage page in document.Pages)
                {
                    foreach (PdfMarkupAnnotation comment in page.Annotations.Comments)
                        yield return comment;
                }
            }
        }

        /// <summary>Every annotation in the document, of any kind.</summary>
        public IEnumerable<PdfAnnotation> AllAnnotations
        {
            get
            {
                foreach (PdfPage page in document.Pages)
                {
                    foreach (PdfAnnotation annotation in page.Annotations)
                        yield return annotation;
                }
            }
        }

        /// <summary>The comments a particular person wrote.</summary>
        /// <param name="author">The name to match, exactly as it is stored.</param>
        public IEnumerable<PdfMarkupAnnotation> CommentsBy(string author)
        {
            ArgumentNullException.ThrowIfNull(author);

            foreach (PdfMarkupAnnotation comment in document.Comments)
            {
                if (string.Equals(comment.Author, author, StringComparison.Ordinal))
                    yield return comment;
            }
        }

        /// <summary>
        /// Reduces every comment in the document to a plain record: author, text, the words it
        /// covers, dates, review state and the replies beneath it. What a review report is built
        /// from, and what serialises to JSON without further work.
        /// </summary>
        public IReadOnlyList<PdfCommentInfo> ExportComments()
        {
            List<PdfCommentInfo> report = [];
            for (int i = 0; i < document.Pages.Count; i++)
                report.AddRange(document.Pages[i].Annotations.ExportComments(i));

            return report;
        }

        /// <summary>
        /// Burns every comment into the page it sits on, across the whole document. The marks stay
        /// visible everywhere; the annotations, and the discussion in them, are gone.
        /// </summary>
        /// <returns>How many comments were flattened.</returns>
        public int FlattenComments()
        {
            int flattened = 0;
            foreach (PdfPage page in document.Pages)
                flattened += page.Annotations.Flatten();

            return flattened;
        }

        /// <summary>
        /// Removes every comment from every page, leaving links and form fields alone. This is what
        /// a "publish a clean copy" step wants.
        /// </summary>
        /// <returns>How many annotations went, pop-ups and replies included.</returns>
        public int RemoveAllComments()
        {
            int removed = 0;
            foreach (PdfPage page in document.Pages)
                removed += page.Annotations.RemoveComments();

            return removed;
        }
    }
}
