using System.Text;
using Inkwright.Cos;
using Xunit;

namespace Inkwright.Layout.Tests;

public class PdfAXmpBudgetTests
{
    [Fact]
    public void OversizedFailedPacketSaturatesTheSharedByteLedger()
    {
        var session = Session(maxBytes: 5, maxCharacters: 32);
        using PdfDocument oversized = WithMetadata("123456");
        using PdfDocument sibling = WithMetadata("<x />");

        Assert.False(session.TryReadXmp(oversized, out _, out string? firstProblem));
        Assert.Contains("XMP byte limit", firstProblem, StringComparison.OrdinalIgnoreCase);

        Assert.False(session.TryReadXmp(sibling, out _, out string? siblingProblem));
        Assert.Contains("XMP byte limit", siblingProblem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonCompleteDecodeChargesItsKnownBytesToTheSharedLedger()
    {
        var session = Session(maxBytes: 10, maxCharacters: 32);
        using PdfDocument undecodable = WithMetadata("broken", PdfName.Get("DCTDecode"));
        using PdfDocument sibling = WithMetadata("<x />");

        Assert.False(session.TryReadXmp(undecodable, out _, out string? firstProblem));
        Assert.Contains("UndecodedFilter", firstProblem, StringComparison.Ordinal);

        Assert.False(session.TryReadXmp(sibling, out _, out string? siblingProblem));
        Assert.Contains("XMP byte limit", siblingProblem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedXmlClosesTheSharedCharacterLedger()
    {
        var session = Session(maxBytes: 32, maxCharacters: 16);
        using PdfDocument malformed = WithMetadata("<broken>");
        using PdfDocument sibling = WithMetadata("<x />");

        Assert.False(session.TryReadXmp(malformed, out _, out string? firstProblem));
        Assert.Contains("well-formed XML", firstProblem, StringComparison.OrdinalIgnoreCase);

        Assert.False(session.TryReadXmp(sibling, out _, out string? siblingProblem));
        Assert.Contains("XMP character limit", siblingProblem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CharacterBoundaryFailureClosesTheSharedCharacterLedger()
    {
        var session = Session(maxBytes: 64, maxCharacters: 8);
        using PdfDocument oversized = WithMetadata("<abcdefgh />");
        using PdfDocument sibling = WithMetadata("<x />");

        Assert.False(session.TryReadXmp(oversized, out _, out string? firstProblem));
        Assert.Contains("XMP character limit", firstProblem, StringComparison.OrdinalIgnoreCase);

        Assert.False(session.TryReadXmp(sibling, out _, out string? siblingProblem));
        Assert.Contains("XMP character limit", siblingProblem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactBoundaryPacketIsCachedWithoutASecondCharge()
    {
        const string packetText = "<x />";
        var session = Session(
            maxBytes: Encoding.UTF8.GetByteCount(packetText),
            maxCharacters: packetText.Length);
        using PdfDocument document = WithMetadata(packetText);
        using PdfDocument sibling = WithMetadata(packetText);

        Assert.True(session.TryReadXmp(document, out PdfAXmpPacket? first, out string? firstProblem));
        Assert.Null(firstProblem);
        Assert.Equal(packetText, first!.Text);

        Assert.True(session.TryReadXmp(document, out PdfAXmpPacket? cached, out string? cachedProblem));
        Assert.Null(cachedProblem);
        Assert.Same(first, cached);

        Assert.False(session.TryReadXmp(sibling, out _, out string? siblingProblem));
        Assert.Contains("XMP byte limit", siblingProblem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedMetadataStillPassesStrictRootDecoding()
    {
        using PdfDocument document = ArchivalDocument();

        Assert.Empty(PdfAProfile.Validate(document, PdfAConformance.A4));
    }

    [Fact]
    public void RootValidationDoesNotTrustSetDataCacheAfterFilterMutation()
    {
        using PdfDocument document = ArchivalDocument();
        PdfStream metadata = document.Catalog.Get(PdfNames.Metadata).AsStream!;
        metadata.Dictionary.Set(
            PdfNames.Filter,
            PdfValue.Name(PdfName.Get("DCTDecode")));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.7.2.1" &&
                       problem.Message.Contains("UndecodedFilter", StringComparison.Ordinal));
    }

    [Fact]
    public void EmbeddedPageMetadataMustBeWellFormedXml()
    {
        byte[] childBytes;
        using (PdfDocument child = ArchivalDocument())
        {
            AddPageMetadata(child, "<broken>", declareTypeAndSubtype: true);
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = ArchivalDocument();
        outer.Attachments.Add(
            "page-metadata.pdf",
            childBytes,
            "application/pdf",
            relationship: AttachmentRelationship.Data);

        Assert.Contains(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("page-metadata.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("well-formed XML", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Type", null)]
    [InlineData("Type", "NotMetadata")]
    [InlineData("Subtype", null)]
    [InlineData("Subtype", "NotXml")]
    public void CatalogMetadataRequiresExactTypeAndSubtype(string key, string? wrongValue)
    {
        using PdfDocument document = ArchivalDocument();
        PdfStream metadata = document.Catalog.Get(PdfNames.Metadata).AsStream!;
        PdfName entry = PdfName.Get(key);
        if (wrongValue is null)
            metadata.Dictionary.Remove(entry);
        else
            metadata.Dictionary.Set(entry, PdfValue.Name(PdfName.Get(wrongValue)));

        string expected = key == "Type" ? "/Type /Metadata" : "/Subtype /XML";
        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.7.2.1" &&
                       problem.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryReachableMetadataStreamRequiresTypeAndSubtype()
    {
        using PdfDocument document = ArchivalDocument();
        AddPageMetadata(document, "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" /></rdf:RDF>");

        IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(document, PdfAConformance.A4);

        Assert.Contains(
            problems,
            problem => problem.Clause == "6.7.2.1" &&
                       problem.Message.Contains("/Type /Metadata", StringComparison.Ordinal));
        Assert.Contains(
            problems,
            problem => problem.Clause == "6.7.2.1" &&
                       problem.Message.Contains("/Subtype /XML", StringComparison.Ordinal));
    }

    [Fact]
    public void ByteValidationChecksEveryLiveMetadataStream()
    {
        using PdfDocument document = ArchivalDocument();
        PdfStream metadata = PdfStream.FromData(Encoding.UTF8.GetBytes("<broken>"), compress: false);
        metadata.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.Metadata));
        metadata.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("XML")));
        document.AddObject(PdfValue.Stream(metadata));

        byte[] bytes = document.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            SetProducer = false,
            UpdateModificationDate = false,
        });

        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A4),
            problem => problem.Clause == "6.7.2.1" &&
                       problem.Message.Contains("well-formed XML", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ByteValidationChecksMetadataEntriesInEveryLiveDictionary()
    {
        using PdfDocument document = ArchivalDocument();
        PdfObjectId metadataId = document.AddObject(PdfValue.Stream(
            PdfStream.FromData(Encoding.UTF8.GetBytes(BareRdfWithoutClaim), compress: false)));
        document.AddObject(PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.Metadata] = PdfValue.Reference(metadataId),
        }));

        byte[] bytes = document.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            SetProducer = false,
            UpdateModificationDate = false,
        });

        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A4),
            problem => problem.Clause == "6.7.2.1" &&
                       problem.Message.Contains("/Type /Metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void ReachableMetadataStreamsShareTheByteLedger()
    {
        using PdfDocument document = ArchivalDocument();
        PdfStream catalog = document.Catalog.Get(PdfNames.Metadata).AsStream!;
        PdfStream page = AddPageMetadata(document, BareRdfWithoutClaim, declareTypeAndSubtype: true);
        long oneByteShort = catalog.DecodedSpan().Length + page.DecodedSpan().Length - 1L;
        var limits = new PdfAValidationLimits(maxTotalXmpBytes: oneByteShort);

        Assert.Contains(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.7.2.1" &&
                       problem.Message.Contains("XMP byte limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MetadataStreamAliasedFromThePageIsChargedOnlyOnce()
    {
        using PdfDocument document = ArchivalDocument();
        PdfValue metadataReference = document.Catalog[PdfNames.Metadata];
        PdfStream metadata = document.Resolve(metadataReference).AsStream!;
        document.Pages[0].Dictionary.Set(PdfNames.Metadata, metadataReference);
        var limits = new PdfAValidationLimits(maxTotalXmpBytes: metadata.DecodedSpan().Length);

        Assert.DoesNotContain(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Message.Contains("XMP byte limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ArbitraryXmlElementsCannotSupplyAnEmbeddedPdfAClaim()
    {
        const string fakeClaim = """
            <fake xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/">
              <pdfaid:part>4</pdfaid:part>
              <pdfaid:rev>2020</pdfaid:rev>
            </fake>
            """;

        IReadOnlyList<PdfAProblem> problems = ValidateEmbeddedClaim("fake-claim.pdf", fakeClaim);

        Assert.Contains(
            problems,
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("fake-claim.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("rdf:RDF", StringComparison.Ordinal));
    }

    [Fact]
    public void BareRdfWrapperCanSupplyTheCatalogClaim()
    {
        using PdfDocument document = ArchivalDocument();
        SetCatalogMetadata(document, BareA4Claim);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause is "6.7.2.1" or "6.7.3");
    }

    [Fact]
    public void PdfAClaimPropertiesCannotBeCombinedAcrossRdfSubjects()
    {
        const string splitClaim = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/">
              <rdf:Description rdf:about="">
                <pdfaid:part>4</pdfaid:part>
              </rdf:Description>
              <rdf:Description rdf:about="urn:not-the-document">
                <pdfaid:rev>2020</pdfaid:rev>
              </rdf:Description>
            </rdf:RDF>
            """;

        IReadOnlyList<PdfAProblem> problems = ValidateEmbeddedClaim("split-subjects.pdf", splitClaim);

        Assert.Contains(
            problems,
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("split-subjects.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("document subject", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyAboutUnderXmlBaseIsNotThePdfDocumentSubject()
    {
        const string basedClaim = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/"
                     xml:base="urn:not-the-pdf-document">
              <rdf:Description rdf:about="">
                <pdfaid:part>4</pdfaid:part>
                <pdfaid:rev>2020</pdfaid:rev>
              </rdf:Description>
            </rdf:RDF>
            """;

        IReadOnlyList<PdfAProblem> problems = ValidateEmbeddedClaim("xml-base-subject.pdf", basedClaim);

        Assert.Contains(
            problems,
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("xml-base-subject.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("document subject", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConflictingPdfAClaimPropertiesFailClosed()
    {
        const string conflictingClaim = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/">
              <rdf:Description rdf:about="">
                <pdfaid:part>4</pdfaid:part>
                <pdfaid:rev>2020</pdfaid:rev>
              </rdf:Description>
              <rdf:Description rdf:about="">
                <pdfaid:part>3</pdfaid:part>
              </rdf:Description>
            </rdf:RDF>
            """;

        IReadOnlyList<PdfAProblem> problems = ValidateEmbeddedClaim("conflicting-claim.pdf", conflictingClaim);

        Assert.Contains(
            problems,
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("conflicting-claim.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("conflicting pdfaid:part", StringComparison.OrdinalIgnoreCase));
    }

    private static PdfAValidationSession Session(long maxBytes, long maxCharacters) =>
        new(
            new PdfAValidationLimits(
                maxTotalXmpBytes: maxBytes,
                maxTotalXmpCharacters: maxCharacters),
            ReadOnlyMemory<byte>.Empty);

    private static PdfDocument WithMetadata(string text, PdfName? filter = null)
    {
        PdfDocument document = PdfDocument.Create();
        var metadata = new PdfStream();
        metadata.SetRawData(Encoding.UTF8.GetBytes(text));
        if (filter is not null)
            metadata.Dictionary.Set(PdfNames.Filter, PdfValue.Name(filter));

        document.Catalog.Set(PdfNames.Metadata, PdfValue.Stream(metadata));
        return document;
    }

    private static PdfDocument ArchivalDocument()
    {
        PdfDocument document = PdfDocument.Create();
        document.Pages.Add();
        PdfAProfile.Apply(document, PdfAConformance.A4);
        return document;
    }

    private static PdfStream AddPageMetadata(
        PdfDocument document,
        string xml,
        bool declareTypeAndSubtype = false)
    {
        PdfStream metadata = PdfStream.FromData(Encoding.UTF8.GetBytes(xml), compress: false);
        if (declareTypeAndSubtype)
        {
            metadata.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.Metadata));
            metadata.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("XML")));
        }

        PdfObjectId metadataId = document.AddObject(PdfValue.Stream(metadata));
        document.Pages[0].Dictionary.Set(PdfNames.Metadata, PdfValue.Reference(metadataId));
        return metadata;
    }

    private static IReadOnlyList<PdfAProblem> ValidateEmbeddedClaim(string name, string packet)
    {
        byte[] childBytes;
        using (PdfDocument child = ArchivalDocument())
        {
            SetCatalogMetadata(child, packet);
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = ArchivalDocument();
        outer.Attachments.Add(
            name,
            childBytes,
            "application/pdf",
            relationship: AttachmentRelationship.Data);
        return PdfAProfile.Validate(outer, PdfAConformance.A4);
    }

    private static void SetCatalogMetadata(PdfDocument document, string packet) =>
        document.Catalog.Get(PdfNames.Metadata).AsStream!
            .SetData(Encoding.UTF8.GetBytes(packet), compress: false);

    private const string BareA4Claim = """
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                 xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/">
          <rdf:Description rdf:about="">
            <pdfaid:part>4</pdfaid:part>
            <pdfaid:rev>2020</pdfaid:rev>
          </rdf:Description>
        </rdf:RDF>
        """;

    private const string BareRdfWithoutClaim = """
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about="" />
        </rdf:RDF>
        """;
}
