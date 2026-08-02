using System.Text;
using Inkwright.Cos;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Layout.Tests;

/// <summary>
/// Guards the clause renumbering and removed requirements in ISO 19005-4:2020.
/// Parts 2 and 3 cannot be used as a numbering proxy for part 4.
/// </summary>
public class PdfA4ClauseCitationTests
{
    [Fact]
    public void StreamExternalDataUsesClauseSixOneSixOne()
    {
        using PdfDocument document = CreateDocument();
        PdfStream stream = PdfStream.FromData("q Q"u8.ToArray(), compress: false);
        stream.Dictionary.Set(PdfName.Get("F"), PdfValue.TextString("outside.dat"));
        Attach(document, PdfValue.Stream(stream));

        AssertClause(document, "carries F,", "6.1.6.1");
    }

    [Fact]
    public void StreamFiltersUseClauseSixOneSixTwo()
    {
        using PdfDocument document = CreateDocument();
        PdfStream stream = PdfStream.FromData("q Q"u8.ToArray(), compress: false);
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfNames.LZWDecode));
        Attach(document, PdfValue.Stream(stream));

        AssertClause(document, "uses LZWDecode", "6.1.6.2");
    }

    [Fact]
    public void PermissionsUseClauseSixOneEleven()
    {
        using PdfDocument document = CreateDocument();
        var permissions = new PdfDictionary(1);
        permissions.Set(PdfName.Get("UR"), PdfValue.True);
        document.Catalog.Set(PdfName.Get("Perms"), PdfValue.Dictionary(permissions));

        AssertClause(document, "permissions dictionary", "6.1.11");
    }

    [Fact]
    public void InlineImageFiltersUseClauseSixOneNine()
    {
        using PdfDocument document = CreateDocument();
        document.Pages[0].AppendContent(
            "q BI /W 1 /H 1 /CS /G /BPC 8 /F /LZW ID x EI Q"u8.ToArray(),
            compress: false);

        AssertClause(document, "inline image", "6.1.9");
    }

    [Fact]
    public void PartFourDoesNotApplyLegacyImplementationLimits()
    {
        using PdfDocument document = CreateDocument();
        document.Pages[0].MediaBox = new PdfRectangle(0, 0, 20000, 2);

        AssertNoProblem(document, "measures 20000 by 2");
    }

    [Fact]
    public void ImageDictionariesUseClauseSixTwoSevenOne()
    {
        using PdfDocument document = CreateDocument();
        PdfStream image = PdfStream.FromData(new byte[] { 0 }, compress: false);
        image.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        image.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Image));
        image.Dictionary.Set(PdfName.Get("Alternates"), PdfValue.Array(new PdfArray()));
        Attach(document, PdfValue.Stream(image));

        AssertClause(document, "carries Alternates", "6.2.7.1");
    }

    [Fact]
    public void FormOpiUsesClauseSixTwoEightOne()
    {
        using PdfDocument document = CreateDocument();
        PdfStream form = Form();
        form.Dictionary.Set(PdfName.Get("OPI"), PdfValue.Dictionary(new PdfDictionary()));
        Attach(document, PdfValue.Stream(form));

        AssertClause(document, "form XObject carries the OPI", "6.2.8.1");
    }

    [Fact]
    public void ReferenceXObjectsUseClauseSixTwoEightTwo()
    {
        using PdfDocument document = CreateDocument();
        PdfStream form = Form();
        form.Dictionary.Set(PdfName.Get("Ref"), PdfValue.Dictionary(new PdfDictionary()));
        Attach(document, PdfValue.Stream(form));

        AssertClause(document, "reference XObject", "6.2.8.2");
    }

    [Fact]
    public void TransparencyUsesClauseSixTwoNine()
    {
        using PdfDocument document = CreateDocument();
        var state = new PdfDictionary
        {
            [PdfNames.Type] = PdfValue.Name(PdfNames.ExtGState),
            [PdfNames.BM] = PdfValue.Name(PdfName.Get("InventedBlendMode")),
        };
        Attach(document, PdfValue.Dictionary(state));

        AssertClause(document, "blend mode InventedBlendMode", "6.2.9");
    }

    [Fact]
    public void MetadataStreamUsesClauseSixSevenTwoOne()
    {
        using PdfDocument document = CreateDocument();

        AssertClause(document, "has no Metadata stream", "6.7.2.1");
    }

    [Fact]
    public void PartFourDoesNotApplyLegacyExtensionSchemaDeclarations()
    {
        using PdfDocument document = CreateDocument();
        const string packet = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:custom="urn:inkwright:test">
                  <custom:Flag>true</custom:Flag>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        PdfStream metadata = PdfStream.FromData(Encoding.UTF8.GetBytes(packet), compress: false);
        metadata.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.Metadata));
        metadata.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("XML")));
        document.Catalog.Set(PdfNames.Metadata, PdfValue.Stream(metadata));

        AssertNoProblem(document, "without describing it as an extension schema");
    }

    [Fact]
    public void PartFourDoesNotApplyTheLegacyRenderingIntentRestriction()
    {
        using PdfDocument document = CreateDocument();
        document.Pages[0].AppendContent("/InventedIntent ri"u8.ToArray(), compress: false);

        AssertNoProblem(document, "rendering intent InventedIntent");
    }

    [Fact]
    public void PartFourDoesNotApplyLegacyLanguageRules()
    {
        using PdfDocument document = CreateDocument();
        document.Catalog.Set(PdfNames.Lang, PdfValue.TextString("english please"));

        AssertNoProblem(document, "catalogue names the language");
    }

    [Fact]
    public void EmbeddedFileAssociationUsesClauseSixNine()
    {
        using PdfDocument document = CreateDocument();
        var specification = new PdfDictionary
        {
            [PdfNames.F] = PdfValue.TextString("attachment.pdf"),
        };
        AttachEmbeddedFile(document, specification);

        AssertClause(document, "needs both the F and UF entries", "6.9");
        AssertClause(document, "states no AFRelationship", "6.9");
    }

    [Fact]
    public void AlternatePresentationsUseClauseSixEleven()
    {
        using PdfDocument document = CreateDocument();
        document.Catalog.Set(
            PdfNames.Names,
            PdfValue.Dictionary(new PdfDictionary
            {
                [PdfName.Get("AlternatePresentations")] = PdfValue.Dictionary(new PdfDictionary()),
            }));

        AssertClause(document, "AlternatePresentations", "6.11");
    }

    [Fact]
    public void DocumentRequirementsUseClauseSixTwelve()
    {
        using PdfDocument document = CreateDocument();
        document.Catalog.Set(PdfName.Get("Requirements"), PdfValue.Array(new PdfArray()));

        AssertClause(document, "catalogue carries Requirements", "6.12");
    }

    [Fact]
    public void PartFourAllowsUsageApplicationDictionaries()
    {
        using PdfDocument document = CreateDocument();
        var configuration = new PdfDictionary
        {
            [PdfNames.Name] = PdfValue.TextString("Archive view"),
            [PdfNames.AS] = PdfValue.Array(new PdfArray()),
        };
        document.Catalog.Set(
            PdfName.Get("OCProperties"),
            PdfValue.Dictionary(new PdfDictionary
            {
                [PdfNames.D] = PdfValue.Dictionary(configuration),
            }));

        AssertNoProblem(document, "configuration carries AS");
    }

    private static PdfDocument CreateDocument()
    {
        PdfDocument document = PdfDocument.Create();
        document.Version = PdfVersion.Pdf20;
        document.Pages.Add();
        return document;
    }

    private static PdfStream Form()
    {
        PdfStream form = PdfStream.FromData("q Q"u8.ToArray(), compress: false);
        form.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        form.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Form));
        return form;
    }

    private static void Attach(PdfDocument document, PdfValue value) =>
        document.Catalog.Set(PdfName.Get("ClauseCitationProbe"), value);

    private static void AttachEmbeddedFile(PdfDocument document, PdfDictionary specification)
    {
        var entries = new PdfArray(
            PdfValue.TextString("attachment.pdf"),
            PdfValue.Dictionary(specification));
        var embeddedFiles = new PdfDictionary
        {
            [PdfNames.Names] = PdfValue.Array(entries),
        };
        document.Catalog.Set(
            PdfNames.Names,
            PdfValue.Dictionary(new PdfDictionary
            {
                [PdfNames.EmbeddedFiles] = PdfValue.Dictionary(embeddedFiles),
            }));
    }

    private static void AssertClause(PdfDocument document, string message, string clause) =>
        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == clause && problem.Message.Contains(message, StringComparison.OrdinalIgnoreCase));

    private static void AssertNoProblem(PdfDocument document, string message) =>
        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Message.Contains(message, StringComparison.OrdinalIgnoreCase));
}
