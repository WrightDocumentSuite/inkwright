using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Inkwright.Security;
using Inkwright.Signing;
using Xunit;

namespace Inkwright.Signing.Tests;

/// <summary>
/// The integrity code of ISO/TS 32004 as it applies to a signed revision. Writing the code into the
/// trailer would disturb the bytes the signature covers, so it hides among the signature's unsigned
/// attributes instead.
/// </summary>
public class MacSignatureTests
{
    [Fact]
    public void AttachesACodeToASignatureAndVerifiesItBack()
    {
        byte[] signed = SignSealed();

        using PdfDocument document = PdfDocument.Load(signed, new IO.PdfReadOptions { Password = "open-me" });
        PdfMacIntegrity integrity = PdfMacIntegrity.Read(document);

        Assert.True(integrity.IsPresent);
        Assert.Equal(PdfMacLocation.AttachedToSignature, integrity.Location);
        Assert.True(integrity.IsAuthentic, integrity.VerificationNote);
        Assert.True(integrity.MatchesContent, integrity.VerificationNote);
        Assert.True(integrity.CoversWholeFile, integrity.CoverageNote);
        Assert.True(integrity.IsVerified, integrity.VerificationNote);
    }

    [Fact]
    public void LeavesTheSignatureItselfIntact()
    {
        byte[] signed = SignSealed();

        // The code is an unsigned attribute, which by definition sits outside what the signature
        // covers, so folding it in must not disturb the signature.
        using PdfDocument document = PdfDocument.Load(signed, new IO.PdfReadOptions { Password = "open-me" });
        IReadOnlyList<PdfSignatureInfo> signatures = PdfSignatureVerifier.Verify(document, signed);

        PdfSignatureInfo signature = Assert.Single(signatures);
        Assert.True(signature.IsIntact, "The signature stopped verifying once a code was attached.");
    }

    [Fact]
    public void DetectsAChangedRawSignatureValueIndependentlyOfSignatureValidation()
    {
        byte[] signed = SignSealed();
        byte[] signatureValue;

        using (PdfDocument original = PdfDocument.Load(
                   signed, new IO.PdfReadOptions { Password = "open-me" }))
        {
            Cos.PdfDictionary authentication = original.Resolve(
                original.Trailer.Get(Cos.PdfName.Get("AuthCode"))).AsDictionary!;
            Cos.PdfDictionary signature = original.Resolve(
                authentication.Get(Cos.PdfName.Get("SigObjRef"))).AsDictionary!;
            signatureValue = PdfMacSignatureBuilder.ReadSignatureValue(
                signature.Get(Cos.PdfNames.Contents).AsStringBytes.ToArray());
        }

        Assert.NotEmpty(signatureValue);
        byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToHexString(signatureValue));
        int at = signed.AsSpan().IndexOf(encoded);
        Assert.True(at >= 0);
        signed[at + encoded.Length - 1] = signed[at + encoded.Length - 1] == (byte)'0' ? (byte)'1' : (byte)'0';

        using PdfDocument changed = PdfDocument.Load(
            signed, new IO.PdfReadOptions { Password = "open-me" });
        PdfMacIntegrity integrity = PdfMacIntegrity.Read(changed);

