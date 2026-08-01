using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Inkwright.Signing.Tests;

/// <summary>
/// The Edwards curves ISO/TS 32002 admits into PDF, checked against the vectors printed in
/// RFC 8032 §7. EdDSA is deterministic, so signing is checked byte for byte, not just round-trip.
/// </summary>
public class EdwardsCurveTests
{
    // RFC 8032 §7.1, TEST 1 (empty message), TEST 2 (one byte) and TEST 3 (two bytes).
    public static TheoryData<string, string, string, string> Ed25519Vectors => new()
    {
        {
            "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
        },
        {
            "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
            "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"
        },
        {
            "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac" +
            "18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"
        },
    };

    [Theory]
    [MemberData(nameof(Ed25519Vectors))]
    public void ReproducesTheEd25519VectorsOfRfc8032(string seed, string publicKey, string message, string signature)
    {
        byte[] seedBytes = Convert.FromHexString(seed);
        byte[] messageBytes = Convert.FromHexString(message);

        Assert.Equal(publicKey, Convert.ToHexString(EdwardsCurve.Ed25519PublicKey(seedBytes)), ignoreCase: true);
        Assert.Equal(signature, Convert.ToHexString(EdwardsCurve.SignEd25519(seedBytes, messageBytes)), ignoreCase: true);
        Assert.True(EdwardsCurve.VerifyEd25519(
            Convert.FromHexString(publicKey), messageBytes, Convert.FromHexString(signature)));
    }

    [Fact]
    public void RejectsATamperedEd25519Signature()
    {
        byte[] publicKey = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        byte[] signature = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

        // A different message, and separately a flipped signature bit.
        Assert.False(EdwardsCurve.VerifyEd25519(publicKey, [1], signature));

        byte[] corrupted = (byte[])signature.Clone();
        corrupted[3] ^= 0x40;
        Assert.False(EdwardsCurve.VerifyEd25519(publicKey, [], corrupted));
    }

    [Fact]
    public void ReproducesTheEd448VectorOfRfc8032()
    {
        Assert.SkipUnless(EdwardsCurve.Ed448Available, "Ed448 needs SHAKE256, which this machine cannot compute.");

        byte[] seed = Convert.FromHexString(
            "6c82a562cb808d10d632be89c8513ebf6c929f34ddfa8c9f63c9960ef6e348a3" +
            "528c8a3fcc2f044e39a3fc5b94492f8f032e7549a20098f95b");
        string publicKey =
            "5fd7449b59b461fd2ce787ec616ad46a1da1342485a70e1f8a0ea75d80e96778" +
            "edf124769b46c7061bd6783df1e50f6cd1fa1abeafe8256180";
        string signature =
            "533a37f6bbe457251f023c0d88f976ae2dfb504a843e34d2074fd823d41a591f" +
            "2b233f034f628281f2fd7a22ddd47d7828c59bd0a21bfd3980ff0d2028d4b18a" +
            "9df63e006c5d1c2d345b925d8dc00b4104852db99ac5c7cdda8530a113a0f4db" +
            "b61149f05a7363268c71d95808ff2e652600";

        Assert.Equal(publicKey, Convert.ToHexString(EdwardsCurve.Ed448PublicKey(seed)), ignoreCase: true);
        Assert.Equal(signature, Convert.ToHexString(EdwardsCurve.SignEd448(seed, [])), ignoreCase: true);
        Assert.True(EdwardsCurve.VerifyEd448(
            Convert.FromHexString(publicKey), [], Convert.FromHexString(signature)));
    }

