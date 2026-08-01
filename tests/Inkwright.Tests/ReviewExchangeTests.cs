using System.Text;
using Inkwright.Annotations;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Sending a round of review out and taking it back in. The tests are written as the exchange is
/// actually used: one reviewer marks up a copy, the marks travel on their own, and a second copy
/// has to end up saying the same thing — twice over, because the same round often arrives twice.
/// </summary>
public class ReviewExchangeTests
{
    [Fact]
    public void CarriesAWholeRoundOfReviewThroughXfdf()
    {
        using PdfDocument reviewed = Reviewed();
        string xfdf = reviewed.ExportXfdf();

        using PdfDocument clean = Blank();
        PdfReviewImportResult result = clean.ImportXfdf(xfdf);

        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Warnings);

        // Saving and reopening is the real test: what matters is what the file says, not what the
        // model happened to be holding.
        using PdfDocument reopened = PdfDocument.Load(clean.ToArray());
        AssertSameReview(reviewed, reopened);
    }

    [Fact]
    public void CarriesAWholeRoundOfReviewThroughFdf()
    {
        using PdfDocument reviewed = Reviewed();
        byte[] fdf = reviewed.ExportFdf();

        Assert.StartsWith("%FDF-1.2", Encoding.Latin1.GetString(fdf), StringComparison.Ordinal);

        using PdfDocument clean = Blank();
        PdfReviewImportResult result = clean.ImportFdf(fdf);

        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Warnings);

        using PdfDocument reopened = PdfDocument.Load(clean.ToArray());
        AssertSameReview(reviewed, reopened);

        // FDF is PDF syntax, so the drawings travel too rather than being reconstructed.
        Assert.All(
            reopened.Comments,
            comment => Assert.NotNull(comment.Dictionary.GetDictionary(PdfName.Get("AP"))));
    }

    [Fact]
    public void TakingTheSameRoundTwiceChangesNothingTheSecondTime()
    {
        using PdfDocument reviewed = Reviewed();
        string xfdf = reviewed.ExportXfdf();

        using PdfDocument clean = Blank();
        PdfReviewImportResult first = clean.ImportXfdf(xfdf);
        int annotations = clean.Pages[0].Annotations.Count;

        PdfReviewImportResult second = clean.ImportXfdf(xfdf);

        Assert.Equal(first.Added, second.Updated);
        Assert.Equal(0, second.Added);
        Assert.Equal(annotations, clean.Pages[0].Annotations.Count);
        AssertSameReview(reviewed, clean);
    }

    [Fact]
    public void RewritesACommentThatCameBackChanged()
    {
        using PdfDocument reviewed = Reviewed();
        using PdfDocument clean = Blank();
        clean.ImportXfdf(reviewed.ExportXfdf());

        PdfMarkupAnnotation note = First(reviewed, PdfAnnotationType.Text);
        note.Contents = "Second thoughts: the rate is right after all";
        note.Color = PdfColor.Parse("#4CAF50");

        PdfReviewImportResult result = clean.ImportXfdf(reviewed.ExportXfdf());
        PdfMarkupAnnotation updated = First(clean, PdfAnnotationType.Text);

        Assert.Equal(0, result.Added);
        Assert.Equal("Second thoughts: the rate is right after all", updated.Contents);
        Assert.Equal(PdfColor.Parse("#4CAF50"), updated.Color);
    }

    [Fact]
    public void KeepsThreadsAndDecisionsTogether()
    {
        using PdfDocument reviewed = Reviewed();
        using PdfDocument clean = Blank();
        clean.ImportXfdf(reviewed.ExportXfdf());

        PdfMarkupAnnotation note = First(clean, PdfAnnotationType.Text);

        Assert.Equal(2, note.Replies.Count);
        Assert.Equal("Which rate?", note.Replies[0].Contents);
        Assert.Equal(PdfReviewState.Completed, note.ReviewState);

        // A reply belongs in the thread, not on the page, so it must not have been drawn.
        Assert.All(note.Replies, reply => Assert.Empty(Drawing(reply)));
    }

    [Fact]
    public void SendsTheAttachedFileWithThePin()
    {
        using PdfDocument reviewed = Reviewed();
        using PdfDocument clean = Blank();
        clean.ImportXfdf(reviewed.ExportXfdf());

        var pin = (PdfFileAttachmentAnnotation)First(clean, PdfAnnotationType.FileAttachment);
        Assert.Equal("figures.txt", pin.FileName);

        PdfStream file = pin.Dictionary
            .GetDictionary(PdfName.Get("FS"))!
            .GetDictionary(PdfName.Get("EF"))!
            .GetStream(PdfName.Get("F"))!;

        Assert.Equal("the raw numbers", Encoding.UTF8.GetString(file.DecodedSpan()));
    }

    [Fact]
    public void ReadsTheXfdfAnotherToolWouldSend()
    {
        using PdfDocument document = Blank();

        PdfReviewImportResult result = document.ImportXfdf(AcrobatStyleXfdf);

        Assert.Equal(2, result.Added);
        Assert.Empty(result.Warnings);

        PdfMarkupAnnotation highlight = First(document, PdfAnnotationType.Highlight);
        Assert.Equal("Prija Raman", highlight.Author);
        Assert.Equal("Worth quoting", highlight.Contents);
        Assert.Equal(PdfColor.Parse("#FFFF00"), highlight.Color);
        Assert.Equal(0.5, highlight.Opacity, 3);
        Assert.True(highlight.IsPrinted);
        Assert.Equal(new DateTimeOffset(2026, 7, 26, 14, 3, 22, TimeSpan.FromHours(3)), highlight.CreatedAt);

        // Two lines of the paragraph were marked, so two quadrilaterals came across.
        Assert.Equal(2, ((PdfTextMarkupAnnotation)highlight).Areas.Count);
        Assert.Single(highlight.Replies);
        Assert.Equal("Agreed", highlight.Replies[0].Contents);
    }

    [Fact]
    public void ReadsAHandWrittenFdfWithNoCrossReferenceTable()
    {
        using PdfDocument document = Blank();

        PdfReviewImportResult result = document.ImportFdf(Encoding.ASCII.GetBytes(HandWrittenFdf));
        PdfMarkupAnnotation note = First(document, PdfAnnotationType.Text);

        Assert.Equal(1, result.Added);
        Assert.Equal("Check the total", note.Contents);
        Assert.Equal("Tom Weir", note.Author);

        // Nothing was drawn for it in the payload, so the library drew the icon itself.
        Assert.NotEmpty(Drawing(note));
    }

    [Fact]
    public void SaysWhatItCouldNotTakeInsteadOfRefusingTheLot()
    {
        using PdfDocument document = Blank();

        PdfReviewImportResult result = document.ImportXfdf("""
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/">
              <ids original="0123456789ABCDEF0123456789ABCDEF"/>
              <annots>
                <text page="7" rect="10,10,30,30" name="offpage"><contents>Nowhere</contents></text>
                <sound page="0" rect="10,10,30,30" name="noisy"/>
                <text page="0" rect="10,10,30,30" name="reply" inreplyto="missing"><contents>Hm</contents></text>
                <text page="0" rect="40,40,60,60" name="fine"><contents>Good</contents></text>
              </annots>
            </xfdf>
            """);

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Skipped);

        Assert.Contains(result.Warnings, w => w.Contains("page 8", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("<sound>", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("'missing'", StringComparison.Ordinal));

        // The reply lost its parent, so it stands on its own rather than vanishing.
        Assert.Equal(2, document.Comments.Count());
    }

    [Fact]
    public void NoticesWhenTheReviewWasWrittenAgainstAnotherFile()
    {
        using PdfDocument saved = PdfDocument.Load(Reviewed().ToArray());
        string xfdf = saved.ExportXfdf();

        // The identity of the file the comments came off travels with them, so a document can tell
        // whether it is the one that was reviewed.
        Assert.Contains("<ids", xfdf, StringComparison.Ordinal);
        Assert.Empty(saved.ImportXfdf(xfdf).Warnings);

        using PdfDocument stranger = PdfDocument.Load(Blank().ToArray());
        Assert.Contains(
            stranger.ImportXfdf(xfdf).Warnings,
            warning => warning.Contains("different file", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesSomethingThatIsNotAReviewAtAll()
    {
        using PdfDocument document = Blank();

        Assert.Throws<PdfFormatException>(() => document.ImportXfdf("<html><body>no</body></html>"));
        Assert.Throws<PdfFormatException>(() => document.ImportXfdf("<xfdf"));
    }

    [Fact]
    public void PicksTheFormatOutOfTheFileItself()
    {
        using PdfDocument reviewed = Reviewed();
        string directory = Directory.CreateTempSubdirectory("inkwright-review").FullName;

        try
        {
            string xfdf = Path.Combine(directory, "round.xfdf");
            string fdf = Path.Combine(directory, "round.fdf");
            reviewed.ExportReview(xfdf);
            reviewed.ExportReview(fdf);

            using PdfDocument fromXml = Blank();
            using PdfDocument fromCos = Blank();

            Assert.True(fromXml.ImportReview(xfdf).Added > 0);
            Assert.True(fromCos.ImportReview(fdf).Added > 0);
            Assert.Equal(fromXml.ExportComments().Count, fromCos.ExportComments().Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SendsCommentsHoldingCharactersXmlCannotCarry()
    {
        using PdfDocument document = Blank();
        document.Pages[0].Annotations.AddNote(
            new PdfPoint(72, 700),
            "a bell \u0007 and a vertical tab \u000B",
            author: "Reviewer \u0001");

        // Control codes have no XML spelling at all, so a writer that passed them through would
        // throw rather than produce a file. ISO 19444-1 5.8.2 spells them as octal escapes.
        string xfdf = document.ExportXfdf();
        Assert.Contains(@"\007", xfdf, StringComparison.Ordinal);
        Assert.Contains(@"\013", xfdf, StringComparison.Ordinal);

        using PdfDocument clean = Blank();
        clean.ImportXfdf(xfdf);

        PdfMarkupAnnotation note = clean.Pages[0].Annotations.Comments.First();
        Assert.Equal("a bell \u0007 and a vertical tab \u000B", note.Contents);
        Assert.Equal("Reviewer \u0001", note.Author);
    }

    [Fact]
    public void KeepsTabsAndLineBreaksAsThemselves()
    {
        using PdfDocument document = Blank();
        document.Pages[0].Annotations.AddNote(new PdfPoint(72, 700), "first line\nsecond\tindented");

        string xfdf = document.ExportXfdf();

        using PdfDocument clean = Blank();
        clean.ImportXfdf(xfdf);

        // XML carries these three, so they travel as themselves rather than as escapes.
        Assert.DoesNotContain(@"\01", xfdf, StringComparison.Ordinal);
        Assert.Equal("first line\nsecond\tindented", clean.Pages[0].Annotations.Comments.First().Contents);
    }

    /// <summary>A page with a sentence on it and no comments: the far end of an exchange.</summary>
    private static PdfDocument Blank()
    {
        var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("The quick brown fox jumps over the lazy dog.", 72, 720, document.Fonts.Helvetica, 16);
            canvas.Commit();
        }

        return document;
    }

    /// <summary>The same page after a reviewer has been over it with every tool they have.</summary>
    private static PdfDocument Reviewed()
    {
        PdfDocument document = Blank();
        PdfAnnotationCollection annotations = document.Pages[0].Annotations;

        PdfTextAnnotation note = annotations.AddNote(
            new PdfPoint(505, 700), "Check the rate in clause four", "Prija Raman", PdfNoteIcon.Help);

        note.Reply("Which rate?", "Tom Weir");
        note.SetReviewState(PdfReviewState.Completed, "Tom Weir", "Fixed in the fourth draft");

        annotations.MarkText("brown fox", PdfAnnotationType.Highlight, "Nice turn of phrase", "Prija Raman");
        annotations.AddFreeText(new PdfRectangle(72, 500, 320, 580), "Needs a new wording", "Prija Raman");
        annotations.AddRectangle(new PdfRectangle(45, 400, 550, 460), "Look here", "Prija Raman", PdfColor.Red);
        annotations.AddEllipse(new PdfRectangle(240, 300, 380, 360), fill: PdfColor.Parse("#FFF3C4"));
        annotations.AddLine(new PdfPoint(72, 280), new PdfPoint(380, 280))
            .SetEndings(PdfLineEnding.None, PdfLineEnding.ClosedArrow);

        annotations.AddInk([[new PdfPoint(80, 200), new PdfPoint(120, 240), new PdfPoint(160, 200)]]);
        annotations.AddPolygon([new PdfPoint(400, 200), new PdfPoint(480, 250), new PdfPoint(520, 180)]);
        annotations.AddStamp(new PdfRectangle(400, 640, 540, 700), "Approved");
        annotations.AddAttachment(new PdfPoint(72, 120), "figures.txt", "the raw numbers"u8.ToArray());

        return document;
    }

    /// <summary>
    /// Compares two documents by what a review report would say about them, which is the only
    /// comparison that matters: the same comments, by the same people, saying the same things.
    /// </summary>
    private static void AssertSameReview(PdfDocument expected, PdfDocument actual)
    {
        IReadOnlyList<PdfCommentInfo> before = expected.ExportComments();
        IReadOnlyList<PdfCommentInfo> after = actual.ExportComments();

        Assert.Equal(before.Count, after.Count);

        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Type, after[i].Type);
            Assert.Equal(before[i].Author, after[i].Author);
            Assert.Equal(before[i].Contents, after[i].Contents);
            Assert.Equal(before[i].Subject, after[i].Subject);
            Assert.Equal(before[i].State, after[i].State);
            Assert.Equal(before[i].PageIndex, after[i].PageIndex);
            Assert.Equal(before[i].QuotedText, after[i].QuotedText);
            Assert.Equal(before[i].Replies.Count, after[i].Replies.Count);
            Assert.Equal(before[i].Bounds.Left, after[i].Bounds.Left, 2);
            Assert.Equal(before[i].Bounds.Top, after[i].Bounds.Top, 2);
            Assert.Equal(
                before[i].CreatedAt?.ToUnixTimeSeconds(),
                after[i].CreatedAt?.ToUnixTimeSeconds());
        }
    }

    private static PdfMarkupAnnotation First(PdfDocument document, PdfAnnotationType type)
    {
        foreach (PdfMarkupAnnotation comment in document.Comments)
        {
            if (comment.Type == type)
                return comment;
        }

        Assert.Fail($"the document carries no {type} comment");
        throw new InvalidOperationException();
    }

    /// <summary>What an annotation actually draws, with the bare state changes stripped out.</summary>
    private static string Drawing(PdfAnnotation annotation)
    {
        if (annotation.Dictionary.GetDictionary(PdfName.Get("AP"))?.GetStream(PdfName.Get("N")) is not { } stream)
            return string.Empty;

        return Encoding.Latin1.GetString(stream.DecodedSpan())
            .Replace("q", string.Empty, StringComparison.Ordinal)
            .Replace("Q", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private const string AcrobatStyleXfdf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
          <f href="contract.pdf"/>
          <annots>
            <highlight page="0" rect="70.5,714.2,190.8,736.0"
                       coords="72,736,188,736,72,716,188,716,72,714,140,714,72,700,140,700"
                       color="#FFFF00" opacity="0.5" flags="print" name="acr-1"
                       title="Prija Raman" subject="Wording"
                       date="D:20260726140500+03'00'" creationdate="D:20260726140322+03'00'">
              <contents>Worth quoting</contents>
              <popup page="0" rect="200,650,380,740" open="no"/>
            </highlight>
            <text page="0" rect="500,700,522,722" name="acr-2" title="Tom Weir"
                  inreplyto="acr-1" replyType="reply" icon="Comment"
                  state="Accepted" statemodel="Review"
                  date="D:20260726141000+03'00'" creationdate="D:20260726141000+03'00'">
              <contents>Agreed</contents>
            </text>
          </annots>
        </xfdf>
        """;

    private const string HandWrittenFdf = """
        %FDF-1.2
        1 0 obj
        << /FDF << /Annots [ 2 0 R ] >> >>
        endobj
        2 0 obj
        << /Type /Annot /Subtype /Text /Page 0 /Rect [ 500 700 522 722 ]
           /Contents (Check the total) /T (Tom Weir) /NM (hand-1) /F 4 /Name /Comment >>
        endobj
        trailer
        << /Root 1 0 R >>
        %%EOF
        """;
}
