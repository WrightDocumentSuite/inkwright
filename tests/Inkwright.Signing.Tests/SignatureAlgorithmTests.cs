using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inkwright.Signing;
using Xunit;

namespace Inkwright.Signing.Tests;

/// <summary>
/// The digest and signature algorithms the technical specifications add to PDF 2.0: the SHA-3
/// family of ISO/TS 32001 and the Edwards curves of ISO/TS 32002.
/// </summary>
public class SignatureAlgorithmTests
{
    [Fact]
    public void NamesTheAlgorithmsTheSpecificationAdmits()
    {
        Assert.Equal("SHA3-256", PdfSignatureAlgorithms.Sha3_256.Name);
        Assert.Equal("SHA3-384", PdfSignatureAlgorithms.Sha3_384.Name);
        Assert.Equal("SHA3-512", PdfSignatureAlgorithms.Sha3_512.Name);
        Assert.Equal("SHAKE256", PdfSignatureAlgorithms.Shake256.Name);
    }

    [Fact]
    public void AlwaysOffersTheShaTwoFamily()
    {
        // These are in .NET itself rather than in the platform library, so they are always there.
        Assert.Contains(HashAlgorithmName.SHA256, PdfSignatureAlgorithms.Available);
        Assert.Contains(HashAlgorithmName.SHA384, PdfSignatureAlgorithms.Available);
        Assert.Contains(HashAlgorithmName.SHA512, PdfSignatureAlgorithms.Available);
    }

    [Fact]
    public void AgreesWithItselfAboutWhatThisMachineCanDo()
    {
        // Whether SHA-3 is available depends on the operating system's cryptographic library, so
        // the test checks that the two ways of asking agree rather than assuming either answer.
        foreach (HashAlgorithmName algorithm in new[]
                 {
                     PdfSignatureAlgorithms.Sha3_256, PdfSignatureAlgorithms.Sha3_384,
                     PdfSignatureAlgorithms.Sha3_512, PdfSignatureAlgorithms.Shake256,
                 })
        {
            bool listed = PdfSignatureAlgorithms.Available.Contains(algorithm);
            Assert.Equal(listed, PdfSignatureAlgorithms.IsSupported(algorithm));
        }
    }

    [Fact]
    public void RejectsAnAlgorithmItDoesNotKnow()
    {
        Assert.False(PdfSignatureAlgorithms.IsSupported(new HashAlgorithmName("MD5")));
        Assert.False(PdfSignatureAlgorithms.IsSupported(HashAlgorithmName.SHA1));
    }

    [Fact]
    public void DoesNotMistakeAnOrdinaryKeyForAnEdwardsOne()
    {
        using X509Certificate2 rsa = CreateRsaCertificate();
        using X509Certificate2 ecdsa = CreateEcdsaCertificate();

        Assert.False(PdfSignatureAlgorithms.IsEdwardsCurve(rsa));
        Assert.False(PdfSignatureAlgorithms.IsEdwardsCurve(ecdsa));

        Assert.Null(PdfSignatureAlgorithms.RequiredDigestForEdwardsCurve(rsa));
        Assert.Null(PdfSignatureAlgorithms.RequiredDigestForEdwardsCurve(ecdsa));
    }

    [Fact]
    public void ImplementsTheCurveDigestTableOfIsoTs32002()
    {
        Assert.Equal(
            ["SHA256", "SHA3-256"],
            PdfSignatureAlgorithms.AllowedDigestsForNamedCurve(PdfSignatureAlgorithms.P256Oid)
                .Select(digest => digest.Name));
        Assert.Equal(
            ["SHA384", "SHA3-384"],
            PdfSignatureAlgorithms.AllowedDigestsForNamedCurve(PdfSignatureAlgorithms.P384Oid)
                .Select(digest => digest.Name));
        Assert.Equal(
            ["SHA512", "SHA3-512"],
            PdfSignatureAlgorithms.AllowedDigestsForNamedCurve(PdfSignatureAlgorithms.P521Oid)
                .Select(digest => digest.Name));
        Assert.Equal(6, PdfSignatureAlgorithms.AllowedDigestsForNamedCurve(
            PdfSignatureAlgorithms.BrainpoolP256r1Oid).Count);
        Assert.Equal(4, PdfSignatureAlgorithms.AllowedDigestsForNamedCurve(
            PdfSignatureAlgorithms.BrainpoolP384r1Oid).Count);
        Assert.Equal(2, PdfSignatureAlgorithms.AllowedDigestsForNamedCurve(
            PdfSignatureAlgorithms.BrainpoolP512r1Oid).Count);

        Assert.True(PdfSignatureAlgorithms.RequiresIso32002DeclarationForNamedCurve(
            PdfSignatureAlgorithms.BrainpoolP256r1Oid));
        Assert.False(PdfSignatureAlgorithms.RequiresIso32002DeclarationForNamedCurve(
            PdfSignatureAlgorithms.P256Oid));
    }

