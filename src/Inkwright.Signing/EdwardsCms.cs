using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Inkwright.Signing;

/// <summary>
/// Checks a CMS signature made with an Edwards-curve key, which ISO/TS 32002 admits into PDF and
/// the platform's <c>SignedCms</c> cannot verify. RFC 8419 fixes the construction: the signature is
/// taken over the full DER-encoded signed attributes rather than over their digest, Ed25519 pairs
/// with SHA-512 and Ed448 with the fixed-length SHAKE256.
/// </summary>
internal static class EdwardsCms
{
    private const string MessageDigestAttributeOid = "1.2.840.113549.1.9.4";
    private const string SigningTimeAttributeOid = "1.2.840.113549.1.9.5";

    /// <summary>
    /// Finds the certificate identified by the first SignerInfo without asking <c>SignedCms</c> to
    /// understand its signature algorithm. Windows rejects EdDSA SignedData while decoding it, so
    /// the portable ASN.1 path has to select the certificate as well as verify the signature.
    /// </summary>
    internal static bool TryGetSignerCertificate(ReadOnlyMemory<byte> cms, out X509Certificate2? certificate)
    {
        certificate = null;

        if (!TryReadFirstSigner(cms, out CmsParts parts))
            return false;

        foreach (ReadOnlyMemory<byte> encoded in parts.Certificates)
        {
            X509Certificate2 candidate;
            try
            {
                candidate = X509CertificateLoader.LoadCertificate(encoded.Span);
            }
            catch (CryptographicException)
            {
                continue;
            }

            if (parts.Issuer.AsSpan().SequenceEqual(candidate.IssuerName.RawData) &&
                SameUnsignedInteger(parts.SerialNumber, candidate.SerialNumberBytes.Span))
            {
                certificate = candidate;
                return true;
            }

            candidate.Dispose();
        }

        return false;
    }

    /// <summary>Verifies the first signer of an Edwards-curve CMS blob.</summary>
    /// <param name="cms">The complete DER-encoded CMS object.</param>
    /// <param name="content">The detached content the signature is supposed to cover.</param>
    /// <param name="certificate">The signer's certificate, carrying the Edwards public key.</param>
    /// <returns>Whether the signature and the content digest both hold.</returns>
    internal static bool Verify(ReadOnlyMemory<byte> cms, ReadOnlySpan<byte> content, X509Certificate2 certificate)
    {
        bool ed448 = certificate.PublicKey.Oid.Value == PdfSignatureAlgorithms.Ed448Oid;
        if (ed448 && !EdwardsCurve.Ed448Available)
            return false;

        if (!TryReadFirstSigner(cms, out CmsParts parts))
            return false;

        byte[] publicKey = certificate.PublicKey.EncodedKeyValue.RawData;
        HashAlgorithmName digest = ed448 ? PdfSignatureAlgorithms.Shake256 : HashAlgorithmName.SHA512;

        if (parts.SignedAttributes.IsEmpty)
        {
            // No signed attributes means the signature is taken straight over the content.
            byte[] direct = content.ToArray();
            return ed448
                ? EdwardsCurve.VerifyEd448(publicKey, direct, parts.Signature)
                : EdwardsCurve.VerifyEd25519(publicKey, direct, parts.Signature);
        }

        // The attributes were signed as a plain SET OF, not as the [0] IMPLICIT they carry inside
        // the structure (RFC 5652 §5.4); only the leading byte differs.
        byte[] message = parts.SignedAttributes.ToArray();
        message[0] = 0x31;

        if (ReadMessageDigest(message) is not { } declared ||
            !declared.AsSpan().SequenceEqual(CmsBuilder.Hash(content, digest)))
        {
            return false;
        }

        return ed448
            ? EdwardsCurve.VerifyEd448(publicKey, message, parts.Signature)
            : EdwardsCurve.VerifyEd25519(publicKey, message, parts.Signature);
    }

