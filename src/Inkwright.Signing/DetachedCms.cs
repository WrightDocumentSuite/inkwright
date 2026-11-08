using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Inkwright.Signing;

/// <summary>
/// Writes a detached CMS SignedData structure directly. The platform's <c>SignedCms</c> insists on
/// being handed a private key it can export, which a hardware module or a signing service will never
/// do; encoding the structure here keeps the digest the only thing that has to travel.
/// </summary>
internal static class DetachedCms
{
    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    private const string DataOid = "1.2.840.113549.1.7.1";
    private const string ContentTypeAttributeOid = "1.2.840.113549.1.9.3";
    private const string MessageDigestAttributeOid = "1.2.840.113549.1.9.4";
    private const string SigningTimeAttributeOid = "1.2.840.113549.1.9.5";
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const string RsaEncryptionOid = "1.2.840.113549.1.1.1";

    internal static byte[] Build(ReadOnlySpan<byte> content, PdfSignatureOptions options, IExternalSigner signer)
    {
        X509Certificate2 certificate = signer.Certificate;
        HashAlgorithmName hash = options.HashAlgorithm;

        byte[] contentDigest = CmsBuilder.Hash(content, hash);
        byte[] signedAttributes = EncodeSignedAttributes(certificate, contentDigest, options);

        // The signature covers the attributes as a plain SET OF, not as the context-specific tag
        // they carry inside the structure (RFC 5652 §5.4). An Edwards curve signs those bytes
        // whole, because RFC 8419 builds the hashing into the algorithm itself; everything else
        // signs their digest.
        byte[] signature = PdfSignatureAlgorithms.IsEdwardsCurve(certificate)
            ? signer.SignMessage(signedAttributes, hash)
            : signer.SignHash(CmsBuilder.Hash(signedAttributes, hash), hash);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(SignedDataOid);

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);

                using (writer.PushSetOf())
                    WriteAlgorithm(writer, CmsBuilder.DigestOid(hash).Value!, writeNull: false);

                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(DataOid);

                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    writer.WriteEncodedValue(certificate.RawData);
                    foreach (X509Certificate2 extra in signer.Chain)
                    {
                        if (!extra.Equals(certificate))
                            writer.WriteEncodedValue(extra.RawData);
                    }
                }

                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    writer.WriteInteger(1);

                    using (writer.PushSequence())
                    {
                        writer.WriteEncodedValue(certificate.IssuerName.RawData);
                        writer.WriteInteger(certificate.SerialNumberBytes.Span);
                    }

                    WriteAlgorithm(writer, CmsBuilder.DigestOid(hash).Value!, writeNull: false);

                    // The same bytes that were signed, retagged as [0] IMPLICIT.
                    writer.WriteEncodedValue(Retag(signedAttributes));

                    (string signatureOid, bool nullParameters) = SignatureAlgorithm(certificate, hash);
                    WriteAlgorithm(writer, signatureOid, nullParameters);
                    writer.WriteOctetString(signature);
                }
            }
        }

        return writer.Encode();
    }

    private static byte[] EncodeSignedAttributes(
        X509Certificate2 certificate, byte[] contentDigest, PdfSignatureOptions options)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSetOf())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(ContentTypeAttributeOid);
                using (writer.PushSetOf())
                    writer.WriteObjectIdentifier(DataOid);
            }

            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(SigningTimeAttributeOid);
                using (writer.PushSetOf())
                    writer.WriteUtcTime(options.SigningTime.UtcDateTime);
            }

            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(MessageDigestAttributeOid);
                using (writer.PushSetOf())
                    writer.WriteOctetString(contentDigest);
            }

            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(SigningCertificateV2Oid);
                using (writer.PushSetOf())
                using (writer.PushSequence())
                using (writer.PushSequence())
                using (writer.PushSequence())
                {
                    if (options.HashAlgorithm != HashAlgorithmName.SHA256)
                        WriteAlgorithm(writer, CmsBuilder.DigestOid(options.HashAlgorithm).Value!, writeNull: false);

                    writer.WriteOctetString(CmsBuilder.Hash(certificate.RawData, options.HashAlgorithm));
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Names the signature algorithm the way the key demands: Edwards curves carry their own
    /// identifiers from RFC 8410, an EC key names the curve-with-digest pair, and RSA keeps the
    /// bare <c>rsaEncryption</c> with NULL parameters that PKCS #7 verifiers expect.
    /// </summary>
    private static (string Oid, bool NullParameters) SignatureAlgorithm(
        X509Certificate2 certificate, HashAlgorithmName hash) => certificate.PublicKey.Oid.Value switch
        {
            PdfSignatureAlgorithms.Ed25519Oid => (PdfSignatureAlgorithms.Ed25519Oid, false),
            PdfSignatureAlgorithms.Ed448Oid => (PdfSignatureAlgorithms.Ed448Oid, false),
            "1.2.840.10045.2.1" => (hash.Name switch
            {
                "SHA384" => "1.2.840.10045.4.3.3",
                "SHA512" => "1.2.840.10045.4.3.4",
                "SHA3-256" => "2.16.840.1.101.3.4.3.10",
                "SHA3-384" => "2.16.840.1.101.3.4.3.11",
                "SHA3-512" => "2.16.840.1.101.3.4.3.12",
                "SHAKE256" => "1.3.6.1.5.5.7.6.33",
                _ => "1.2.840.10045.4.3.2",
            }, false),
            _ => (RsaEncryptionOid, true),
        };

    private static void WriteAlgorithm(AsnWriter writer, string oid, bool writeNull)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            if (writeNull)
                writer.WriteNull();
        }
    }

    private static byte[] Retag(byte[] setOf)
    {
        // A DER SET OF starts with 0x31; the implicit [0] form differs only in that first byte.
        byte[] copy = (byte[])setOf.Clone();
        copy[0] = 0xA0;
        return copy;
    }
}
