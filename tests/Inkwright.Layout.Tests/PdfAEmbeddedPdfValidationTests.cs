using System.Text;
using Inkwright.Cos;
using Inkwright.Filters;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Layout.Tests;

public class PdfAEmbeddedPdfValidationTests
{
    [Theory]
    [InlineData(PdfAConformance.A1B)]
    [InlineData(PdfAConformance.A2B)]
    public void PartTwoAcceptsEmbeddedPdfAFromAnAllowedPart(PdfAConformance embeddedConformance)
    {
        using PdfDocument document = OuterWithAttachment("archive.pdf", Conforming(embeddedConformance));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Message.Contains("Embedded PDF", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B)]
    [InlineData(PdfAConformance.A2B)]
    [InlineData(PdfAConformance.A4)]
    public void PartFourAcceptsEmbeddedPdfAFromAnAllowedPart(PdfAConformance embeddedConformance)
    {
        using PdfDocument document = OuterWithAttachment("archive.pdf", Conforming(embeddedConformance));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Message.Contains("Embedded PDF", StringComparison.Ordinal));
    }

    [Fact]
    public void PartTwoValidatesAnXrefLiveFilespecOutsideTheCatalogGraph()
    {
        using PdfDocument document = Blank();
        PdfAProfile.Apply(document, PdfAConformance.A2B);
        PdfValue specification = AddAttachment(
            document,
            "xref-live-orphan.pdf",
            "not a PDF"u8.ToArray());
        document.Catalog.Remove(PdfNames.Names);
        document.Catalog.Remove(PdfNames.AF);

        byte[] bytes = document.ToArray(new PdfSaveOptions
        {
            SetProducer = false,
            UpdateModificationDate = false,
        });
        using (PdfDocument reopened = PdfDocument.Load(bytes))
            Assert.Contains(specification.AsReference.Number, reopened.Reader!.ObjectNumbers);

        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A2B),
            problem => problem.Clause == "6.8" &&
                       problem.Message.Contains("xref-live-orphan.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("not a PDF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PartFourKeepsClauseSixNineConditionalOnTheCatalogNameTree()
    {
        using PdfDocument document = Blank();
        PdfAProfile.Apply(document, PdfAConformance.A4);
        PdfValue specification = AddAttachment(
            document,
            "xref-live-outside-name-tree.bin",
            "not a PDF"u8.ToArray());
        document.Catalog.Remove(PdfNames.Names);
        document.Catalog.Remove(PdfNames.AF);

        byte[] bytes = document.ToArray(new PdfSaveOptions
        {
            SetProducer = false,
            UpdateModificationDate = false,
        });
        using (PdfDocument reopened = PdfDocument.Load(bytes))
            Assert.Contains(specification.AsReference.Number, reopened.Reader!.ObjectNumbers);

        Assert.DoesNotContain(
            PdfAProfile.Validate(bytes, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("xref-live-outside-name-tree.bin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, PdfAConformance.A3B, "6.8")]
    [InlineData(PdfAConformance.A2B, PdfAConformance.A4, "6.8")]
    [InlineData(PdfAConformance.A4, PdfAConformance.A3B, "6.9")]
    public void RejectsAnEmbeddedPdfAFromAPartTheContainerDoesNotPermit(
        PdfAConformance container,
        PdfAConformance embedded,
        string clause)
    {
        using PdfDocument document = OuterWithAttachment("later-part.pdf", Conforming(embedded));

        Assert.Contains(
            PdfAProfile.Validate(document, container),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("later-part.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains(embedded.Name, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void RejectsNonPdfBytesEvenWhenTheMimeTypeClaimsPdf(
        PdfAConformance conformance,
        string clause)
    {
        using PdfDocument document = OuterWithAttachment("not-a-pdf.pdf", "plain text"u8.ToArray());

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("not-a-pdf.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("not a PDF", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void RejectsMalformedPdfBytes(PdfAConformance conformance, string clause)
    {
        using PdfDocument document = OuterWithAttachment(
            "broken.pdf",
            "%PDF-2.0\n1 0 obj\n<< /Type /Catalog"u8.ToArray());

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("broken.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("cannot be opened", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Root", "Root entry")]
    [InlineData("Pages", "Pages entry")]
    [InlineData("Kids", "Kids entry")]
    public void RecursiveReaderRejectsDirectRequiredDocumentStructure(
        string shape,
        string expectedMessage)
    {
        using PdfDocument document = OuterWithAttachment(
            $"direct-{shape.ToLowerInvariant()}.pdf",
            PdfWithDirectRequiredStructure(shape));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains(expectedMessage, StringComparison.Ordinal) &&
                       problem.Message.Contains("indirect reference", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void RejectsAnEncryptedEmbeddedPdf(PdfAConformance conformance, string clause)
    {
        using PdfDocument encrypted = Blank();
        byte[] bytes = encrypted.ToArray(new PdfSaveOptions
        {
            Encryption = new PdfEncryptionSettings { UserPassword = "open" },
        });
        using PdfDocument document = OuterWithAttachment("sealed.pdf", bytes);

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("sealed.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void RejectsAPdfThatMakesNoPdfAClaim(PdfAConformance conformance, string clause)
    {
        using PdfDocument ordinary = Blank();
        using PdfDocument document = OuterWithAttachment("ordinary.pdf", ordinary.ToArray());

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("ordinary.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("PDF/A identification", StringComparison.Ordinal));
    }

    [Fact]
    public void TruncatedFlateMetadataCannotSupplyARecursivePdfAClaim()
    {
        byte[] childBytes;
        byte[] metadataBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            PdfStream metadata = child.Catalog.Get(PdfName.Get("Metadata")).AsStream!;
            metadataBytes = metadata.DecodedSpan().ToArray();
            byte[] encoded = FlateFilter.Encode(metadataBytes);
            metadata.Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfNames.FlateDecode));
            metadata.SetRawData(encoded[..^4]);
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        // The compatibility reader can recover the complete prefix. Recursive validation must not
        // use that tolerant result as an archival claim.
        using (PdfDocument tolerant = PdfDocument.Load(childBytes))
        {
            Assert.Equal(
                metadataBytes,
                tolerant.Catalog.Get(PdfName.Get("Metadata")).AsStream!.DecodedSpan().ToArray());
        }

        using PdfDocument outer = OuterWithAttachment("truncated-metadata.pdf", childBytes);

        Assert.Contains(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("truncated-metadata.pdf", StringComparison.Ordinal) &&
                       (problem.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                        problem.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase) ||
                       problem.Message.Contains("validated", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void InvalidUtf8MetadataCannotSupplyARecursivePdfAClaim()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            PdfStream metadata = child.Catalog.Get(PdfNames.Metadata).AsStream!;
            byte[] packet = metadata.DecodedSpan().ToArray();
            ReadOnlySpan<byte> partElement = "<pdfaid:part>4</pdfaid:part>"u8;
            int part = packet.AsSpan().IndexOf(partElement);
            Assert.True(part >= 0);
            packet[part + "<pdfaid:part>"u8.Length] = 0xFF;
            metadata.SetData(packet, compress: false);
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = OuterWithAttachment("invalid-utf8-xmp.pdf", childBytes);

        Assert.Contains(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("invalid-utf8-xmp.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("invalid encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Utf16MetadataCanSupplyARecursivePdfAClaim()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            PdfStream metadata = child.Catalog.Get(PdfNames.Metadata).AsStream!;
            string packet = Encoding.UTF8.GetString(metadata.DecodedSpan());
            byte[] body = Encoding.Unicode.GetBytes(packet);
            byte[] utf16 = new byte[Encoding.Unicode.Preamble.Length + body.Length];
            Encoding.Unicode.Preamble.CopyTo(utf16.AsSpan());
            body.CopyTo(utf16, Encoding.Unicode.Preamble.Length);
            metadata.SetData(utf16, compress: false);
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = OuterWithAttachment("utf16-xmp.pdf", childBytes);

        Assert.DoesNotContain(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Message.Contains("utf16-xmp.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("Embedded PDF", StringComparison.Ordinal));
    }

    [Fact]
    public void XmpDtdCannotSupplyARecursivePdfAClaim()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            PdfStream metadata = child.Catalog.Get(PdfNames.Metadata).AsStream!;
            string packet = Encoding.UTF8.GetString(metadata.DecodedSpan());
            packet = packet.Replace(
                "<x:xmpmeta",
                "<!DOCTYPE x:xmpmeta []><x:xmpmeta",
                StringComparison.Ordinal);
            metadata.SetData(Encoding.UTF8.GetBytes(packet), compress: false);
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = OuterWithAttachment("dtd-xmp.pdf", childBytes);

        Assert.Contains(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("dtd-xmp.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public void RecursiveXmpByteLimitIsExactAndTheParsedPacketIsReused(int adjustment, bool rejected)
    {
        byte[] childBytes;
        int xmpBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            xmpBytes = child.Catalog.Get(PdfNames.Metadata).AsStream!.DecodedSpan().Length;
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = OuterWithAttachment("bounded-xmp.pdf", childBytes);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 16 * 1024 * 1024,
            maxTotalXmpBytes: xmpBytes + adjustment);

        bool found = PdfAValidator.Validate(outer, PdfAConformance.A4, limits).Any(
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("bounded-xmp.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("XMP byte limit", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(rejected, found);
    }

    [Fact]
    public void RecursiveXmpCharacterLimitIsEnforcedDuringXmlParsing()
    {
        byte[] childBytes;
        int xmpCharacters;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            PdfStream metadata = child.Catalog.Get(PdfNames.Metadata).AsStream!;
            xmpCharacters = Encoding.UTF8.GetString(metadata.DecodedSpan()).Length;
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = OuterWithAttachment("character-bounded-xmp.pdf", childBytes);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 16 * 1024 * 1024,
            maxTotalXmpBytes: 16 * 1024 * 1024,
            maxTotalXmpCharacters: xmpCharacters - 1L);

        Assert.Contains(
            PdfAValidator.Validate(outer, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("character-bounded-xmp.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("XMP character limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecursiveChildrenShareOneXmpByteLedger()
    {
        byte[] childBytes;
        int xmpBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            xmpBytes = child.Catalog.Get(PdfNames.Metadata).AsStream!.DecodedSpan().Length;
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = Blank();
        AddAttachment(outer, "first-xmp.pdf", childBytes);
        AddAttachment(outer, "second-xmp.pdf", childBytes);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 16 * 1024 * 1024,
            maxTotalXmpBytes: xmpBytes * 2L - 1L);

        Assert.Contains(
            PdfAValidator.Validate(outer, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("XMP byte limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompressedRootXmpIsBoundedBeforeItIsMaterialized()
    {
        const int xmpLimit = 8 * 1024;
        byte[] file;
        int decodedLength;
        using (PdfDocument source = Blank())
        {
            PdfAProfile.Apply(source, PdfAConformance.A4);
            PdfStream metadata = source.Catalog.Get(PdfNames.Metadata).AsStream!;
            string packet = Encoding.UTF8.GetString(metadata.DecodedSpan())
                .Replace("</x:xmpmeta>", new string(' ', 64 * 1024) + "</x:xmpmeta>", StringComparison.Ordinal);
            byte[] decoded = Encoding.UTF8.GetBytes(packet);
            decodedLength = decoded.Length;
            metadata.SetData(decoded, compress: true);
            file = source.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        Assert.True(decodedLength > xmpLimit);
        using PdfDocument document = PdfDocument.Load(file);
        Assert.True(document.Catalog.Get(PdfNames.Metadata).AsStream!.RawLength < xmpLimit);
        var limits = new PdfAValidationLimits(maxTotalXmpBytes: xmpLimit);

        Assert.Contains(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.7.2.1" &&
                       problem.Message.Contains("XMP byte limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MetadataAliasedAsEfCannotBypassTheXmpDecodeCap()
    {
        const int xmpLimit = 8 * 1024;
        byte[] childBytes = PdfA4FWithMetadataAliasedAsEf(64 * 1024, out int decodedXmpBytes);
        Assert.True(decodedXmpBytes > xmpLimit);
        using PdfDocument outer = OuterWithAttachment("metadata-ef-bomb.pdf", childBytes);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 32 * 1024 * 1024,
            maxTotalXmpBytes: xmpLimit);

        Assert.Contains(
            PdfAValidator.Validate(outer, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("metadata-ef-bomb.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("XMP byte limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MetadataAliasedAsEfIsDecodedAndChargedOnlyOnce()
    {
        byte[] childBytes = PdfA4FWithMetadataAliasedAsEf(0, out int decodedXmpBytes);
        using PdfDocument outer = OuterWithAttachment("metadata-ef-once.pdf", childBytes);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 32 * 1024 * 1024,
            maxTotalXmpBytes: decodedXmpBytes);

        IReadOnlyList<PdfAProblem> problems =
            PdfAValidator.Validate(outer, PdfAConformance.A4, limits);

        Assert.Contains(
            problems,
            problem => problem.Message.Contains("metadata-ef-once.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("Type /Metadata", StringComparison.Ordinal));
        Assert.DoesNotContain(
            problems,
            problem => problem.Message.Contains("metadata-ef-once.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("XMP byte limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AggregateInternalStreamDecodeWorkSharesTheRecursiveBudget()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            for (int i = 0; i < 6; i++)
            {
                PdfObjectId streamId = child.AddObject(
                    PdfValue.Stream(PdfStream.FromData(new byte[4_096], compress: true)));
                child.Catalog.Set(
                    PdfName.Get($"BudgetProbe{i}"),
                    PdfValue.Reference(streamId));
            }

            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = OuterWithAttachment("internal-work.pdf", childBytes);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: childBytes.Length * 2L);

        Assert.Contains(
            PdfAValidator.Validate(outer, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("internal-work.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("decode-work", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void APartFourBogusEfDictionaryCannotHideAMalformedInternalStream()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            byte[] encoded = FlateFilter.Encode("malformed internal stream"u8);
            var malformed = new PdfStream(
                new PdfDictionary
                {
                    [PdfNames.Filter] = PdfValue.Name(PdfNames.FlateDecode),
                },
                encoded[..^4]);
            PdfObjectId streamId = child.AddObject(PdfValue.Stream(malformed));
            var bogus = new PdfDictionary
            {
                [PdfNames.EF] = PdfValue.Dictionary(new PdfDictionary
                {
                    [PdfNames.F] = PdfValue.Reference(streamId),
                }),
            };
            PdfObjectId bogusId = child.AddObject(PdfValue.Dictionary(bogus));
            child.Catalog.Set(PdfName.Get("Bogus"), PdfValue.Reference(bogusId));
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = OuterWithAttachment("bogus-ef.pdf", childBytes);

        Assert.Contains(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("bogus-ef.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecursiveReaderRejectsExternalFileKeysOnAnInternalStream()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            var stream = new PdfStream(
                new PdfDictionary
                {
                    [PdfNames.F] = PdfValue.Name(PdfNames.FlateDecode),
                },
                "payload"u8.ToArray());
            PdfObjectId streamId = child.AddObject(PdfValue.Stream(stream));
            child.Catalog.Set(PdfName.Get("ExternalStream"), PdfValue.Reference(streamId));
            childBytes = child.ToArray(new PdfSaveOptions
            {
                SetProducer = false,
                UpdateModificationDate = false,
            });
        }

        using PdfDocument outer = OuterWithAttachment("external-stream.pdf", childBytes);

        Assert.Contains(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("external-stream.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("external-file stream keys", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProtectedReaderStopsParserAllocationsAtTheSharedValueLimit()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A4);
            var probe = new PdfArray(128);
            for (int i = 0; i < 128; i++)
                probe.Add(PdfValue.Integer(i));
            child.Catalog.Set(PdfName.Get("Probe"), PdfValue.Array(probe));
            childBytes = child.ToArray();
        }

        using PdfDocument outer = OuterWithAttachment("parser-budget.pdf", childBytes);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 16 * 1024 * 1024,
            maxGraphValues: 1_024,
            maxParsedValues: 100);

        Assert.Contains(
            PdfAValidator.Validate(outer, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("parser-budget.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("parsed values", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsAnUnknownPartFourRevisionInsteadOfApplyingThe2020Rules()
    {
        using PdfDocument embedded = Blank();
        PdfAProfile.Apply(embedded, PdfAConformance.A4);
        PdfStream metadata = embedded.Catalog.Get(PdfName.Get("Metadata")).AsStream!;
        string packet = Encoding.UTF8.GetString(metadata.DecodedSpan())
            .Replace(">2020</pdfaid:rev>", ">2099</pdfaid:rev>", StringComparison.Ordinal);
        metadata.SetData(Encoding.UTF8.GetBytes(packet), compress: false);
        using PdfDocument document = OuterWithAttachment("future-revision.pdf", embedded.ToArray());

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("future-revision.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("unsupported PDF/A-4 revision", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains("2099", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B)]
    [InlineData(PdfAConformance.A4)]
    public void AGenericMimeTypeDoesNotInvalidateAConformingEmbeddedPdf(PdfAConformance conformance)
    {
        using PdfDocument document = Blank();
        AddAttachment(
            document,
            "archive.pdf",
            Conforming(conformance == PdfAConformance.A2B ? PdfAConformance.A2B : PdfAConformance.A4),
            mime: "application/octet-stream");

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Message.Contains("archive.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("Embedded PDF", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A3B)]
    [InlineData(PdfAConformance.A4E)]
    [InlineData(PdfAConformance.A4F)]
    public void ArbitraryFileProfilesDoNotRecursivelyOpenAttachments(PdfAConformance conformance)
    {
        using PdfDocument document = OuterWithAttachment("payload.bin", "not a PDF"u8.ToArray());

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Message.Contains("Embedded PDF", StringComparison.Ordinal) ||
                        problem.Message.Contains("cannot be opened", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A3A, "6.1.7.2")]
    [InlineData(PdfAConformance.A3B, "6.1.7.2")]
    [InlineData(PdfAConformance.A3U, "6.1.7.2")]
    [InlineData(PdfAConformance.A4E, "6.1.6.2")]
    [InlineData(PdfAConformance.A4F, "6.1.6.2")]
    public void ArbitraryFileProfilesRejectMalformedFlatePayloads(
        PdfAConformance conformance,
        string clause)
    {
        byte[] payload = "arbitrary payload"u8.ToArray();
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(
            document,
            "malformed-flate.bin",
            payload,
            mime: "application/octet-stream");
        byte[] encoded = FlateFilter.Encode(payload);
        ConfigureFilters(
            EmbeddedStream(document, specification),
            encoded[..^4],
            PdfNames.FlateDecode);

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("malformed-flate.bin", StringComparison.Ordinal) &&
                       problem.Message.Contains("invalid or truncated", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains("FlateDecode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A3B, "6.1.7.2")]
    [InlineData(PdfAConformance.A4E, "6.1.6.2")]
    [InlineData(PdfAConformance.A4F, "6.1.6.2")]
    public void ArbitraryFileProfilesAcceptValidFlatePayloads(
        PdfAConformance conformance,
        string clause)
    {
        byte[] payload = "arbitrary payload that is deliberately not a PDF"u8.ToArray();
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(
            document,
            "valid-flate.bin",
            payload,
            mime: "application/octet-stream");
        ConfigureFilters(
            EmbeddedStream(document, specification),
            FlateFilter.Encode(payload),
            PdfNames.FlateDecode);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("valid-flate.bin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DCTDecode")]
    [InlineData("JPXDecode")]
    public void ArbitraryFilesAllowATerminalImageCodecAfterAValidatedPrefix(string terminalFilter)
    {
        byte[] nativeImageBytes = "opaque native image payload"u8.ToArray();
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(
            document,
            "native-image.bin",
            nativeImageBytes,
            mime: "application/octet-stream");
        ConfigureFilters(
            EmbeddedStream(document, specification),
            FlateFilter.Encode(nativeImageBytes),
            PdfNames.FlateDecode,
            PdfName.Get(terminalFilter));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4F),
            problem => problem.Clause == "6.1.6.2" &&
                       problem.Message.Contains("native-image.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void TerminalImageCodecCannotHideAMalformedGeneralPurposePrefix()
    {
        byte[] nativeImageBytes = "opaque native image payload"u8.ToArray();
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(
            document,
            "malformed-image-prefix.bin",
            nativeImageBytes,
            mime: "application/octet-stream");
        byte[] encoded = FlateFilter.Encode(nativeImageBytes);
        ConfigureFilters(
            EmbeddedStream(document, specification),
            encoded[..^4],
            PdfNames.FlateDecode,
            PdfNames.DCTDecode);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4F),
            problem => problem.Clause == "6.1.6.2" &&
                       problem.Message.Contains("malformed-image-prefix.bin", StringComparison.Ordinal) &&
                       problem.Message.Contains("invalid or truncated", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains("FlateDecode", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedArbitraryFilterFailurePropagatesWithItsCompletePath()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            byte[] payload = "nested arbitrary payload"u8.ToArray();
            PdfValue specification = AddAttachment(
                child,
                "payload.bin",
                payload,
                mime: "application/octet-stream");
            PdfAProfile.Apply(child, PdfAConformance.A4F);
            byte[] encoded = FlateFilter.Encode(payload);
            ConfigureFilters(
                EmbeddedStream(child, specification),
                encoded[..^4],
                PdfNames.FlateDecode);
            childBytes = child.ToArray();
        }

        using PdfDocument outer = Blank();
        PdfAProfile.Apply(outer, PdfAConformance.A4);
        AddAttachment(outer, "child.pdf", childBytes);

        Assert.Contains(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("child.pdf -> payload.bin", StringComparison.Ordinal) &&
                       problem.Message.Contains("ISO 19005 §6.1.6.2", StringComparison.Ordinal) &&
                       problem.Message.Contains("invalid or truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecursiveStrictPipelineDoesNotInterpretInlineDecodeParameters()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            byte[] payload = "nested arbitrary payload"u8.ToArray();
            PdfValue specification = AddAttachment(
                child,
                "inline-dp.bin",
                payload,
                mime: "application/octet-stream");
            PdfStream stream = EmbeddedStream(child, specification);
            ConfigureFilters(stream, FlateFilter.Encode(payload), PdfNames.FlateDecode);
            stream.Dictionary.Set(PdfName.Get("DP"), PdfValue.Integer(7));
            PdfAProfile.Apply(child, PdfAConformance.A4F);
            childBytes = child.ToArray();
        }

        using PdfDocument outer = Blank();
        PdfAProfile.Apply(outer, PdfAConformance.A4);
        AddAttachment(outer, "child.pdf", childBytes);

        Assert.DoesNotContain(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Message.Contains(
                "child.pdf -> inline-dp.bin",
                StringComparison.Ordinal));
    }

    [Fact]
    public void NestedArbitraryFilesDoNotConsumeRecursiveFileOrDepthSlots()
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            AddAttachment(
                child,
                "payload.bin",
                "not a PDF"u8.ToArray(),
                mime: "application/octet-stream");
            PdfAProfile.Apply(child, PdfAConformance.A4F);
            childBytes = child.ToArray();
        }

        using PdfDocument outer = Blank();
        PdfAProfile.Apply(outer, PdfAConformance.A4);
        AddAttachment(outer, "child.pdf", childBytes);
        var limits = new PdfAValidationLimits(
            maxDepth: 1,
            maxEmbeddedFiles: 1,
            maxTotalDecodedBytes: 16 * 1024 * 1024);

        IReadOnlyList<PdfAProblem> problems =
            PdfAValidator.Validate(outer, PdfAConformance.A4, limits);

        Assert.DoesNotContain(
            problems,
            problem => problem.Message.Contains("file-count limit", StringComparison.OrdinalIgnoreCase) ||
                       problem.Message.Contains("nesting limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ArbitraryFlatePayloadsShareTheCumulativeDecodeWorkBudget()
    {
        byte[] first = Enumerable.Repeat((byte)'A', 96).ToArray();
        byte[] second = Enumerable.Repeat((byte)'B', 96).ToArray();
        using PdfDocument document = Blank();
        PdfValue firstSpecification = AddAttachment(
            document,
            "first.bin",
            first,
            mime: "application/octet-stream");
        PdfValue secondSpecification = AddAttachment(
            document,
            "second.bin",
            second,
            mime: "application/octet-stream");
        ConfigureFilters(
            EmbeddedStream(document, firstSpecification),
            FlateFilter.Encode(first),
            PdfNames.FlateDecode);
        ConfigureFilters(
            EmbeddedStream(document, secondSpecification),
            FlateFilter.Encode(second),
            PdfNames.FlateDecode);
        var limits = new PdfAValidationLimits(
            maxDepth: 1,
            maxEmbeddedFiles: 1,
            maxTotalDecodedBytes: 144);

        IReadOnlyList<PdfAProblem> problems =
            PdfAValidator.Validate(document, PdfAConformance.A4F, limits);

        Assert.Contains(
            problems,
            problem => problem.Clause == "6.1.6.2" &&
                       problem.Message.Contains("decode-work", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            problems,
            problem => problem.Message.Contains("file-count limit", StringComparison.OrdinalIgnoreCase) ||
                       problem.Message.Contains("nesting limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BasePartFourDoesNotApplyEmbeddedFilesRulesToAnAfOnlyFileSpecification()
    {
        using PdfDocument document = OuterWithAttachment("schema.bin", "not a PDF"u8.ToArray());
        document.Catalog.GetDictionary(PdfNames.Names)!.Remove(PdfNames.EmbeddedFiles);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("schema.bin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void DistinctEfFileAndUnicodeFileStreamsAreBothValidated(
        PdfAConformance conformance,
        string clause)
    {
        using PdfDocument document = Blank();
        PdfValue specification = document.Attachments.Add(
            "two-payloads.pdf",
            Conforming(conformance),
            "application/pdf",
            relationship: AttachmentRelationship.Data);
        PdfDictionary fileSpecification = document.Resolve(specification).AsDictionary!;
        PdfDictionary embeddedFiles = fileSpecification.GetDictionary(PdfNames.EF)!;
        PdfStream invalidUnicodePayload = PdfStream.FromData("not a PDF"u8.ToArray(), compress: false);
        invalidUnicodePayload.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.EmbeddedFile));
        invalidUnicodePayload.Dictionary.Set(
            PdfNames.Subtype,
            PdfValue.Name(PdfName.Get("application/octet-stream")));
        PdfObjectId invalidId = document.AddObject(PdfValue.Stream(invalidUnicodePayload));
        embeddedFiles.Set(PdfNames.UF, PdfValue.Reference(invalidId));

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("two-payloads.pdf [EF/UF]", StringComparison.Ordinal) &&
                       problem.Message.Contains("not a PDF", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void AlternativeEfPayloadsAreRecursivelyValidated(
        PdfAConformance conformance,
        string clause)
    {
        using PdfDocument document = Blank();
        PdfValue specificationValue = AddAttachment(
            document,
            "alternative.pdf",
            Conforming(conformance));
        PdfDictionary specification = document.Resolve(specificationValue).AsDictionary!;
        PdfName dos = PdfName.Get("DOS");
        specification.Set(dos, PdfValue.String("alternative.pdf"u8.ToArray()));
        PdfValue alternative = AddEmbeddedFileStream(document, "not a PDF"u8.ToArray());
        specification.GetDictionary(PdfNames.EF)!.Set(dos, alternative);

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("alternative.pdf [EF/DOS]", StringComparison.Ordinal) &&
                       problem.Message.Contains("not a PDF", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void RelatedFilePayloadsAreRecursivelyValidated(
        PdfAConformance conformance,
        string clause)
    {
        using PdfDocument document = Blank();
        PdfValue specificationValue = AddAttachment(
            document,
            "primary.pdf",
            Conforming(conformance));
        PdfDictionary specification = document.Resolve(specificationValue).AsDictionary!;
        PdfValue relatedPayload = AddEmbeddedFileStream(document, "not a PDF"u8.ToArray());
        specification.Set(
            PdfName.Get("RF"),
            PdfValue.Dictionary(new PdfDictionary
            {
                [PdfNames.F] = PdfValue.Array(new PdfArray(
                    PdfValue.String("related.pdf"u8.ToArray()),
                    relatedPayload)),
            }));

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("primary.pdf [RF/F: related.pdf]", StringComparison.Ordinal) &&
                       problem.Message.Contains("not a PDF", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A3B, "6.1.7.2", "EF/DOS")]
    [InlineData(PdfAConformance.A3B, "6.1.7.2", "RF/F")]
    [InlineData(PdfAConformance.A4F, "6.1.6.2", "EF/DOS")]
    [InlineData(PdfAConformance.A4F, "6.1.6.2", "RF/F")]
    public void ArbitraryProfilesStrictlyDecodeAlternativeAndRelatedPayloads(
        PdfAConformance conformance,
        string clause,
        string location)
    {
        using PdfDocument document = Blank();
        PdfValue specificationValue = AddAttachment(
            document,
            "primary.bin",
            "primary payload"u8.ToArray(),
            mime: "application/octet-stream");
        PdfDictionary specification = document.Resolve(specificationValue).AsDictionary!;
        byte[] decoded = "alternative payload"u8.ToArray();
        byte[] malformed = FlateFilter.Encode(decoded)[..^4];
        PdfValue payloadValue = AddEmbeddedFileStream(document, decoded);
        PdfStream payload = document.Resolve(payloadValue).AsStream!;
        ConfigureFilters(payload, malformed, PdfNames.FlateDecode);

        if (location == "EF/DOS")
        {
            PdfName dos = PdfName.Get("DOS");
            specification.Set(dos, PdfValue.String("alternative.bin"u8.ToArray()));
            specification.GetDictionary(PdfNames.EF)!.Set(dos, payloadValue);
        }
        else
        {
            specification.Set(
                PdfName.Get("RF"),
                PdfValue.Dictionary(new PdfDictionary
                {
                    [PdfNames.F] = PdfValue.Array(new PdfArray(
                        PdfValue.String("related.bin"u8.ToArray()),
                        payloadValue)),
                }));
        }

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains(location, StringComparison.Ordinal) &&
                       problem.Message.Contains("invalid or truncated", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains("FlateDecode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("EF/DOS")]
    [InlineData("RF/F")]
    public void NestedAlternativeAndRelatedFailuresRetainTheirPayloadPath(string location)
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfValue specificationValue = AddAttachment(
                child,
                "primary.bin",
                "primary payload"u8.ToArray(),
                mime: "application/octet-stream");
            PdfAProfile.Apply(child, PdfAConformance.A4F);
            PdfDictionary specification = child.Resolve(specificationValue).AsDictionary!;
            byte[] decoded = "alternative payload"u8.ToArray();
            PdfValue payloadValue = AddEmbeddedFileStream(child, decoded);
            ConfigureFilters(
                child.Resolve(payloadValue).AsStream!,
                FlateFilter.Encode(decoded)[..^4],
                PdfNames.FlateDecode);

            if (location == "EF/DOS")
            {
                PdfName dos = PdfName.Get("DOS");
                specification.Set(dos, PdfValue.String("alternative.bin"u8.ToArray()));
                specification.GetDictionary(PdfNames.EF)!.Set(dos, payloadValue);
            }
            else
            {
                specification.Set(
                    PdfName.Get("RF"),
                    PdfValue.Dictionary(new PdfDictionary
                    {
                        [PdfNames.F] = PdfValue.Array(new PdfArray(
                            PdfValue.String("related.bin"u8.ToArray()),
                            payloadValue)),
                    }));
            }

            childBytes = child.ToArray();
        }

        using PdfDocument outer = Blank();
        AddAttachment(outer, "child.pdf", childBytes);

        Assert.Contains(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains($"child.pdf -> primary.bin [{location}", StringComparison.Ordinal) &&
                       problem.Message.Contains("ISO 19005 §6.1.6.2", StringComparison.Ordinal) &&
                       problem.Message.Contains("invalid or truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ef-unknown-key")]
    [InlineData("ef-alternative-not-stream")]
    [InlineData("ef-alternative-without-name")]
    [InlineData("rf-not-dictionary")]
    [InlineData("rf-without-ef")]
    [InlineData("rf-unknown-key")]
    [InlineData("rf-key-missing-from-ef")]
    [InlineData("rf-value-not-array")]
    [InlineData("rf-odd-array")]
    [InlineData("rf-name-not-string")]
    [InlineData("rf-payload-not-stream")]
    public void AlternativeAndRelatedFileShapesFailClosed(string shape)
    {
        using PdfDocument document = Blank();
        PdfValue specificationValue = AddAttachment(
            document,
            "structural.bin",
            "primary"u8.ToArray(),
            mime: "application/octet-stream");
        PdfDictionary specification = document.Resolve(specificationValue).AsDictionary!;
        PdfDictionary ef = specification.GetDictionary(PdfNames.EF)!;
        PdfName rfName = PdfName.Get("RF");
        PdfName dos = PdfName.Get("DOS");
        PdfValue relatedPayload = AddEmbeddedFileStream(document, "related"u8.ToArray());

        switch (shape)
        {
            case "ef-unknown-key":
                ef.Set(PdfName.Get("UnknownPlatform"), relatedPayload);
                break;
            case "ef-alternative-not-stream":
                specification.Set(dos, PdfValue.String("alternative.bin"u8.ToArray()));
                ef.Set(dos, PdfValue.Integer(7));
                break;
            case "ef-alternative-without-name":
                ef.Set(dos, relatedPayload);
                break;
            case "rf-not-dictionary":
                specification.Set(rfName, PdfValue.Integer(7));
                break;
            case "rf-without-ef":
                specification.Remove(PdfNames.EF);
                specification.Set(rfName, PdfValue.Dictionary(new PdfDictionary()));
                break;
            case "rf-unknown-key":
                specification.Set(rfName, PdfValue.Dictionary(new PdfDictionary
                {
                    [PdfName.Get("UnknownPlatform")] = PdfValue.Array(new PdfArray()),
                }));
                break;
            case "rf-key-missing-from-ef":
                specification.Set(rfName, RelatedFiles(dos, relatedPayload));
                break;
            case "rf-value-not-array":
                specification.Set(rfName, PdfValue.Dictionary(new PdfDictionary
                {
                    [PdfNames.F] = PdfValue.Integer(7),
                }));
                break;
            case "rf-odd-array":
                specification.Set(rfName, PdfValue.Dictionary(new PdfDictionary
                {
                    [PdfNames.F] = PdfValue.Array(new PdfArray(
                        PdfValue.String("related.bin"u8.ToArray()))),
                }));
                break;
            case "rf-name-not-string":
                specification.Set(rfName, PdfValue.Dictionary(new PdfDictionary
                {
                    [PdfNames.F] = PdfValue.Array(new PdfArray(
                        PdfValue.Integer(7),
                        relatedPayload)),
                }));
                break;
            default:
                specification.Set(rfName, PdfValue.Dictionary(new PdfDictionary
                {
                    [PdfNames.F] = PdfValue.Array(new PdfArray(
                        PdfValue.String("related.bin"u8.ToArray()),
                        PdfValue.Integer(7))),
                }));
                break;
        }

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4F),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("structural.bin", StringComparison.Ordinal) &&
                       problem.Message.Contains("cannot be established", StringComparison.OrdinalIgnoreCase));

        static PdfValue RelatedFiles(PdfName key, PdfValue payload) =>
            PdfValue.Dictionary(new PdfDictionary
            {
                [key] = PdfValue.Array(new PdfArray(
                    PdfValue.String("related.bin"u8.ToArray()),
                    payload)),
            });
    }

    [Fact]
    public void OneStreamReusedAcrossEfAndRfConsumesOneRecursiveSlot()
    {
        using PdfDocument document = Blank();
        PdfValue specificationValue = AddAttachment(
            document,
            "shared.pdf",
            Conforming(PdfAConformance.A4));
        PdfDictionary specification = document.Resolve(specificationValue).AsDictionary!;
        PdfDictionary ef = specification.GetDictionary(PdfNames.EF)!;
        PdfValue shared = ef.Get(PdfNames.F);
        PdfName dos = PdfName.Get("DOS");
        specification.Set(dos, PdfValue.String("shared.pdf"u8.ToArray()));
        ef.Set(dos, shared);
        specification.Set(
            PdfName.Get("RF"),
            PdfValue.Dictionary(new PdfDictionary
            {
                [PdfNames.F] = PdfValue.Array(new PdfArray(
                    PdfValue.String("related.pdf"u8.ToArray()),
                    shared)),
            }));
        var limits = new PdfAValidationLimits(
            maxDepth: 2,
            maxEmbeddedFiles: 1,
            maxTotalDecodedBytes: 16 * 1024 * 1024);

        Assert.DoesNotContain(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Message.Contains("file-count limit", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void RecursiveStrictPipelineRejectsInlineOnlyFilterAbbreviations(
        PdfAConformance outerConformance,
        string outerClause)
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A1B);
            byte[] content = "q Q"u8.ToArray();
            var stream = new PdfStream(
                new PdfDictionary
                {
                    [PdfNames.Filter] = PdfValue.Name(PdfName.Get("Fl")),
                },
                FlateFilter.Encode(content));
            PdfObjectId streamId = child.AddObject(PdfValue.Stream(stream));
            child.Pages[0].Dictionary.Set(PdfNames.Contents, PdfValue.Reference(streamId));
            childBytes = child.ToArray();
        }

        using PdfDocument outer = Blank();
        AddAttachment(outer, "abbreviated-filter.pdf", childBytes);

        Assert.Contains(
            PdfAProfile.Validate(outer, outerConformance),
            problem => problem.Clause == outerClause &&
                       problem.Message.Contains("abbreviated-filter.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("Fl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Fl")]
    [InlineData("A85")]
    [InlineData("AHx")]
    [InlineData("RL")]
    [InlineData("DCT")]
    [InlineData("CCF")]
    [InlineData("LZW")]
    public void RecursiveStrictPipelineRejectsEveryInlineOnlyFilterAbbreviation(string filter)
    {
        byte[] childBytes;
        using (PdfDocument child = Blank())
        {
            PdfAProfile.Apply(child, PdfAConformance.A1B);
            byte[] content = "q Q"u8.ToArray();
            byte[] encoded = filter switch
            {
                "Fl" => FlateFilter.Encode(content),
                "A85" => Ascii85Filter.Encode(content),
                "AHx" => AsciiHexFilter.Encode(content),
                "RL" => RunLengthFilter.Encode(content),
                _ => content,
            };
            var stream = new PdfStream(
                new PdfDictionary
                {
                    [PdfNames.Filter] = PdfValue.Name(PdfName.Get(filter)),
                },
                encoded);
            PdfObjectId streamId = child.AddObject(PdfValue.Stream(stream));
            child.Pages[0].Dictionary.Set(PdfNames.Contents, PdfValue.Reference(streamId));
            childBytes = child.ToArray();
        }

        using PdfDocument outer = Blank();
        AddAttachment(outer, "inline-abbreviation.pdf", childBytes);

        Assert.Contains(
            PdfAProfile.Validate(outer, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("inline-abbreviation.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains(filter, StringComparison.Ordinal));
    }

    [Fact]
    public void SameEfStreamReferencedByBothNamesConsumesOneValidationSlot()
    {
        using PdfDocument document = OuterWithAttachment("shared.pdf", Conforming(PdfAConformance.A4));
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 1,
            maxTotalDecodedBytes: 16 * 1024 * 1024);

        Assert.DoesNotContain(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Message.Contains("file-count limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NestedFailureReportsTheCompleteAttachmentPathAgainstTheContainersClause()
    {
        byte[] middle = ClaimedWithAttachment(
            PdfAConformance.A4,
            "inner.pdf",
            "not a PDF"u8.ToArray());
        using PdfDocument document = OuterWithAttachment("outer.pdf", middle);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("outer.pdf -> inner.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("not a PDF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AValidClaimDoesNotHideASubstantiveNestedConformanceFailure()
    {
        byte[] invalid = ClaimedButInvalid(PdfAConformance.A2B, document =>
            document.Catalog.Set(PdfName.Get("Requirements"), PdfValue.Array(new PdfArray())));
        using PdfDocument document = OuterWithAttachment("claimed-but-invalid.pdf", invalid);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("claimed-but-invalid.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("Requirements", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsAnEmbeddedStreamWhoseTerminalFilterCannotProducePdfBytes()
    {
        using PdfDocument document = Blank();
        AddAttachment(
            document,
            "image-filtered.pdf",
            Conforming(PdfAConformance.A4),
            terminalFilter: PdfNames.DCTDecode);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("image-filtered.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("DCTDecode", StringComparison.Ordinal) &&
                       problem.Message.Contains("recursively validated", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsAnUnsupportedEmbeddedStreamFilterInsteadOfTreatingRawBytesAsDecoded()
    {
        using PdfDocument document = Blank();
        AddAttachment(
            document,
            "unsupported-filter.pdf",
            Conforming(PdfAConformance.A4),
            terminalFilter: PdfName.Get("BogusDecode"));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("unsupported-filter.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("BogusDecode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("asciihex")]
    [InlineData("ascii85")]
    [InlineData("runlength")]
    [InlineData("flate")]
    public void ToleratedMalformedFilterDataCannotBecomeAConformingEmbeddedPdf(string encoding)
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(document, $"malformed-{encoding}.pdf", embedded);
        PdfStream stream = EmbeddedStream(document, specification);
        PdfName filter;
        byte[] malformed;

        switch (encoding)
        {
            case "asciihex":
                filter = PdfName.Get("ASCIIHexDecode");
                byte[] hex = AsciiHexFilter.Encode(embedded);
                malformed = [.. hex[..4], (byte)'G', .. hex[4..]];
                break;
            case "ascii85":
                filter = PdfName.Get("ASCII85Decode");
                malformed = Ascii85Filter.Encode(embedded)[..^1];
                break;
            case "runlength":
                filter = PdfName.Get("RunLengthDecode");
                malformed = RunLengthFilter.Encode(embedded)[..^1];
                break;
            default:
                filter = PdfNames.FlateDecode;
                malformed = FlateFilter.Encode(embedded)[..^4];
                break;
        }

        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Name(filter));
        stream.Dictionary.Remove(PdfNames.DecodeParms);
        stream.SetRawData(malformed);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains($"malformed-{encoding}.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("contains invalid data", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompressedPayloadUsesTheActualBoundWhenParamsSizeIsMissingOrUnderstated(bool understated)
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(document, "bounded.pdf", embedded);
        PdfStream stream = EmbeddedStream(document, specification);
        PdfDictionary parameters = stream.Dictionary.GetDictionary(PdfNames.Params)!;
        if (understated)
            parameters.Set(PdfNames.Size, PdfValue.Integer(1));
        else
            parameters.Remove(PdfNames.Size);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 128);

        IReadOnlyList<PdfAProblem> problems = PdfAValidator.Validate(document, PdfAConformance.A4, limits);

        Assert.Contains(
            problems,
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("bounded.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("budget", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            problems,
            problem => problem.Message.Contains("cannot be opened", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PredictorDimensionsCannotAllocatePastTheRecursiveBudget()
    {
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(
            document,
            "predictor-bomb.pdf",
            Conforming(PdfAConformance.A4));
        PdfStream stream = EmbeddedStream(document, specification);
        stream.Dictionary.GetDictionary(PdfNames.Params)!.Remove(PdfNames.Size);
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfNames.FlateDecode));
        stream.Dictionary.Set(PdfNames.DecodeParms, PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.Predictor] = PdfValue.Integer(12),
            [PdfNames.Colors] = PdfValue.Integer(32),
            [PdfNames.BitsPerComponent] = PdfValue.Integer(16),
            [PdfNames.Columns] = PdfValue.Integer(int.MaxValue),
        }));
        stream.SetRawData(FlateFilter.Encode([0, 1]));
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 1_024);

        Assert.Contains(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("predictor-bomb.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("safety limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OversizedPredictorNumberCannotWrapIntoAValidDecodeParameter()
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(document, "wrapped-predictor.pdf", embedded);
        PdfStream stream = EmbeddedStream(document, specification);
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfNames.FlateDecode));
        stream.Dictionary.Set(PdfNames.DecodeParms, PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.Predictor] = PdfValue.Integer(4_294_967_297),
        }));
        stream.SetRawData(FlateFilter.Encode(embedded));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("wrapped-predictor.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("malformed DecodeParms", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BoundedDecoderHandlesAValidAscii85AndFlateFilterChain()
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(document, "filter-chain.pdf", embedded);
        PdfStream stream = EmbeddedStream(document, specification);
        byte[] encoded = Ascii85Filter.Encode(FlateFilter.Encode(embedded));
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Array(new PdfArray(
            PdfValue.Name(PdfName.Get("ASCII85Decode")),
            PdfValue.Name(PdfNames.FlateDecode))));
        stream.Dictionary.Remove(PdfNames.DecodeParms);
        stream.SetRawData(encoded);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Message.Contains("filter-chain.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("Embedded PDF", StringComparison.Ordinal));
    }

    [Fact]
    public void RecursiveDecoderRejectsCryptAfterAnotherFilter()
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(document, "late-crypt.pdf", embedded);
        PdfStream stream = EmbeddedStream(document, specification);
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Array(new PdfArray(
            PdfValue.Name(PdfNames.FlateDecode),
            PdfValue.Name(PdfName.Get("Crypt")))));
        stream.Dictionary.Remove(PdfNames.DecodeParms);
        stream.SetRawData(FlateFilter.Encode(embedded));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("late-crypt.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("malformed DecodeParms", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecursiveDecoderAllowsFirstIdentityCryptBeforeFlate()
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(document, "first-crypt.pdf", embedded);
        PdfStream stream = EmbeddedStream(document, specification);
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Array(new PdfArray(
            PdfValue.Name(PdfName.Get("Crypt")),
            PdfValue.Name(PdfNames.FlateDecode))));
        stream.Dictionary.Remove(PdfNames.DecodeParms);
        stream.SetRawData(FlateFilter.Encode(embedded));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Message.Contains("first-crypt.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("Embedded PDF", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyFilterArrayCannotBypassTheDecodedByteBudget()
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(document, "empty-filter-array.pdf", embedded);
        PdfStream stream = EmbeddedStream(document, specification);
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Array(new PdfArray()));
        stream.Dictionary.GetDictionary(PdfNames.Params)!.Remove(PdfNames.Size);
        stream.SetRawData(embedded);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 128);

        Assert.Contains(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("empty-filter-array.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("budget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExcessiveFilterChainIsReportedWithoutWalkingEveryStage()
    {
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(
            document,
            "too-many-filters.pdf",
            Conforming(PdfAConformance.A4));
        PdfStream stream = EmbeddedStream(document, specification);
        var filters = new PdfArray(65);
        byte[] encoded = Conforming(PdfAConformance.A4);
        for (int i = 0; i < 65; i++)
        {
            filters.Add(PdfValue.Name(PdfNames.FlateDecode));
            encoded = FlateFilter.Encode(encoded);
        }
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Array(filters));
        stream.Dictionary.Remove(PdfNames.DecodeParms);
        stream.SetRawData(encoded);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("too-many-filters.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("more than 64 stream filters", StringComparison.Ordinal));
    }

    [Fact]
    public void RunLengthDecoderSignalsTheExactPerCallLimit()
    {
        using PdfDocument document = Blank();
        PdfValue specification = AddAttachment(
            document,
            "run-length.pdf",
            Conforming(PdfAConformance.A4));
        PdfStream stream = EmbeddedStream(document, specification);
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfName.Get("RunLengthDecode")));
        stream.Dictionary.GetDictionary(PdfNames.Params)!.Remove(PdfNames.Size);
        stream.SetRawData(new byte[] { 129, 65, 128 }); // 128 copies of 'A', then EOD
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 16);

        Assert.Contains(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("run-length.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("safety limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecursiveValidationStopsAtTheConfiguredDepth()
    {
        byte[] leaf = Conforming(PdfAConformance.A4);
        byte[] middle = ClaimedWithAttachment(PdfAConformance.A4, "middle.pdf", leaf);
        using PdfDocument document = OuterWithAttachment("outer.pdf", middle);
        var limits = new PdfAValidationLimits(
            maxDepth: 1,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 16 * 1024 * 1024);

        Assert.Contains(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("outer.pdf -> middle.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("nesting limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecursiveValidationStopsAtTheConfiguredFileCount()
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        AddAttachment(document, "01-first.pdf", embedded);
        AddAttachment(document, "02-must-not-decode.pdf", embedded, terminalFilter: PdfNames.DCTDecode);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 1,
            maxTotalDecodedBytes: 16 * 1024 * 1024);

        IReadOnlyList<PdfAProblem> problems = PdfAValidator.Validate(document, PdfAConformance.A4, limits);

        Assert.Contains(
            problems,
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("file-count limit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            problems,
            problem => problem.Message.Contains("02-must-not-decode.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("DCTDecode", StringComparison.Ordinal));
    }

    [Fact]
    public void RecursiveValidationStopsAtTheConfiguredByteBudget()
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        AddAttachment(document, "large.pdf", embedded, terminalFilter: PdfNames.DCTDecode);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: embedded.Length - 1L);

        IReadOnlyList<PdfAProblem> problems = PdfAValidator.Validate(document, PdfAConformance.A4, limits);

        Assert.Contains(
            problems,
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("large.pdf", StringComparison.Ordinal) &&
                       problem.Message.Contains("safety limit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(problems, problem => problem.Message.Contains("DCTDecode", StringComparison.Ordinal));
    }

    [Fact]
    public void CyclicEmbeddedFilesNameTreeDoesNotRepeatRecursiveValidation()
    {
        byte[] embedded = Conforming(PdfAConformance.A4);
        using PdfDocument document = Blank();
        AddAttachment(document, "once.pdf", embedded);
        PdfDictionary root = document.Catalog.GetDictionary(PdfNames.Names)!
            .GetDictionary(PdfNames.EmbeddedFiles)!;
        root.Set(
            PdfName.Get("Kids"),
            PdfValue.Array(new PdfArray(PdfValue.Dictionary(root))));
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 1,
            maxTotalDecodedBytes: 16 * 1024 * 1024);

        Assert.DoesNotContain(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Message.Contains("file-count limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContentFingerprintStopsAnEmbeddedFileThatRepeatsAnActiveAncestor()
    {
        byte[] bytes = Conforming(PdfAConformance.A4);
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 16 * 1024 * 1024);
        var validation = new PdfAValidationSession(limits, bytes);

        Assert.True(validation.TryReserve(
            "root.pdf -> root.pdf",
            declaredSize: null,
            out PdfAValidationReservation? reservation,
            out string? reservationProblem));
        Assert.Null(reservationProblem);
        using (reservation)
        {
            Assert.False(reservation!.TryAccept(bytes, out string? problem));
            Assert.Contains("content cycle", problem, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExcessivelyDeepEmbeddedFilesTreeReportsThatDiscoveryWasTruncated()
    {
        using PdfDocument document = Blank();
        PdfValue key = PdfValue.TextString("terminal.pdf");
        var current = new PdfDictionary
        {
            [PdfNames.Names] = PdfValue.Array(new PdfArray(
                key,
                PdfValue.Dictionary(new PdfDictionary()))),
            [PdfNames.Limits] = PdfValue.Array(new PdfArray(key, key)),
        };

        for (int depth = 0; depth <= 65; depth++)
        {
            PdfValue child = PdfValue.Reference(document.AddObject(PdfValue.Dictionary(current)));
            current = new PdfDictionary
            {
                [PdfName.Get("Kids")] = PdfValue.Array(new PdfArray(child)),
                [PdfNames.Limits] = PdfValue.Array(new PdfArray(key, key)),
            };
        }
        current.Remove(PdfNames.Limits);

        document.Catalog.Set(PdfNames.Names, PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.EmbeddedFiles] = PdfValue.Dictionary(current),
        }));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("name-tree depth", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains("cannot be established", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SharedObjectGraphBudgetStopsThePreValidationFlatteningPass()
    {
        using PdfDocument document = Blank();
        var kids = new PdfArray(100);
        for (int i = 0; i < 100; i++)
        {
            kids.Add(PdfValue.Dictionary(new PdfDictionary
            {
                [PdfNames.Names] = PdfValue.Array(new PdfArray()),
            }));
        }

        document.Catalog.Set(PdfNames.Names, PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.EmbeddedFiles] = PdfValue.Dictionary(new PdfDictionary
            {
                [PdfName.Get("Kids")] = PdfValue.Array(kids),
            }),
        }));
        var limits = new PdfAValidationLimits(
            maxDepth: 4,
            maxEmbeddedFiles: 8,
            maxTotalDecodedBytes: 16 * 1024 * 1024,
            maxGraphValues: 64);

        Assert.Contains(
            PdfAValidator.Validate(document, PdfAConformance.A4, limits),
            problem => problem.Clause == "6.1" &&
                       problem.Message.Contains("64 reachable values", StringComparison.Ordinal) &&
                       problem.Message.Contains("cannot be established", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExcessivelyWideEmbeddedFilesTreeReportsItsOwnTraversalLimit()
    {
        using PdfDocument document = Blank();
        var kids = new PdfArray(5_000);
        PdfValue sharedSpecification = PdfValue.Dictionary(new PdfDictionary());
        for (int i = 0; i < 5_000; i++)
        {
            PdfValue key = PdfValue.TextString(i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
            var leaf = new PdfDictionary
            {
                [PdfNames.Names] = PdfValue.Array(new PdfArray(
                    key,
                    sharedSpecification)),
                [PdfNames.Limits] = PdfValue.Array(new PdfArray(key, key)),
            };
            kids.Add(PdfValue.Reference(document.AddObject(PdfValue.Dictionary(leaf))));
        }

        document.Catalog.Set(PdfNames.Names, PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.EmbeddedFiles] = PdfValue.Dictionary(new PdfDictionary
            {
                [PdfName.Get("Kids")] = PdfValue.Array(kids),
            }),
        }));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("4096 name-tree nodes or entries", StringComparison.Ordinal) &&
                       problem.Message.Contains("cannot be established", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.8")]
    [InlineData(PdfAConformance.A4, "6.9")]
    public void MalformedEmbeddedFilesRootFailsClosedAgainstTheContainerClause(
        PdfAConformance conformance,
        string clause)
    {
        using PdfDocument document = Blank();
        document.Catalog.Set(PdfNames.Names, PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.EmbeddedFiles] = PdfValue.Integer(42),
        }));

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("malformed data", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains("cannot be established", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("catalog-names-stream")]
    [InlineData("names-not-array")]
    [InlineData("odd-names")]
    [InlineData("non-string-key")]
    [InlineData("non-dictionary-value")]
    [InlineData("kids-not-array")]
    [InlineData("non-dictionary-kid")]
    [InlineData("cycle")]
    [InlineData("shared-node")]
    [InlineData("both-names-and-kids")]
    [InlineData("neither-names-nor-kids")]
    [InlineData("unsorted-names")]
    [InlineData("duplicate-names")]
    [InlineData("missing-limits")]
    [InlineData("wrong-limits")]
    [InlineData("indirect-limits")]
    [InlineData("direct-kid")]
    [InlineData("overlapping-child-ranges")]
    [InlineData("unordered-child-ranges")]
    public void MalformedEmbeddedFilesNameTreeCannotHideAttachmentConformance(string shape)
    {
        using PdfDocument document = Blank();
        PdfDictionary root = MalformedNameTree(shape, document);

        if (shape != "catalog-names-stream")
        {
            document.Catalog.Set(PdfNames.Names, PdfValue.Dictionary(new PdfDictionary
            {
                [PdfNames.EmbeddedFiles] = PdfValue.Dictionary(root),
            }));
        }

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("malformed data", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains("cannot be established", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ef-not-dictionary")]
    [InlineData("f-not-stream")]
    [InlineData("uf-not-stream")]
    [InlineData("no-ef-stream")]
    public void MalformedEfEntriesFailClosedEvenForArbitraryFileProfiles(string shape)
    {
        using PdfDocument document = Blank();
        PdfValue specificationValue = document.Attachments.Add(
            "malformed-ef.bin",
            "payload"u8.ToArray(),
            relationship: AttachmentRelationship.Data);
        PdfDictionary specification = document.Resolve(specificationValue).AsDictionary!;
        PdfDictionary embeddedFiles = specification.GetDictionary(PdfNames.EF)!;

        switch (shape)
        {
            case "ef-not-dictionary":
                specification.Set(PdfNames.EF, PdfValue.Integer(1));
                break;
            case "f-not-stream":
                embeddedFiles.Set(PdfNames.F, PdfValue.Integer(1));
                break;
            case "uf-not-stream":
                embeddedFiles.Set(PdfNames.UF, PdfValue.Integer(1));
                break;
            default:
                embeddedFiles.Remove(PdfNames.F);
                embeddedFiles.Remove(PdfNames.UF);
                break;
        }

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4F),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("malformed-ef.bin", StringComparison.Ordinal) &&
                       problem.Message.Contains("cannot be established", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MalformedOneOfTwoEfPayloadsIsNotHiddenByTheValidOtherPayload()
    {
        using PdfDocument document = Blank();
        PdfValue specificationValue = document.Attachments.Add(
            "mixed-ef.pdf",
            Conforming(PdfAConformance.A4),
            "application/pdf",
            relationship: AttachmentRelationship.Data);
        PdfDictionary embeddedFiles = document.Resolve(specificationValue).AsDictionary!
            .GetDictionary(PdfNames.EF)!;
        embeddedFiles.Set(PdfNames.UF, PdfValue.Integer(7));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("EF/UF", StringComparison.Ordinal) &&
                       problem.Message.Contains("cannot be established", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("F")]
    [InlineData("UF")]
    public void FileSpecificationNamesMustActuallyBeStrings(string key)
    {
        using PdfDocument document = Blank();
        PdfValue specificationValue = document.Attachments.Add(
            "bad-name.pdf",
            Conforming(PdfAConformance.A4),
            "application/pdf",
            relationship: AttachmentRelationship.Data);
        document.Resolve(specificationValue).AsDictionary!
            .Set(PdfName.Get(key), PdfValue.Integer(1));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains($"{key} entry", StringComparison.Ordinal) &&
                       problem.Message.Contains("not a string", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] Conforming(PdfAConformance conformance)
    {
        using PdfDocument document = Blank();
        PdfAProfile.Apply(document, conformance);
        return document.ToArray();
    }

    private static byte[] PdfWithDirectRequiredStructure(string shape)
    {
        using PdfDocument document = Blank();
        PdfAProfile.Apply(document, PdfAConformance.A4);

        string catalogReference = $"{document.CatalogId.Number} 0 R";
        string pagesReference = $"{document.PageTreeRootId.Number} 0 R";
        string pageReference = $"{document.Pages[0].Id.Number} 0 R";
        byte[] bytes = document.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            SetProducer = false,
            UpdateModificationDate = false,
        });

        (string marker, string replacement) = shape switch
        {
            "Root" => ($"/Root {catalogReference}", $"/Root {Direct(catalogReference)}"),
            "Pages" => ($"/Pages {pagesReference}", $"/Pages {Direct(pagesReference)}"),
            "Kids" => ($"/Kids[{pageReference}]", $"/Kids[{Direct(pageReference)}]"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        return ReplaceSingleAscii(bytes, marker, replacement);

        static string Direct(string reference) => "<<>>".PadRight(reference.Length);
    }

    private static byte[] ReplaceSingleAscii(byte[] source, string marker, string replacement)
    {
        Assert.Equal(marker.Length, replacement.Length);
        byte[] needle = Encoding.ASCII.GetBytes(marker);
        int offset = source.AsSpan().IndexOf(needle);
        Assert.True(offset >= 0, $"The generated PDF does not contain '{marker}'.");
        Assert.Equal(-1, source.AsSpan((offset + needle.Length)..).IndexOf(needle));

        byte[] result = (byte[])source.Clone();
        Encoding.ASCII.GetBytes(replacement).CopyTo(result, offset);
        return result;
    }

    private static byte[] ClaimedWithAttachment(
        PdfAConformance conformance,
        string name,
        byte[] content)
    {
        using PdfDocument document = Blank();
        PdfAProfile.Apply(document, conformance);
        AddAttachment(document, name, content);
        return document.ToArray();
    }

    private static byte[] PdfA4FWithMetadataAliasedAsEf(int padding, out int decodedXmpBytes)
    {
        using PdfDocument document = Blank();
        PdfValue specificationValue = document.Attachments.Add(
            "metadata.xml",
            "placeholder"u8.ToArray(),
            "application/xml",
            relationship: AttachmentRelationship.Data);
        PdfAProfile.Apply(document, PdfAConformance.A4F);
        PdfValue metadataReference = document.Catalog[PdfNames.Metadata];
        PdfStream metadata = document.Resolve(metadataReference).AsStream!;
        string packet = Encoding.UTF8.GetString(metadata.DecodedSpan());
        if (padding > 0)
        {
            packet = packet.Replace(
                "</x:xmpmeta>",
                new string(' ', padding) + "</x:xmpmeta>",
                StringComparison.Ordinal);
        }

        byte[] decoded = Encoding.UTF8.GetBytes(packet);
        decodedXmpBytes = decoded.Length;
        metadata.SetData(decoded, compress: true);
        metadata.Dictionary.Remove(PdfNames.Type);

        PdfDictionary specification = document.Resolve(specificationValue).AsDictionary!;
        PdfDictionary ef = specification.GetDictionary(PdfNames.EF)!;
        ef.Set(PdfNames.F, metadataReference);
        ef.Set(PdfNames.UF, metadataReference);

        return document.ToArray(new PdfSaveOptions
        {
            SetProducer = false,
            UpdateModificationDate = false,
        });
    }

    private static byte[] ClaimedButInvalid(
        PdfAConformance conformance,
        Action<PdfDocument> invalidate)
    {
        using PdfDocument document = Blank();
        PdfAProfile.Apply(document, conformance);
        invalidate(document);
        return document.ToArray();
    }

    private static PdfDocument OuterWithAttachment(string name, byte[] content)
    {
        PdfDocument document = Blank();
        AddAttachment(document, name, content);
        return document;
    }

    private static PdfValue AddAttachment(
        PdfDocument document,
        string name,
        byte[] content,
        PdfName? terminalFilter = null,
        string mime = "application/pdf")
    {
        PdfValue specification = document.Attachments.Add(
            name,
            content,
            mime,
            relationship: AttachmentRelationship.Data);

        if (terminalFilter is null)
            return specification;

        PdfStream embedded = document.Resolve(specification).AsDictionary!
            .GetDictionary(PdfNames.EF)!
            .GetStream(PdfNames.F)!;
        embedded.Dictionary.Remove(PdfNames.DecodeParms);
        embedded.Dictionary.Set(PdfNames.Filter, PdfValue.Name(terminalFilter));
        embedded.SetRawData(content);
        return specification;
    }

    private static PdfStream EmbeddedStream(PdfDocument document, PdfValue specification) =>
        document.Resolve(specification).AsDictionary!
            .GetDictionary(PdfNames.EF)!
            .GetStream(PdfNames.F)!;

    private static PdfValue AddEmbeddedFileStream(PdfDocument document, byte[] content)
    {
        PdfStream stream = PdfStream.FromData(content, compress: false);
        stream.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.EmbeddedFile));
        stream.Dictionary.Set(
            PdfNames.Subtype,
            PdfValue.Name(PdfName.Get("application/octet-stream")));
        return PdfValue.Reference(document.AddObject(PdfValue.Stream(stream)));
    }

    private static void ConfigureFilters(PdfStream stream, byte[] raw, params PdfName[] filters)
    {
        PdfValue filterValue;
        if (filters.Length == 1)
        {
            filterValue = PdfValue.Name(filters[0]);
        }
        else
        {
            var entries = new PdfArray(filters.Length);
            foreach (PdfName filter in filters)
                entries.Add(PdfValue.Name(filter));
            filterValue = PdfValue.Array(entries);
        }

        stream.Dictionary.Set(PdfNames.Filter, filterValue);
        stream.Dictionary.Remove(PdfNames.DecodeParms);
        stream.SetRawData(raw);
    }

    private static PdfDictionary MalformedNameTree(string shape, PdfDocument document)
    {
        var root = new PdfDictionary();
        var emptyLeaf = new PdfDictionary
        {
            [PdfNames.Names] = PdfValue.Array(new PdfArray()),
        };

        switch (shape)
        {
            case "catalog-names-stream":
                document.Catalog.Set(
                    PdfNames.Names,
                    PdfValue.Stream(PdfStream.FromData("not a dictionary"u8.ToArray(), compress: false)));
                return root;
            case "names-not-array":
                root.Set(PdfNames.Names, PdfValue.Integer(1));
                break;
            case "odd-names":
                root.Set(PdfNames.Names, PdfValue.Array(new PdfArray(PdfValue.TextString("orphan"))));
                break;
            case "non-string-key":
                root.Set(PdfNames.Names, PdfValue.Array(new PdfArray(
                    PdfValue.Integer(1), PdfValue.Dictionary(new PdfDictionary()))));
                break;
            case "non-dictionary-value":
                root.Set(PdfNames.Names, PdfValue.Array(new PdfArray(
                    PdfValue.TextString("file.pdf"), PdfValue.Integer(1))));
                break;
            case "kids-not-array":
                root.Set(PdfName.Get("Kids"), PdfValue.Integer(1));
                break;
            case "non-dictionary-kid":
                root.Set(PdfName.Get("Kids"), PdfValue.Array(new PdfArray(PdfValue.Integer(1))));
                break;
            case "cycle":
                root.Set(
                    PdfName.Get("Kids"),
                    PdfValue.Array(new PdfArray(PdfValue.Dictionary(root))));
                break;
            case "shared-node":
                root.Set(PdfName.Get("Kids"), PdfValue.Array(new PdfArray(
                    PdfValue.Dictionary(emptyLeaf),
                    PdfValue.Dictionary(emptyLeaf))));
                break;
            case "both-names-and-kids":
                root.Set(PdfNames.Names, PdfValue.Array(new PdfArray()));
                root.Set(
                    PdfName.Get("Kids"),
                    PdfValue.Array(new PdfArray(PdfValue.Dictionary(emptyLeaf))));
                break;
            case "unsorted-names":
                root.Set(PdfNames.Names, NameEntries("b.pdf", "a.pdf"));
                break;
            case "duplicate-names":
                root.Set(PdfNames.Names, NameEntries("a.pdf", "a.pdf"));
                break;
            case "missing-limits":
                root.Set(
                    PdfName.Get("Kids"),
                    PdfValue.Array(new PdfArray(Indirect(Leaf("a.pdf", includeLimits: false)))));
                break;
            case "wrong-limits":
                PdfDictionary wrongLimits = Leaf("a.pdf", includeLimits: false);
                wrongLimits.Set(PdfNames.Limits, Limits("b.pdf", "c.pdf"));
                root.Set(
                    PdfName.Get("Kids"),
                    PdfValue.Array(new PdfArray(Indirect(wrongLimits))));
                break;
            case "indirect-limits":
                PdfDictionary indirectLimits = Leaf("a.pdf", includeLimits: false);
                indirectLimits.Set(
                    PdfNames.Limits,
                    PdfValue.Reference(document.AddObject(Limits("a.pdf", "a.pdf"))));
                root.Set(
                    PdfName.Get("Kids"),
                    PdfValue.Array(new PdfArray(Indirect(indirectLimits))));
                break;
            case "direct-kid":
                root.Set(
                    PdfName.Get("Kids"),
                    PdfValue.Array(new PdfArray(PdfValue.Dictionary(Leaf("a.pdf")))));
                break;
            case "overlapping-child-ranges":
                root.Set(
                    PdfName.Get("Kids"),
                    PdfValue.Array(new PdfArray(
                        Indirect(Leaf("a.pdf")),
                        Indirect(Leaf("a.pdf")))));
                break;
            case "unordered-child-ranges":
                root.Set(
                    PdfName.Get("Kids"),
                    PdfValue.Array(new PdfArray(
                        Indirect(Leaf("b.pdf")),
                        Indirect(Leaf("a.pdf")))));
                break;
            default:
                break;
        }

        return root;

        PdfValue Indirect(PdfDictionary dictionary) =>
            PdfValue.Reference(document.AddObject(PdfValue.Dictionary(dictionary)));

        static PdfDictionary Leaf(string key, bool includeLimits = true)
        {
            var leaf = new PdfDictionary
            {
                [PdfNames.Names] = NameEntries(key),
            };
            if (includeLimits)
                leaf.Set(PdfNames.Limits, Limits(key, key));
            return leaf;
        }

        static PdfValue NameEntries(params string[] keys)
        {
            var names = new PdfArray(keys.Length * 2);
            foreach (string key in keys)
            {
                names.Add(PdfValue.TextString(key));
                names.Add(PdfValue.Dictionary(new PdfDictionary()));
            }
            return PdfValue.Array(names);
        }

        static PdfValue Limits(string minimum, string maximum) =>
            PdfValue.Array(new PdfArray(
                PdfValue.TextString(minimum),
                PdfValue.TextString(maximum)));
    }

    private static PdfDocument Blank()
    {
        PdfDocument document = PdfDocument.Create();
        document.Version = PdfVersion.Pdf20;
        document.Pages.Add();
        return document;
    }
}
