using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Filters;
using Inkwright.IO;
using Inkwright.Syntax;
using Xunit;

namespace Inkwright.Tests;

[CollectionDefinition("Process-wide PDF filter limit", DisableParallelization = true)]
public sealed class ProcessWidePdfFilterLimitCollection;

[Collection("Process-wide PDF filter limit")]
public class PdfResourceLimitTests
{
    [Fact]
    public void RejectsAnInputLargerThanTheRequestBudget()
    {
        byte[] file = MinimalPdf.OnePage();
        var limits = new PdfResourceLimits(maxInputBytes: file.Length - 1);

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => PdfReader.Open(file, new PdfReadOptions { ResourceLimits = limits }));

        Assert.Equal(nameof(PdfResourceLimits.MaxInputBytes), exception.LimitName);
        Assert.Equal(file.Length - 1, exception.Limit);
        Assert.Equal(file.Length, exception.Observed);
    }

    [Fact]
    public async Task AsyncStreamReadHonoursCancellation()
    {
        using var stream = new MemoryStream(MinimalPdf.OnePage());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PdfReader.OpenAsync(stream, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void SeekableStreamIsReadDirectlyIntoItsExactSizedInputBuffer()
    {
        byte[] file = MinimalPdf.OnePage();
        using var stream = new ReadSizeTrackingMemoryStream(file);

        _ = PdfReader.Open(stream);

        Assert.Equal(file.Length, stream.FirstSynchronousBufferLength);
    }

    [Fact]
    public async Task SeekableAsyncStreamIsReadDirectlyIntoItsExactSizedInputBuffer()
    {
        byte[] file = MinimalPdf.OnePage();
        using var stream = new ReadSizeTrackingMemoryStream(file);

        _ = await PdfReader.OpenAsync(
            stream,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(file.Length, stream.FirstAsynchronousBufferLength);
    }

    [Fact]
    public void RejectsAStreamWhoseDecodedOutputExceedsItsLimit()
    {
        byte[] file = ContentDocument(PdfStream.FromData(new byte[4_096]));
        var limits = new PdfResourceLimits(maxDecodedBytesPerStream: 1_024);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => document.Pages[0].GetContent());

        Assert.Equal(nameof(PdfResourceLimits.MaxDecodedBytesPerStream), exception.LimitName);
    }

    [Fact]
    public void TotalDecodedBudgetIsSharedByLazyStreamsInOneDocument()
    {
        byte[] file = ContentDocument(
            PdfStream.FromData(new byte[600]),
            PdfStream.FromData(new byte[600]));
        var limits = new PdfResourceLimits(
            maxDecodedBytesPerStream: 1_024,
            maxTotalDecodedBytes: 1_000,
            maxTotalDecodeWorkBytes: 10_000);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => document.Pages[0].GetContent());

        Assert.Equal(nameof(PdfResourceLimits.MaxTotalDecodedBytes), exception.LimitName);
    }

    [Fact]
    public void DecodeWorkBudgetCountsEveryMaterialisedFilterStage()
    {
        byte[] file = ContentDocument(
            PdfStream.FromData(new byte[600]),
            PdfStream.FromData(new byte[600]));
        var limits = new PdfResourceLimits(
            maxDecodedBytesPerStream: 2_000,
            maxTotalDecodedBytes: 10_000,
            maxTotalDecodeWorkBytes: 1_000);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => document.Pages[0].GetContent());

        Assert.Equal(nameof(PdfResourceLimits.MaxTotalDecodeWorkBytes), exception.LimitName);
    }

    [Fact]
    public void MultiStageWorkDoesNotConsumeTheRetainedOutputBudget()
    {
        byte[] payload = Enumerable.Repeat((byte)'A', 60).ToArray();
        byte[] file = ContentDocument(TwiceAscii85Encoded(payload));
        var limits = new PdfResourceLimits(
            maxDecodedBytesPerStream: 100,
            maxTotalDecodedBytes: 70,
            maxTotalDecodeWorkBytes: 200);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        Assert.Equal(payload, document.Pages[0].GetContent());
    }

    [Fact]
    public void MultiStageDecodeStillHonoursItsSeparateWorkBudget()
    {
        byte[] payload = Enumerable.Repeat((byte)'A', 60).ToArray();
        byte[] file = ContentDocument(TwiceAscii85Encoded(payload));
        var limits = new PdfResourceLimits(
            maxDecodedBytesPerStream: 100,
            maxTotalDecodedBytes: 70,
            maxTotalDecodeWorkBytes: 120);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => document.Pages[0].GetContent());

        Assert.Equal(nameof(PdfResourceLimits.MaxTotalDecodeWorkBytes), exception.LimitName);
    }

    [Fact]
    public void UnfilteredStreamsDoNotConsumeDecodeWork()
    {
        byte[] file = ContentDocument(PdfStream.FromData(new byte[600], compress: false));
        var limits = new PdfResourceLimits(
            maxDecodedBytesPerStream: 1_000,
            maxTotalDecodedBytes: 1_000,
            maxTotalDecodeWorkBytes: 1);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        Assert.Equal(600, document.Pages[0].GetContent().Length);
    }

    [Fact]
    public void ExhaustedWorkBudgetDoesNotMisclassifyAnUnfilteredOutputLimit()
    {
        byte[] file = ContentDocument(
            PdfStream.FromData(new byte[600]),
            PdfStream.FromData(new byte[600], compress: false));
        var limits = new PdfResourceLimits(
            maxDecodedBytesPerStream: 1_000,
            maxTotalDecodedBytes: 1_000,
            maxTotalDecodeWorkBytes: 600);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => document.Pages[0].GetContent());

        Assert.Equal(nameof(PdfResourceLimits.MaxTotalDecodedBytes), exception.LimitName);
    }

    [Fact]
    public void RejectsAFilterChainBeforeDecodingItsPrefix()
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Array(new PdfArray(
                PdfValue.Name(PdfNames.Crypt),
                PdfValue.Name(PdfNames.Crypt),
                PdfValue.Name(PdfNames.Crypt))),
        };
        byte[] file = ContentDocument(new PdfStream(dictionary, "q Q"u8.ToArray()));
        var limits = new PdfResourceLimits(maxFilterStages: 2);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => document.Pages[0].GetContent());

        Assert.Equal(nameof(PdfResourceLimits.MaxFilterStages), exception.LimitName);
    }

    [Fact]
    public void CountsFreeAndLiveCrossReferenceRowsBeforeMaterialisingThem()
    {
        var limits = new PdfResourceLimits(maxCrossReferenceEntries: 5);

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => PdfReader.Open(MinimalPdf.OnePage(), Options(limits)));

        Assert.Equal(nameof(PdfResourceLimits.MaxCrossReferenceEntries), exception.LimitName);
        Assert.Equal(6, exception.Observed);
    }

    [Fact]
    public void BoundsTheNumberOfIndexedObjectsIndependentlyOfXrefWork()
    {
        var limits = new PdfResourceLimits(maxObjects: 4);

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => PdfReader.Open(MinimalPdf.OnePage(), Options(limits)));

        Assert.Equal(nameof(PdfResourceLimits.MaxObjects), exception.LimitName);
        Assert.Equal(5, exception.Observed);
    }

    [Fact]
    public void BoundsObjectStreamIndexAllocations()
    {
        byte[] file;
        using (var authored = PdfDocument.Create())
        {
            authored.Pages.Add().SetContent("q Q"u8.ToArray());
            file = authored.ToArray();
        }

        var limits = new PdfResourceLimits(maxStructuralEntries: 1);
        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => PdfDocument.Load(file, Options(limits)));

        Assert.Equal(nameof(PdfResourceLimits.MaxStructuralEntries), exception.LimitName);
    }

    [Fact]
    public void BoundsLazyCosParsingAcrossTheDocument()
    {
        var limits = new PdfResourceLimits(maxParsedValues: 4);

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => PdfReader.Open(MinimalPdf.OnePage(), Options(limits)));

        Assert.Equal(nameof(PdfResourceLimits.MaxParsedValues), exception.LimitName);
    }

    [Fact]
    public void BoundsNestedArraysWithoutTurningOnProtectedSyntaxRules()
    {
        string source = MinimalPdf.Source.Replace(
            "/Type /Catalog /Pages 2 0 R >>",
            "/Type /Catalog /Pages 2 0 R /Deep [[[[0]]]] >>",
            StringComparison.Ordinal);
        var limits = new PdfResourceLimits(maxNestingDepth: 3);

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => PdfReader.Open(MinimalPdf.Build(source), Options(limits)));

        Assert.Equal(nameof(PdfResourceLimits.MaxNestingDepth), exception.LimitName);
    }

    [Fact]
    public void PageContentCapIncludesOnlySeparatorsBetweenStreams()
    {
        byte[] file = ContentDocument(
            PdfStream.FromData("a"u8.ToArray(), compress: false),
            PdfStream.FromData("b"u8.ToArray(), compress: false));
        var limits = new PdfResourceLimits(
            maxDecodedBytesPerStream: 3,
            maxTotalDecodedBytes: 10,
            maxTotalDecodeWorkBytes: 10);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        Assert.Equal("a\nb"u8.ToArray(), document.Pages[0].GetContent());
    }

    [Fact]
    public void PageContentOverTheCombinedCapThrowsInsteadOfReturningAPrefix()
    {
        byte[] file = ContentDocument(
            PdfStream.FromData("a"u8.ToArray(), compress: false),
            PdfStream.FromData("b"u8.ToArray(), compress: false));
        var limits = new PdfResourceLimits(
            maxDecodedBytesPerStream: 2,
            maxTotalDecodedBytes: 10,
            maxTotalDecodeWorkBytes: 10);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));

        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => document.Pages[0].GetContent());

        Assert.Equal(nameof(PdfResourceLimits.MaxDecodedBytesPerStream), exception.LimitName);
        Assert.Equal(3, exception.Observed);
    }

    [Fact]
    public void BoundedReaderKeepsTheCompatibilityDecodersTolerance()
    {
        byte[] payload = "BT ET"u8.ToArray();
        byte[] raw = [(byte)' ', .. FlateFilter.Encode(payload)];
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfNames.FlateDecode),
        };
        byte[] file = ContentDocument(new PdfStream(dictionary, raw));
        using PdfDocument document = PdfDocument.Load(file);

        Assert.Equal(payload, document.Pages[0].GetContent());
    }

    [Fact]
    public void BoundedReaderKeepsMalformedLzwPrefixRecovery()
    {
        // Clear, 'A', an out-of-range code, another code and EOD, packed at nine bits. The public
        // compatibility decoder has always returned the clean prefix before the invalid code.
        byte[] malformed = [0x80, 0x10, 0x65, 0x90, 0x38, 0x08];
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfName.Get("LZWDecode")),
        };
        byte[] file = ContentDocument(new PdfStream(dictionary, malformed));
        using PdfDocument document = PdfDocument.Load(file);

        Assert.Equal("A"u8.ToArray(), document.Pages[0].GetContent());
    }

    [Fact]
    public void BoundedReaderKeepsTolerantPredictorTailRecovery()
    {
        byte[] predicted = [0, 1];
        var parameters = new PdfDictionary
        {
            [PdfNames.Predictor] = PdfValue.Integer(12),
            [PdfNames.Colors] = PdfValue.Integer(1),
            [PdfNames.BitsPerComponent] = PdfValue.Integer(8),
            [PdfNames.Columns] = PdfValue.Integer(2),
        };
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfNames.FlateDecode),
            [PdfNames.DecodeParms] = PdfValue.Dictionary(parameters),
        };
        byte[] file = ContentDocument(new PdfStream(dictionary, FlateFilter.Encode(predicted)));
        using PdfDocument document = PdfDocument.Load(file);

        Assert.Equal(new byte[] { 1, 0 }, document.Pages[0].GetContent());
    }

    [Fact]
    public void ReportsTheProcessWideStreamCeilingByItsOwnName()
    {
        byte[] file = ContentDocument(PdfStream.FromData(new byte[2_048]));
        long saved = PdfFilterLimits.MaxDecodedBytes;
        try
        {
            PdfFilterLimits.MaxDecodedBytes = 1_024;
            var limits = new PdfResourceLimits(
                maxDecodedBytesPerStream: 4_096,
                maxTotalDecodedBytes: 8_192,
                maxTotalDecodeWorkBytes: 8_192);
            using PdfDocument document = PdfDocument.Load(file, Options(limits));

            PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
                () => document.Pages[0].GetContent());

            Assert.Equal(PdfResourceBudget.ProcessWideDecodedBytesLimitName, exception.LimitName);
            Assert.Equal(1_024, exception.Limit);
        }
        finally
        {
            PdfFilterLimits.MaxDecodedBytes = saved;
        }
    }

    [Fact]
    public async Task ConcurrentDecodeOfOneStreamChargesTheResultOnce()
    {
        const int length = 4 * 1024 * 1024;
        byte[] file = ContentDocument(PdfStream.FromData(new byte[length]));
        var limits = new PdfResourceLimits(
            maxDecodedBytesPerStream: length,
            maxTotalDecodedBytes: length,
            maxTotalDecodeWorkBytes: length);
        using PdfDocument document = PdfDocument.Load(file, Options(limits));
        PdfStream stream = document.Pages[0].Dictionary.Get(PdfNames.Contents)
            .AsArray!.Get(0).AsStream!;
        using var start = new ManualResetEventSlim();

        Task<ReadOnlyMemory<byte>>[] decodes = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return stream.Decode();
            }))
            .ToArray();
        start.Set();

        ReadOnlyMemory<byte>[] results = await Task.WhenAll(decodes);

        Assert.All(results, result => Assert.Equal(length, result.Length));
    }

    [Fact]
    public void ParsedValueBudgetIncludesTheStreamWrapper()
    {
        PdfResourceLimitException exception = Assert.Throws<PdfResourceLimitException>(
            () => ParseSingleStream(maxParsedValues: 3));

        Assert.Equal(nameof(PdfResourceLimits.MaxParsedValues), exception.LimitName);
        Assert.Equal(4, exception.Observed);
    }

    private static PdfReadOptions Options(PdfResourceLimits limits) =>
        new() { ResourceLimits = limits };

    private static byte[] ContentDocument(params PdfStream[] streams)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        var contents = new PdfArray { Resolver = document };
        foreach (PdfStream stream in streams)
        {
            PdfObjectId id = document.AddObject(PdfValue.Stream(stream));
            contents.Add(PdfValue.Reference(id));
        }

        page.Dictionary.Set(PdfNames.Contents, PdfValue.Array(contents));
        return document.ToArray(new PdfSaveOptions { UseObjectStreams = false });
    }

    private static PdfStream TwiceAscii85Encoded(ReadOnlySpan<byte> payload)
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Array(new PdfArray(
                PdfValue.Name(PdfNames.ASCII85Decode),
                PdfValue.Name(PdfNames.ASCII85Decode))),
        };
        byte[] encoded = Ascii85Filter.Encode(Ascii85Filter.Encode(payload));
        return new PdfStream(dictionary, encoded);
    }

    private static void ParseSingleStream(int maxParsedValues)
    {
        var limits = new PdfResourceLimits(maxParsedValues: maxParsedValues);
        var reader = new BudgetedPayloadReader(new PdfResourceBudget(limits));
        var parser = new PdfObjectParser(
            "<< /Length 0 >>\nstream\nendstream"u8,
            streams: reader);
        _ = parser.ParseValue();
    }

    private sealed class BudgetedPayloadReader(PdfResourceBudget budget) :
        IPdfStreamPayloadReader,
        IPdfResourceBudgetProvider
    {
        IPdfStreamDecodePolicy? IPdfStreamDecodePolicyProvider.DecodePolicy => null;

        PdfResourceBudget? IPdfResourceBudgetProvider.ResourceBudget => budget;

        ReadOnlyMemory<byte> IPdfStreamPayloadReader.ReadPayload(
            PdfDictionary dictionary,
            int dataStart,
            out int endPosition)
        {
            _ = dictionary;
            endPosition = dataStart;
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    private sealed class ReadSizeTrackingMemoryStream(byte[] data) : MemoryStream(data, writable: false)
    {
        internal int? FirstSynchronousBufferLength { get; private set; }

        internal int? FirstAsynchronousBufferLength { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            FirstSynchronousBufferLength ??= buffer.Length;
            return base.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            FirstAsynchronousBufferLength ??= buffer.Length;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
