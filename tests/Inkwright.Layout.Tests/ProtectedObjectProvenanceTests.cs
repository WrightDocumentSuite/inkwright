using System.Text;
using Inkwright.Cos;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Layout.Tests;

public sealed class ProtectedObjectProvenanceTests
{
    [Fact]
    public void RejectsAnObjectWhoseXrefOffsetPointsInsideAnotherStreamPayload()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            PdfStream carrier = PdfStream.FromData(ReadOnlyMemory<byte>.Empty, compress: false);
            document.AddObject(PdfValue.Stream(carrier));
            PdfObjectId probe = document.AddObject(PdfValue.Integer(42));
            byte[] fakeObject = Encoding.ASCII.GetBytes(
                $"\n{probe.Number} 0 obj\n42\nendobj\n");
            carrier.SetData(fakeObject, compress: false);

            byte[] bytes = document.ToArray(new PdfSaveOptions
            {
                UseObjectStreams = false,
                UpdateModificationDate = false,
                SetProducer = false,
            });
            int fakeOffset = bytes.AsSpan().IndexOf(fakeObject) + 1;
            Assert.True(fakeOffset > 0);
            SetNormalOffset(bytes, probe.Number, fakeOffset);

            Assert.Contains(
                PdfAProfile.Validate(bytes, PdfAConformance.A4),
                problem => problem.Clause == "6.1" &&
                           (problem.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase) ||
                            problem.Message.Contains("stream payload", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void RejectsOverlappingNormalObjectRangesOutsideAStream()
    {
        AssertReaderProvenanceFailure(
            OverlappingNormalObjectsFixture(),
            "overlap");
    }

    [Fact]
    public void RejectsAnObjectHeaderWhoseXrefOffsetPointsInsideAComment()
    {
        AssertReaderProvenanceFailure(
            ObjectHeaderInsideCommentFixture(),
            "comment");
    }

    [Fact]
    public void RejectsACrossReferenceOffsetInsideAComment()
    {
        AssertReaderProvenanceFailure(
            CrossReferenceInsideCommentFixture(),
            "comment");
    }

    [Fact]
    public void RejectsAPreviousCrossReferenceSectionInsideARawStreamPayload()
    {
        AssertReaderProvenanceFailure(
            PreviousCrossReferenceInsideStreamFixture(),
            "stream payload");
    }

    [Fact]
    public void RejectsAPreviousCrossReferenceSectionInsideAShadowedStreamPayload()
    {
        AssertReaderProvenanceFailure(
            PreviousCrossReferenceInsideShadowedStreamFixture(),
            "stream payload");
    }

    [Fact]
    public void RejectsAPreviousCrossReferenceSectionInsideAShadowedLiteralString()
    {
        AssertReaderProvenanceFailure(
            PreviousCrossReferenceInsideShadowedLiteralFixture(),
            "overlap");
    }

    [Fact]
    public void AcceptsAShadowedStreamWhoseIndirectLengthWasLaterOverridden()
    {
        Assert.DoesNotContain(
            PdfAProfile.Validate(
                OrdinaryIncrementalShadowedStreamWithOverriddenLengthFixture(),
                PdfAConformance.A4),
            problem => problem.Clause == "6.1");
    }

    [Fact]
    public void RejectsAPreviousCrossReferenceSectionInsideAnUnindexedStreamCarrier()
    {
        AssertReaderProvenanceFailure(
            PreviousCrossReferenceInsideUnindexedStreamFixture(),
            "revision body");
    }

    [Fact]
    public void RejectsAPreviousCrossReferenceSectionInsideAnUnindexedLiteralCarrier()
    {
        AssertReaderProvenanceFailure(
            PreviousCrossReferenceInsideUnindexedLiteralFixture(),
            "revision body");
    }

    [Fact]
    public void AcceptsManySparseIncrementalRevisionsWithinTheStructuralBudget()
    {
        Assert.DoesNotContain(
            PdfAProfile.Validate(
                ManySparseIncrementalRevisionsFixture(revisionCount: 256),
                PdfAConformance.A4),
            problem => problem.Clause == "6.1");
    }

    private static void AssertReaderProvenanceFailure(byte[] bytes, string expected)
    {
        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A4),
            problem => problem.Clause == "6.1" &&
                       problem.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] OverlappingNormalObjectsFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int carrier = Position();
        Write("3 0 obj\n(4 0 obj\n42\nendobj)\nendobj\n");
        int nested = Array.IndexOf(output.GetBuffer(), (byte)'4', carrier);
        Assert.True(nested > carrier);
        int xref = Position();
        Write("xref\n0 5\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Row(carrier);
        Row(nested);
        Write("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return output.ToArray();

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] ObjectHeaderInsideCommentFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        Write("%");
        int commentedObject = Position();
        Write("3 0 obj 42 endobj\n");
        int xref = Position();
        Write("xref\n0 4\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Row(commentedObject);
        Write("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return output.ToArray();

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] CrossReferenceInsideCommentFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        Write("%");
        int xref = Position();
        Write("xref\n0 3\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Write("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return output.ToArray();

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] PreviousCrossReferenceInsideStreamFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int carrier = Position();
        Write("3 0 obj\n<< /Length ");
        int lengthPosition = Position();
        Write("000000 >>\nstream\n");
        int payloadStart = Position();

        using var payload = new MemoryStream();
        WritePayload("xref\n0 4\n0000000000 65535 f \n");
        PayloadRow(catalog);
        PayloadRow(pages);
        PayloadRow(carrier);
        WritePayload("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        WritePayload($"startxref\n{payloadStart}\n%%EOF\n");
        byte[] payloadBytes = payload.ToArray();
        output.Write(payloadBytes);
        Write("\nendstream\nendobj\n");

        int latestXref = Position();
        Write("xref\n0 4\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Row(carrier);
        Write($"trailer\n<< /Size 4 /Root 1 0 R /Prev {payloadStart} >>\n");
        Write($"startxref\n{latestXref}\n%%EOF\n");

        byte[] result = output.ToArray();
        Encoding.ASCII.GetBytes(payloadBytes.Length.ToString("D6", System.Globalization.CultureInfo.InvariantCulture))
            .CopyTo(result, lengthPosition);
        return result;

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void PayloadRow(int offset) => WritePayload($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        void WritePayload(string value) => payload.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] PreviousCrossReferenceInsideShadowedStreamFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int oldCarrier = Position();
        Write("3 0 obj\n<< /Length ");
        int lengthPosition = Position();
        Write("000000 >>\nstream\n");
        int payloadStart = Position();

        using var payload = new MemoryStream();
        WritePayload("xref\n0 4\n0000000000 65535 f \n");
        PayloadRow(catalog);
        PayloadRow(pages);
        PayloadRow(oldCarrier);
        WritePayload("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        WritePayload($"startxref\n{payloadStart}\n%%EOF\n");
        byte[] payloadBytes = payload.ToArray();
        output.Write(payloadBytes);
        Write("\nendstream\nendobj\n");

        int newCarrier = Position();
        Write("3 0 obj\n42\nendobj\n");
        int latestXref = Position();
        Write("xref\n0 4\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Row(newCarrier);
        Write($"trailer\n<< /Size 4 /Root 1 0 R /Prev {payloadStart} >>\n");
        Write($"startxref\n{latestXref}\n%%EOF\n");

        byte[] result = output.ToArray();
        Encoding.ASCII.GetBytes(payloadBytes.Length.ToString("D6", System.Globalization.CultureInfo.InvariantCulture))
            .CopyTo(result, lengthPosition);
        return result;

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void PayloadRow(int offset) => WritePayload($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        void WritePayload(string value) => payload.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] PreviousCrossReferenceInsideShadowedLiteralFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int oldCarrier = Position();
        Write("3 0 obj\n(");
        int embeddedXref = Position();
        Write("xref\n0 4\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Row(oldCarrier);
        Write("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        Write($"startxref\n{embeddedXref}\n%%EOF\n");
        Write(")\nendobj\n");

        int newCarrier = Position();
        Write("3 0 obj\n42\nendobj\n");
        int latestXref = Position();
        Write("xref\n0 4\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Row(newCarrier);
        Write($"trailer\n<< /Size 4 /Root 1 0 R /Prev {embeddedXref} >>\n");
        Write($"startxref\n{latestXref}\n%%EOF\n");
        return output.ToArray();

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] OrdinaryIncrementalShadowedStreamWithOverriddenLengthFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int oldLength = Position();
        Write("4 0 obj\n5\nendobj\n");
        int oldCarrier = Position();
        Write("3 0 obj\n<< /Length 4 0 R >>\nstream\nabcde\nendstream\nendobj\n");
        int oldXref = Position();
        Write("xref\n0 5\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Row(oldCarrier);
        Row(oldLength);
        Write("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        Write($"startxref\n{oldXref}\n%%EOF\n");

        int newCarrier = Position();
        Write("3 0 obj\n42\nendobj\n");
        int newLength = Position();
        Write("4 0 obj\n1\nendobj\n");
        int latestXref = Position();
        Write("xref\n0 5\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Row(newCarrier);
        Row(newLength);
        Write($"trailer\n<< /Size 5 /Root 1 0 R /Prev {oldXref} >>\n");
        Write($"startxref\n{latestXref}\n%%EOF\n");
        return output.ToArray();

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] PreviousCrossReferenceInsideUnindexedStreamFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        Write("9 0 obj\n<< /Length ");
        int lengthPosition = Position();
        Write("000000 >>\nstream\n");
        int embeddedXref = Position();

        using var payload = new MemoryStream();
        WritePayload("xref\n0 3\n0000000000 65535 f \n");
        PayloadRow(catalog);
        PayloadRow(pages);
        WritePayload("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        WritePayload($"startxref\n{embeddedXref}\n%%EOF\n");
        byte[] payloadBytes = payload.ToArray();
        output.Write(payloadBytes);
        Write("\nendstream\nendobj\n");

        int latestXref = Position();
        Write("xref\n0 3\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Write($"trailer\n<< /Size 3 /Root 1 0 R /Prev {embeddedXref} >>\n");
        Write($"startxref\n{latestXref}\n%%EOF\n");

        byte[] result = output.ToArray();
        Encoding.ASCII.GetBytes(payloadBytes.Length.ToString("D6", System.Globalization.CultureInfo.InvariantCulture))
            .CopyTo(result, lengthPosition);
        return result;

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void PayloadRow(int offset) => WritePayload($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        void WritePayload(string value) => payload.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] PreviousCrossReferenceInsideUnindexedLiteralFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        Write("9 0 obj\n(");
        int embeddedXref = Position();
        Write("xref\n0 3\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Write("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        Write($"startxref\n{embeddedXref}\n%%EOF\n");
        Write(")\nendobj\n");

        int latestXref = Position();
        Write("xref\n0 3\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Write($"trailer\n<< /Size 3 /Root 1 0 R /Prev {embeddedXref} >>\n");
        Write($"startxref\n{latestXref}\n%%EOF\n");
        return output.ToArray();

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] ManySparseIncrementalRevisionsFixture(int revisionCount)
    {
        Assert.True(revisionCount >= 2);
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int shared = Position();
        Write("3 0 obj\n0\nendobj\n");
        int unique = Position();
        Write("4 0 obj\n0\nendobj\n");
        int previousXref = Position();
        Write("xref\n0 5\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Row(shared);
        Row(unique);
        Write("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        Write($"startxref\n{previousXref}\n%%EOF\n");

        for (int revision = 1; revision < revisionCount; revision++)
        {
            shared = Position();
            Write($"3 0 obj\n{revision}\nendobj\n");
            int uniqueNumber = 4 + revision;
            unique = Position();
            Write($"{uniqueNumber} 0 obj\n{revision}\nendobj\n");
            int currentXref = Position();
            Write("xref\n0 1\n0000000000 65535 f \n");
            Write("3 1\n");
            Row(shared);
            Write($"{uniqueNumber} 1\n");
            Row(unique);
            Write($"trailer\n<< /Size {uniqueNumber + 1} /Root 1 0 R /Prev {previousXref} >>\n");
            Write($"startxref\n{currentXref}\n%%EOF\n");
            previousXref = currentXref;
        }

        return output.ToArray();

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static void SetNormalOffset(byte[] bytes, int number, int offset)
    {
        int row = Row(bytes, number);
        Encoding.ASCII.GetBytes(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
            .CopyTo(bytes, row);
        bytes[row + 17] = (byte)'n';
    }

    private static int Row(byte[] bytes, int number)
    {
        int marker = bytes.AsSpan().LastIndexOf("\nxref\n"u8);
        Assert.True(marker >= 0, "The generated PDF did not use a classic xref table.");
        int position = marker + "\nxref\n".Length;

        while (position < bytes.Length)
        {
            int lineLength = bytes.AsSpan(position).IndexOf((byte)'\n');
            Assert.True(lineLength >= 0);
            ReadOnlySpan<byte> line = bytes.AsSpan(position, lineLength).TrimEnd((byte)'\r');
            Assert.False(line.StartsWith("trailer"u8), $"Object {number} was absent from the xref table.");

            string[] header = Encoding.ASCII.GetString(line)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, header.Length);
            int first = int.Parse(header[0], System.Globalization.CultureInfo.InvariantCulture);
            int count = int.Parse(header[1], System.Globalization.CultureInfo.InvariantCulture);
            position += lineLength + 1;

            if (number >= first && number < first + count)
                return position + checked((number - first) * 20);

            position += checked(count * 20);
        }

        throw new Xunit.Sdk.XunitException($"Object {number} was absent from the xref table.");
    }
}
