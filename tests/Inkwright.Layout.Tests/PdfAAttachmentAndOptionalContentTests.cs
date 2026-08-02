using Inkwright.Cos;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Layout.Tests;

public class PdfAAttachmentAndOptionalContentTests
{
    [Fact]
    public void PartThreeRequiresAnEmbeddedFileMimeTypeUnderAnnexE()
    {
        using PdfDocument document = DocumentWithAttachment(PdfName.Get("Data"), includeMime: false);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A3B),
            problem => problem.Clause == "E.1" &&
                       problem.Message.Contains("MIME type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PartFourDoesNotRequireAnEmbeddedFileMimeType()
    {
        using PdfDocument document = DocumentWithAttachment(PdfName.Get("Data"), includeMime: false);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Message.Contains("MIME type", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A3B)]
    [InlineData(PdfAConformance.A4)]
    public void CustomAssociatedFileRelationshipsAreAllowed(PdfAConformance conformance)
    {
        using PdfDocument document = DocumentWithAttachment(PdfName.Get("AcmeInvoiceSource"), includeMime: true);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Message.Contains("relationship AcmeInvoiceSource", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A3B, "E.2")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void MissingAssociatedFileRelationshipUsesThePartSpecificClause(
        PdfAConformance conformance, string clause)
    {
        using PdfDocument document = DocumentWithAttachment(relationship: null, includeMime: true);

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("states no AFRelationship", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void AttachmentPayloadRatherThanItsMimeTypeDeterminesWhetherItIsPdf(
        PdfAConformance conformance, string clause)
    {
        using PdfDocument document = DocumentWithAttachment(
            PdfName.Get("Data"),
            includeMime: true,
            mime: "application/notpdf");

        IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(document, conformance);

        Assert.Contains(
            problems,
            problem => problem.Clause == clause &&
                       problem.Message.Contains("not a PDF", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            problems,
            problem => problem.Message.Contains("application/notpdf", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsAttachmentInBranchedEmbeddedFilesNameTree()
    {
        using PdfDocument document = Fresh();
        PdfValue specification = AddSpecification(
            document,
            CreateFileSpecification(document, relationship: null, includeMime: true));
        var leaf = new PdfDictionary
        {
            [PdfNames.Names] = PdfValue.Array(new PdfArray(
                PdfValue.TextString("attachment.bin"), specification)),
        };
        var root = new PdfDictionary();
        var kids = new PdfArray(
            PdfValue.Dictionary(new PdfDictionary()),
            PdfValue.Dictionary(leaf));
        root.Set(PdfName.Get("Kids"), PdfValue.Array(kids));
        kids.Add(PdfValue.Dictionary(root)); // malformed cycle: traversal must remain bounded
        SetEmbeddedFilesTree(document, root);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A3B),
            problem => problem.Clause == "E.2" &&
                       problem.Message.Contains("states no AFRelationship", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("page")]
    [InlineData("structure")]
    public void FindsAssociatedFileOutsideTheEmbeddedFilesNameTree(string location)
    {
        using PdfDocument document = Fresh();
        PdfValue specification = AddSpecification(
            document,
            CreateFileSpecification(document, relationship: null, includeMime: true));
        var associatedFiles = new PdfArray(specification);

        switch (location)
        {
            case "catalog":
                document.Catalog.Set(PdfName.Get("AF"), PdfValue.Array(associatedFiles));
                break;
            case "page":
                document.Pages[0].Dictionary.Set(PdfName.Get("AF"), PdfValue.Array(associatedFiles));
                break;
            default:
                document.Catalog.Set(PdfName.Get("StructTreeRoot"), PdfValue.Dictionary(new PdfDictionary
                {
                    [PdfName.Get("AF")] = PdfValue.Array(associatedFiles),
                }));
                break;
        }

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A3B),
            problem => problem.Clause == "E.2" &&
                       problem.Message.Contains("states no AFRelationship", StringComparison.Ordinal));
    }

    [Fact]
    public void PartOneFindsEmbeddedFileReachedOnlyThroughAssociatedFiles()
    {
        using PdfDocument document = Fresh();
        PdfValue specification = AddSpecification(
            document,
            CreateFileSpecification(document, PdfName.Get("Data"), includeMime: true));
        document.Catalog.Set(
            PdfName.Get("AF"),
            PdfValue.Array(new PdfArray(specification)));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A1B),
            problem => problem.Clause == "6.1.11" &&
                       problem.Message.Contains("embedded files", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PartFourFRecognizesEmbeddedFilesKeyWithABranchedNameTree()
    {
        using PdfDocument document = Fresh();
        PdfValue specification = AddSpecification(
            document,
            CreateFileSpecification(document, PdfName.Get("Data"), includeMime: true));
        var leaf = new PdfDictionary
        {
            [PdfNames.Names] = PdfValue.Array(new PdfArray(
                PdfValue.TextString("attachment.bin"), specification)),
        };
        SetEmbeddedFilesTree(document, new PdfDictionary
        {
            [PdfName.Get("Kids")] = PdfValue.Array(new PdfArray(
                PdfValue.Dictionary(new PdfDictionary()),
                PdfValue.Dictionary(leaf))),
        });

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4F),
            problem => problem.Clause == "A.2");
    }

    [Fact]
    public void PartFourFStillRequiresEmbeddedFilesKeyWhenAssociatedFileIsReachableElsewhere()
    {
        using PdfDocument document = Fresh();
        PdfValue specification = AddSpecification(
            document,
            CreateFileSpecification(document, PdfName.Get("Data"), includeMime: true));
        document.Catalog.Set(
            PdfName.Get("AF"),
            PdfValue.Array(new PdfArray(specification)));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4F),
            problem => problem.Clause == "A.2" &&
                       problem.Message.Contains("EmbeddedFiles key", StringComparison.Ordinal));
    }

    [Fact]
    public void SameFileSpecificationReachedByNameAndAssociationIsCheckedOnce()
    {
        using PdfDocument document = Fresh();
        PdfValue specification = AddSpecification(
            document,
            CreateFileSpecification(document, relationship: null, includeMime: true));
        SetEmbeddedFilesTree(document, new PdfDictionary
        {
            [PdfNames.Names] = PdfValue.Array(new PdfArray(
                PdfValue.TextString("attachment.bin"), specification)),
        });
        document.Catalog.Set(
            PdfName.Get("AF"),
            PdfValue.Array(new PdfArray(specification)));

        Assert.Single(
            PdfAProfile.Validate(document, PdfAConformance.A3B),
            problem => problem.Clause == "E.2" &&
                       problem.Message.Contains("states no AFRelationship", StringComparison.Ordinal));
    }

    [Fact]
    public void FileSpecificationWithEfMustBeAnIndirectObject()
    {
        using PdfDocument document = Fresh();
        PdfDictionary specification = CreateFileSpecification(document, PdfName.Get("Data"), includeMime: true);
        SetEmbeddedFilesTree(document, new PdfDictionary
        {
            [PdfNames.Names] = PdfValue.Array(new PdfArray(
                PdfValue.TextString("attachment.bin"),
                PdfValue.Dictionary(specification))),
        });

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4F),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("not an indirect object", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileSpecificationWithEfRequiresTheExactFilespecType(bool wrongType)
    {
        using PdfDocument document = Fresh();
        PdfDictionary specification = CreateFileSpecification(document, PdfName.Get("Data"), includeMime: true);
        if (wrongType)
            specification.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("FileSpec")));
        else
            specification.Remove(PdfNames.Type);

        PdfValue reference = AddSpecification(document, specification);
        SetEmbeddedFilesTree(document, new PdfDictionary
        {
            [PdfNames.Names] = PdfValue.Array(new PdfArray(
                PdfValue.TextString("attachment.bin"), reference)),
        });

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4F),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("exact Type Filespec", StringComparison.Ordinal));
    }

