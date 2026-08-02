using Inkwright.Cos;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Layout.Tests;

public sealed class PdfAFileSyntaxTests
{
    [Fact]
    public void RequiresSixteenByteIdentifiersInAPdfTwoTrailer()
    {
        byte[] bytes = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out _);
        using PdfDocument document = PdfDocument.Load(bytes);

        AssertRejectedForMinimum(new PdfArray(
            PdfValue.String(new byte[15], hex: true),
            PdfValue.String(new byte[16], hex: true)));

        AssertRejectedForMinimum(new PdfArray(
            PdfValue.String(new byte[16], hex: true),
            PdfValue.String(new byte[15], hex: true)));

        document.Trailer.Set(PdfName.Get("ID"), PdfValue.Array(new PdfArray(
            PdfValue.String(new byte[16], hex: true),
            PdfValue.Name(PdfName.Get("NotAByteString")))));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.1.3" &&
                       problem.Message.Contains("ID", StringComparison.Ordinal));

        document.Trailer.Set(PdfName.Get("ID"), PdfValue.Array(new PdfArray(
            PdfValue.String(new byte[16], hex: true),
            PdfValue.String(new byte[16], hex: true))));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Message.Contains("16 bytes", StringComparison.Ordinal));

        void AssertRejectedForMinimum(PdfArray identifiers)
        {
            document.Trailer.Set(PdfName.Get("ID"), PdfValue.Array(identifiers));

            Assert.Contains(
                PdfAProfile.Validate(document, PdfAConformance.A4),
                problem => problem.Clause == "6.1.3" &&
                           problem.Message.Contains("16 bytes", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "6.1.8")]
    [InlineData(PdfAConformance.A2B, "6.1.9")]
    [InlineData(PdfAConformance.A4, "6.1.8")]
    public void RequiresAnEndOfLineBeforeEveryIndirectObject(PdfAConformance level, string clause)
    {
        byte[] bytes = PdfAConformanceTests.BuildArchival(level, out _);
        int firstHeader = bytes.AsSpan().IndexOf(" 0 obj"u8);
        Assert.True(firstHeader > 0);
        int nextHeader = bytes.AsSpan(firstHeader + " 0 obj".Length).IndexOf(" 0 obj"u8);
        Assert.True(nextHeader >= 0);
        int header = firstHeader + " 0 obj".Length + nextHeader;

        int beforeNumber = header - 1;
        while (beforeNumber >= 0 && bytes[beforeNumber] is >= (byte)'0' and <= (byte)'9')
            beforeNumber--;

        Assert.True(beforeNumber >= 0 && bytes[beforeNumber] is (byte)'\r' or (byte)'\n');
        bytes[beforeNumber] = (byte)' ';

        Assert.Contains(
            PdfAProfile.Validate(bytes, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("not preceded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "6.1.8")]
    [InlineData(PdfAConformance.A2B, "6.1.9")]
    [InlineData(PdfAConformance.A4, "6.1.8")]
    public void RequiresAnEndOfLineAfterEveryIndirectObject(PdfAConformance level, string clause)
    {
        byte[] bytes = PdfAConformanceTests.BuildArchival(level, out _);
        int endObject = bytes.AsSpan().IndexOf("endobj"u8);
        Assert.True(endObject >= 0);

        int afterKeyword = endObject + "endobj".Length;
        Assert.True(afterKeyword < bytes.Length && bytes[afterKeyword] is (byte)'\r' or (byte)'\n');
        bytes[afterKeyword] = (byte)' ';

        Assert.Contains(
            PdfAProfile.Validate(bytes, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("not followed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "6.1.8")]
    [InlineData(PdfAConformance.A2B, "6.1.9")]
    [InlineData(PdfAConformance.A4, "6.1.8")]
    public void AcceptsFormFeedAsTheSingleWhiteSpaceInAnIndirectObjectHeader(
        PdfAConformance level,
        string clause)
    {
        byte[] bytes = PdfAConformanceTests.BuildArchival(level, out _);
        int header = bytes.AsSpan().IndexOf(" 0 obj"u8);
        Assert.True(header >= 0);
        bytes[header] = 0x0C;

        Assert.DoesNotContain(
            PdfAProfile.Validate(bytes, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("single white-space", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x09)]
    [InlineData(0x0C)]
    public void AlternateHeaderWhitespaceCannotHideAMissingEndOfLineAfterObj(byte separator)
    {
        byte[] bytes = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out _);
        int header = bytes.AsSpan().IndexOf(" 0 obj\n"u8);
        Assert.True(header >= 0);

        bytes[header + 2] = separator;
        bytes[header + " 0 obj".Length] = (byte)' ';

        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A4),
            problem => problem.Clause == "6.1.8" &&
                       problem.Message.Contains(
                           "obj keyword",
                           StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains(
                           "not followed",
                           StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "6.1.6")]
    [InlineData(PdfAConformance.A2B, "6.1.6")]
    [InlineData(PdfAConformance.A4, "6.1.5")]
    public void ReportsOddLengthHexadecimalStringsAgainstTheCorrectPart(
        PdfAConformance level, string clause)
    {
        _ = PdfAConformanceTests.BuildArchival(level, out PdfDocument document);
        using (document)
        {
            document.Catalog.Set(
                PdfName.Get("HexProbe"),
                PdfValue.String([0xAB, 0xCD], hex: true));

            byte[] bytes = document.ToArray(new PdfSaveOptions
            {
                UseObjectStreams = false,
                UpdateModificationDate = false,
                SetProducer = false,
            });
            int probe = bytes.AsSpan().IndexOf("<ABCD>"u8);
            Assert.True(probe >= 0);
            bytes[probe + 4] = (byte)' ';

            Assert.Contains(
                PdfAProfile.Validate(bytes, level),
                problem => problem.Clause == clause &&
                           problem.Message.Contains("odd number", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ReportsOddLengthHexadecimalStringsInsideCompressedObjects()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            PdfObjectId probeId = document.AddObject(
                PdfValue.String([0xAB, 0xCD], hex: true));
            document.Catalog.Set(PdfName.Get("CompressedHexProbe"), PdfValue.Reference(probeId));

            byte[] bytes = document.ToArray(new PdfSaveOptions
            {
                UseObjectStreams = true,
                Compress = false,
                UpdateModificationDate = false,
                SetProducer = false,
            });

            using (PdfDocument reopened = PdfDocument.Load(bytes))
                Assert.True(reopened.Reader!.IsInObjectStream(probeId.Number));

            int probe = bytes.AsSpan().IndexOf("<ABCD>"u8);
            Assert.True(probe >= 0);
            bytes[probe + 4] = (byte)' ';

            Assert.Contains(
                PdfAProfile.Validate(bytes, PdfAConformance.A4),
                problem => problem.Clause == "6.1.5" &&
                           problem.Message.Contains("odd number", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ByteAndFileValidationUseTheProtectedReader(bool validateFile)
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            PdfObjectId probeId = document.AddObject(PdfValue.Integer(42));
            byte[] bytes = SaveWithoutObjectStreams(document);
            byte[] header = System.Text.Encoding.ASCII.GetBytes($"{probeId.Number} 0 obj");
            int at = bytes.AsSpan().IndexOf(header);
            Assert.True(at >= 0);
            bytes[at + header.Length - "0 obj".Length] = (byte)'1';

            IReadOnlyList<PdfAProblem> problems;
            if (validateFile)
            {
                string path = Path.Combine(Path.GetTempPath(), $"inkwright-pdfa-{Guid.NewGuid():N}.pdf");
                try
                {
                    File.WriteAllBytes(path, bytes);
                    problems = PdfAProfile.ValidateFile(path, PdfAConformance.A4);
                }
                finally
                {
                    File.Delete(path);
                }
            }
            else
            {
                problems = PdfAProfile.Validate(bytes, PdfAConformance.A4);
            }

            Assert.Contains(
                problems,
                problem => problem.Clause == "6.1" &&
                           problem.Message.Contains("generation", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TopLevelByteValidationPreflightsEveryReachableInternalStream()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            var dictionary = new PdfDictionary
            {
                [PdfNames.Filter] = PdfValue.Name(PdfNames.FlateDecode),
            };
            var malformed = new PdfStream(dictionary, new byte[] { 0x78, 0x9C, 0x00 });
            PdfObjectId probeId = document.AddObject(PdfValue.Stream(malformed));
            document.Catalog.Set(PdfName.Get("MalformedStreamProbe"), PdfValue.Reference(probeId));

            byte[] bytes = SaveWithoutObjectStreams(document);
            IReadOnlyList<PdfAProblem> problems =
                PdfAProfile.Validate(bytes, PdfAConformance.A4);

            Assert.Contains(
                problems,
                problem => problem.Clause == "6.1.6.2" &&
                           problem.Message.Contains("reachable internal stream", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "6.1.7")]
    [InlineData(PdfAConformance.A2B, "6.1.7.1")]
    [InlineData(PdfAConformance.A4, "6.1.6.1")]
    public void ReportsAStreamWithoutAnEndOfLineAgainstTheCorrectPart(
        PdfAConformance level, string clause)
    {
        byte[] bytes = PdfAConformanceTests.BuildArchival(level, out _);
        int stream = bytes.AsSpan().IndexOf("stream\n"u8);
        Assert.True(stream >= 0);
        bytes[stream + "stream".Length] = (byte)' ';

        Assert.Contains(
            PdfAProfile.Validate(bytes, level),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("neither a line feed", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoresEndstreamAndHexLikeBytesInsideTheRawStreamPayload()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            PdfStream probe = PdfStream.FromData("payload endstream <A>"u8.ToArray(), compress: false);
            PdfObjectId probeId = document.AddObject(PdfValue.Stream(probe));
            document.Catalog.Set(PdfName.Get("PayloadProbe"), PdfValue.Reference(probeId));

            byte[] bytes = SaveWithoutObjectStreams(document);
            IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(bytes, PdfAConformance.A4);

            Assert.DoesNotContain(
                problems,
                problem => problem.Clause == "6.1.6.1" &&
                           problem.Message.Contains("endstream keyword", StringComparison.Ordinal));
            Assert.DoesNotContain(
                problems,
                problem => problem.Clause == "6.1.5" &&
                           problem.Message.Contains("odd number", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NameEndingInStreamDoesNotHideALaterOddLengthHexadecimalString()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            document.Catalog.Set(
                PdfName.Get("NameProbe"),
                PdfValue.Name(PdfName.Get("application/octet-stream")));
            document.Catalog.Set(
                PdfName.Get("HexProbe"),
                PdfValue.String([0xAB, 0xCD], hex: true));

            byte[] bytes = SaveWithoutObjectStreams(document);
            int name = bytes.AsSpan().IndexOf("octet-stream"u8);
            int hex = bytes.AsSpan().IndexOf("<ABCD>"u8);
            Assert.True(name >= 0 && name < hex);
            bytes[hex + 4] = (byte)' ';

            Assert.Contains(
                PdfAProfile.Validate(bytes, PdfAConformance.A4),
                problem => problem.Clause == "6.1.5" &&
                           problem.Message.Contains("odd number", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void LiteralTextContainingStreamIsNotAStreamDelimiter()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            document.Catalog.Set(
                PdfName.Get("LiteralProbe"),
                PdfValue.TextString("not a stream marker"));

            Assert.DoesNotContain(
                PdfAProfile.Validate(SaveWithoutObjectStreams(document), PdfAConformance.A4),
                problem => problem.Clause == "6.1.6.1" &&
                           problem.Message.Contains("stream keyword", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void XrefTextInsideAStreamPayloadIsNotACrossReferenceSection()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            PdfStream probe = PdfStream.FromData("xref is payload, not syntax"u8.ToArray(), compress: false);
            document.AddObject(PdfValue.Stream(probe));

            Assert.DoesNotContain(
                PdfAProfile.Validate(SaveWithoutObjectStreams(document), PdfAConformance.A4),
                problem => problem.Clause == "6.1.4" &&
                           problem.Message.Contains("xref keyword", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static byte[] SaveWithoutObjectStreams(PdfDocument document) =>
        document.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            UpdateModificationDate = false,
            SetProducer = false,
        });
}
