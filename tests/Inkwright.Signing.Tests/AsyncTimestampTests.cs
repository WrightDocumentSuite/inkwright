using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Filters;
using Inkwright.IO;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Signing.Tests;

public class AsyncTimestampTests
{
    [Fact]
    public async Task DocumentTimestampUsesAsyncAuthority()
    {
        using var inner = new TestTimestampAuthority();
        var authority = new AsyncOnlyAuthority(inner);
        using PdfDocument document = PdfDocument.Load(TestDocuments.Build());

        byte[] stamped = await PdfTimestamper.TimestampAsync(
            document,
            new PdfTimestampOptions { Client = authority },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, authority.AsyncRequests);
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(stamped)).TimestampIntact);
        Assert.Null(Forms.PdfForm.Open(document));
    }

    [Fact]
    public async Task SignatureTimestampUsesAsyncAuthority()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Async Timestamped Signer");
        using var inner = new TestTimestampAuthority();
        var authority = new AsyncOnlyAuthority(inner);
        using PdfDocument document = PdfDocument.Load(TestDocuments.Build());

        byte[] signed = await PdfSigner.SignAsync(document, new PdfSignatureOptions
        {
            Certificate = certificate,
            Level = PdfSignatureLevel.Timestamped,
            TimestampClient = authority,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, authority.AsyncRequests);
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(signed)).TimestampIntact);
        Assert.Null(Forms.PdfForm.Open(document));
    }

    [Fact]
    public async Task AsyncTimestampSupportsLegacySynchronousAuthority()
    {
        using var authority = new TestTimestampAuthority();

        byte[] stamped = await PdfTimestamper.TimestampAsync(
            TestDocuments.Build(),
            new PdfTimestampOptions { Client = authority },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, authority.Requests);
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(stamped)).TimestampIntact);
    }

    [Fact]
    public async Task AsyncSignaturePreservesEncryptedSnapshot()
    {
        using var source = PdfDocument.Create();
        source.Pages.Add(PageSize.A4).SetContent("BT ET"u8.ToArray());
        byte[] encrypted = source.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            Encryption = new PdfEncryptionSettings { UserPassword = "open" },
        });

        using PdfDocument document = PdfDocument.Load(
            encrypted,
            new IO.PdfReadOptions { Password = "open" });
        using X509Certificate2 certificate =
            TestDocuments.CreateCertificate("CN=Encrypted Async Signer");
        using var inner = new TestTimestampAuthority();

        byte[] signed = await PdfSigner.SignAsync(document, new PdfSignatureOptions
        {
            Certificate = certificate,
            Level = PdfSignatureLevel.Timestamped,
            TimestampClient = new AsyncOnlyAuthority(inner),
        }, TestContext.Current.CancellationToken);

        using PdfDocument reopened = PdfDocument.Load(
            signed,
            new IO.PdfReadOptions { Password = "open" });
        PdfSignatureInfo signature = Assert.Single(PdfSignatureVerifier.Verify(reopened, signed));
        Assert.Null(signature.Problem);
        Assert.True(signature.IsIntact);
        Assert.True(signature.TimestampIntact);
    }

    [Fact]
    public async Task SetObjectPreservesNonZeroGenerationAcrossIncrementalSaveAndAsyncSnapshot()
    {
        byte[] original = BuildGenerationFixture();
        var probeId = new PdfObjectId(4, 5);

        byte[] incrementallyUpdated;
        using (PdfDocument document = PdfDocument.Load(original, new PdfReadOptions { Recover = false }))
        {
            ReplaceProbe(document, probeId, "sync");
            incrementallyUpdated = document.ToIncrementalArray(new PdfSaveOptions { UseObjectStreams = false });
        }

        AssertGenerationAndProbe(original, incrementallyUpdated, probeId, "sync");

        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Generation Snapshot Signer");
        byte[] signed;
        using (PdfDocument document = PdfDocument.Load(original, new PdfReadOptions { Recover = false }))
        {
            ReplaceProbe(document, probeId, "async");
            signed = await PdfSigner.SignAsync(document, new PdfSignatureOptions
            {
                Certificate = certificate,
            }, TestContext.Current.CancellationToken);
        }

        AssertGenerationAndProbe(original, signed, probeId, "async");
    }

    [Fact]
    public async Task AsyncOperationsRejectARebuiltCrossReferenceWithoutMutatingTheCaller()
    {
        byte[] broken = BreakStartXref(TestDocuments.Build());
        using PdfDocument document = PdfDocument.Load(broken);
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Rebuilt Snapshot Signer");
        using var authority = new TestTimestampAuthority();

        InvalidOperationException signing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PdfSigner.SignAsync(
                document,
                new PdfSignatureOptions { Certificate = certificate },
                TestContext.Current.CancellationToken));
        InvalidOperationException timestamping = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PdfTimestamper.TimestampAsync(
                document,
                new PdfTimestampOptions { Client = authority },
                TestContext.Current.CancellationToken));

        Assert.Contains("cross-reference", signing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cross-reference", timestamping.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Forms.PdfForm.Open(document));
    }

    [Fact]
    public async Task AsyncSignatureKeepsNewPdfA4InformationDictionarySuppressed()
    {
        using PdfDocument document = BuildPdfA4();
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=PDF-A4 Async Signer");

        byte[] signed = await PdfSigner.SignAsync(document, new PdfSignatureOptions
        {
            Certificate = certificate,
        }, TestContext.Current.CancellationToken);

        AssertPdfA4HasNoInformationDictionary(signed);
    }

    [Fact]
    public async Task AsyncTimestampKeepsNewPdfA4InformationDictionarySuppressed()
    {
        using PdfDocument document = BuildPdfA4();
        using var authority = new TestTimestampAuthority();

        byte[] stamped = await PdfTimestamper.TimestampAsync(
            document,
            new PdfTimestampOptions { Client = authority },
            TestContext.Current.CancellationToken);

        AssertPdfA4HasNoInformationDictionary(stamped);
    }

    [Fact]
    public async Task AsyncSignaturePreservesAnExistingSignatureAndDirtyState()
    {
        using X509Certificate2 firstCertificate =
            TestDocuments.CreateCertificate("CN=First Async Snapshot Signer");
        using X509Certificate2 secondCertificate =
            TestDocuments.CreateCertificate("CN=Second Async Snapshot Signer");
        using var initial = PdfDocument.Load(TestDocuments.Build());
        byte[] firstRevision = PdfSigner.Sign(initial, new PdfSignatureOptions
        {
            Certificate = firstCertificate,
            FieldName = "First",
        });

        using PdfDocument document = PdfDocument.Load(firstRevision);
        document.Info.Title = "State copied into the async snapshot";

        byte[] secondRevision = await PdfSigner.SignAsync(document, new PdfSignatureOptions
        {
            Certificate = secondCertificate,
            FieldName = "Second",
        }, TestContext.Current.CancellationToken);

        using PdfDocument reopened = PdfDocument.Load(secondRevision);
        PdfSignatureInfo[] signatures = [.. PdfSignatureVerifier.Verify(reopened, secondRevision)];
        Assert.Equal(2, signatures.Length);
        Assert.All(signatures, signature => Assert.True(signature.IsIntact, signature.Problem));
        Assert.Equal("State copied into the async snapshot", reopened.Info.Title);
        Assert.Single(PdfSignatureVerifier.Verify(document, firstRevision));
    }

    [Fact]
    public async Task CancellationReachesAuthority()
    {
        var authority = new WaitingAuthority();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));

        Task<byte[]> operation = PdfTimestamper.TimestampAsync(
            TestDocuments.Build(),
            new PdfTimestampOptions { Client = authority },
            cancellation.Token);

        await authority.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task CanceledDocumentTimestampLeavesCallerDocumentReusable()
    {
        using PdfDocument document = PdfDocument.Load(TestDocuments.Build());
        document.Catalog.Remove(Cos.PdfNames.Version);
        document.MarkChanged(document.CatalogId);
        Assert.False(document.Catalog.ContainsKey(Cos.PdfNames.Version));
        var waiting = new WaitingAuthority();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        Task<byte[]> operation = PdfTimestamper.TimestampAsync(
            document,
            new PdfTimestampOptions { Client = waiting },
            cancellation.Token);

        await waiting.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Null(Forms.PdfForm.Open(document));
        Assert.False(document.Catalog.ContainsKey(Cos.PdfNames.Version));

        using var inner = new TestTimestampAuthority();
        byte[] retry = await PdfTimestamper.TimestampAsync(
            document,
            new PdfTimestampOptions { Client = new AsyncOnlyAuthority(inner) },
            TestContext.Current.CancellationToken);

        Assert.Equal("Timestamp", Assert.Single(PdfSignatureVerifier.Verify(retry)).FieldName);
    }

    [Fact]
    public async Task CanceledSignatureTimestampLeavesCallerDocumentReusable()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Transactional Async Signer");
        using PdfDocument document = PdfDocument.Load(TestDocuments.Build());
        document.Catalog.Remove(Cos.PdfNames.Version);
        document.MarkChanged(document.CatalogId);
        Assert.False(document.Catalog.ContainsKey(Cos.PdfNames.Version));
        var waiting = new WaitingAuthority();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var options = new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Approval",
            Level = PdfSignatureLevel.Timestamped,
            TimestampClient = waiting,
        };

        Task<byte[]> operation = PdfSigner.SignAsync(document, options, cancellation.Token);
        await waiting.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Null(Forms.PdfForm.Open(document));
        Assert.False(document.Catalog.ContainsKey(Cos.PdfNames.Version));

        using var inner = new TestTimestampAuthority();
        byte[] retry = await PdfSigner.SignAsync(document, new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Approval",
            Level = PdfSignatureLevel.Timestamped,
            TimestampClient = new AsyncOnlyAuthority(inner),
        }, TestContext.Current.CancellationToken);

        Assert.Equal("Approval", Assert.Single(PdfSignatureVerifier.Verify(retry)).FieldName);
    }

    [Fact]
    public async Task HttpAuthorityUsesInjectedClientAndRfc3161MediaTypes()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var authority = new HttpTimestampAuthority(new Uri("https://tsa.example.test/rfc3161"), client);

        byte[] response = await authority.SendAsync(
            new byte[] { 1, 2, 3 },
            TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 4, 5, 6 }, response);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri("https://tsa.example.test/rfc3161"), handler.Uri);
        Assert.Equal("application/timestamp-query", handler.ContentType?.MediaType);
        Assert.Equal("application/timestamp-reply", handler.Accept?.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, handler.Body);
    }

    [Fact]
    public async Task HttpAuthorityBoundsTheResponse()
    {
        using var client = new HttpClient(new RecordingHandler());
        var authority = new HttpTimestampAuthority(new Uri("https://tsa.example.test"), client)
        {
            MaxResponseBytes = 2,
        };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            authority.SendAsync(new byte[] { 1 }, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void SynchronousTimestampRejectsTrailingResponseData()
    {
        using var inner = new TestTimestampAuthority();

        Assert.Throws<CryptographicException>(() => PdfTimestamper.Timestamp(
            TestDocuments.Build(),
            new PdfTimestampOptions { Client = new TrailingResponseAuthority(inner) }));
    }

    [Fact]
    public async Task AsyncTimestampRejectsAnInvalidTokenSignature()
    {
        using var inner = new TestTimestampAuthority(corruptSignature: true);

        await Assert.ThrowsAsync<CryptographicException>(() => PdfTimestamper.TimestampAsync(
            TestDocuments.Build(),
            new PdfTimestampOptions { Client = new AsyncOnlyAuthority(inner) },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanceledSignFileLeavesExistingDestinationUntouched()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("inkwright-sign-cancel-");
        string sourcePath = Path.Combine(directory.FullName, "source.pdf");
        string destinationPath = Path.Combine(directory.FullName, "signed.pdf");
        byte[] originalDestination = "existing destination"u8.ToArray();

        try
        {
            await File.WriteAllBytesAsync(
                sourcePath,
                TestDocuments.Build(),
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                destinationPath,
                originalDestination,
                TestContext.Current.CancellationToken);

            using X509Certificate2 certificate =
                TestDocuments.CreateCertificate("CN=Canceled File Signer");
            var authority = new WaitingAuthority();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(5));

            Task operation = PdfSigner.SignFileAsync(
                sourcePath,
                destinationPath,
                new PdfSignatureOptions
                {
                    Certificate = certificate,
                    Level = PdfSignatureLevel.Timestamped,
                    TimestampClient = authority,
                },
                cancellation.Token);

            await authority.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(originalDestination, await File.ReadAllBytesAsync(
                destinationPath,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void HttpAuthorityRejectsInvalidEndpointAndTimeout()
    {
        Assert.Throws<ArgumentException>(() => new HttpTimestampAuthority(new Uri("file:///tmp/tsa")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HttpTimestampAuthority(new Uri("https://tsa.example.test")) { Timeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HttpTimestampAuthority(new Uri("https://tsa.example.test")) { Timeout = TimeSpan.FromDays(100) });
    }

    private sealed class AsyncOnlyAuthority(TestTimestampAuthority inner) : ITimestampAuthority
    {
        internal int AsyncRequests { get; private set; }

        public byte[] Send(ReadOnlyMemory<byte> request) =>
            throw new InvalidOperationException("The asynchronous API must not call the synchronous transport.");

        public ValueTask<byte[]> SendAsync(
            ReadOnlyMemory<byte> request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncRequests++;
            return ValueTask.FromResult(inner.Send(request));
        }
    }

    private sealed class WaitingAuthority : ITimestampAuthority
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] Send(ReadOnlyMemory<byte> request) =>
            throw new InvalidOperationException("The asynchronous API must not call the synchronous transport.");

        public async ValueTask<byte[]> SendAsync(
            ReadOnlyMemory<byte> request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    private sealed class TrailingResponseAuthority(TestTimestampAuthority inner) : ITimestampAuthority
    {
        public byte[] Send(ReadOnlyMemory<byte> request) => [.. inner.Send(request), 0];
    }

    private static void ReplaceProbe(PdfDocument document, PdfObjectId id, string value)
    {
        var replacement = new PdfDictionary(1);
        replacement.Set(PdfName.Get("Value"), PdfValue.TextString(value));
        document.SetObject(id, PdfValue.Dictionary(replacement));
    }

    private static void AssertGenerationAndProbe(
        byte[] original,
        byte[] updated,
        PdfObjectId id,
        string expected)
    {
        string appended = Encoding.ASCII.GetString(updated.AsSpan(original.Length));
        Assert.Contains($"{id.Number} {id.Generation} obj", appended, StringComparison.Ordinal);
        Assert.DoesNotContain($"{id.Number} 0 obj", appended, StringComparison.Ordinal);

        using PdfDocument reopened = PdfDocument.Load(updated, new PdfReadOptions
        {
            Recover = false,
            StreamDecodePolicy = new StrictDecodePolicy(),
        });
        Assert.Equal(id.Generation, reopened.Reader!.GetGeneration(id.Number));
        Assert.Equal(
            expected,
            reopened.Catalog.GetDictionary(PdfName.Get("Probe"))?.GetTextString(PdfName.Get("Value")));
    }

    private static byte[] BuildGenerationFixture()
    {
        var body = new StringBuilder("%PDF-1.7\n");
        int[] offsets = new int[5];

        AddObject(1, 0, "<< /Type /Catalog /Pages 2 0 R /Probe 4 5 R >>");
        AddObject(2, 0, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        AddObject(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>");
        AddObject(4, 5, "<< /Value (before) >>");

        int xref = Encoding.ASCII.GetByteCount(body.ToString());
        body.Append("xref\n0 5\n0000000000 65535 f \n");
        for (int number = 1; number < offsets.Length; number++)
        {
            int generation = number == 4 ? 5 : 0;
            body.Append(offsets[number].ToString("D10", CultureInfo.InvariantCulture));
            body.Append(' ');
            body.Append(generation.ToString("D5", CultureInfo.InvariantCulture));
            body.Append(" n \n");
        }

        body.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n");
        body.Append(xref.ToString(CultureInfo.InvariantCulture));
        body.Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());

        void AddObject(int number, int generation, string value)
        {
            offsets[number] = Encoding.ASCII.GetByteCount(body.ToString());
            body.Append(number.ToString(CultureInfo.InvariantCulture));
            body.Append(' ');
            body.Append(generation.ToString(CultureInfo.InvariantCulture));
            body.Append(" obj\n");
            body.Append(value);
            body.Append("\nendobj\n");
        }
    }

    private static byte[] BreakStartXref(byte[] source)
    {
        byte[] broken = (byte[])source.Clone();
        int keyword = broken.AsSpan().LastIndexOf("startxref"u8);
        Assert.True(keyword >= 0);

        int position = keyword + "startxref".Length;
        while (position < broken.Length && char.IsWhiteSpace((char)broken[position]))
            position++;

        while (position < broken.Length && char.IsAsciiDigit((char)broken[position]))
            broken[position++] = (byte)'9';

        return broken;
    }

    private static PdfDocument BuildPdfA4()
    {
        PdfDocument document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);
        PdfAProfile.Apply(document, PdfAConformance.A4);
        return document;
    }

    private static void AssertPdfA4HasNoInformationDictionary(byte[] file)
    {
        PdfReader reader = PdfReader.Open(file, new PdfReadOptions { Recover = false });
        Assert.False(reader.Trailer.ContainsKey(PdfNames.Info));

        using PdfDocument reopened = PdfDocument.Load(file, new PdfReadOptions { Recover = false });
        Assert.False(reopened.HadInformationDictionary);
        Assert.Empty(PdfAProfile.Validate(file, PdfAConformance.A4));
    }

    private sealed class StrictDecodePolicy : IPdfStreamDecodePolicy
    {
        private const int StructuralLimit = 1_048_576;
        private int _structuralEntries;

        public int MaxStructuralEntries => StructuralLimit;

        public void ConsumeStructuralEntries(int count)
        {
            if (count <= 0 || count > StructuralLimit - _structuralEntries)
                throw new PdfFormatException("The strict test reader exhausted its structural-entry budget.");

            _structuralEntries += count;
        }

        public void ConsumeParsedValues(int count)
        {
            if (count <= 0)
                throw new PdfFormatException("The strict test reader received an invalid parsed-value charge.");
        }

        public ReadOnlyMemory<byte> Decode(PdfStream stream, out PdfName? undecodedFilter) =>
            PdfFilterPipeline.Decode(stream.RawData, stream.Dictionary, out undecodedFilter);

        public PdfBoundedDecodeResult DecodeBounded(PdfStream stream, int maxDecodedBytes) =>
            PdfFilterPipeline.DecodeBounded(stream.RawData, stream.Dictionary, maxDecodedBytes);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal HttpMethod? Method { get; private set; }
        internal Uri? Uri { get; private set; }
        internal MediaTypeHeaderValue? ContentType { get; private set; }
        internal MediaTypeWithQualityHeaderValue? Accept { get; private set; }
        internal byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType;
            Accept = request.Headers.Accept.SingleOrDefault();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([4, 5, 6]),
            };
        }
    }
}
