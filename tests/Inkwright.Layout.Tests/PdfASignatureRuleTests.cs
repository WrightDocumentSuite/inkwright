using System.Globalization;
using System.Text;
using Inkwright.Cos;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Layout.Tests;

public sealed class PdfASignatureRuleTests
{
    [Fact]
    public void PartFourDoesNotApplyTheOldAnnexBFileShapeRules()
    {
        using PdfDocument document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);
        AttachSignature(document, "Unregistered.Handler", new PdfArray(PdfValue.Integer(0)));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "B.1");
    }

    [Theory]
    [InlineData(PdfAConformance.A2B)]
    [InlineData(PdfAConformance.A3B)]
    public void PartsTwoAndThreeDoNotTurnReaderSubFilterSupportIntoAFileRestriction(
        PdfAConformance conformance)
    {
        byte[] bytes = BuildSignatureRevision(conformance, "Vendor.PAdES.Profile");
        using PdfDocument document = PdfDocument.Load(bytes);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Message.Contains("SubFilter", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsAByteRangeThatEndsAtAnEarlierCompleteRevision()
    {
        byte[] firstRevision = BuildSignatureRevision();
        byte[] secondRevision;

        using (PdfDocument previous = PdfDocument.Load(firstRevision))
        {
            previous.Info.Title = "Incrementally updated";
            secondRevision = previous.ToIncrementalArray();
        }

        using PdfDocument document = PdfDocument.Load(secondRevision);

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "B.1");
    }

    [Fact]
    public void RejectsAByteRangeThatStopsInsideARevision()
    {
        byte[] bytes = BuildSignatureRevision(tailLengthAdjustment: -32);
        using PdfDocument document = PdfDocument.Load(bytes);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "B.1" &&
                       problem.Message.Contains("complete PDF revision", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsASignatureWithoutContents()
    {
        byte[] bytes = PdfAConformanceTests.BuildArchival(PdfAConformance.A2B, out _);
        using PdfDocument document = PdfDocument.Load(bytes);
        AttachSignature(document, null, PdfArray.OfNumbers(0, bytes.Length - 5, bytes.Length, 0));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "B.1" &&
                       problem.Message.Contains("Contents", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAByteRangeWhoseGapIsNotExactlyContents()
    {
        byte[] bytes = BuildSignatureRevision(firstLengthAdjustment: -1);
        using PdfDocument document = PdfDocument.Load(bytes);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "B.1" &&
                       problem.Message.Contains("Contents", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsIndirectSignatureContents()
    {
        byte[] bytes = PdfAConformanceTests.BuildArchival(PdfAConformance.A2B, out _);
        using PdfDocument document = PdfDocument.Load(bytes);

        PdfObjectId contentsId = document.AddObject(PdfValue.String([0x30], hex: true));
        AttachSignature(
            document,
            null,
            PdfArray.OfNumbers(0, bytes.Length - 5, bytes.Length, 0),
            PdfValue.Reference(contentsId));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "B.1" &&
                       problem.Message.Contains("direct hexadecimal string", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsLiteralSignatureContents()
    {
        byte[] bytes = PdfAConformanceTests.BuildArchival(PdfAConformance.A2B, out _);
        using PdfDocument document = PdfDocument.Load(bytes);
        AttachSignature(
            document,
            null,
            PdfArray.OfNumbers(0, bytes.Length - 5, bytes.Length, 0),
            PdfValue.String([0x30], hex: false));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "B.1" &&
                       problem.Message.Contains("direct hexadecimal string", StringComparison.Ordinal));
    }

    private static byte[] BuildSignatureRevision(
        PdfAConformance conformance = PdfAConformance.A2B,
        string? subFilter = null,
        int firstLengthAdjustment = 0,
        int tailLengthAdjustment = 0)
    {
        const string placeholder = "[0 9999999999 9999999999 9999999999]";

        byte[] original = PdfAConformanceTests.BuildArchival(conformance, out _);
        using PdfDocument document = PdfDocument.Load(original);
        AttachSignature(
            document,
            subFilter,
            PdfArray.OfNumbers(0, 9999999999, 9999999999, 9999999999),
            PdfValue.String([0x30, 0x31, 0x00, 0x00], hex: true));

        byte[] revision = document.ToIncrementalArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            Compress = false,
            UpdateModificationDate = false,
            SetProducer = false,
        });
        Span<byte> bytes = revision;
        int rangeStart = bytes.LastIndexOf(Encoding.ASCII.GetBytes(placeholder));
        int byteRangeKey = bytes.LastIndexOf("/ByteRange"u8);
        string byteRangeTail = byteRangeKey < 0
            ? "missing /ByteRange"
            : Encoding.ASCII.GetString(bytes[byteRangeKey..Math.Min(bytes.Length, byteRangeKey + 96)]);
        Assert.True(rangeStart >= 0, $"The ByteRange placeholder was not serialized: {byteRangeTail}");

        int relativeContents = bytes[rangeStart..].IndexOf("/Contents"u8);
        Assert.True(relativeContents >= 0, "The signature dictionary has no Contents entry.");

        int hexStart = rangeStart + relativeContents;
        while (hexStart < bytes.Length && bytes[hexStart] != (byte)'<')
            hexStart++;

        int hexEnd = hexStart;
        while (hexEnd < bytes.Length && bytes[hexEnd] != (byte)'>')
            hexEnd++;

        Assert.True(hexEnd < bytes.Length, "The Contents placeholder was not terminated.");

        int afterHex = hexEnd + 1;
        int tailLength = revision.Length - afterHex + tailLengthAdjustment;
        string range = string.Create(
            CultureInfo.InvariantCulture,
            $"[0 {hexStart + firstLengthAdjustment} {afterHex} {tailLength}]")
            .PadRight(placeholder.Length);
        Assert.True(range.Length <= placeholder.Length, "The patched ByteRange does not fit.");

        Encoding.ASCII.GetBytes(range, bytes[rangeStart..]);
        return revision;
    }

    private static void AttachSignature(
        PdfDocument document,
        string? subFilter,
        PdfArray byteRange,
        PdfValue? contents = null)
    {
        var signature = new PdfDictionary
        {
            [PdfNames.Type] = PdfValue.Name(PdfName.Get("Sig")),
            [PdfNames.ByteRange] = PdfValue.Array(byteRange),
        };

        if (subFilter is not null)
            signature.Set(PdfName.Get("SubFilter"), PdfValue.Name(PdfName.Get(subFilter)));

        if (contents is { } value)
            signature.Set(PdfNames.Contents, value);

        document.Catalog.Set(PdfName.Get("SignatureProbe"), PdfValue.Dictionary(signature));
        document.SetObject(document.CatalogId, PdfValue.Dictionary(document.Catalog));
    }
}
