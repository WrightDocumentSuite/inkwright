using System.Text;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Filters;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Protected-reader checks for the classic cross-reference form of ISO 32000-2:2020 Annex F.
/// The fixture deliberately keeps the hint payload empty because these tests validate the Part 2,
/// Part 3, and Part 11 indexing contract rather than the binary hint-table grammar in F.4.
/// </summary>
public sealed class ProtectedLinearizationAnnexFTests
{
    [Fact]
    public void AcceptsAClassicAnnexFLayout()
    {
        using PdfDocument document = Load(Fixture());

        Assert.Single(document.Pages);
    }

    [Fact]
    public void AcceptsANonzeroFirstPageIndexWhenOMatchesPageP()
    {
        using PdfDocument document = Load(NonzeroFirstPageIndexFixture(firstPageIndex: 1));

        Assert.Equal(2, document.Pages.Count);
    }

    [Fact]
    public void RejectsWhenODoesNotIdentifyThePageAtP()
    {
        AssertRejected(NonzeroFirstPageIndexFixture(firstPageIndex: 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsTThatDoesNotIdentifyTheWhitespaceBeforeObjectZero(int delta)
    {
        AssertRejected(Fixture(new FixtureMutation(TDelta: delta)));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void RejectsHintRangeThatDoesNotMatchThePrimaryHintStream(
        int offsetDelta,
        int lengthDelta)
    {
        AssertRejected(Fixture(new FixtureMutation(
            HintOffsetDelta: offsetDelta,
            HintLengthDelta: lengthDelta)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/S -1")]
    [InlineData("/S 1 0 R")]
    public void RejectsAnInvalidPrimarySharedObjectHintPosition(string entry)
    {
        AssertRejected(Fixture(new FixtureMutation(PrimaryHintEntries: entry)));
    }

    [Fact]
    public void RejectsACommentCountedAsHintStreamOverhead()
    {
        AssertRejected(Fixture(new FixtureMutation(CommentInsideHintRange: true)));
    }

    [Fact]
    public void AcceptsAnOverflowHintWithoutStandardTablePositions()
    {
        using PdfDocument document = Load(Fixture(new FixtureMutation(OverflowHintEntries: "")));

        Assert.Single(document.Pages);
    }

    [Fact]
    public void RejectsAStandardTablePositionInTheOverflowHint()
    {
        AssertRejected(Fixture(new FixtureMutation(OverflowHintEntries: "/S 0")));
    }

    [Fact]
    public void RejectsAnIndirectHintLengthThatIsAbsentFromTheFirstPageXref()
    {
        AssertRejected(Fixture(new FixtureMutation(IndirectHintLengthFromMainXref: true)));
    }

    [Fact]
    public void AcceptsAnImmediateIndirectHintLengthIndexedByTheFirstPageXref()
    {
        using PdfDocument document = Load(
            Fixture(new FixtureMutation(IndirectHintLengthInFirstPageXref: true)));

        Assert.Single(document.Pages);
    }

    [Fact]
    public void RejectsAnIndirectHintLengthWithTheWrongGeneration()
    {
        AssertRejected(Fixture(new FixtureMutation(
            IndirectHintLengthInFirstPageXref: true,
            HintLengthReferenceGeneration: 1)));
    }

    [Fact]
    public void RejectsAnObjectBetweenTheHintStreamAndItsIndirectLength()
    {
        AssertRejected(Fixture(new FixtureMutation(
            IndirectHintLengthInFirstPageXref: true,
            InterveningObjectBeforeHintLength: true)));
    }

    [Fact]
    public void RejectsANonClassicFirstPageXrefFailClosed()
    {
        byte[] bytes = Fixture();
        int marker = bytes.AsSpan().IndexOf("\nxref\n2 "u8);
        Assert.True(marker >= 0);
        "6 0 "u8.CopyTo(bytes.AsSpan(marker + 1, 4));

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() => Load(bytes));
        Assert.Contains("xref streams are rejected fail-closed", exception.Message);
    }

    [Fact]
    public void RejectsOThatDoesNotIdentifyAPageObject()
    {
        AssertRejected(Fixture(new FixtureMutation(FirstPageObjectNumber: 3)));
    }

    [Fact]
    public void RejectsNThatDoesNotMatchThePageTree()
    {
        AssertRejected(Fixture(new FixtureMutation(PageCount: 2)));
    }

    [Fact]
    public void RejectsEThatDoesNotIdentifyTheEndOfTheFirstPageSection()
    {
        AssertRejected(Fixture(new FixtureMutation(FirstPageEndDelta: 1)));
    }

    [Fact]
    public void RejectsAnUnsupportedLinearizationMajorVersion()
    {
        AssertRejected(Fixture(new FixtureMutation(LinearizedVersion: 2)));
    }

    [Fact]
    public void RejectsAFreeRowInTheFirstPageCrossReferenceTable()
    {
        AssertRejected(Fixture(new FixtureMutation(FreeHintRow: true)));
    }

    [Fact]
    public void RejectsASplitFirstPageCrossReferenceTable()
    {
        AssertRejected(Fixture(new FixtureMutation(SplitFirstPageSubsection: true)));
    }

    [Fact]
    public void RejectsASplitMainCrossReferenceTable()
    {
        AssertRejected(Fixture(new FixtureMutation(SplitMainSubsection: true)));
    }

    [Fact]
    public void RejectsAFreeNonzeroRowInTheMainCrossReferenceTable()
    {
        AssertRejected(Fixture(new FixtureMutation(FreeNonzeroMainRow: true)));
    }

    [Fact]
    public void RejectsAGenerationOneCatalogInTheFirstPageCrossReferenceTable()
    {
        AssertRejected(Fixture(new FixtureMutation(FirstPageCatalogGenerationOne: true)));
    }

    [Fact]
    public void RejectsAGenerationOneObjectInTheMainCrossReferenceTable()
    {
        AssertRejected(Fixture(new FixtureMutation(MainPagesGenerationOne: true)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsAGapOrOverlapBetweenTheOriginalObjectGroups(int boundaryDelta)
    {
        AssertRejected(Fixture(new FixtureMutation(OriginalGroupBoundaryDelta: boundaryDelta)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RejectsAFirstPageThatInheritsARequiredAttribute(
        bool omitMediaBox,
        bool omitResources)
    {
        AssertRejected(Fixture(new FixtureMutation(
            OmitFirstPageMediaBox: omitMediaBox,
            OmitFirstPageResources: omitResources)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void RejectsAnUnusableFirstPageResourcesValue(string value)
    {
        AssertRejected(Fixture(new FixtureMutation(FirstPageResourcesValue: value)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("<<>>")]
    [InlineData("[0 0 /Bad 792]")]
    [InlineData("[0 0 0 792]")]
    public void RejectsAnUnusableFirstPageMediaBoxValue(string value)
    {
        AssertRejected(Fixture(new FixtureMutation(FirstPageMediaBoxValue: value)));
    }

    [Fact]
    public void RejectsAnExtraMainTrailerEntry()
    {
        AssertRejected(Fixture(new FixtureMutation(MainTrailerExtraEntry: true)));
    }

    [Fact]
    public void RejectsAFirstPageTrailerWithoutRootEvenWhenMainSuppliesIt()
    {
        AssertRejected(Fixture(new FixtureMutation(MoveFirstRootToMainTrailer: true)));
    }

    [Fact]
    public void RejectsAMainTrailerSizeThatUsesTheCombinedObjectCount()
    {
        AssertRejected(Fixture(new FixtureMutation(MainSizeUsesCombinedCount: true)));
    }

    [Fact]
    public void AcceptsReferencedFirstPageContentsInsidePartSix()
    {
        using PdfDocument document = Load(Fixture(new FixtureMutation(FirstPageContents: true)));

        Assert.Single(document.Pages);
    }

    [Fact]
    public void RejectsEAtTheBoundaryBeforeReferencedFirstPageContents()
    {
        AssertRejected(Fixture(new FixtureMutation(
            FirstPageContents: true,
            FirstPageEndBeforeContents: true)));
    }

    [Fact]
    public void RejectsAFirstPageCrossReferenceTableWithoutTheLinearizationRow()
    {
        AssertRejected(Fixture(new FixtureMutation(OmitLinearizationRow: true)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void RejectsAReferenceToAnAnnexFInfrastructureObject(int objectNumber)
    {
        AssertRejected(Fixture(new FixtureMutation(ReferencedInfrastructureObject: objectNumber)));
    }

    private static PdfDocument Load(byte[] bytes) =>
        PdfDocument.Load(
            bytes,
            new PdfReadOptions
            {
                Recover = false,
                StreamDecodePolicy = new ProtectedDecodePolicy(),
            });

    private static void AssertRejected(byte[] bytes)
    {
        Assert.Throws<PdfFormatException>(() =>
        {
            using PdfDocument _ = Load(bytes);
        });
    }

    private static byte[] NonzeroFirstPageIndexFixture(int firstPageIndex)
    {
        Assert.InRange(firstPageIndex, 0, 1);
        using var output = new MemoryStream();
        Write("%PDF-2.0\n%âãÏÓ\n");

        int linearizationOffset = Position();
        Write(
            "3 0 obj\n" +
            "<< /Linearized 1 /L 0000000000 /H [0000000000 0000000000] " +
            $"/O 5 /E 0000000000 /N 2 /P {firstPageIndex} /T 0000000000 >>\n" +
            "endobj\n");

        int firstXrefOffset = Position();
        Write("xref\n3 4\n");
        var rowPositions = new Dictionary<int, int>();
        for (int number = 3; number <= 6; number++)
        {
            rowPositions.Add(number, Position());
            Write("0000000000 00000 n \n");
        }

        Write("trailer\n<< /Size 7 /Root 4 0 R /Prev 0000000000 >>\n");
        int previousOffsetPosition = Position() - " >>\n".Length - 10;

        int catalogOffset = Position();
        Write("4 0 obj\n<< /Type /Catalog /Pages 1 0 R >>\nendobj\n");

        int hintOffset = Position();
        Write("6 0 obj\n<< /Length 0 /S 0 >>\nstream\n\nendstream\nendobj\n");
        int hintLength = Position() - hintOffset;

        int firstPageOffset = Position();
        Write(
            "5 0 obj\n" +
            "<< /Type /Page /Parent 1 0 R /MediaBox [0 0 612 792] /Resources <<>> >>\n" +
            "endobj\n");
        int firstPageEnd = Position();

        int pagesOffset = Position();
        Write("1 0 obj\n<< /Type /Pages /Kids [2 0 R 5 0 R] /Count 2 >>\nendobj\n");
        int otherPageOffset = Position();
        Write(
            "2 0 obj\n" +
            "<< /Type /Page /Parent 1 0 R /MediaBox [0 0 612 792] /Resources <<>> >>\n" +
            "endobj\n");

        int mainXrefOffset = Position();
        Write("xref\n0 3\n");
        int mainFirstEntryWhitespace = Position() - 1;
        Write("0000000000 65535 f \n");
        Write($"{pagesOffset:D10} 00000 n \n");
        Write($"{otherPageOffset:D10} 00000 n \n");
        Write("trailer\n<< /Size 3 >>\n");
        Write($"startxref\n{firstXrefOffset}\n%%EOF\n");
        int originalLength = Position();

        PatchDecimal(linearizationOffset, "/L ", originalLength);
        PatchDecimal(linearizationOffset, "/H [", hintOffset);
        PatchDecimal(
            linearizationOffset,
            $"/H [{hintOffset:D10} ",
            hintLength);
        PatchDecimal(linearizationOffset, "/E ", firstPageEnd);
        PatchDecimal(linearizationOffset, "/T ", mainFirstEntryWhitespace);
        PatchDecimalAt(previousOffsetPosition, mainXrefOffset);
        PatchDecimalAt(rowPositions[3], linearizationOffset);
        PatchDecimalAt(rowPositions[4], catalogOffset);
        PatchDecimalAt(rowPositions[5], firstPageOffset);
        PatchDecimalAt(rowPositions[6], hintOffset);
        return output.ToArray();

        int Position() => checked((int)output.Position);

        void Write(string value) => output.Write(Encoding.Latin1.GetBytes(value));

        void PatchDecimal(int searchStart, string marker, int value)
        {
            byte[] buffer = output.GetBuffer();
            ReadOnlySpan<byte> haystack =
                buffer.AsSpan(searchStart, checked((int)output.Length) - searchStart);
            int relative = haystack.IndexOf(Encoding.ASCII.GetBytes(marker));
            Assert.True(relative >= 0, $"Fixture marker '{marker}' was not found.");
            PatchDecimalAt(searchStart + relative + marker.Length, value);
        }

        void PatchDecimalAt(int position, int value) =>
            Encoding.ASCII.GetBytes($"{value:D10}").CopyTo(output.GetBuffer(), position);
    }

    private static byte[] Fixture(FixtureMutation? mutation = null)
    {
        mutation ??= new FixtureMutation();

        using var output = new MemoryStream();
        Write("%PDF-2.0\n%âãÏÓ\n");

        bool hasOverflowHint = mutation.OverflowHintEntries is not null;
        bool hasMisplacedHintLength = mutation.IndirectHintLengthFromMainXref;
        bool hasIndexedHintLength = mutation.IndirectHintLengthInFirstPageXref;
        bool hasIndirectHintLength = hasMisplacedHintLength || hasIndexedHintLength;
        bool hasFirstPageContents = mutation.FirstPageContents;
        Assert.False(hasOverflowHint && (hasIndirectHintLength || hasFirstPageContents));
        Assert.False(hasIndirectHintLength && hasFirstPageContents);
        Assert.False(hasMisplacedHintLength && hasIndexedHintLength);
        Assert.False(mutation.HintLengthReferenceGeneration != 0 && !hasIndexedHintLength);
        Assert.False(mutation.InterveningObjectBeforeHintLength && !hasIndexedHintLength);
        Assert.True(mutation.OriginalGroupBoundaryDelta is >= -1 and <= 1);
        Assert.False(
            mutation.OriginalGroupBoundaryDelta != 0 &&
            (mutation.SplitMainSubsection || mutation.FreeNonzeroMainRow));
        Assert.False(mutation.FirstPageEndBeforeContents && !hasFirstPageContents);
        string overflowHintSlots = hasOverflowHint ? " 0000000000 0000000000" : string.Empty;
        int hintObjectNumber = hasIndirectHintLength ? 7 : hasFirstPageContents ? 6 : 5;
        int pagesObjectNumber = hasMisplacedHintLength ? 6 : 1;
        int hintLengthObjectNumber = hasMisplacedHintLength ? 1 : 6;
        int catalogGeneration = mutation.FirstPageCatalogGenerationOne ? 1 : 0;
        int pagesGeneration = mutation.MainPagesGenerationOne ? 1 : 0;

        int linearizationOffset = Position();
        Write(
            "2 0 obj\n" +
            $"<< /Linearized {mutation.LinearizedVersion} " +
            $"/L 0000000000 /H [0000000000 0000000000{overflowHintSlots}] " +
            $"/O {mutation.FirstPageObjectNumber} /E 0000000000 /N {mutation.PageCount} " +
            "/T 0000000000 >>\n" +
            "endobj\n");

        int firstXrefOffset = Position();
        Write("xref\n");
        var rowPositions = new Dictionary<int, int>();
        if (mutation.OmitLinearizationRow)
        {
            Write("3 3\n");
            WriteFirstPageRow(3);
            WriteFirstPageRow(4);
            WriteFirstPageRow(5);
        }
        else if (mutation.SplitFirstPageSubsection)
        {
            Write("2 2\n");
            WriteFirstPageRow(2);
            WriteFirstPageRow(3);
            Write("4 2\n");
            WriteFirstPageRow(4);
            WriteFirstPageRow(5);
        }
        else
        {
            int firstPageRowStart = 2;
            int firstPageRowCount = hasIndirectHintLength ? 6 :
                hasFirstPageContents ? 5 :
                hasOverflowHint ? 5 : 4;
            Write($"{firstPageRowStart} {firstPageRowCount}\n");
            for (int number = firstPageRowStart;
                 number < firstPageRowStart + firstPageRowCount;
                 number++)
            {
                WriteFirstPageRow(number);
            }
        }

        int size = hasIndirectHintLength ? 8 :
            hasOverflowHint || hasFirstPageContents ? 7 : 6;
        string firstTrailerRoot = mutation.MoveFirstRootToMainTrailer
            ? string.Empty
            : $" /Root 3 {catalogGeneration} R";
        Write(
            $"trailer\n<< /Size {size}{firstTrailerRoot} " +
            "/Prev 0000000000 >>\n");
        int previousOffsetPosition = Position() - " >>\n".Length - 10;

        int catalogOffset = Position();
        string infrastructureReference = mutation.ReferencedInfrastructureObject > 0
            ? $" /AnnexFProbe {mutation.ReferencedInfrastructureObject} 0 R"
            : string.Empty;
        Write(
            $"3 {catalogGeneration} obj\n" +
            $"<< /Type /Catalog /Pages {pagesObjectNumber} {pagesGeneration} R" +
            $"{infrastructureReference} >>\n" +
            "endobj\n");

        int dummyOffset = -1;
        if (hasIndirectHintLength && !mutation.InterveningObjectBeforeHintLength)
        {
            dummyOffset = Position();
            Write("5 0 obj\nnull\nendobj\n");
        }

        int hintOffset = Position();
        string hintLengthEntry = hasIndirectHintLength
            ? $"{hintLengthObjectNumber} {mutation.HintLengthReferenceGeneration} R"
            : "0";
        Write(
            $"{hintObjectNumber} 0 obj\n" +
            $"<< /Length {hintLengthEntry} {mutation.PrimaryHintEntries} >>\n" +
            "stream\n\nendstream\nendobj\n");
        if (mutation.CommentInsideHintRange)
            Write("% not stream-object overhead\n");

        if (hasIndirectHintLength && mutation.InterveningObjectBeforeHintLength)
        {
            dummyOffset = Position();
            Write("5 0 obj\nnull\nendobj\n");
        }

        int indirectLengthOffset = -1;
        if (hasIndirectHintLength)
        {
            indirectLengthOffset = Position();
            Write($"{hintLengthObjectNumber} 0 obj\n0\nendobj\n");
        }

        int hintLength = Position() - hintOffset;

        int pageOffset = Position();
        string pageMediaBox = mutation.OmitFirstPageMediaBox
            ? string.Empty
            : $" /MediaBox {mutation.FirstPageMediaBoxValue ?? "[0 0 612 792]"}";
        string pageResources = mutation.OmitFirstPageResources
            ? string.Empty
            : $" /Resources {mutation.FirstPageResourcesValue ?? "<<>>"}";
        string pageContents = hasFirstPageContents ? " /Contents 5 0 R" : string.Empty;
        Write(
            "4 0 obj\n" +
            $"<< /Type /Page /Parent {pagesObjectNumber} {pagesGeneration} R" +
            $"{pageMediaBox}{pageResources}{pageContents} >>\n" +
            "endobj\n");
        int pageObjectEnd = Position();

        int contentsOffset = -1;
        if (hasFirstPageContents)
        {
            contentsOffset = Position();
            Write("5 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");
        }

        int firstPageEnd = mutation.FirstPageEndBeforeContents ? pageObjectEnd : Position();

        int pagesOffset = Position();
        string inheritedMediaBox = mutation.OmitFirstPageMediaBox
            ? " /MediaBox [0 0 612 792]"
            : string.Empty;
        string inheritedResources = mutation.OmitFirstPageResources
            ? " /Resources <<>>"
            : string.Empty;
        Write(
            $"{pagesObjectNumber} {pagesGeneration} obj\n" +
            $"<< /Type /Pages /Kids [4 0 R] /Count 1" +
            $"{inheritedMediaBox}{inheritedResources} >>\nendobj\n");

        int overflowHintOffset = -1;
        int overflowHintLength = 0;
        if (hasOverflowHint)
        {
            overflowHintOffset = Position();
            Write(
                $"6 0 obj\n<< /Length 0 {mutation.OverflowHintEntries} >>\n" +
                "stream\n\nendstream\nendobj\n");
            overflowHintLength = Position() - overflowHintOffset;
        }

        int mainXrefOffset = Position();
        Write("xref\n");
        int mainRowCount = mutation.FreeNonzeroMainRow
            ? 3
            : 2 + mutation.OriginalGroupBoundaryDelta;
        Write(mutation.SplitMainSubsection ? "0 1\n" : $"0 {mainRowCount}\n");
        int mainFirstEntryWhitespace = Position() - 1;
        int freeListHead = mutation.FreeHintRow ? 5 : 0;
        Write($"{freeListHead:D10} 65535 f \n");
        if (mutation.SplitMainSubsection)
            Write("1 1\n");
        if (mutation.SplitMainSubsection || mainRowCount >= 2)
        {
            Write(
                $"{(hasMisplacedHintLength ? indirectLengthOffset : pagesOffset):D10} " +
                $"{pagesGeneration:D5} n \n");
        }
        if (mutation.FreeNonzeroMainRow)
            Write("0000000000 65535 f \n");
        else if (mutation.OriginalGroupBoundaryDelta > 0)
            Write($"{linearizationOffset:D10} 00000 n \n");

        int exactMainSize = mutation.SplitMainSubsection ? 2 : mainRowCount;
        int mainTrailerSize = mutation.MainSizeUsesCombinedCount ? size : exactMainSize;
        string mainTrailerExtra = mutation.MoveFirstRootToMainTrailer
            ? $" /Root 3 {catalogGeneration} R"
            : mutation.MainTrailerExtraEntry ? " /Info null" : string.Empty;
        Write($"trailer\n<< /Size {mainTrailerSize}{mainTrailerExtra} >>\n");
        Write($"startxref\n{firstXrefOffset}\n%%EOF\n");
        int originalLength = Position();

        if (mutation.OriginalGroupBoundaryDelta < 0)
        {
            int updateXrefOffset = Position();
            Write("xref\n1 1\n");
            Write($"{pagesOffset:D10} {pagesGeneration:D5} n \n");
            Write(
                $"trailer\n<< /Size {size} /Root 3 {catalogGeneration} R " +
                $"/Prev {firstXrefOffset} >>\n");
            Write($"startxref\n{updateXrefOffset}\n%%EOF\n");
        }

        PatchDecimal(linearizationOffset, "/L ", originalLength);
        PatchDecimal(
            linearizationOffset,
            "/H [",
            hintOffset + mutation.HintOffsetDelta);
        PatchDecimal(
            linearizationOffset,
            $"/H [{hintOffset + mutation.HintOffsetDelta:D10} ",
            hintLength + mutation.HintLengthDelta);
        if (hasOverflowHint)
        {
            PatchDecimal(
                linearizationOffset,
                $"/H [{hintOffset + mutation.HintOffsetDelta:D10} " +
                $"{hintLength + mutation.HintLengthDelta:D10} ",
                overflowHintOffset);
            PatchDecimal(
                linearizationOffset,
                $"/H [{hintOffset + mutation.HintOffsetDelta:D10} " +
                $"{hintLength + mutation.HintLengthDelta:D10} {overflowHintOffset:D10} ",
                overflowHintLength);
        }
        PatchDecimal(
            linearizationOffset,
            "/E ",
            firstPageEnd + mutation.FirstPageEndDelta);
        PatchDecimal(
            linearizationOffset,
            "/T ",
            mainFirstEntryWhitespace + mutation.TDelta);
        PatchDecimalAt(previousOffsetPosition, mainXrefOffset);

        PatchRow(2, linearizationOffset);
        PatchRow(3, catalogOffset);
        PatchRow(4, pageOffset);
        if (hasIndirectHintLength)
        {
            PatchRow(5, dummyOffset);
            PatchRow(6, hasIndexedHintLength ? indirectLengthOffset : pagesOffset);
            PatchRow(7, hintOffset);
        }
        else if (!mutation.FreeHintRow)
        {
            PatchRow(5, hintOffset);
        }
        if (hasFirstPageContents)
        {
            PatchRow(5, contentsOffset);
            PatchRow(6, hintOffset);
        }
        if (hasOverflowHint)
            PatchRow(6, overflowHintOffset);

        return output.ToArray();

        int Position() => checked((int)output.Position);

        void Write(string value) => output.Write(Encoding.Latin1.GetBytes(value));

        void WriteFirstPageRow(int objectNumber)
        {
            rowPositions.Add(objectNumber, Position());
            if (objectNumber == 5 && mutation.FreeHintRow)
                Write("0000000000 00001 f \n");
            else if (objectNumber == 3 && mutation.FirstPageCatalogGenerationOne)
                Write("0000000000 00001 n \n");
            else
                Write("0000000000 00000 n \n");
        }

        void PatchRow(int objectNumber, int offset)
        {
            if (rowPositions.TryGetValue(objectNumber, out int position))
                PatchDecimalAt(position, offset);
        }

        void PatchDecimal(int searchStart, string marker, int value)
        {
            byte[] buffer = output.GetBuffer();
            ReadOnlySpan<byte> haystack =
                buffer.AsSpan(searchStart, checked((int)output.Length) - searchStart);
            int relative = haystack.IndexOf(Encoding.ASCII.GetBytes(marker));
            Assert.True(relative >= 0, $"Fixture marker '{marker}' was not found.");
            PatchDecimalAt(searchStart + relative + marker.Length, value);
        }

        void PatchDecimalAt(int position, int value) =>
            Encoding.ASCII.GetBytes($"{value:D10}").CopyTo(output.GetBuffer(), position);
    }

    private sealed record FixtureMutation(
        int TDelta = 0,
        int HintOffsetDelta = 0,
        int HintLengthDelta = 0,
        int FirstPageObjectNumber = 4,
        int PageCount = 1,
        int FirstPageEndDelta = 0,
        int LinearizedVersion = 1,
        bool FreeHintRow = false,
        bool SplitFirstPageSubsection = false,
        bool SplitMainSubsection = false,
        bool FreeNonzeroMainRow = false,
        bool FirstPageCatalogGenerationOne = false,
        bool MainPagesGenerationOne = false,
        int OriginalGroupBoundaryDelta = 0,
        bool OmitFirstPageMediaBox = false,
        bool OmitFirstPageResources = false,
        string? FirstPageMediaBoxValue = null,
        string? FirstPageResourcesValue = null,
        bool MainTrailerExtraEntry = false,
        bool MoveFirstRootToMainTrailer = false,
        bool MainSizeUsesCombinedCount = false,
        bool FirstPageContents = false,
        bool FirstPageEndBeforeContents = false,
        bool OmitLinearizationRow = false,
        int ReferencedInfrastructureObject = 0,
        string PrimaryHintEntries = "/S 0",
        bool CommentInsideHintRange = false,
        string? OverflowHintEntries = null,
        bool IndirectHintLengthFromMainXref = false,
        bool IndirectHintLengthInFirstPageXref = false,
        int HintLengthReferenceGeneration = 0,
        bool InterveningObjectBeforeHintLength = false);

    private sealed class ProtectedDecodePolicy : IPdfStreamDecodePolicy
    {
        private int _structuralEntries;

        public int MaxStructuralEntries => 1_048_576;

        public void ConsumeStructuralEntries(int count)
        {
            if (count <= 0 || count > MaxStructuralEntries - _structuralEntries)
                throw new PdfFormatException("The fixture exceeded its structural-entry budget.");

            _structuralEntries += count;
        }

        public void ConsumeParsedValues(int count)
        {
            if (count <= 0)
                throw new PdfFormatException("The fixture received an invalid parser charge.");
        }

        public ReadOnlyMemory<byte> Decode(PdfStream stream, out PdfName? undecodedFilter) =>
            PdfFilterPipeline.Decode(stream.RawData, stream.Dictionary, out undecodedFilter);

        public PdfBoundedDecodeResult DecodeBounded(PdfStream stream, int maxDecodedBytes) =>
            PdfFilterPipeline.DecodeBounded(stream.RawData, stream.Dictionary, maxDecodedBytes);
    }
}
