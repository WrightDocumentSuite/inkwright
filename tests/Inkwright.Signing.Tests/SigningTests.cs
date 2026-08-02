using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inkwright.Cos;
using Inkwright.Forms;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Signing.Tests;

public class SigningTests
{
    [Fact]
    public void SignsADocumentAndVerifiesIt()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Inkwright Test Signer");
        byte[] unsigned = BuildDocument();

        byte[] signed = Sign(unsigned, new PdfSignatureOptions
        {
            Certificate = certificate,
            Reason = "Approved for release",
            Location = "London",
            FieldName = "Approval",
        });

        IReadOnlyList<PdfSignatureInfo> signatures = PdfSignatureVerifier.Verify(signed);

        PdfSignatureInfo signature = Assert.Single(signatures);
        Assert.Equal("Approval", signature.FieldName);
        Assert.True(signature.IsIntact, signature.Problem);
        Assert.True(signature.CoversWholeDocument);
        Assert.True(signature.IsValid);
        Assert.Equal("Approved for release", signature.Reason);
        Assert.Equal(certificate.Thumbprint, signature.SignerCertificate?.Thumbprint);
    }

    [Fact]
    public void LeavesTheOriginalBytesInPlace()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Inkwright Test Signer");
        byte[] unsigned = BuildDocument();

        byte[] signed = Sign(unsigned, new PdfSignatureOptions { Certificate = certificate });

        Assert.True(signed.Length > unsigned.Length);
        Assert.True(signed.AsSpan(0, unsigned.Length).SequenceEqual(unsigned),
            "Signing must append, never rewrite: anything else breaks signatures that came before.");
    }

    [Fact]
    public void NoticesWhenTheDocumentIsChangedAfterSigning()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Inkwright Test Signer");
        byte[] signed = Sign(BuildDocument(), new PdfSignatureOptions { Certificate = certificate });

        // Flip a byte inside the covered range, well away from the signature itself.
        byte[] tampered = (byte[])signed.Clone();
        int index = tampered.AsSpan().IndexOf("Hello"u8);
        Assert.True(index > 0, "The test document should contain the text being tampered with.");
        tampered[index] = (byte)'J';

        PdfSignatureInfo signature = Assert.Single(PdfSignatureVerifier.Verify(tampered));
        Assert.False(signature.IsIntact);
        Assert.False(signature.IsValid);
    }

    [Fact]
    public void KeepsEarlierSignaturesValidWhenSigningAgain()
    {
        using X509Certificate2 first = CreateCertificate("CN=First Signer");
        using X509Certificate2 second = CreateCertificate("CN=Second Signer");

        byte[] once = Sign(BuildDocument(), new PdfSignatureOptions { Certificate = first, FieldName = "First" });
        byte[] twice = Sign(once, new PdfSignatureOptions { Certificate = second, FieldName = "Second" });

        IReadOnlyList<PdfSignatureInfo> signatures = PdfSignatureVerifier.Verify(twice);

        Assert.Equal(2, signatures.Count);
        Assert.All(signatures, signature => Assert.True(signature.IsIntact, signature.Problem));

        PdfSignatureInfo earlier = signatures.First(s => s.FieldName == "First");
        PdfSignatureInfo later = signatures.First(s => s.FieldName == "Second");

        // The first signature no longer covers the whole file, which is exactly what it should say.
        Assert.False(earlier.CoversWholeDocument);
        Assert.True(later.CoversWholeDocument);
    }

    [Fact]
    public void DrawsAVisibleSignature()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Visible Signer");

        byte[] signed = Sign(BuildDocument(), new PdfSignatureOptions
        {
            Certificate = certificate,
            Appearance = PdfRectangle.FromSize(60, 60, 220, 70),
            Reason = "Checked",
        });

        using PdfDocument document = PdfDocument.Load(signed);

        PdfArray annotations = document.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        PdfDictionary widget = annotations.GetDictionary(annotations.Count - 1)!;
        Assert.Equal("Sig", widget.GetName(PdfName.Get("FT"))!.Value);
        Assert.Equal(220, PdfRectangle.FromArray(widget.GetArray(PdfName.Get("Rect"))).Width, 1);

        // The appearance lives in the annotation, not in the page content, so that is where the
        // signer's name has to show up.
        PdfStream appearance = widget.GetDictionary(PdfName.Get("AP"))!.GetStream(PdfName.Get("N"))!;
        string drawing = System.Text.Encoding.ASCII.GetString(appearance.DecodedSpan());
        Assert.Contains("(Visible Signer) Tj", drawing, StringComparison.Ordinal);
        Assert.Contains("(Checked) Tj", drawing, StringComparison.Ordinal);
    }

    [Fact]
    public void SignsAPasswordProtectedDocument()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Confidential Signer");

        using var source = PdfDocument.Create();
        source.Pages.Add(PageSize.A4).SetContent("BT /F1 12 Tf 72 700 Td (Contract) Tj ET"u8.ToArray());
        byte[] locked = source.ToArray(new PdfSaveOptions
        {
            Encryption = new PdfEncryptionSettings { UserPassword = "open" },
        });

        using PdfDocument document = PdfDocument.Load(locked, new IO.PdfReadOptions { Password = "open" });
        byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions { Certificate = certificate });

        using PdfDocument reopened = PdfDocument.Load(signed, new IO.PdfReadOptions { Password = "open" });
        PdfSignatureInfo signature = Assert.Single(PdfSignatureVerifier.Verify(reopened, signed));

        // The signed blob is exempt from encryption (§7.6.2). Encrypting it along with every other
        // string leaves a signature that will not parse, let alone verify.
        Assert.Null(signature.Problem);
        Assert.True(signature.IsIntact);
        Assert.True(signature.CoversWholeDocument);
        Assert.Equal("CN=Confidential Signer", signature.SignerCertificate!.Subject);
    }

    [Fact]
    public void SignsAnArchivalDocumentWithoutBreakingTheClaim()
    {
        string? fontPath = Fonts.SystemFonts.Find("Arial") ?? Fonts.SystemFonts.Find("DejaVuSans");
        Assert.SkipWhen(fontPath is null, "PDF/A needs an embeddable font and none was found.");

        using X509Certificate2 certificate = CreateCertificate("CN=Archival Signer");
        using var document = PdfDocument.Create();
        Fonts.PdfFont font = document.Fonts.Embed(fontPath!);

        BuildArchivalPage(document, font);

        Layout.PdfAProfile.Apply(document, Layout.PdfAConformance.A2B, new Layout.PdfAOptions { Language = "en" });
        byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions { Certificate = certificate });

        using PdfDocument reopened = PdfDocument.Load(signed);

        // Annex B wants the digest to cover everything but the signature itself, and the rest of
        // the file to stay exactly as archival as it was before signing.
        Assert.Empty(Layout.PdfAProfile.Validate(reopened, Layout.PdfAConformance.A2B));
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(reopened, signed)).IsIntact);
    }

    private static void BuildArchivalPage(PdfDocument document, Fonts.PdfFont font)
    {
        Report.Create()
            .Page(page =>
            {
                page.DefaultTextStyle(TextStyle.Default.Font(font));
                page.Content().Text("Signed and archived.");
            })
            .Render(document);
    }

    [Fact]
    public void SignsThroughAnExternalSigner()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Remote Signer");
        var remote = new RemoteSigner(certificate);

        byte[] signed = Sign(BuildDocument(), new PdfSignatureOptions
        {
            ExternalSigner = remote,
            FieldName = "Remote",
        });

        Assert.True(remote.WasCalled, "The external signer should have produced the signature.");

        PdfSignatureInfo signature = Assert.Single(PdfSignatureVerifier.Verify(signed));
        Assert.True(signature.IsIntact, signature.Problem);
        Assert.Equal(certificate.Thumbprint, signature.SignerCertificate?.Thumbprint);
    }

    [Fact]
    public void TimestampsASignatureProducedByAnExternalSigner()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Remote Timestamped Signer");
        using var authority = new TestTimestampAuthority();
        var remote = new RemoteSigner(certificate);

        byte[] signed = Sign(BuildDocument(), new PdfSignatureOptions
        {
            ExternalSigner = remote,
            Level = PdfSignatureLevel.Timestamped,
            TimestampClient = authority,
            FieldName = "RemoteTimestamped",
        });

        Assert.True(remote.WasCalled, "The external signer should have produced the signature.");
        Assert.Equal(1, authority.Requests);

        PdfSignatureInfo signature = Assert.Single(PdfSignatureVerifier.Verify(signed));
        Assert.True(signature.IsIntact, signature.Problem);
        Assert.True(signature.TimestampIntact, "The RFC 3161 token must bind to the remote signature value.");
        Assert.NotNull(signature.TimestampTime);
        Assert.Equal(certificate.Thumbprint, signature.SignerCertificate?.Thumbprint);
    }

    [Fact]
    public void SetsTheSignatureFlagsAViewerLooksFor()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Inkwright Test Signer");
        byte[] signed = Sign(BuildDocument(), new PdfSignatureOptions { Certificate = certificate });

        using PdfDocument document = PdfDocument.Load(signed);
        PdfForm form = PdfForm.Open(document)!;

        Assert.Equal(3, form.Root.GetInteger(PdfName.Get("SigFlags")));

        PdfFormField field = Assert.Single(form.Fields, f => f.Type == PdfFieldType.Signature);
        PdfDictionary signature = field.Inherited(PdfName.Get("V")).AsDictionary!;
        Assert.Equal("ETSI.CAdES.detached", signature.GetName(PdfName.Get("SubFilter"))!.Value);
        Assert.Equal(4, signature.GetArray(PdfName.Get("ByteRange"))!.Count);
    }

    [Fact]
    public void FillsAnExistingEmptySignatureFieldWithoutDuplicatingIt()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Existing Field Signer");
        using var source = PdfDocument.Create();
        PdfPage page = source.Pages.Add(PageSize.A4);
        PdfForm form = PdfForm.OpenOrCreate(source);
        PdfRectangle bounds = new(72, 620, 272, 680);
        PdfDictionary widget = form.CreateWidget(page, bounds, "Approval", "Sig");
        form.Register(widget, "Approval");
        byte[] unsigned = source.ToArray(new PdfSaveOptions { UseObjectStreams = false });

        byte[] signed = Sign(unsigned, new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Approval",
        });

        using PdfDocument document = PdfDocument.Load(signed);
        PdfFormField field = Assert.Single(PdfForm.Open(document)!.Fields);
        Assert.Equal("Approval", field.FullName);
        Assert.Equal(PdfFieldType.Signature, field.Type);
        Assert.Equal(bounds, field.Bounds);
        Assert.NotNull(field.Widgets[0].GetDictionary(PdfName.Get("AP")));
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(document, signed)).IsIntact);
    }

    [Fact]
    public void RefusesToFillASignatureFieldTwice()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Existing Field Signer");
        byte[] once = Sign(BuildDocument(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Approval",
        });

        using PdfDocument document = PdfDocument.Load(once);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfSigner.Sign(document, new PdfSignatureOptions
            {
                Certificate = certificate,
                FieldName = "Approval",
            }));

        Assert.Contains("already filled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsThePadesSigningTimeOnlyInCms()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Timed Signer");
        var expected = new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.Zero);
        byte[] signed = Sign(BuildDocument(), new PdfSignatureOptions
        {
            Certificate = certificate,
            SigningTime = expected,
        });

        using PdfDocument document = PdfDocument.Load(signed);
        PdfFormField field = Assert.Single(PdfForm.Open(document)!.Fields, f => f.Type == PdfFieldType.Signature);
        PdfDictionary dictionary = field.Inherited(PdfName.Get("V")).AsDictionary!;

        // ISO 32000-2 §12.8.3.4.2 permits /M or the CMS signing-time attribute, never both.
        Assert.False(dictionary.ContainsKey(PdfName.Get("M")));
        PdfSignatureInfo verified = Assert.Single(PdfSignatureVerifier.Verify(document, signed));
        Assert.Equal(expected, verified.SigningTime);
    }

    [Fact]
    public void RejectsOverlappingSignatureByteRangesBeforeCryptographicVerification()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Range Signer");
        byte[] signed = Sign(BuildDocument(), new PdfSignatureOptions { Certificate = certificate });
        using PdfDocument document = PdfDocument.Load(signed);
        PdfFormField field = Assert.Single(PdfForm.Open(document)!.Fields, f => f.Type == PdfFieldType.Signature);
        PdfDictionary dictionary = field.Inherited(PdfName.Get("V")).AsDictionary!;
        dictionary.Set(PdfName.Get("ByteRange"), PdfValue.Array(PdfArray.OfNumbers(0, 100, 50, 100)));

        PdfSignatureInfo verified = Assert.Single(PdfSignatureVerifier.Verify(document, signed));
        Assert.False(verified.IsIntact);
        Assert.False(verified.CoversWholeDocument);
        Assert.False(verified.IsValid);
        Assert.Contains("overlap", verified.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoesNotIgnoreTrailingBytesOutsideTheSignatureRange()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Range Signer");
        byte[] signed = Sign(BuildDocument(), new PdfSignatureOptions { Certificate = certificate });
        using PdfDocument document = PdfDocument.Load(signed);
        PdfFormField field = Assert.Single(PdfForm.Open(document)!.Fields, f => f.Type == PdfFieldType.Signature);
        PdfDictionary dictionary = field.Inherited(PdfName.Get("V")).AsDictionary!;
        PdfArray original = dictionary.GetArray(PdfName.Get("ByteRange"))!;
        dictionary.Set(PdfName.Get("ByteRange"), PdfValue.Array(PdfArray.OfNumbers(
            original.GetInteger(0),
            original.GetInteger(1),
            original.GetInteger(2),
            original.GetInteger(3) - 4)));

        PdfSignatureInfo verified = Assert.Single(PdfSignatureVerifier.Verify(document, signed));
        Assert.False(verified.CoversWholeDocument);
        Assert.False(verified.IsValid);
    }

    [Fact]
    public void ReportsWhenTheReservedSpaceIsTooSmall()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=Inkwright Test Signer");

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            Sign(BuildDocument(), new PdfSignatureOptions { Certificate = certificate, SignatureSize = 32 }));

        Assert.Contains("SignatureSize", failure.Message, StringComparison.Ordinal);
    }

    private static byte[] Sign(byte[] file, PdfSignatureOptions options)
    {
        using PdfDocument document = PdfDocument.Load(file);
        return PdfSigner.Sign(document, options);
    }

    /// <summary>
    /// The content is left uncompressed so that a test can change a byte of visible text and see
    /// the signature notice.
    /// </summary>
    private static byte[] BuildDocument()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        page.SetContent(
            "BT /F1 14 Tf 60 700 Td (Hello, this document is about to be signed.) Tj ET"u8.ToArray(),
            compress: false);

        var font = new PdfDictionary(4);
        font.Set(PdfName.Get("Type"), PdfValue.Name("Font"));
        font.Set(PdfName.Get("Subtype"), PdfValue.Name("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));

        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(font))));
        page.Resources.Set(PdfName.Get("Font"), PdfValue.Dictionary(fonts));

        return document.ToArray(new PdfSaveOptions { UseObjectStreams = false, Compress = false });
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }

    /// <summary>Stands in for a hardware module: it holds the key and only ever sees a digest.</summary>
    private sealed class RemoteSigner : IExternalSigner
    {
        private readonly RSA _key;

        internal RemoteSigner(X509Certificate2 certificate)
        {
            _key = certificate.GetRSAPrivateKey()!;
            Certificate = X509CertificateLoader.LoadCertificate(certificate.RawData);
        }

        public X509Certificate2 Certificate { get; }

        public bool WasCalled { get; private set; }

        public byte[] SignHash(ReadOnlyMemory<byte> digest, HashAlgorithmName hashAlgorithm)
        {
            WasCalled = true;
            return _key.SignHash(digest.Span, hashAlgorithm, RSASignaturePadding.Pkcs1);
        }
    }
}