    [Fact]
    public void SignsAndVerifiesAPdfWithEd25519()
    {
        byte[] seed = RandomNumberGenerator.GetBytes(32);
        using X509Certificate2 certificate = CreateEd25519Certificate(seed, "CN=Edwards Signer");

        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            ExternalSigner = new Ed25519Signer(certificate, seed),
            HashAlgorithm = HashAlgorithmName.SHA512,
            FieldName = "Edwards",
        });

        PdfSignatureInfo info = Assert.Single(PdfSignatureVerifier.Verify(signed));
        Assert.True(info.IsIntact, info.Problem);
        Assert.True(info.IsValid);
        Assert.Equal(certificate.Thumbprint, info.SignerCertificate?.Thumbprint);
    }

    [Fact]
    public void NoticesTamperingUnderAnEd25519Signature()
    {
        byte[] seed = RandomNumberGenerator.GetBytes(32);
        using X509Certificate2 certificate = CreateEd25519Certificate(seed, "CN=Edwards Signer");

        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            ExternalSigner = new Ed25519Signer(certificate, seed),
            HashAlgorithm = HashAlgorithmName.SHA512,
        });

        byte[] tampered = (byte[])signed.Clone();
        int index = tampered.AsSpan().IndexOf("Hello"u8);
        Assert.True(index > 0);
        tampered[index] = (byte)'J';

        PdfSignatureInfo info = Assert.Single(PdfSignatureVerifier.Verify(tampered));
        Assert.False(info.IsIntact);
    }

    [Fact]
    public void HoldsEd25519ToItsFixedDigest()
    {
        byte[] seed = RandomNumberGenerator.GetBytes(32);
        using X509Certificate2 certificate = CreateEd25519Certificate(seed, "CN=Edwards Signer");

        // ISO/TS 32002 pairs Ed25519 with SHA-512; the default SHA-256 is not an option.
        Assert.Throws<ArgumentException>(() => TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            ExternalSigner = new Ed25519Signer(certificate, seed),
        }));
    }

    /// <summary>
    /// Builds a self-signed Ed25519 certificate by hand: the platform's certificate builder cannot
    /// hold an Edwards key, but it loads one happily.
    /// </summary>
    private static X509Certificate2 CreateEd25519Certificate(byte[] seed, string subject)
    {
        byte[] publicKey = EdwardsCurve.Ed25519PublicKey(seed);
        string commonName = subject["CN=".Length..];

        var body = new AsnWriter(AsnEncodingRules.DER);
        using (body.PushSequence())
        {
            using (body.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                body.WriteInteger(2);

            body.WriteInteger(RandomNumberGenerator.GetInt32(1, int.MaxValue));

            using (body.PushSequence())
                body.WriteObjectIdentifier("1.3.101.112");

            WriteName(body, commonName);

            using (body.PushSequence())
            {
                body.WriteUtcTime(DateTimeOffset.UtcNow.AddDays(-1));
                body.WriteUtcTime(DateTimeOffset.UtcNow.AddYears(1));
            }

            WriteName(body, commonName);

            using (body.PushSequence())
            {
                using (body.PushSequence())
                    body.WriteObjectIdentifier("1.3.101.112");

                body.WriteBitString(publicKey);
            }
        }

        byte[] encodedBody = body.Encode();
        byte[] signature = EdwardsCurve.SignEd25519(seed, encodedBody);

        var certificate = new AsnWriter(AsnEncodingRules.DER);
        using (certificate.PushSequence())
        {
            certificate.WriteEncodedValue(encodedBody);

            using (certificate.PushSequence())
                certificate.WriteObjectIdentifier("1.3.101.112");

            certificate.WriteBitString(signature);
        }

        return X509CertificateLoader.LoadCertificate(certificate.Encode());
    }

    private static void WriteName(AsnWriter writer, string commonName)
    {
        using (writer.PushSequence())
        using (writer.PushSetOf())
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("2.5.4.3");
            writer.WriteCharacterString(UniversalTagNumber.UTF8String, commonName);
        }
    }

    /// <summary>
    /// An external signer holding an Ed25519 key. It only ever answers for whole messages: EdDSA
    /// hashes internally, so a digest is the one thing it cannot sign.
    /// </summary>
    private sealed class Ed25519Signer(X509Certificate2 certificate, byte[] seed) : IExternalSigner
    {
        public X509Certificate2 Certificate => certificate;

        public byte[] SignHash(ReadOnlyMemory<byte> digest, HashAlgorithmName hashAlgorithm) =>
            throw new NotSupportedException("Ed25519 signs messages, not digests.");

        public byte[] SignMessage(ReadOnlyMemory<byte> message, HashAlgorithmName hashAlgorithm) =>
            EdwardsCurve.SignEd25519(seed, message.Span);
    }
}
