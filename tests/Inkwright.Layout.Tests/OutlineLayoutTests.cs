using Inkwright.Cos;
using Xunit;

namespace Inkwright.Layout.Tests;

/// <summary>
/// Bookmarks declared from the composer: the outline is built from where the content actually
/// flowed, so a heading and its bookmark stay on the same page whatever the pagination does.
/// </summary>
public class OutlineLayoutTests
{
    [Fact]
    public void BuildsAnOutlineFromComposerBookmarks()
    {
        byte[] file = Report.Create()
            .Page(page =>
            {
                page.Content().Column(column =>
                {
                    column.Item().Bookmark("Chapter 1").Text("Chapter 1");
                    column.Item().Bookmark("Section 1.1", 1).Text("Section 1.1");
                    column.Item().Bookmark("Chapter 2").Text("Chapter 2");
                });
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(file);
        PdfOutline outline = document.Outline!;

        Assert.Equal(2, outline.Count);
        Assert.Equal("Chapter 1", outline[0].Title);
        Assert.Equal("Chapter 2", outline[1].Title);

        PdfOutlineItem section = Assert.Single(outline[0].Children);
        Assert.Equal("Section 1.1", section.Title);
    }

    /// <summary>
    /// ISO 14289-2 §8.8 wants intra-document destinations to name structure elements, so a
    /// bookmark placed in a tagged section points at the element its content produced rather than
    /// at a page that reflowing would invalidate.
    /// </summary>
    [Fact]
    public void PointsBookmarksAtStructureElementsWhenTheSectionIsTagged()
    {
        byte[] file = Report.Create()
            .Page(page =>
            {
                page.Tagged("en-GB");
                page.Content().Column(column =>
                {
                    column.Item().Bookmark("Chapter 1").Text("Chapter 1");
                    column.Item().Bookmark("Chapter 2").Text("Chapter 2");
                });
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(file);

        foreach (PdfOutlineItem item in document.Outline!)
        {
            PdfArray destination = document.Resolve(item.Dictionary.Get(PdfName.Get("Dest"))).AsArray!;
            PdfDictionary target = document.Resolve(destination.Get(0)).AsDictionary!;
            Assert.Equal("StructElem", target.GetName(PdfNames.Type)?.Value);
        }

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);
        Assert.DoesNotContain(problems, problem => problem.Clause == "8.8");
    }

    /// <summary>An untagged report has no structure to point at, so it keeps page destinations.</summary>
    [Fact]
    public void KeepsPageDestinationsWhenThereIsNoStructureToPointAt()
    {
        byte[] file = Report.Create()
            .Page(page => page.Content().Bookmark("Only chapter").Text("Only chapter"))
            .ToArray();

        using PdfDocument document = PdfDocument.Load(file);

        PdfOutlineItem item = document.Outline![0];
        PdfArray destination = document.Resolve(item.Dictionary.Get(PdfName.Get("Dest"))).AsArray!;
        PdfDictionary target = document.Resolve(destination.Get(0)).AsDictionary!;
        Assert.Equal("Page", target.GetName(PdfNames.Type)?.Value);
    }

    [Fact]
    public void PointsEachBookmarkAtThePageItsContentLandsOn()
    {
        byte[] file = Report.Create()
            .Page(page =>
            {
                page.Margin(20);
                page.Content().Column(column =>
                {
                    column.Item().Bookmark("Start").Text("On the first page");
                    column.Item().PageBreak();
                    column.Item().Bookmark("After the break").Text("On the second page");
                });
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(file);
        PdfOutline outline = document.Outline!;

        Assert.Equal(2, outline.Count);

        // The two bookmarks resolve to two different pages, in reading order.
        PdfObjectId firstPage = document.Pages[0].Id;
        PdfObjectId secondPage = document.Pages[1].Id;

        Assert.Equal(firstPage.Number, DestinationPage(document, outline[0]));
        Assert.Equal(secondPage.Number, DestinationPage(document, outline[1]));
    }

    [Fact]
    public void PlacesTheBookmarkNearTheTopOfTheContentItMarks()
    {
        byte[] file = Report.Create()
            .Page(page =>
            {
                page.Size(PageSize.A4);
                page.Margin(50);
                page.Content().Column(column =>
                {
                    column.Item().Height(200).Text("A tall block above the heading");
                    column.Item().Bookmark("Halfway down").Text("The heading");
                });
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(file);
        PdfArray destination = Destination(document, document.Outline![0]);

        // The heading sits below a 200-point block and a 50-point margin, so its top is well under
        // the page height (A4 is about 842 points) and well above the bottom.
        double top = destination.Get(3).AsNumber;
        Assert.InRange(top, 500, 600);
    }

    // The raw indexer is used rather than Get, which would resolve the page reference into the
    // page dictionary and lose the object number the test is checking.
    private static int DestinationPage(PdfDocument document, PdfOutlineItem item) =>
        Destination(document, item)[0].AsReference.Number;

    private static PdfArray Destination(PdfDocument document, PdfOutlineItem item) =>
        item.Dictionary.GetArray(PdfName.Get("Dest")) ?? throw new Xunit.Sdk.XunitException("The bookmark has no destination.");
}