    /// <summary>Reads the CMS signing-time without asking the platform to decode an EdDSA signer.</summary>
    internal static DateTimeOffset? ReadSigningTime(ReadOnlyMemory<byte> cms)
    {
        if (!TryReadFirstSigner(cms, out CmsParts parts) || parts.SignedAttributes.IsEmpty)
            return null;

        try
        {
            var tag = new Asn1Tag(TagClass.ContextSpecific, 0);
            var reader = new AsnReader(parts.SignedAttributes, AsnEncodingRules.BER);
            AsnReader attributes = reader.ReadSetOf(skipSortOrderValidation: true, expectedTag: tag);

            while (attributes.HasData)
            {
                AsnReader attribute = attributes.ReadSequence();
                if (attribute.ReadObjectIdentifier() != SigningTimeAttributeOid)
                    continue;

                AsnReader values = attribute.ReadSetOf();
                if (!values.HasData)
                    return null;

                Asn1Tag valueTag = values.PeekTag();
                return valueTag.HasSameClassAndValue(Asn1Tag.UtcTime)
                    ? values.ReadUtcTime()
                    : valueTag.HasSameClassAndValue(Asn1Tag.GeneralizedTime)
                        ? values.ReadGeneralizedTime()
                        : null;
            }
        }
        catch (AsnContentException)
        {
        }

        return null;
    }

    /// <summary>
    /// Walks the CMS structure down to the first <c>SignerInfo</c> and pulls out the two pieces the
    /// check needs: the signed attributes exactly as encoded, and the signature value.
    /// </summary>
    private static bool TryReadFirstSigner(ReadOnlyMemory<byte> cms, out CmsParts parts)
    {
        parts = default;

        try
        {
            var reader = new AsnReader(cms, AsnEncodingRules.BER);
            AsnReader contentInfo = reader.ReadSequence();
            contentInfo.ReadObjectIdentifier();

            AsnReader wrapper = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            AsnReader signedData = wrapper.ReadSequence();

            signedData.ReadInteger();
            signedData.ReadSetOf();
            signedData.ReadSequence();

            List<ReadOnlyMemory<byte>> certificates = [];
            if (signedData.PeekTag() is { TagClass: TagClass.ContextSpecific, TagValue: 0 })
            {
                AsnReader certificateSet = signedData.ReadSetOf(
                    skipSortOrderValidation: true,
                    expectedTag: new Asn1Tag(TagClass.ContextSpecific, 0));

                while (certificateSet.HasData)
                {
                    // Other CertificateChoices are context-specific. X.509 certificates are the
                    // ordinary SEQUENCE values and are the only form PDF signatures use here.
                    if (certificateSet.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                        certificates.Add(certificateSet.ReadEncodedValue());
                    else
                        certificateSet.ReadEncodedValue();
                }
            }

            if (signedData.PeekTag() is { TagClass: TagClass.ContextSpecific, TagValue: 1 })
                signedData.ReadEncodedValue();

            AsnReader signerInfos = signedData.ReadSetOf();
            AsnReader signer = signerInfos.ReadSequence();

            signer.ReadInteger();
            AsnReader signerIdentifier = signer.ReadSequence();
            byte[] issuer = signerIdentifier.ReadEncodedValue().ToArray();
            byte[] serialNumber = signerIdentifier.ReadIntegerBytes().ToArray();
            signer.ReadEncodedValue();

            ReadOnlyMemory<byte> signedAttributes = default;
            if (signer.PeekTag() is { TagClass: TagClass.ContextSpecific, TagValue: 0 })
                signedAttributes = signer.ReadEncodedValue();

            signer.ReadEncodedValue();
            byte[] signature = signer.ReadOctetString();
            parts = new CmsParts(certificates, issuer, serialNumber, signedAttributes, signature);
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>Finds the content digest the signer declared among the signed attributes.</summary>
    private static byte[]? ReadMessageDigest(byte[] attributes)
    {
        try
        {
            var reader = new AsnReader(attributes, AsnEncodingRules.BER);
            AsnReader set = reader.ReadSetOf();

            while (set.HasData)
            {
                AsnReader attribute = set.ReadSequence();
                if (attribute.ReadObjectIdentifier() != MessageDigestAttributeOid)
                    continue;

                return attribute.ReadSetOf().ReadOctetString();
            }
        }
        catch (AsnContentException)
        {
        }

        return null;
    }

    private static bool SameUnsignedInteger(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        while (left.Length > 1 && left[0] == 0)
            left = left[1..];

        while (right.Length > 1 && right[0] == 0)
            right = right[1..];

        return left.SequenceEqual(right);
    }

    private readonly record struct CmsParts(
        IReadOnlyList<ReadOnlyMemory<byte>> Certificates,
        byte[] Issuer,
        byte[] SerialNumber,
        ReadOnlyMemory<byte> SignedAttributes,
        byte[] Signature);
}
