using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Inkwright.Security;

/// <summary>
/// Builds the integrity tokens of ISO/TS 32004 §6.3. A token is a cryptographic message syntax
/// <c>AuthenticatedData</c>: a freshly generated key wrapped under one derived from the file
/// encryption key, the digests being vouched for, and a code computed over them.
/// </summary>
internal static class PdfMacWriter
{
    private const string AuthenticatedDataOid = "1.2.840.113549.1.9.16.1.2";
    private const string IntegrityInfoOid = "1.0.32004.1.0";
    private const string WrapKeyDerivationOid = "1.0.32004.1.1";
    private const string Aes256WrapOid = "2.16.840.1.101.3.4.1.45";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string HmacSha256Oid = "1.2.840.113549.2.9";
    private const string ContentTypeAttributeOid = "1.2.840.113549.1.9.3";
    private const string MessageDigestAttributeOid = "1.2.840.113549.1.9.4";
    private const string AlgorithmProtectionAttributeOid = "1.2.840.113549.1.9.52";

    /// <summary>How long the salt the key derivation uses must be (§5.1.1).</summary>
    internal const int SaltLength = 32;

    /// <summary>Builds a token vouching for a stretch of a file.</summary>
    /// <param name="fileKey">The document's file encryption key.</param>
    /// <param name="salt">The salt from the encryption dictionary.</param>
    /// <param name="dataDigest">The digest of the bytes the token covers.</param>
    /// <param name="signatureDigest">The digest of the signature it hangs from, when it hangs from one.</param>
    internal static byte[] Build(
        ReadOnlySpan<byte> fileKey,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> dataDigest,
        ReadOnlySpan<byte> signatureDigest)
    {
        byte[] payload = BuildIntegrityInfo(dataDigest, signatureDigest);
        byte[] attributes = BuildAuthenticatedAttributes(payload);

        // A fresh key each time, wrapped under one derived from the file key. The wrapping is what
        // ties the token to the password: only a holder of the file key can unwrap it.
        byte[] macKey = RandomNumberGenerator.GetBytes(32);
        byte[] wrappingKey = AesKeyWrap.DeriveWrappingKey(fileKey, salt, 32);
        byte[] wrappedKey = AesKeyWrap.Wrap(wrappingKey, macKey);

        byte[] mac = HMACSHA256.HashData(macKey, attributes);

        return Assemble(wrappedKey, payload, attributes, mac);
    }

    /// <summary>Encodes the payload: a version, the file digest, and optionally a signature digest.</summary>
    private static byte[] BuildIntegrityInfo(ReadOnlySpan<byte> dataDigest, ReadOnlySpan<byte> signatureDigest)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            writer.WriteInteger(0);
            writer.WriteOctetString(dataDigest);

            if (!signatureDigest.IsEmpty)
                writer.WriteOctetString(signatureDigest, new Asn1Tag(TagClass.ContextSpecific, 0));
        }

        return writer.Encode();
    }

    /// <summary>
    /// Encodes the attributes the code is computed over (§6.3.6): what the payload is, its digest,
    /// and which algorithms were used. The last of those is what stops an attacker substituting a
    /// weaker algorithm and recomputing.
    /// </summary>
    private static byte[] BuildAuthenticatedAttributes(byte[] payload)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSetOf())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(ContentTypeAttributeOid);
                using (writer.PushSetOf())
                    writer.WriteObjectIdentifier(IntegrityInfoOid);
            }

            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(MessageDigestAttributeOid);
                using (writer.PushSetOf())
                    writer.WriteOctetString(SHA256.HashData(payload));
            }

            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(AlgorithmProtectionAttributeOid);
                using (writer.PushSetOf())
                {
                    using (writer.PushSequence())
                    {
                        WriteAlgorithm(writer, Sha256Oid);

                        // The message authentication algorithm is tagged, which is how a reader
                        // tells it from a signature algorithm in the same position.
                        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                        {
                            writer.WriteObjectIdentifier(HmacSha256Oid);
                            writer.WriteNull();
                        }
                    }
                }
            }
        }

        return writer.Encode();
    }

    private static byte[] Assemble(byte[] wrappedKey, byte[] payload, byte[] attributes, byte[] mac)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(AuthenticatedDataOid);

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                using (writer.PushSequence())
                {
                    // Version zero is what a password recipient with authenticated attributes needs.
                    writer.WriteInteger(0);

                    using (writer.PushSetOf())
                    {
                        // A password recipient is the fourth choice, so it carries the tag three.
                        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
                        {
                            writer.WriteInteger(0);

                            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                                writer.WriteObjectIdentifier(WrapKeyDerivationOid);

                            WriteAlgorithm(writer, Aes256WrapOid);
                            writer.WriteOctetString(wrappedKey);
                        }
                    }

                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier(HmacSha256Oid);
                        writer.WriteNull();
                    }

                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                        writer.WriteObjectIdentifier(Sha256Oid);

                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier(IntegrityInfoOid);
                        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                            writer.WriteOctetString(payload);
                    }

                    // The attributes travel under a context tag but were authenticated as a set.
                    WriteRetagged(writer, attributes, new Asn1Tag(TagClass.ContextSpecific, 2));
                    writer.WriteOctetString(mac);
                }
            }
        }

        return writer.Encode();
    }

    private static void WriteAlgorithm(AsnWriter writer, string oid)
    {
        using (writer.PushSequence())
            writer.WriteObjectIdentifier(oid);
    }

    /// <summary>Copies an encoded set under a different tag, leaving its contents untouched.</summary>
    private static void WriteRetagged(AsnWriter writer, byte[] set, Asn1Tag tag)
    {
        var reader = new AsnReader(set, AsnEncodingRules.DER);
        AsnReader contents = reader.ReadSetOf();

        using (writer.PushSetOf(tag))
        {
            while (contents.HasData)
                writer.WriteEncodedValue(contents.ReadEncodedValue().Span);
        }
    }
}
