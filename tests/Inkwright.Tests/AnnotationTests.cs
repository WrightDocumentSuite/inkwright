using System.Text;
using Inkwright.Annotations;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Fonts;
using Inkwright.Text;
using Xunit;

namespace Inkwright.Tests;

public class AnnotationTests
{
    [Fact]
    public void AddsACommentThatSurvivesASaveAndReload()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfTextAnnotation note = page.Annotations.AddNote(
            new PdfPoint(72, 700), "Check this figure against the source.", "Ada Lovelace", PdfNoteIcon.Comment);

        note.Subject = "Figures";

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfMarkupAnnotation comment = Assert.Single(reloaded.Pages[0].Annotations.Comments);

        Assert.Equal(PdfAnnotationType.Text, comment.Type);
        Assert.Equal("Check this figure against the source.", comment.Contents);
        Assert.Equal("Ada Lovelace", comment.Author);
        Assert.Equal("Figures", comment.Subject);
        Assert.NotNull(comment.CreatedAt);
        Assert.True(comment.IsPrinted);

        // The icon is drawn by us, so a reader that does not invent one still shows the note.
        Assert.NotNull(comment.Dictionary.GetDictionary(PdfName.Get("AP")));

        // A pop-up window is what carries the text in a reader's comment pane.
        Assert.Contains(reloaded.Pages[0].Annotations, annotation => annotation is PdfPopupAnnotation);
    }

    [Fact]
    public void MarksTheWordsTheCallerNamesRatherThanCoordinates()
    {
        using var document = PdfDocument.Create();
        PdfPage page = WriteSentence(document);

        IReadOnlyList<PdfTextMarkupAnnotation> marks = page.Annotations.MarkText(
            "brown fox", PdfAnnotationType.Highlight, "Nice turn of phrase", "Reviewer");

        PdfTextMarkupAnnotation mark = Assert.Single(marks);
        PdfRectangle area = Assert.Single(mark.Areas);

        IReadOnlyList<PdfTextMatch> matches = page.FindText("brown fox");
        Assert.Single(matches);
        Assert.True(area.Width > 30, $"the mark should span the phrase, but it is {area.Width:0.0} wide");
        Assert.True(area.Contains(matches[0].Bounds.Center), "the mark should sit over the words it marks");

        string appearance = AppearanceText(document, mark);
        Assert.Contains(" re", appearance, StringComparison.Ordinal);
        Assert.Contains("/Multiply", ExtGStateText(mark), StringComparison.Ordinal);
    }

    [Fact]
    public void EditsACommentInPlaceWithoutLeavingOldDrawingsBehind()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(100, 600), "First thought", "Reviewer");

        PdfObjectId appearance = AppearanceId(note);
        DateTimeOffset? created = note.CreatedAt;

        note.Contents = "Second thought, better expressed";
        note.Color = PdfColor.Parse("#4CAF50");
        note.Icon = PdfNoteIcon.Help;
        note.Rectangle = new PdfRectangle(300, 500, 322, 522);

        // Every edit redraws the icon; redrawing must overwrite the stream rather than orphan it.
        Assert.Equal(appearance, AppearanceId(note));

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        var edited = (PdfTextAnnotation)Assert.Single(reloaded.Pages[0].Annotations.Comments);

        Assert.Equal("Second thought, better expressed", edited.Contents);
        Assert.Equal(PdfNoteIcon.Help, edited.Icon);
        Assert.Equal(300, edited.Rectangle.Left, 3);
        Assert.Equal(created?.ToUnixTimeSeconds(), edited.CreatedAt?.ToUnixTimeSeconds());
        Assert.True(edited.ModifiedAt >= edited.CreatedAt);
    }

    [Fact]
    public void DeletesACommentWithItsPopupAndItsWholeThread()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "Please clarify", "Reviewer");
        PdfTextAnnotation answer = note.Reply("Rewritten in the next draft.", "Author");
        answer.Reply("Thanks.", "Reviewer");
        page.Annotations.AddLink(new PdfRectangle(72, 100, 200, 120), "https://example.org");

        Assert.Single(page.Annotations.Comments);
        Assert.Equal(3, note.Thread.Count);

        note.Delete();

        Assert.True(note.IsDeleted);
        Assert.Empty(page.Annotations.Comments);
        Assert.Single(page.Annotations);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfAnnotation survivor = Assert.Single(reloaded.Pages[0].Annotations);
        Assert.Equal(PdfAnnotationType.Link, survivor.Type);
    }

    [Fact]
    public void ThreadsRepliesAndRecordsWhoDecidedWhat()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "This table is wrong", "Reviewer");
        note.Reply("Which row?", "Author");
        note.SetReviewState(PdfReviewState.Accepted, "Author", "Fixed in revision 4.");

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfMarkupAnnotation comment = Assert.Single(reloaded.Pages[0].Annotations.Comments);

        Assert.Equal(2, comment.Replies.Count);
        Assert.All(comment.Replies, reply => Assert.True(reply.IsReply));
        Assert.Equal(PdfReviewState.Accepted, comment.ReviewState);
        Assert.Equal(PdfReviewState.None, comment.ReviewStateBy("Reviewer"));

        // A reply belongs in the thread, not on the page: its appearance is deliberately empty so
        // that no reader invents an icon for it.
        Assert.All(comment.Replies, reply => Assert.Empty(DrawnOperators(reply)));
    }

    [Fact]
    public void WritesEveryMarkupKindAndReadsThemBack()
    {
        using var document = PdfDocument.Create();
        PdfPage page = WriteSentence(document);

        page.Annotations.MarkText("quick", PdfAnnotationType.Underline);
        page.Annotations.MarkText("lazy", PdfAnnotationType.StrikeOut);
        page.Annotations.MarkText("dog", PdfAnnotationType.Squiggly);
        page.Annotations.AddFreeText(new PdfRectangle(72, 500, 320, 580), "A note written on the page itself.", "Editor");
        page.Annotations.AddRectangle(new PdfRectangle(72, 400, 220, 460), "Look here", "Editor", PdfColor.Red);
        page.Annotations.AddEllipse(new PdfRectangle(240, 400, 380, 460), fill: PdfColor.Parse("#FFF3C4"));
        page.Annotations.AddLine(new PdfPoint(72, 380), new PdfPoint(380, 380));
        page.Annotations.AddInk([[new PdfPoint(80, 300), new PdfPoint(120, 340), new PdfPoint(160, 300)]]);
        page.Annotations.AddStamp(new PdfRectangle(400, 640, 540, 700), "Approved");
        page.Annotations.AddAttachment(new PdfPoint(72, 200), "notes.txt", "the raw numbers"u8.ToArray());

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfAnnotationType[] kinds = [.. reloaded.Pages[0].Annotations.Comments.Select(comment => comment.Type)];

        Assert.Equal(
            [
                PdfAnnotationType.Underline, PdfAnnotationType.StrikeOut, PdfAnnotationType.Squiggly,
                PdfAnnotationType.FreeText, PdfAnnotationType.Square, PdfAnnotationType.Circle,
                PdfAnnotationType.Line, PdfAnnotationType.Ink, PdfAnnotationType.Stamp,
                PdfAnnotationType.FileAttachment,
            ],
            kinds);

        Assert.All(
            reloaded.Pages[0].Annotations.Comments,
            comment => Assert.NotNull(comment.Dictionary.GetDictionary(PdfName.Get("AP"))));

        var freeText = (PdfFreeTextAnnotation)reloaded.Pages[0].Annotations.Comments.First(
            comment => comment.Type == PdfAnnotationType.FreeText);
        Assert.Contains("A note written", AppearanceText(reloaded, freeText), StringComparison.Ordinal);

        var ink = (PdfInkAnnotation)reloaded.Pages[0].Annotations.Comments.First(
            comment => comment.Type == PdfAnnotationType.Ink);
        Assert.Equal(3, Assert.Single(ink.Strokes).Count);
    }

    [Fact]
    public void LeavesAnotherToolsDrawingAloneWhenTheTextIsEdited()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        page.Annotations.AddNote(new PdfPoint(72, 700), "Original", "Reviewer");

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfMarkupAnnotation comment = Assert.Single(reloaded.Pages[0].Annotations.Comments);
        PdfObjectId before = AppearanceId(comment);

        comment.Contents = "Edited by a tool that cannot draw the original icon";

        Assert.Equal(before, AppearanceId(comment));

        // The caller can still insist, and then the drawing is replaced.
        comment.RegenerateAppearance();
        Assert.NotEqual(before, AppearanceId(comment));
    }

    [Fact]
    public void AppendsCommentsToASignedFileWithoutTouchingWhatIsThere()
    {
        using var original = PdfDocument.Create();
        original.Pages.Add(PageSize.A4);
        byte[] bytes = original.ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);
        document.Pages[0].Annotations.AddNote(new PdfPoint(72, 700), "Added later", "Reviewer");

        using var buffer = new MemoryStream();
        document.SaveIncremental(buffer);
        byte[] updated = buffer.ToArray();

        Assert.True(updated.Length > bytes.Length);
        Assert.Equal(bytes, updated[..bytes.Length]);

        using PdfDocument reloaded = PdfDocument.Load(updated);
        Assert.Equal("Added later", Assert.Single(reloaded.Pages[0].Annotations.Comments).Contents);
    }

    [Fact]
    public void StripsEveryCommentButKeepsLinksAndFields()
    {
        using var document = PdfDocument.Create();
        PdfPage first = document.Pages.Add(PageSize.A4);
        PdfPage second = document.Pages.Add(PageSize.A4);

        first.Annotations.AddNote(new PdfPoint(72, 700), "One", "A").Reply("Two", "B");
        second.Annotations.AddHighlight([new PdfRectangle(72, 600, 300, 620)], "Three", "A");
        second.Annotations.AddLink(new PdfRectangle(72, 100, 200, 120), "https://example.org");

        int removed = document.RemoveAllComments();

        Assert.True(removed >= 5, $"expected the notes, the highlight and their pop-ups to go, removed {removed}");
        Assert.Empty(document.Comments);
        Assert.Single(document.AllAnnotations);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        Assert.Empty(reloaded.Comments);
        Assert.Equal(PdfAnnotationType.Link, Assert.Single(reloaded.AllAnnotations).Type);
    }

    [Fact]
    public void NoticesAnnotationsAddedBehindItsBack()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        page.Annotations.AddNote(new PdfPoint(72, 700), "One", "A");

        int before = page.Annotations.Count;

        // This is what the forms and signing packages do when they register a widget.
        var widget = new PdfDictionary(3);
        widget.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("Annot")));
        widget.Set(PdfName.Get("Subtype"), PdfValue.Name(PdfName.Get("Widget")));
        widget.Set(PdfName.Get("Rect"), PdfValue.Array(PdfArray.OfNumbers(10, 10, 100, 30)));
        page.Annotations.RawArray.Add(PdfValue.Reference(document.AddObject(PdfValue.Dictionary(widget))));

        Assert.Equal(before + 1, page.Annotations.Count);
        Assert.Contains(page.Annotations, annotation => annotation.Type == PdfAnnotationType.Widget);

        // The wrapper for the note has to survive the rebuild, or editing it would orphan its drawing.
        PdfMarkupAnnotation note = Assert.Single(page.Annotations.Comments);
        PdfObjectId appearance = AppearanceId(note);
        note.Contents = "Changed";
        Assert.Equal(appearance, AppearanceId(note));
    }

    [Fact]
    public void SeparatesParagraphsTheWayReadersExpect()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "first\r\nsecond\nthird", "A");

        // The specification asks for a carriage return between paragraphs and says a line feed is
        // not one; the API speaks line feeds because the rest of .NET does.
        Assert.Equal("first\rsecond\rthird", note.Dictionary.GetTextString(PdfName.Get("Contents")));
        Assert.Equal("first\nsecond\nthird", note.Contents);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        Assert.Equal("first\nsecond\nthird", Assert.Single(reloaded.Pages[0].Annotations.Comments).Contents);
    }

    [Fact]
    public void ImportingAPageLeavesTheRestOfTheDocumentBehind()
    {
        using var source = PdfDocument.Create();
        PdfPage first = source.Pages.Add(PageSize.A4);
        source.Pages.Add(PageSize.A4);
        source.Pages.Add(PageSize.A4);

        first.Annotations.AddNote(new PdfPoint(72, 700), "note", "A");
        first.Annotations.AddLink(new PdfRectangle(72, 100, 200, 120), "https://example.org").LinkTo(2);

        using var target = PdfDocument.Create();
        PdfPage imported = target.Pages.Import(first);

        Assert.Equal(3, imported.Annotations.Count);
        Assert.All(
            imported.Annotations,
            annotation => Assert.True(
                !annotation.Dictionary.TryGetRaw(PdfName.Get("P"), out PdfValue page) ||
                page.AsReference == imported.Id,
                "an imported annotation should point at the page it landed on"));

        // The link had nowhere to go, so it must not have dragged its target page along.
        var link = (PdfLinkAnnotation)imported.Annotations.First(a => a.Type == PdfAnnotationType.Link);
        Assert.Null(link.DestinationPageIndex);
        Assert.Equal(1, CountPageObjects(target));

        // Importing the whole document keeps the same link working.
        using var whole = PdfDocument.Create();
        whole.Pages.ImportAll(source);
        var carried = (PdfLinkAnnotation)whole.Pages[0].Annotations.First(a => a.Type == PdfAnnotationType.Link);
        Assert.Equal(2, carried.DestinationPageIndex);
    }

    [Fact]
    public void ReadsBackTheWordsAMarkCovers()
    {
        using var document = PdfDocument.Create();
        PdfPage page = WriteSentence(document);

        page.Annotations.MarkText("brown fox", PdfAnnotationType.Highlight, "Nice", "Reviewer");
        page.Annotations.AddNote(new PdfPoint(500, 700), "Beside the text", "Reviewer");

        IReadOnlyList<PdfCommentInfo> report = document.ExportComments();

        Assert.Equal(2, report.Count);
        Assert.Equal("brown fox", report[0].QuotedText);
        Assert.Equal(0, report[0].PageIndex);
        Assert.Equal("Reviewer", report[0].Author);

        // A sticky note sits beside the text, so quoting whatever is behind its icon would mislead.
        Assert.Null(report[1].QuotedText);
    }

    [Fact]
    public void ExportsThreadsAsPlainRecords()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "Wrong figure", "Reviewer");
        note.Reply("Fixed", "Author");
        note.SetReviewState(PdfReviewState.Completed, "Author");

        PdfCommentInfo comment = Assert.Single(document.ExportComments());

        Assert.Equal("Wrong figure", comment.Contents);
        Assert.Equal(PdfReviewState.Completed, comment.State);
        Assert.Equal(2, comment.Replies.Count);
        Assert.Equal("Fixed", comment.Replies[0].Contents);
    }

    [Fact]
    public void FlattensCommentsIntoThePage()
    {
        using var document = PdfDocument.Create();
        PdfPage page = WriteSentence(document);

        page.Annotations.MarkText("lazy dog", PdfAnnotationType.Highlight, "Check", "Reviewer")[0]
            .Reply("Will do", "Author");

        page.Annotations.AddLink(new PdfRectangle(72, 100, 200, 120), "https://example.org");

        int before = ContentOperations.Parse(page.GetContent()).Count;
        int flattened = document.FlattenComments();

        Assert.Equal(1, flattened);
        Assert.Empty(page.Annotations.Comments);

        // The link is not a comment, so flattening leaves it clickable.
        Assert.Single(page.Annotations);

        ContentOperations operations = ContentOperations.Parse(page.GetContent());
        Assert.True(operations.Count > before, "flattening should have added operators to the page");
        Assert.Contains(
            Enumerable.Range(0, operations.Count),
            i => operations.GetOperator(i) == PdfOperator.DrawXObject);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        Assert.Empty(reloaded.Comments);
        Assert.NotNull(reloaded.Pages[0].Resources.GetDictionary(PdfName.Get("XObject")));
    }

    [Fact]
    public void TakesTheAttachedFileAwayWithThePin()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        page.Annotations.AddAttachment(new PdfPoint(72, 200), "notes.txt", "the raw numbers"u8.ToArray());

        Assert.Single(EmbeddedFileNames(document));

        document.RemoveAllComments();

        Assert.Empty(EmbeddedFileNames(document));

        using PdfDocument reloaded = PdfDocument.Load(
            Save(document, new PdfSaveOptions { RemoveUnusedObjects = true }));

        Assert.Empty(EmbeddedFileNames(reloaded));
        Assert.DoesNotContain("raw numbers", Encoding.Latin1.GetString(reloaded.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void WritesTheReviewShapesAcrobatKnows()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        page.Annotations.AddPolygon(
            [new PdfPoint(80, 600), new PdfPoint(200, 660), new PdfPoint(260, 580)],
            "Around this", "Reviewer", fill: PdfColor.Parse("#FFF3C4"));

        page.Annotations.AddPolyLine([new PdfPoint(80, 500), new PdfPoint(160, 540), new PdfPoint(240, 500)]);
        page.Annotations.AddCaret(new PdfPoint(300, 420), "Insert a comma", "Reviewer");
        page.Annotations.AddLine(new PdfPoint(80, 380), new PdfPoint(300, 380))
            .SetEndings(PdfLineEnding.None, PdfLineEnding.ClosedArrow);

        page.Annotations.AddCallout(
            new PdfRectangle(320, 620, 520, 680), new PdfPoint(240, 590), "Check this corner", "Reviewer");

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfMarkupAnnotation[] comments = [.. reloaded.Pages[0].Annotations.Comments];

        var polygon = (PdfPolygonAnnotation)comments[0];
        Assert.True(polygon.IsClosed);
        Assert.Equal(3, polygon.Vertices.Count);

        var polyline = (PdfPolygonAnnotation)comments[1];
        Assert.False(polyline.IsClosed);

        Assert.IsType<PdfCaretAnnotation>(comments[2]);
        Assert.Equal(PdfLineEnding.ClosedArrow, ((PdfLineAnnotation)comments[3]).EndEnding);

        var callout = (PdfFreeTextAnnotation)comments[4];
        Assert.Equal(2, callout.CalloutLine.Count);
        Assert.Equal(240, callout.CalloutLine[0].X, 3);
        Assert.Equal(320, callout.TextBox.Left, 3);
        Assert.Contains("Check this", AppearanceText(reloaded, callout), StringComparison.Ordinal);

        Assert.All(comments, comment => Assert.NotEmpty(DrawnOperators(comment)));
    }

    [Fact]
    public void ReadsCommentsWrittenByOtherTools()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "The reference corpus is not present.");

        int filesWithComments = 0;
        int comments = 0;
        int replies = 0;

        foreach (string file in Corpus.Files)
        {
            if (filesWithComments >= 40)
                break;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                continue;
            }

            using PdfDocument? document = TryLoad(bytes);
            if (document is null)
                continue;

            bool found = false;
            foreach (PdfMarkupAnnotation comment in Sample(document))
            {
                found = true;
                comments++;
                replies += comment.Replies.Count;

                Assert.NotEqual(PdfAnnotationType.Unknown, comment.Type);
                Assert.False(comment.Rectangle.IsEmpty && comment.Type == PdfAnnotationType.Highlight);
                _ = comment.Contents;
                _ = comment.Author;
                _ = comment.CreatedAt;
                _ = comment.ReviewState;
            }

            if (found)
                filesWithComments++;
        }

        Assert.True(filesWithComments > 5, $"only {filesWithComments} corpus files carried comments");
        Assert.True(comments > 20, $"only {comments} comments were read from the corpus");
        Assert.True(replies >= 0);
    }

    [Fact]
    public void EditsACommentInAFileWrittenByAnotherToolAndSavesIncrementally()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "The reference corpus is not present.");

        foreach (string file in Corpus.Files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            using PdfDocument? document = TryLoad(bytes);
            if (document is null)
                continue;

            PdfMarkupAnnotation? comment = null;
            foreach (PdfMarkupAnnotation candidate in Sample(document))
            {
                comment = candidate;
                break;
            }

            if (comment is null || comment.Page.Annotations.Count > 200)
                continue;

            comment.Contents = "Edited by Inkwright";
            comment.Author = "Inkwright";
            int before = comment.Page.Annotations.Count;
            comment.Delete();

            using var buffer = new MemoryStream();
            document.SaveIncremental(buffer);

            using PdfDocument reloaded = PdfDocument.Load(buffer.ToArray());
            Assert.True(
                reloaded.Pages[0].Annotations.Count <= before,
                $"{Path.GetFileName(file)} kept the deleted comment");

            return;
        }

        Assert.Fail("no corpus file carried a comment to edit");
    }

    private static IEnumerable<PdfMarkupAnnotation> Sample(PdfDocument document)
    {
        int pages = Math.Min(document.Pages.Count, 5);
        for (int i = 0; i < pages; i++)
        {
            IEnumerable<PdfMarkupAnnotation> comments;
            try
            {
                comments = [.. document.Pages[i].Annotations.Comments];
            }
            catch (PdfFormatException)
            {
                continue;
            }

            foreach (PdfMarkupAnnotation comment in comments)
                yield return comment;
        }
    }

    private static PdfDocument? TryLoad(byte[] bytes)
    {
        try
        {
            return PdfDocument.Load(bytes, Corpus.ReadOptions);
        }
        catch (Exception exception) when (exception is PdfFormatException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static PdfPage WriteSentence(PdfDocument document)
    {
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfFont font = document.Fonts.Helvetica;

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("The quick brown fox jumps over the lazy dog.", 72, 720, font, 16);
            canvas.Commit();
        }

        return page;
    }

    private static byte[] Save(PdfDocument document, PdfSaveOptions options)
    {
        using var buffer = new MemoryStream();
        document.Save(buffer, options);
        return buffer.ToArray();
    }

    private static List<string> EmbeddedFileNames(PdfDocument document)
    {
        List<string> names = [];
        PdfArray? tree = document.Catalog.GetDictionary(PdfName.Get("Names"))?
            .GetDictionary(PdfName.Get("EmbeddedFiles"))?
            .GetArray(PdfName.Get("Names"));

        for (int i = 0; tree is not null && i < tree.Count; i += 2)
            names.Add(tree.Get(i).AsTextString ?? string.Empty);

        return names;
    }

    private static int CountPageObjects(PdfDocument document)
    {
        byte[] bytes = Save(document, new PdfSaveOptions { UseObjectStreams = false, Compress = false });
        return Encoding.Latin1.GetString(bytes).Split("/MediaBox").Length - 1;
    }

    private static PdfObjectId AppearanceId(PdfAnnotation annotation)
    {
        PdfDictionary appearance = annotation.Dictionary.GetDictionary(PdfName.Get("AP"))!;
        return appearance.TryGetRaw(PdfName.Get("N"), out PdfValue value) && value.IsReference
            ? value.AsReference
            : PdfObjectId.None;
    }

    /// <summary>The marking operators in an annotation's appearance, ignoring bare state changes.</summary>
    private static string DrawnOperators(PdfAnnotation annotation)
    {
        PdfDictionary? appearance = annotation.Dictionary.GetDictionary(PdfName.Get("AP"));
        if (appearance?.GetStream(PdfName.Get("N")) is not { } stream)
            return string.Empty;

        string content = Encoding.Latin1.GetString(stream.DecodedSpan());
        return content.Replace("q", string.Empty, StringComparison.Ordinal)
                      .Replace("Q", string.Empty, StringComparison.Ordinal)
                      .Trim();
    }

    private static string AppearanceText(PdfDocument document, PdfAnnotation annotation)
    {
        PdfStream stream = annotation.Dictionary.GetDictionary(PdfName.Get("AP"))!.GetStream(PdfName.Get("N"))!;
        return Encoding.Latin1.GetString(stream.DecodedSpan());
    }

    private static string ExtGStateText(PdfAnnotation annotation)
    {
        PdfStream stream = annotation.Dictionary.GetDictionary(PdfName.Get("AP"))!.GetStream(PdfName.Get("N"))!;
        PdfDictionary? resources = stream.Dictionary.GetDictionary(PdfName.Get("Resources"));
        PdfDictionary? states = resources?.GetDictionary(PdfName.Get("ExtGState"));

        var text = new StringBuilder();
        if (states is null)
            return string.Empty;

        foreach (PdfName key in states.Keys)
        {
            if (states.GetDictionary(key) is not { } state)
                continue;

            foreach (PdfName entry in state.Keys)
                text.Append('/').Append(state.GetName(entry)?.Value ?? entry.Value).Append(' ');
        }

        return text.ToString();
    }

    [Fact]
    public void InternalLinkOverloadAndGoToActionResolveDestinationPageIndex()
    {
        using var document = PdfDocument.Create();
        PdfPage first = document.Pages.Add(PageSize.A4);
        PdfPage second = document.Pages.Add(PageSize.A4);

        PdfLinkAnnotation directLink = first.Annotations.AddLink(
            new PdfRectangle(72, 700, 180, 720),
            destinationPageIndex: 1);
        Assert.Equal(1, directLink.DestinationPageIndex);
        Assert.Null(directLink.Uri);

        // Also verify /A << /S /GoTo /D [ pageRef /Fit ] >> action dictionaries.
        PdfLinkAnnotation actionLink = first.Annotations.AddLink(
            new PdfRectangle(72, 660, 180, 680),
            "https://example.org");
        var destination = new PdfArray(2);
        destination.Add(PdfValue.Reference(second.Id));
        destination.Add(PdfValue.Name(PdfName.Get("Fit")));
        var action = new PdfDictionary(3);
        action.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("Action")));
        action.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get("GoTo")));
        action.Set(PdfName.Get("D"), PdfValue.Array(destination));
        actionLink.Dictionary.Remove(PdfName.Get("Dest"));
        actionLink.Dictionary.Set(PdfName.Get("A"), PdfValue.Dictionary(action));

        Assert.Equal(1, actionLink.DestinationPageIndex);
    }
}