    [Fact]
    public void EmbeddedFileStreamRejectsAWrongTypeButAllowsAnAbsentType()
    {
        using PdfDocument wrong = DocumentWithAttachment(PdfName.Get("Data"), includeMime: true);
        PdfStream wrongStream = wrong.Catalog.GetDictionary(PdfNames.Names)!
            .GetDictionary(PdfNames.EmbeddedFiles)!
            .GetArray(PdfNames.Names)!
            .GetDictionary(1)!
            .GetDictionary(PdfNames.EF)!
            .GetStream(PdfNames.F)!;
        wrongStream.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("File")));

        Assert.Contains(
            PdfAProfile.Validate(wrong, PdfAConformance.A4F),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("other than EmbeddedFile", StringComparison.Ordinal));

        using PdfDocument absent = DocumentWithAttachment(PdfName.Get("Data"), includeMime: true);
        PdfDictionary absentSpecification = absent.Catalog.GetDictionary(PdfNames.Names)!
            .GetDictionary(PdfNames.EmbeddedFiles)!
            .GetArray(PdfNames.Names)!
            .GetDictionary(1)!;
        absentSpecification.GetDictionary(PdfNames.EF)!
            .GetStream(PdfNames.F)!
            .Dictionary.Remove(PdfNames.Type);

        Assert.DoesNotContain(
            PdfAProfile.Validate(absent, PdfAConformance.A4F),
            problem => problem.Message.Contains("other than EmbeddedFile", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A3B, "6.9")]
    [InlineData(PdfAConformance.A4, "6.10")]
    public void DuplicateGroupCannotHideAMissingGroupInOrder(
        PdfAConformance conformance, string clause)
    {
        using PdfDocument document = DocumentWithOptionalContent(
            (first, _) => new PdfArray(first, first));

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("does not list every", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A3B, "6.9")]
    [InlineData(PdfAConformance.A4, "6.10")]
    public void HierarchyLabelsCannotHideAMissingGroupInOrder(
        PdfAConformance conformance, string clause)
    {
        using PdfDocument document = DocumentWithOptionalContent(
            (first, _) => new PdfArray(
                PdfValue.Array(new PdfArray(PdfValue.TextString("Visible layers"), first))));

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("does not list every", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A3B)]
    [InlineData(PdfAConformance.A4)]
    public void CompleteNestedOrderMayContainHierarchyLabels(PdfAConformance conformance)
    {
        using PdfDocument document = DocumentWithOptionalContent(
            (first, second) => new PdfArray(
                PdfValue.Array(new PdfArray(
                    PdfValue.TextString("Visible layers"),
                    first,
                    PdfValue.Array(new PdfArray(PdfValue.TextString("Details"), second))))));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Message.Contains("does not list every", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A3B, "6.9")]
    [InlineData(PdfAConformance.A4, "6.10")]
    public void CyclicOrderArrayIsBoundedAndStillReportsAMissingGroup(
        PdfAConformance conformance, string clause)
    {
        using PdfDocument document = DocumentWithOptionalContent((first, _) =>
        {
            var cycle = new PdfArray(first);
            cycle.Add(PdfValue.Array(cycle));
            return cycle;
        });

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("does not list every", StringComparison.OrdinalIgnoreCase));
    }

    private static PdfDocument DocumentWithAttachment(
        PdfName? relationship,
        bool includeMime,
        string mime = "application/octet-stream")
    {
        PdfDocument document = Fresh();
        PdfValue specification = AddSpecification(
            document,
            CreateFileSpecification(document, relationship, includeMime, mime));
        SetEmbeddedFilesTree(document, new PdfDictionary
        {
            [PdfNames.Names] = PdfValue.Array(new PdfArray(
                PdfValue.TextString("attachment.bin"), specification)),
        });
        return document;
    }

    private static PdfDictionary CreateFileSpecification(
        PdfDocument document,
        PdfName? relationship,
        bool includeMime,
        string mime = "application/octet-stream")
    {
        PdfStream embedded = PdfStream.FromData("payload"u8.ToArray(), compress: false);
        embedded.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.EmbeddedFile));
        if (includeMime)
            embedded.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get(mime)));
        PdfObjectId embeddedId = document.AddObject(PdfValue.Stream(embedded));

        var specification = new PdfDictionary
        {
            [PdfNames.Type] = PdfValue.Name(PdfName.Get("Filespec")),
            [PdfNames.F] = PdfValue.TextString("attachment.bin"),
            [PdfName.Get("UF")] = PdfValue.TextString("attachment.bin"),
            [PdfName.Get("EF")] = PdfValue.Dictionary(new PdfDictionary
            {
                [PdfNames.F] = PdfValue.Reference(embeddedId),
            }),
        };
        if (relationship is not null)
            specification.Set(PdfNames.AFRelationship, PdfValue.Name(relationship));

        return specification;
    }

    private static PdfValue AddSpecification(PdfDocument document, PdfDictionary specification) =>
        PdfValue.Reference(document.AddObject(PdfValue.Dictionary(specification)));

    private static void SetEmbeddedFilesTree(PdfDocument document, PdfDictionary tree)
    {
        document.Catalog.Set(PdfNames.Names, PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.EmbeddedFiles] = PdfValue.Dictionary(tree),
        }));
    }

    private static PdfDocument DocumentWithOptionalContent(
        Func<PdfValue, PdfValue, PdfArray> order)
    {
        PdfDocument document = Fresh();
        PdfValue first = Group(document, "Background");
        PdfValue second = Group(document, "Details");
        var configuration = new PdfDictionary
        {
            [PdfNames.Name] = PdfValue.TextString("Archive view"),
            [PdfName.Get("Order")] = PdfValue.Array(order(first, second)),
        };
        document.Catalog.Set(PdfName.Get("OCProperties"), PdfValue.Dictionary(new PdfDictionary
        {
            [PdfName.Get("OCGs")] = PdfValue.Array(new PdfArray(first, second)),
            [PdfNames.D] = PdfValue.Dictionary(configuration),
        }));
        return document;

        static PdfValue Group(PdfDocument owner, string name) => PdfValue.Reference(owner.AddObject(
            PdfValue.Dictionary(new PdfDictionary
            {
                [PdfNames.Type] = PdfValue.Name(PdfName.Get("OCG")),
                [PdfNames.Name] = PdfValue.TextString(name),
            })));
    }

    private static PdfDocument Fresh()
    {
        PdfDocument document = PdfDocument.Create();
        document.Version = PdfVersion.Pdf20;
        document.Pages.Add();
        return document;
    }
}