    [Fact]
    public void RejectsADigestThatDoesNotBelongToTheEcdsaCurve()
    {
        using X509Certificate2 certificate = CreateEcdsaCertificate();
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4).SetContent("BT ET"u8.ToArray());

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            PdfSigner.Sign(document, new PdfSignatureOptions
            {
                Certificate = certificate,
                HashAlgorithm = HashAlgorithmName.SHA512,
            }));

        Assert.Contains("P-256", error.Message, StringComparison.Ordinal);
        Assert.Contains("SHA256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignsP384WithItsRequiredSha384Digest()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=Inkwright P384 Test", key, HashAlgorithmName.SHA384);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4).SetContent("BT ET"u8.ToArray());

        byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions
        {
            Certificate = certificate,
            HashAlgorithm = HashAlgorithmName.SHA384,
        });

        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(signed)).IsIntact);
    }

    [Fact]
    public void RefusesToSignWithADigestThisMachineCannotCompute()
    {
        // Pick something certainly unavailable: an algorithm outside the admitted set.
        var missing = new HashAlgorithmName("SHA3-224");

        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4).SetContent("BT ET"u8.ToArray());

        using X509Certificate2 certificate = CreateRsaCertificate();

        PlatformNotSupportedException error = Assert.Throws<PlatformNotSupportedException>(
            () => PdfSigner.Sign(document, new PdfSignatureOptions
            {
                Certificate = certificate,
                HashAlgorithm = missing,
            }));

        Assert.Contains("SHA3-224", error.Message, StringComparison.Ordinal);

        // The message lists what can be used instead, so the caller can act on it.
        Assert.Contains("SHA256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignsWithEveryDigestThisMachineOffers()
    {
        using X509Certificate2 certificate = CreateRsaCertificate();

        foreach (HashAlgorithmName algorithm in PdfSignatureAlgorithms.Available)
        {
            using var document = PdfDocument.Create();
            document.Pages.Add(PageSize.A4).SetContent("BT ET"u8.ToArray());

            byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions
            {
                Certificate = certificate,
                HashAlgorithm = algorithm,
            });

            // A signature made with the digest has to verify with it too, or the choice of
            // algorithm has gone astray somewhere between signing and reading back.
            IReadOnlyList<PdfSignatureInfo> signatures = PdfSignatureVerifier.Verify(signed);

            PdfSignatureInfo signature = Assert.Single(signatures);
            Assert.True(signature.IsIntact, $"A signature made with {algorithm.Name} did not verify.");
        }
    }

    [Fact]
    public void NamesTheSpecificationWhenItUsesOneOfItsAlgorithms()
    {
        Assert.SkipUnless(
            PdfSignatureAlgorithms.IsSupported(PdfSignatureAlgorithms.Sha3_256),
            "This machine has no SHA-3, so no document can be signed with it.");

        using X509Certificate2 certificate = CreateRsaCertificate();
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4).SetContent("BT ET"u8.ToArray());

        byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions
        {
            Certificate = certificate,
            HashAlgorithm = PdfSignatureAlgorithms.Sha3_256,
        });

        using PdfDocument reopened = PdfDocument.Load(signed);
        Cos.PdfDictionary extensions = reopened.Resolve(
            reopened.Catalog.Get(Cos.PdfName.Get("Extensions"))).AsDictionary!;

        Cos.PdfArray declared = reopened.Resolve(extensions.Get(Cos.PdfName.Get("ISO_"))).AsArray!;
        Cos.PdfDictionary entry = reopened.Resolve(declared[0]).AsDictionary!;

        Assert.Equal(32001, entry.GetInteger(Cos.PdfName.Get("ExtensionLevel")));
    }

    private static X509Certificate2 CreateRsaCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Inkwright Algorithm Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }

    private static X509Certificate2 CreateEcdsaCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Inkwright Curve Test", key, HashAlgorithmName.SHA256);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }
}
