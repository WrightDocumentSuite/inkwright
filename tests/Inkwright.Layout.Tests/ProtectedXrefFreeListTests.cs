using System.Text;
using Inkwright.Cos;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Layout.Tests;

public sealed class ProtectedXrefFreeListTests
{
    [Fact]
    public void RejectsObjectZeroFreePointerOutsideSize()
    {
        (byte[] bytes, _) = BuildClassicTableWithProbe();
        SetFreeRow(bytes, 0, nextFree: 9_999, generation: 65_535);

        AssertFreeListProblem(bytes, "outside");
    }

    [Fact]
    public void RejectsFreeListPointerToAnInUseObject()
    {
        (byte[] bytes, int probe) = BuildClassicTableWithProbe();
        SetFreeRow(bytes, 0, nextFree: probe, generation: 65_535);

        AssertFreeListProblem(bytes, "free object");
    }

    [Fact]
    public void RejectsFreeListCycleThatDoesNotReturnToObjectZero()
    {
        (byte[] bytes, int probe) = BuildClassicTableWithProbe();
        SetFreeRow(bytes, 0, nextFree: probe, generation: 65_535);
        SetFreeRow(bytes, probe, nextFree: probe, generation: 1);

        AssertFreeListProblem(bytes, "cycle");
    }

    [Fact]
    public void RejectsNonTerminalFreeEntryOutsideTheLinkedList()
    {
        (byte[] bytes, int probe) = BuildClassicTableWithProbe();
        SetFreeRow(bytes, probe, nextFree: 0, generation: 1);

        AssertFreeListProblem(bytes, "outside the linked list");
    }

    [Fact]
    public void AcceptsACompleteFreeListChain()
    {
        (byte[] bytes, int probe) = BuildClassicTableWithProbe();
        SetFreeRow(bytes, 0, nextFree: probe, generation: 65_535);
        SetFreeRow(bytes, probe, nextFree: 0, generation: 1);

        Assert.Empty(PdfAProfile.Validate(bytes, PdfAConformance.A4));
    }

    [Fact]
    public void AcceptsATerminalFreeEntryOutsideTheLinkedList()
    {
        (byte[] bytes, int probe) = BuildClassicTableWithProbe();
        SetFreeRow(bytes, probe, nextFree: 0, generation: 65_535);

        Assert.Empty(PdfAProfile.Validate(bytes, PdfAConformance.A4));
    }

    private static (byte[] Bytes, int Probe) BuildClassicTableWithProbe()
    {
        _ = PdfAConformanceTests.BuildArchival(PdfAConformance.A4, out PdfDocument document);
        using (document)
        {
            PdfObjectId probe = document.AddObject(PdfValue.Integer(42));
            byte[] bytes = document.ToArray(new PdfSaveOptions
            {
                UseObjectStreams = false,
                UpdateModificationDate = false,
                SetProducer = false,
            });

            Assert.True(Row(bytes, 0) >= 0);
            Assert.Equal((byte)'n', bytes[Row(bytes, probe.Number) + 17]);
            return (bytes, probe.Number);
        }
    }

    private static void SetFreeRow(byte[] bytes, int number, int nextFree, int generation)
    {
        int row = Row(bytes, number);
        if (bytes[row + 17] == (byte)'n')
        {
            int objectOffset = int.Parse(
                Encoding.ASCII.GetString(bytes, row, 10),
                System.Globalization.CultureInfo.InvariantCulture);
            int endobj = bytes.AsSpan(objectOffset).IndexOf("endobj"u8);
            Assert.True(endobj >= 0);

            // A free xref slot must not leave an unindexed indirect object in the revision body.
            // Preserve all following offsets by replacing the probe with same-length trivia.
            bytes.AsSpan(objectOffset, endobj + "endobj".Length).Fill((byte)' ');
        }

        Encoding.ASCII.GetBytes(nextFree.ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
            .CopyTo(bytes, row);
        Encoding.ASCII.GetBytes(generation.ToString("D5", System.Globalization.CultureInfo.InvariantCulture))
            .CopyTo(bytes, row + 11);
        bytes[row + 17] = (byte)'f';
    }

    private static int Row(byte[] bytes, int number)
    {
        int marker = bytes.AsSpan().LastIndexOf("\nxref\n"u8);
        Assert.True(marker >= 0, "The generated PDF did not use a classic xref table.");
        int position = marker + "\nxref\n".Length;
        int row = -1;

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
            {
                row = position + checked((number - first) * 20);
                break;
            }

            position += checked(count * 20);
        }

        Assert.InRange(row, 0, bytes.Length - 20);
        Assert.Equal((byte)' ', bytes[row + 10]);
        Assert.Equal((byte)' ', bytes[row + 16]);
        Assert.Equal((byte)' ', bytes[row + 18]);
        Assert.Equal((byte)'\n', bytes[row + 19]);
        return row;
    }

    private static void AssertFreeListProblem(byte[] bytes, string fragment)
    {
        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A4),
            problem => problem.Clause == "6.1" &&
                       problem.Message.Contains("free", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
