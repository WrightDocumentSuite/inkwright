using System.Text;
using Inkwright.Cos;
using Inkwright.IO;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Layout.Tests;

public sealed class PdfAFileStructureRuleTests
{
    [Theory]
    [InlineData(PdfAConformance.A1B)]
    [InlineData(PdfAConformance.A2B)]
    [InlineData(PdfAConformance.A4)]
    public void TrailerIdentifierIsAnArrayOfExactlyTwoStrings(PdfAConformance level)
    {
        using PdfDocument document = CreateDocument(level);

        PdfValue[] invalidIdentifiers =
        [
            PdfValue.Array(new PdfArray(PdfValue.String(new byte[16], hex: true))),
            PdfValue.Array(new PdfArray(
                PdfValue.String(new byte[16], hex: true),
                PdfValue.String(new byte[16], hex: true),
                PdfValue.String(new byte[16], hex: true))),
            PdfValue.Array(new PdfArray(
                PdfValue.String(new byte[16], hex: true),
                PdfValue.Name(PdfName.Get("NotAString")))),
        ];

        foreach (PdfValue invalid in invalidIdentifiers)
        {
            document.Trailer.Set(PdfNames.ID, invalid);

            Assert.Contains(
                PdfAProfile.Validate(document, level),
                problem => problem.Clause == "6.1.3" &&
                           problem.Message.Contains("ID", StringComparison.Ordinal));
        }

        document.Trailer.Set(PdfNames.ID, PdfValue.Array(Identifier(16, 16)));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == "6.1.3" &&
                       problem.Message.Contains("ID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B)]
    [InlineData(PdfAConformance.A4)]
    public void TrailerIdentifierAndItsStringsMayBeIndirect(PdfAConformance level)
    {
        using PdfDocument document = CreateDocument(level);
        PdfObjectId first = document.AddObject(PdfValue.String(new byte[16], hex: true));
        PdfObjectId second = document.AddObject(PdfValue.String(new byte[16], hex: true));
        PdfObjectId array = document.AddObject(PdfValue.Array(new PdfArray(
            PdfValue.Reference(first),
            PdfValue.Reference(second))));
        document.Trailer.Set(PdfNames.ID, PdfValue.Reference(array));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == "6.1.3" &&
                       problem.Message.Contains("ID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "6.1.7")]
    [InlineData(PdfAConformance.A2B, "6.1.7")]
    [InlineData(PdfAConformance.A4, "6.1.6")]
    public void StreamObjectsMustBeIndirect(PdfAConformance level, string clause)
    {
        using PdfDocument document = CreateDocument(level);
        document.Catalog.Set(
            PdfName.Get("DirectStream"),
            PdfValue.Stream(PdfStream.FromData("payload"u8.ToArray(), compress: false)));

        Assert.Contains(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("embedded directly", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, false)]
    [InlineData(PdfAConformance.A2B, false)]
    [InlineData(PdfAConformance.A4, true)]
    public void SixteenByteIdentifierMinimumBelongsOnlyToPartFour(
        PdfAConformance level, bool rejected)
    {
        using PdfDocument document = CreateDocument(level);
        document.Trailer.Set(PdfNames.ID, PdfValue.Array(Identifier(15, 16)));

        bool found = PdfAProfile.Validate(document, level).Any(
            problem => problem.Clause == "6.1.3" &&
                       problem.Message.Contains("16 bytes", StringComparison.Ordinal));

        Assert.Equal(rejected, found);
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "ASCIIHexDecode")]
    [InlineData(PdfAConformance.A1B, "AHx")]
    [InlineData(PdfAConformance.A1B, "ASCII85Decode")]
    [InlineData(PdfAConformance.A1B, "A85")]
    [InlineData(PdfAConformance.A1B, "FlateDecode")]
    [InlineData(PdfAConformance.A1B, "Fl")]
    [InlineData(PdfAConformance.A1B, "RunLengthDecode")]
    [InlineData(PdfAConformance.A1B, "RL")]
    [InlineData(PdfAConformance.A1B, "CCITTFaxDecode")]
    [InlineData(PdfAConformance.A1B, "CCF")]
    [InlineData(PdfAConformance.A1B, "DCTDecode")]
    [InlineData(PdfAConformance.A1B, "DCT")]
    [InlineData(PdfAConformance.A2B, "ASCIIHexDecode")]
    [InlineData(PdfAConformance.A2B, "AHx")]
    [InlineData(PdfAConformance.A2B, "ASCII85Decode")]
    [InlineData(PdfAConformance.A2B, "A85")]
    [InlineData(PdfAConformance.A2B, "FlateDecode")]
    [InlineData(PdfAConformance.A2B, "Fl")]
    [InlineData(PdfAConformance.A2B, "RunLengthDecode")]
    [InlineData(PdfAConformance.A2B, "RL")]
    [InlineData(PdfAConformance.A2B, "CCITTFaxDecode")]
    [InlineData(PdfAConformance.A2B, "CCF")]
    [InlineData(PdfAConformance.A2B, "DCTDecode")]
    [InlineData(PdfAConformance.A2B, "DCT")]
    [InlineData(PdfAConformance.A4, "ASCIIHexDecode")]
    [InlineData(PdfAConformance.A4, "AHx")]
    [InlineData(PdfAConformance.A4, "ASCII85Decode")]
    [InlineData(PdfAConformance.A4, "A85")]
    [InlineData(PdfAConformance.A4, "FlateDecode")]
    [InlineData(PdfAConformance.A4, "Fl")]
    [InlineData(PdfAConformance.A4, "RunLengthDecode")]
    [InlineData(PdfAConformance.A4, "RL")]
    [InlineData(PdfAConformance.A4, "CCITTFaxDecode")]
    [InlineData(PdfAConformance.A4, "CCF")]
    [InlineData(PdfAConformance.A4, "DCTDecode")]
    [InlineData(PdfAConformance.A4, "DCT")]
    public void InlineImagesAcceptThePartsAllowedFullAndAbbreviatedFilterNames(
        PdfAConformance level, string filter)
    {
        using PdfDocument document = InlineImageDocument(level, filter);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, level),
            problem => problem.Message.Contains("inline image", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains("filter", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "InkwrightUnknownDecode", "6.1.10")]
    [InlineData(PdfAConformance.A1B, "LZWDecode", "6.1.10")]
    [InlineData(PdfAConformance.A1B, "LZW", "6.1.10")]
    [InlineData(PdfAConformance.A1B, "JBIG2Decode", "6.1.10")]
    [InlineData(PdfAConformance.A1B, "JPXDecode", "6.1.10")]
    [InlineData(PdfAConformance.A2B, "InkwrightUnknownDecode", "6.1.10")]
    [InlineData(PdfAConformance.A2B, "LZWDecode", "6.1.10")]
    [InlineData(PdfAConformance.A2B, "LZW", "6.1.10")]
    [InlineData(PdfAConformance.A2B, "JBIG2Decode", "6.1.10")]
    [InlineData(PdfAConformance.A2B, "JPXDecode", "6.1.10")]
    [InlineData(PdfAConformance.A4, "InkwrightUnknownDecode", "6.1.9")]
    [InlineData(PdfAConformance.A4, "LZWDecode", "6.1.9")]
    [InlineData(PdfAConformance.A4, "LZW", "6.1.9")]
    [InlineData(PdfAConformance.A4, "JBIG2Decode", "6.1.9")]
    [InlineData(PdfAConformance.A4, "JPXDecode", "6.1.9")]
    public void InlineImagesRejectFiltersOutsideTheInlineImageTable(
        PdfAConformance level, string filter, string clause)
    {
        using PdfDocument document = InlineImageDocument(level, filter);

        Assert.Contains(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains(filter, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "DigestLocation")]
    [InlineData(PdfAConformance.A2B, "DigestMethod")]
    [InlineData(PdfAConformance.A2B, "DigestValue")]
    [InlineData(PdfAConformance.A3B, "DigestLocation")]
    [InlineData(PdfAConformance.A3B, "DigestMethod")]
    [InlineData(PdfAConformance.A3B, "DigestValue")]
    public void PartsTwoAndThreeRejectLegacyDigestKeysInDocMdpReferences(
        PdfAConformance level, string key)
    {
        using PdfDocument document = DocMdpDocument(level, key);

        Assert.Contains(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == "6.1.12" &&
                       problem.Message.Contains(key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DigestLocation")]
    [InlineData("DigestMethod")]
    [InlineData("DigestValue")]
    public void PartFourDoesNotApplyTheRemovedDocMdpDigestRestriction(string key)
    {
        using PdfDocument document = DocMdpDocument(PdfAConformance.A4, key);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Message.Contains(key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.1.7.2")]
    [InlineData(PdfAConformance.A4, "6.1.6.2")]
    public void CryptUsesTheDecodeParametersAtItsOwnFilterIndex(
        PdfAConformance level, string clause)
    {
        using PdfDocument document = StreamFilterDocument(
            level,
            DecodeParameters("Identity", "NotIdentity"));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("Crypt filter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.1.7.2")]
    [InlineData(PdfAConformance.A4, "6.1.6.2")]
    public void CryptRejectsANonIdentityNameAtItsOwnFilterIndex(
        PdfAConformance level, string clause)
    {
        using PdfDocument document = StreamFilterDocument(
            level,
            DecodeParameters("NotIdentity", "Identity"));

        Assert.Contains(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("Crypt filter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.1.7.2")]
    [InlineData(PdfAConformance.A4, "6.1.6.2")]
    public void CryptAfterAnotherFilterIsRejected(PdfAConformance level, string clause)
    {
        using PdfDocument document = StreamFilterDocument(
            level,
            DecodeParameters("Identity", "Identity"),
            cryptFirst: false);

        Assert.Contains(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("Crypt", StringComparison.Ordinal) &&
                       problem.Message.Contains("first", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.1.7.2")]
    [InlineData(PdfAConformance.A4, "6.1.6.2")]
    public void AFirstIdentityCryptFilterIsAllowed(PdfAConformance level, string clause)
    {
        using PdfDocument document = StreamFilterDocument(
            level,
            DecodeParameters("Identity", "Identity"),
            cryptFirst: true);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("Crypt filter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A2B, "6.1.7.2")]
    [InlineData(PdfAConformance.A4, "6.1.6.2")]
    public void ASingleIdentityCryptFilterIsAllowed(PdfAConformance level, string clause)
    {
        using PdfDocument document = CreateDocument(level);
        PdfStream stream = PdfStream.FromData(new byte[] { 0 }, compress: false);
        stream.Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfName.Get("Crypt")));
        document.Catalog.Set(PdfName.Get("FilterProbe"), PdfValue.Stream(stream));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("Crypt filter", StringComparison.Ordinal));
    }

    [Fact]
    public void ByteValidationChecksFiltersOnUnreachableLiveStreams()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            var dictionary = new PdfDictionary
            {
                [PdfNames.Filter] = PdfValue.Name(PdfNames.LZWDecode),
            };

            // Clear, "A", EOD, padded to the final byte. The stream is valid LZW so the failure
            // must come from PDF/A's file-level filter rule rather than from malformed data.
            document.AddObject(PdfValue.Stream(
                new PdfStream(dictionary, new byte[] { 0x80, 0x10, 0x60, 0x20 })));

            byte[] bytes = document.ToArray(new PdfSaveOptions
            {
                UseObjectStreams = false,
                UpdateModificationDate = false,
                SetProducer = false,
            });

            Assert.Contains(
                PdfAProfile.Validate(bytes, PdfAConformance.A4),
                problem => problem.Clause == "6.1.6.2" &&
                           problem.Message.Contains("LZWDecode", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void WrongGenerationReferenceCannotHideALaterLiveStream()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            var decoy = new PdfDictionary();
            document.AddObject(PdfValue.Dictionary(decoy));

            var streamDictionary = new PdfDictionary
            {
                [PdfNames.Filter] = PdfValue.Name(PdfNames.LZWDecode),
            };
            PdfObjectId streamId = document.AddObject(PdfValue.Stream(
                new PdfStream(streamDictionary, new byte[] { 0x80, 0x10, 0x60, 0x20 })));
            decoy.Set(
                PdfName.Get("WrongGenerationDecoy"),
                PdfValue.Reference(new PdfObjectId(streamId.Number, streamId.Generation + 1)));

            byte[] bytes = document.ToArray(new PdfSaveOptions
            {
                UseObjectStreams = false,
                UpdateModificationDate = false,
                SetProducer = false,
            });

            Assert.Contains(
                PdfAProfile.Validate(bytes, PdfAConformance.A4),
                problem => problem.Clause == "6.1.6.2" &&
                           problem.Message.Contains("LZWDecode", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ByteValidationChecksImplementationLimitsOnUnreachableLiveValues()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A2B, out PdfDocument document);
        using (document)
        {
            document.AddObject(PdfValue.String(new byte[32_768], hex: true));

            byte[] bytes = document.ToArray(new PdfSaveOptions
            {
                UseObjectStreams = false,
                UpdateModificationDate = false,
                SetProducer = false,
            });

            Assert.Contains(
                PdfAProfile.Validate(bytes, PdfAConformance.A2B),
                problem => problem.Clause == "6.1.13" &&
                           problem.Message.Contains("string is longer", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static PdfArray Identifier(int firstLength, int secondLength) => new(
        PdfValue.String(new byte[firstLength], hex: true),
        PdfValue.String(new byte[secondLength], hex: true));

    private static PdfArray DecodeParameters(string firstName, string secondName) => new(
        PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.Name] = PdfValue.Name(PdfName.Get(firstName)),
        }),
        PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.Name] = PdfValue.Name(PdfName.Get(secondName)),
        }));

    private static PdfDocument InlineImageDocument(PdfAConformance level, string filter)
    {
        PdfDocument document = CreateDocument(level);
        document.Pages[0].AppendContent(
            Encoding.ASCII.GetBytes(
                $"q BI /W 1 /H 1 /CS /G /BPC 8 /L 1 /F /{filter} ID x EI Q"),
            compress: false);
        return document;
    }

    private static PdfDocument StreamFilterDocument(
        PdfAConformance level,
        PdfArray decodeParameters,
        bool cryptFirst = true)
    {
        PdfDocument document = CreateDocument(level);
        PdfStream stream = PdfStream.FromData(new byte[] { 0 }, compress: false);
        PdfValue crypt = PdfValue.Name(PdfName.Get("Crypt"));
        PdfValue flate = PdfValue.Name(PdfNames.FlateDecode);
        stream.Dictionary.Set(
            PdfNames.Filter,
            PdfValue.Array(cryptFirst
                ? new PdfArray(crypt, flate)
                : new PdfArray(flate, crypt)));
        stream.Dictionary.Set(PdfNames.DecodeParms, PdfValue.Array(decodeParameters));
        document.Catalog.Set(PdfName.Get("FilterProbe"), PdfValue.Stream(stream));
        return document;
    }

    private static PdfDocument DocMdpDocument(PdfAConformance level, string forbiddenKey)
    {
        PdfDocument document = CreateDocument(level);
        var reference = new PdfDictionary
        {
            [PdfName.Get("TransformMethod")] = PdfValue.Name(PdfName.Get("DocMDP")),
            [PdfName.Get(forbiddenKey)] = PdfValue.Integer(0),
        };
        PdfObjectId referenceId = document.AddObject(PdfValue.Dictionary(reference));

        var signature = new PdfDictionary
        {
            [PdfName.Get("Reference")] = PdfValue.Array(new PdfArray(PdfValue.Reference(referenceId))),
        };
        PdfObjectId signatureId = document.AddObject(PdfValue.Dictionary(signature));

        var permissions = new PdfDictionary
        {
            [PdfName.Get("DocMDP")] = PdfValue.Reference(signatureId),
        };
        document.Catalog.Set(PdfName.Get("Perms"), PdfValue.Dictionary(permissions));
        return document;
    }

    private static PdfDocument CreateDocument(PdfAConformance level)
    {
        PdfDocument document = PdfDocument.Create();
        document.Version = level == PdfAConformance.A4 ? PdfVersion.Pdf20 : PdfVersion.Pdf17;
        document.Pages.Add();
        return document;
    }
}
