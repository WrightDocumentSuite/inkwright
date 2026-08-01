using Inkwright.Cos;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// The document outline of ISO 32000-1 §12.3.3: the bookmark tree a reader shows in its navigation
/// pane, read and written through the document model.
/// </summary>
public class OutlineTests
{
    [Fact]
    public void AddsTopLevelBookmarksAndReadsThemBack()
    {
        using var document = PdfDocument.Create();
        PdfPage first = document.Pages.Add(PageSize.A4);
        PdfPage second = document.Pages.Add(PageSize.A4);

        PdfOutline outline = PdfOutline.OpenOrCreate(document);
        outline.Add("Introduction", PdfDestination.FitPage(first));
        outline.Add("Conclusion", PdfDestination.XyzLocation(second, top: 800));

        byte[] file = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(file);

        PdfOutline reloaded = reopened.Outline!;
        Assert.Equal(2, reloaded.Count);
        Assert.Equal("Introduction", reloaded[0].Title);
        Assert.Equal("Conclusion", reloaded[1].Title);
    }

    [Fact]
    public void NestsChildrenAndWritesTheLinkedList()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfOutline outline = PdfOutline.OpenOrCreate(document);
        PdfOutlineItem chapter = outline.Add("Chapter 1", PdfDestination.FitPage(page));
        chapter.Add("Section 1.1", PdfDestination.FitPage(page));
        chapter.Add("Section 1.2", PdfDestination.FitPage(page));

        byte[] file = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(file);

        PdfOutlineItem reloadedChapter = Assert.Single(reopened.Outline!);
        Assert.Equal(2, reloadedChapter.Children.Count);
        Assert.Equal("Section 1.1", reloadedChapter.Children[0].Title);
        Assert.Equal("Section 1.2", reloadedChapter.Children[1].Title);

        // The root records how many rows show when everything is open: the chapter and its two
        // sections make three.
        PdfDictionary root = reopened.Catalog.GetDictionary(PdfNames.Outlines)!;
        Assert.Equal(3, root.GetInteger(PdfNames.Count));
    }

    [Fact]
    public void ChainsSiblingsWithPrevNextFirstLast()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfOutline outline = PdfOutline.OpenOrCreate(document);
        outline.Add("One", PdfDestination.FitPage(page));
        outline.Add("Two", PdfDestination.FitPage(page));
        outline.Add("Three", PdfDestination.FitPage(page));

        byte[] file = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(file);

        PdfDictionary root = reopened.Catalog.GetDictionary(PdfNames.Outlines)!;
        PdfDictionary firstItem = reopened.Resolve(root.Get(PdfNames.First)).AsDictionary!;
        PdfDictionary lastItem = reopened.Resolve(root.Get(PdfNames.Last)).AsDictionary!;

        Assert.Equal("One", firstItem.GetTextString(PdfNames.Title));
        Assert.Equal("Three", lastItem.GetTextString(PdfNames.Title));

        // The middle item is reachable both ways and has both neighbours.
        PdfDictionary middle = reopened.Resolve(firstItem.Get(PdfNames.Next)).AsDictionary!;
        Assert.Equal("Two", middle.GetTextString(PdfNames.Title));
        Assert.Equal("Three", reopened.Resolve(middle.Get(PdfNames.Next)).AsDictionary!.GetTextString(PdfNames.Title));
        Assert.Equal("One", reopened.Resolve(middle.Get(PdfNames.Prev)).AsDictionary!.GetTextString(PdfNames.Title));
    }

    [Fact]
    public void RemovesABookmarkAndKeepsTheChainWhole()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfOutline outline = PdfOutline.OpenOrCreate(document);
        outline.Add("Keep me", PdfDestination.FitPage(page));
        PdfOutlineItem doomed = outline.Add("Delete me", PdfDestination.FitPage(page));
        outline.Add("Keep me too", PdfDestination.FitPage(page));

        Assert.True(outline.Remove(doomed));

        byte[] file = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(file);

        PdfOutline reloaded = reopened.Outline!;
        Assert.Equal(2, reloaded.Count);
        Assert.Equal(["Keep me", "Keep me too"], reloaded.Select(item => item.Title));
    }

    [Fact]
    public void WritesAnExplicitDestinationArray()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfOutline outline = PdfOutline.OpenOrCreate(document);
        outline.Add("Top", PdfDestination.XyzLocation(page, left: 10, top: 750, zoom: 1.5));

        byte[] file = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(file);

        PdfDictionary item = reopened.Resolve(
            reopened.Catalog.GetDictionary(PdfNames.Outlines)!.Get(PdfNames.First)).AsDictionary!;
        PdfArray destination = item.GetArray(PdfNames.Dest)!;

        // Page reference, then the XYZ mode, then left, top and zoom.
        Assert.Equal("XYZ", destination.GetName(1)!.Value);
        Assert.Equal(10, destination.Get(2).AsNumber, 3);
        Assert.Equal(750, destination.Get(3).AsNumber, 3);
        Assert.Equal(1.5, destination.Get(4).AsNumber, 3);
    }

    [Fact]
    public void SetsColourAndStyle()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfOutline outline = PdfOutline.OpenOrCreate(document);
        PdfOutlineItem item = outline.Add("Styled", PdfDestination.FitPage(page));
        item.SetColor(PdfColor.Rgb(1, 0, 0));
        item.SetStyle(italic: true, bold: true);

        byte[] file = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(file);

        PdfDictionary dictionary = reopened.Resolve(
            reopened.Catalog.GetDictionary(PdfNames.Outlines)!.Get(PdfNames.First)).AsDictionary!;

        Assert.Equal(3, dictionary.GetInteger(PdfName.Get("F")));
        PdfArray colour = dictionary.GetArray(PdfName.Get("C"))!;
        Assert.Equal(1, colour.Get(0).AsNumber, 3);
        Assert.Equal(0, colour.Get(1).AsNumber, 3);
    }

    [Fact]
    public void ReturnsNothingWhenThereIsNoOutline()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);
        Assert.Null(document.Outline);
    }
}
