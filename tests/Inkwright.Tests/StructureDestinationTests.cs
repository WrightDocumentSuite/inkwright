using Inkwright.Cos;
using Inkwright.IO;
using Inkwright.Tagging;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Structure destinations (ISO 32000-2 §12.3.2.3): a destination whose first element points at a
/// structure element instead of a page, so it survives a reflow that moves the content.
/// </summary>
public class StructureDestinationTests
{
    [Fact]
    public void BookmarkPointsAtAStructureElement()
    {
        // A minimal tagged document: a structure tree root with one paragraph element that claims
        // a marked-content sequence on the page.
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        page.SetContent("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Heading) Tj ET EMC"u8.ToArray());

        var paragraph = new PdfDictionary(4);
        paragraph.Set(PdfName.Get("Type"), PdfValue.Name("StructElem"));
        paragraph.Set(PdfName.Get("S"), PdfValue.Name("H1"));
        paragraph.Set(PdfName.Get("Pg"), PdfValue.Reference(page.Id));
        paragraph.Set(PdfName.Get("K"), PdfValue.Integer(0));
        PdfObjectId paragraphId = document.AddObject(PdfValue.Dictionary(paragraph));

        var root = new PdfDictionary(2);
        root.Set(PdfName.Get("Type"), PdfValue.Name("StructTreeRoot"));
        root.Set(PdfName.Get("K"), PdfValue.Reference(paragraphId));
        paragraph.Set(PdfName.Get("P"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(root))));
        document.Catalog.Set(PdfName.Get("StructTreeRoot"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(root))));

        // Re-read so the structure node carries the identity a destination needs.
        byte[] tagged = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument reopened = PdfDocument.Load(tagged);

        PdfStructureNode heading = reopened.Structure[0];
        Assert.True(heading.Id.IsValid);

        PdfOutline outline = PdfOutline.OpenOrCreate(reopened);
        PdfOutlineItem item = outline.Add("Heading");
        item.SetStructureDestination(PdfDestination.ToStructureElement(heading, top: 750));

        byte[] withOutline = reopened.ToIncrementalArray();
        using PdfDocument final = PdfDocument.Load(withOutline);

        PdfDictionary bookmark = final.Resolve(
            final.Catalog.GetDictionary(PdfNames.Outlines)!.Get(PdfNames.First)).AsDictionary!;

        // A structure destination occupies the normal Dest entry; its first element is the
        // structure element rather than a page (§12.3.2.3).
        PdfArray structureDestination = bookmark.GetArray(PdfNames.Dest)!;
        Assert.False(bookmark.ContainsKey(PdfName.Get("SD")));
        Assert.Equal("XYZ", structureDestination.GetName(1)!.Value);
        Assert.Equal(heading.Id.Number, structureDestination[0].AsReference.Number);
        Assert.Equal(PdfVersion.Pdf20, final.Version);
    }

    [Fact]
    public void APageDestinationIsNotAStructureDestination()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        PdfDestination pageDestination = PdfDestination.FitPage(page);
        Assert.False(pageDestination.IsStructureDestination);

        // SetStructureDestination refuses a plain page destination, so the two never get confused.
        PdfOutline outline = PdfOutline.OpenOrCreate(document);
        PdfOutlineItem item = outline.Add("Plain");
        Assert.Throws<ArgumentException>(() => item.SetStructureDestination(pageDestination));
    }
}