        Assert.True(integrity.IsAuthentic, integrity.VerificationNote);
        Assert.False(integrity.MatchesContent);
        Assert.False(integrity.IsVerified);
        Assert.Contains("signature", integrity.VerificationNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DistinguishesCmsBytesEndingInZeroFromPdfContentsPadding()
    {
        using X509Certificate2 certificate = CreateCertificate();
        var options = new PdfSignatureOptions { Certificate = certificate };
        byte[] cms = CmsBuilder.Build(
            "covered"u8.ToArray(), options, new CertificateSigner(certificate, []));
        byte[] signatureValue = PdfMacSignatureBuilder.ReadSignatureValue(cms);

        Assert.NotEmpty(signatureValue);
        int signatureAt = cms.AsSpan().IndexOf(signatureValue);
        Assert.True(signatureAt >= 0);

        // A raw RSA signature is the final value in this CMS. Ending it in zero keeps the ASN.1
        // structure valid and models the random one-in-256 case that used to be mistaken for PDF
        // /Contents padding.
        cms[signatureAt + signatureValue.Length - 1] = 0;
        signatureValue[^1] = 0;
        Assert.Equal(0, cms[^1]);

        byte[] paddedCms = [.. cms, .. new byte[32]];

        var tokenWriter = new AsnWriter(AsnEncodingRules.DER);
        tokenWriter.WriteOctetString([0]);
        byte[] token = tokenWriter.Encode();
        byte[] attached = PdfMacSignatureBuilder.Attach(paddedCms, token);
        byte[] paddedAttached = [.. attached, .. new byte[32]];

        Assert.Equal(0, attached[^1]);
        Assert.Equal(signatureValue, PdfMacSignatureBuilder.ReadSignatureValue(paddedAttached));
        Assert.Equal(token, PdfMacSignatureAttribute.Extract(paddedAttached));
    }

    [Fact]
    public void PointsAtTheSignatureItHangsFrom()
    {
        byte[] signed = SignSealed();

        using PdfDocument document = PdfDocument.Load(signed, new IO.PdfReadOptions { Password = "open-me" });

        Cos.PdfDictionary authentication = document.Resolve(
            document.Trailer.Get(Cos.PdfName.Get("AuthCode"))).AsDictionary!;

        Assert.Equal("AttachedToSig", authentication.GetName(Cos.PdfName.Get("MACLocation"))?.Value);

        // The reference has to resolve to the signature dictionary, because that is where the
        // byte range the code covers actually lives.
        Cos.PdfDictionary signature = document.Resolve(
            authentication.Get(Cos.PdfName.Get("SigObjRef"))).AsDictionary!;

        Assert.NotNull(signature.GetArray(Cos.PdfName.Get("ByteRange")));

        // A signature-attached code states no byte range of its own.
        Assert.True(authentication.Get(Cos.PdfName.Get("ByteRange")).IsNull);
    }

    [Fact]
    public void RefusesToCodeAnUnencryptedDocument()
    {
        using X509Certificate2 certificate = CreateCertificate();
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4).SetContent("BT ET"u8.ToArray());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => PdfSigner.Sign(document, new PdfSignatureOptions
            {
                Certificate = certificate,
                AddIntegrityCode = true,
            }));

        Assert.Contains("encrypted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenTheDocumentWasEncryptedWithoutASalt()
    {
        using var source = PdfDocument.Create();
        source.Pages.Add(PageSize.A4).SetContent("BT ET"u8.ToArray());

        // Encrypted, but without integrity protection, so there is no salt to key a code to.
        byte[] locked = source.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            Encryption = new PdfEncryptionSettings { UserPassword = "open-me" },
        });

        using X509Certificate2 certificate = CreateCertificate();
        using PdfDocument document = PdfDocument.Load(locked, new IO.PdfReadOptions { Password = "open-me" });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => PdfSigner.Sign(document, new PdfSignatureOptions
            {
                Certificate = certificate,
                AddIntegrityCode = true,
            }));

        Assert.Contains("salt", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PdfSaveOptions.AddIntegrityCode), error.Message, StringComparison.Ordinal);
    }

    /// <summary>Signs an encrypted document, asking for a code as well.</summary>
    private static byte[] SignSealed()
    {
        using var source = PdfDocument.Create();
        source.Pages.Add(PageSize.A4).SetContent("BT /F1 12 Tf 72 700 Td (Contract) Tj ET"u8.ToArray());

        // The salt has to be in the document from the start, because it is meant to stay the same
        // through every revision and the encryption dictionary cannot be rewritten afterwards.
        byte[] locked = source.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            AddIntegrityCode = true,
            Encryption = new PdfEncryptionSettings { UserPassword = "open-me", OwnerPassword = "owner" },
        });

        using X509Certificate2 certificate = CreateCertificate();
        using PdfDocument document = PdfDocument.Load(locked, new IO.PdfReadOptions { Password = "open-me" });

        return PdfSigner.Sign(document, new PdfSignatureOptions
        {
            Certificate = certificate,
            AddIntegrityCode = true,
        });
    }

    private static X509Certificate2 CreateCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Inkwright Integrity Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }
}
