using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Metadata;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// The three application notes the PDF Association publishes alongside PDF 2.0. They add nothing to
/// the standard; they say how parts of it are meant to be used, and the parts they cover are ones
/// producers routinely get wrong.
/// </summary>
public class ApplicationNoteTests
{
    [Fact]
    public void AlwaysSaysHowAnAttachmentRelatesToTheDocument()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);

        // Application Note 002 asks for the relationship whenever a file is associated with
        // something, so it is written even when the caller names none.
        document.Attachments.Add("notes.txt", "plain notes"u8.ToArray());

        PdfDictionary specification = FirstSpecification(document);
        Assert.Equal("Unspecified", specification.GetName(PdfNames.AFRelationship)?.Value);
    }

    [Fact]
    public void WritesTheThreeThingsAnAssociatedFileNeeds()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);

        document.Attachments.Add(
            "invoice.xml",
            "<invoice/>"u8.ToArray(),
            "text/xml",
            "The machine-readable invoice",
            AttachmentRelationship.Data);

        PdfDictionary specification = FirstSpecification(document);

        // The relationship, on the file specification.
        Assert.Equal("Data", specification.GetName(PdfNames.AFRelationship)?.Value);

        PdfStream embedded = document.Resolve(
            document.Resolve(specification.Get(PdfNames.EF)).AsDictionary!.Get(PdfNames.F)).AsStream!;

        // The MIME type, on the embedded file stream.
        Assert.Equal("text/xml", embedded.Dictionary.GetName(PdfNames.Subtype)?.Value);

        // The modification date, in the parameters.
        PdfDictionary parameters = document.Resolve(
            embedded.Dictionary.Get(PdfNames.Params)).AsDictionary!;

        Assert.NotNull(parameters.GetTextString(PdfNames.ModDate));
    }

    [Fact]
    public void SetsBlackPointCompensationTheWayTheStandardSpellsIt()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.BlackPointCompensation(PdfBlackPointCompensation.On);
            canvas.Rectangle(10, 10, 50, 50).Fill();
            canvas.Commit();
        }

        PdfDictionary states = document.Resolve(
            page.Resources.Get(PdfName.Get("ExtGState"))).AsDictionary!;

        PdfName key = states.Keys.ToArray()[0];
        PdfDictionary state = document.Resolve(states.Get(key)).AsDictionary!;

        // The three values are spelled exactly as Application Note 001 gives them.
        Assert.Equal("ON", state.GetName(PdfName.Get("UseBlackPtComp"))?.Value);
    }

    [Theory]
    [InlineData(PdfBlackPointCompensation.On, "ON")]
    [InlineData(PdfBlackPointCompensation.Off, "OFF")]
    [InlineData(PdfBlackPointCompensation.Default, "Default")]
    public void SpellsEveryBlackPointSettingCorrectly(PdfBlackPointCompensation setting, string expected)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.BlackPointCompensation(setting);
            canvas.Commit();
        }

        PdfDictionary states = document.Resolve(page.Resources.Get(PdfName.Get("ExtGState"))).AsDictionary!;
        PdfDictionary state = document.Resolve(states.Get(states.Keys.ToArray()[0])).AsDictionary!;

        Assert.Equal(expected, state.GetName(PdfName.Get("UseBlackPtComp"))?.Value);
    }

    [Fact]
    public void AttachesMetadataToAnIndividualObject()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        const string Xmp = """<?xpacket begin=""?><x:xmpmeta xmlns:x="adobe:ns:meta/"/>""";
        PdfObjectMetadata.Attach(document, page.Dictionary, Xmp);

        Assert.Equal(Xmp, PdfObjectMetadata.Read(document, page.Dictionary));

        // The stream identifies itself the way a scanner looking for metadata expects.
        PdfStream stream = document.Resolve(page.Dictionary.Get(PdfName.Get("Metadata"))).AsStream!;
        Assert.Equal("Metadata", stream.Dictionary.GetName(PdfNames.Type)?.Value);
        Assert.Equal("XML", stream.Dictionary.GetName(PdfNames.Subtype)?.Value);
    }

    [Fact]
    public void KnowsWhichPlacementsOtherProgramsWillLookAt()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        // Application Note 003 lists the pages and page-level objects among the understood places.
        Assert.True(PdfObjectMetadata.IsRecognisedPlacement(document, page.Dictionary));
        Assert.True(PdfObjectMetadata.IsRecognisedPlacement(document, document.Catalog));

        var image = new PdfDictionary(2);
        image.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("XObject")));
        image.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Image")));
        Assert.True(PdfObjectMetadata.IsRecognisedPlacement(document, image));

        // An annotation is not on the list, so metadata there is a private note.
        var annotation = new PdfDictionary(2);
        annotation.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Annot")));
        annotation.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Text")));
        Assert.False(PdfObjectMetadata.IsRecognisedPlacement(document, annotation));
    }

    [Fact]
    public void SurvivesBeingSavedAndReadBack()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfObjectMetadata.Attach(document, page.Dictionary, "<x/>");

        using PdfDocument reopened = PdfDocument.Load(document.ToArray());
        Assert.Equal("<x/>", PdfObjectMetadata.Read(reopened, reopened.Pages[0].Dictionary));
    }

    private static PdfDictionary FirstSpecification(PdfDocument document)
    {
        PdfDictionary names = document.Resolve(document.Catalog.Get(PdfName.Get("Names"))).AsDictionary!;
        PdfDictionary tree = document.Resolve(names.Get(PdfName.Get("EmbeddedFiles"))).AsDictionary!;
        PdfArray entries = document.Resolve(tree.Get(PdfName.Get("Names"))).AsArray!;

        return document.Resolve(entries[1]).AsDictionary!;
    }
}
