using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Inkwright.Security;

/// <summary>
/// A parsed integrity token: the cryptographic message syntax structure of ISO/TS 32004 §6.3, which
/// is an <c>AuthenticatedData</c> holding a wrapped key, the digests it vouches for, and the code
/// computed over them.
/// </summary>
internal sealed class PdfMacToken
{
    /// <summary>The content type of an authenticated data structure (RFC 5652 §9.1).</summary>
    private const string AuthenticatedDataOid = "1.2.840.113549.1.9.16.1.2";

    /// <summary>The content type the standard gives its own payload (§6.2).</summary>
    private const string IntegrityInfoOid = "1.0.32004.1.0";

    /// <summary>AES-256 key wrapping without padding, the only wrapping Table 7 admits.</summary>
    private const string Aes256WrapOid = "2.16.840.1.101.3.4.1.45";

    private const string WrapKeyDerivationOid = "1.0.32004.1.1";
    private const string HmacSha256Oid = "1.2.840.113549.2.9";
    private const string ContentTypeAttributeOid = "1.2.840.113549.1.9.3";
    private const string MessageDigestAttributeOid = "1.2.840.113549.1.9.4";
    private const string AlgorithmProtectionAttributeOid = "1.2.840.113549.1.9.52";

    /// <summary>The wrapped key that protects the code.</summary>
    internal required byte[] WrappedKey { get; init; }

    /// <summary>The authenticated attributes, re-encoded as the set they are signed as.</summary>
    internal required byte[] AuthenticatedAttributes { get; init; }

    /// <summary>The code itself.</summary>
    internal required byte[] Mac { get; init; }

    /// <summary>The payload the code covers.</summary>
    internal required byte[] Content { get; init; }

    /// <summary>The authenticated digest that binds the CMS attributes to <see cref="Content"/>.</summary>
    internal required byte[] ContentDigest { get; init; }

    /// <summary>The digest the token claims for the covered bytes of the file.</summary>
    internal required byte[] DataDigest { get; init; }

    /// <summary>The digest the token claims for the signature it hangs from, when it hangs from one.</summary>
    internal byte[]? SignatureDigest { get; init; }

    /// <summary>
    /// Which hash the token used. The standard lets a producer choose (§6.3.4), so a verifier that
    /// assumes one will disagree with perfectly good files.
    /// </summary>
    internal required HashAlgorithmName Digest { get; init; }

    /// <summary>Reads a token.</summary>
    /// <param name="token">The cryptographic message syntax bytes.</param>
    /// <returns>The parsed token, or <see langword="null"/> when the bytes are not one.</returns>
    internal static PdfMacToken? Read(ReadOnlyMemory<byte> token)
    {
        try
        {
            // Writers commonly reserve a fixed-width PDF string and fill unused bytes with zero.
            // Parse exactly one DER value and accept only zero padding after it; never trim the
            // buffer, because a legitimate DER value itself may end in a zero byte.
            var envelope = new AsnReader(token, AsnEncodingRules.DER);
            ReadOnlyMemory<byte> encoded = envelope.ReadEncodedValue();
            if (token.Span[encoded.Length..].IndexOfAnyExcept((byte)0) >= 0)
                return null;

            return Parse(encoded);
        }
        catch (AsnContentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static PdfMacToken? Parse(ReadOnlyMemory<byte> token)
    {
        var outer = new AsnReader(token, AsnEncodingRules.DER);
        AsnReader info = outer.ReadSequence();

        if (info.ReadObjectIdentifier() != AuthenticatedDataOid)
            return null;

        AsnReader wrapper = info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        AsnReader data = wrapper.ReadSequence();

        if (!data.TryReadInt32(out int version) || version != 0)
            return null;

        // The originator information is optional and carries nothing this check needs.
        if (data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            data.ReadEncodedValue();

        AsnReader recipients = data.ReadSetOf();
        byte[] wrappedKey = ReadWrappedKey(recipients);
        if (recipients.HasData)
            return null;

        // The algorithm identifiers are read to confirm the token uses what the standard allows.
        AsnReader macAlgorithm = data.ReadSequence();
        if (macAlgorithm.ReadObjectIdentifier() != HmacSha256Oid)
            return null;
        ReadOptionalNull(macAlgorithm);
        if (macAlgorithm.HasData)
            return null;

        if (!data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
            return null;

        AsnReader identifier = data.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1));
        string digestOid = identifier.ReadObjectIdentifier();
        ReadOptionalNull(identifier);
        if (identifier.HasData || !TryFromOid(digestOid, out HashAlgorithmName digest))
            return null;

        AsnReader encapsulated = data.ReadSequence();
        if (encapsulated.ReadObjectIdentifier() != IntegrityInfoOid)
            return null;

        AsnReader contentHolder = encapsulated.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        byte[] content = contentHolder.ReadOctetString();
        if (contentHolder.HasData || encapsulated.HasData)
            return null;

        // The attributes are transmitted with a context tag but authenticated as a plain set, so
        // the bytes have to be re-encoded before the code can be checked against them.
        var attributeTag = new Asn1Tag(TagClass.ContextSpecific, 2);
        if (!data.PeekTag().HasSameClassAndValue(attributeTag))
            return null;

        ReadOnlyMemory<byte> encodedAttributes = data.ReadEncodedValue();
        byte[] attributes = ReEncodeAsSet(encodedAttributes, attributeTag);
        if (!TryReadAuthenticatedAttributes(attributes, digestOid, out byte[] contentDigest))
            return null;

        byte[] mac = data.ReadOctetString();
        if (mac.Length != 32 || data.HasData || wrapper.HasData || info.HasData || outer.HasData)
            return null;

        if (!TryReadIntegrityInfo(content, out byte[] dataDigest, out byte[]? signatureDigest))
            return null;

        int digestLength = DigestLength(digest);
        if (dataDigest.Length != digestLength ||
            (signatureDigest is not null && signatureDigest.Length != digestLength) ||
            contentDigest.Length != digestLength)
        {
            return null;
        }

        return new PdfMacToken
        {
            WrappedKey = wrappedKey,
            AuthenticatedAttributes = attributes,
            Mac = mac,
            Content = content,
            ContentDigest = contentDigest,
            DataDigest = dataDigest,
            SignatureDigest = signatureDigest,
            Digest = digest,
        };
    }

    private static bool TryFromOid(string oid, out HashAlgorithmName algorithm)
    {
        algorithm = oid switch
        {
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            "2.16.840.1.101.3.4.2.8" => HashAlgorithmName.SHA3_256,
            "2.16.840.1.101.3.4.2.9" => HashAlgorithmName.SHA3_384,
            "2.16.840.1.101.3.4.2.10" => HashAlgorithmName.SHA3_512,
            _ => default,
        };

        return algorithm.Name is not null;
    }

    /// <summary>
    /// Reads the single password recipient the standard allows (§6.3.3) and returns the wrapped
    /// key it carries.
    /// </summary>
    private static byte[] ReadWrappedKey(AsnReader recipients)
    {
        // A password recipient is the fourth choice of RecipientInfo, so it is tagged with three.
        AsnReader recipient = recipients.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3));
        if (!recipient.TryReadInt32(out int version) || version != 0)
            throw new InvalidOperationException("The integrity token has an unsupported recipient version.");

        // Optional in generic CMS, but mandatory and fixed by ISO/TS 32004 §6.3.3.
        if (!recipient.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            throw new InvalidOperationException("The integrity token names no key derivation algorithm.");

        AsnReader derivation = recipient.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        if (derivation.ReadObjectIdentifier() != WrapKeyDerivationOid || derivation.HasData)
            throw new InvalidOperationException("The integrity token uses an unsupported key derivation algorithm.");

        AsnReader encryption = recipient.ReadSequence();
        string algorithm = encryption.ReadObjectIdentifier();
        ReadOptionalNull(encryption);

        if (algorithm != Aes256WrapOid || encryption.HasData)
            throw new InvalidOperationException($"The integrity token wraps its key with {algorithm}.");

        byte[] wrapped = recipient.ReadOctetString();
        if (wrapped.Length != 40 || recipient.HasData)
            throw new InvalidOperationException("The integrity token does not carry one wrapped 256-bit MAC key.");

        return wrapped;
    }

    /// <summary>
    /// Reads the attributes required by §6.3.6 and returns the digest that binds them to eContent.
    /// Unknown attributes are permitted, but duplicates and malformed standard attributes are not.
    /// </summary>
    private static bool TryReadAuthenticatedAttributes(
        byte[] encoded,
        string digestOid,
        out byte[] contentDigest)
    {
        contentDigest = [];
        var reader = new AsnReader(encoded, AsnEncodingRules.DER);
        AsnReader attributes = reader.ReadSetOf();
        int contentTypes = 0;
        int messageDigests = 0;
        int protections = 0;

        while (attributes.HasData)
        {
            AsnReader attribute = attributes.ReadSequence();
            string oid = attribute.ReadObjectIdentifier();
            AsnReader values = attribute.ReadSetOf();

            switch (oid)
            {
                case ContentTypeAttributeOid:
                    contentTypes++;
                    if (contentTypes != 1 || values.ReadObjectIdentifier() != IntegrityInfoOid || values.HasData)
                        return false;
                    break;

                case MessageDigestAttributeOid:
                    messageDigests++;
                    if (messageDigests != 1)
                        return false;
                    contentDigest = values.ReadOctetString();
                    if (values.HasData)
                        return false;
                    break;

                case AlgorithmProtectionAttributeOid:
                    protections++;
                    if (protections != 1 || !ValidateAlgorithmProtection(values, digestOid))
                        return false;
                    break;

                default:
                    while (values.HasData)
                        values.ReadEncodedValue();
                    break;
            }

            if (attribute.HasData)
                return false;
        }

        return !reader.HasData && contentTypes == 1 && messageDigests == 1;
    }

    private static bool ValidateAlgorithmProtection(AsnReader values, string digestOid)
    {
        if (!values.HasData)
            return false;

        AsnReader protection = values.ReadSequence();
        AsnReader protectedDigest = protection.ReadSequence();
        string protectedDigestOid = protectedDigest.ReadObjectIdentifier();
        ReadOptionalNull(protectedDigest);

        var macTag = new Asn1Tag(TagClass.ContextSpecific, 2);
        if (protectedDigest.HasData || protectedDigestOid != digestOid ||
            !protection.HasData || !protection.PeekTag().HasSameClassAndValue(macTag))
        {
            return false;
        }

        AsnReader protectedMac = protection.ReadSequence(macTag);
        string protectedMacOid = protectedMac.ReadObjectIdentifier();
        ReadOptionalNull(protectedMac);

        return protectedMacOid == HmacSha256Oid && !protectedMac.HasData &&
               !protection.HasData && !values.HasData;
    }

    private static void ReadOptionalNull(AsnReader reader)
    {
        if (reader.HasData && reader.PeekTag().HasSameClassAndValue(Asn1Tag.Null))
            reader.ReadNull();
    }

    /// <summary>Reads the payload: a version, the digest of the file, and optionally of a signature.</summary>
    private static bool TryReadIntegrityInfo(
        byte[] content, out byte[] dataDigest, out byte[]? signatureDigest)
    {
        dataDigest = [];
        signatureDigest = null;

        var reader = new AsnReader(content, AsnEncodingRules.DER);
        AsnReader sequence = reader.ReadSequence();

        if (!sequence.TryReadInt32(out int version) || version != 0)
            return false;

        dataDigest = sequence.ReadOctetString();

        if (sequence.HasData &&
            sequence.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            signatureDigest = sequence.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
        }

        return !sequence.HasData && !reader.HasData;
    }

    /// <summary>
    /// Replaces the context tag of the attribute block with the universal set tag, which is the
    /// form the code was computed over (RFC 5652 §9.3).
    /// </summary>
    private static byte[] ReEncodeAsSet(ReadOnlyMemory<byte> encoded, Asn1Tag original)
    {
        var reader = new AsnReader(encoded, AsnEncodingRules.DER);
        AsnReader set = reader.ReadSetOf(original);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSetOf())
        {
            while (set.HasData)
                writer.WriteEncodedValue(set.ReadEncodedValue().Span);
        }

        return writer.Encode();
    }

    /// <summary>
    /// Checks that the code matches, which proves whoever wrote it knew the file encryption key.
    /// </summary>
    /// <param name="fileKey">The document's file encryption key.</param>
    /// <param name="salt">The salt the encryption dictionary carries.</param>
    /// <returns>Whether the code is correct for this key and these attributes.</returns>
    internal bool VerifyMac(ReadOnlySpan<byte> fileKey, ReadOnlySpan<byte> salt)
    {
        if (salt.Length != PdfMacWriter.SaltLength)
            return false;

        byte[] wrappingKey = AesKeyWrap.DeriveWrappingKey(fileKey, salt, 32);

        if (!AesKeyWrap.TryUnwrap(wrappingKey, WrappedKey, out byte[] macKey) || macKey.Length != 32)
            return false;

        byte[] computed = HMACSHA256.HashData(macKey, AuthenticatedAttributes);
        return CryptographicOperations.FixedTimeEquals(computed, Mac);
    }

    /// <summary>
    /// Checks the mandatory CMS message-digest attribute that binds the authenticated attributes
    /// to the encapsulated integrity payload.
    /// </summary>
    internal bool VerifyContentDigest()
    {
        byte[] computed = Hash(Content);
        return computed.Length > 0 && CryptographicOperations.FixedTimeEquals(computed, ContentDigest);
    }

    /// <summary>
    /// Checks that the payload's digest matches the bytes the token says it covers. The code and
    /// this digest answer different questions: the code says the token is genuine, and the digest
    /// says the token is talking about this file.
    /// </summary>
    /// <param name="covered">The bytes the byte range names.</param>
    internal bool VerifyDataDigest(ReadOnlySpan<byte> covered)
    {
        byte[] computed = Hash(covered);
        return computed.Length > 0 && CryptographicOperations.FixedTimeEquals(computed, DataDigest);
    }

    /// <summary>Checks the digest that binds an attached PDF MAC to the raw CMS signature value.</summary>
    internal bool VerifySignatureDigest(ReadOnlySpan<byte> signature)
    {
        if (SignatureDigest is null)
            return false;

        byte[] computed = Hash(signature);
        return computed.Length > 0 && CryptographicOperations.FixedTimeEquals(computed, SignatureDigest);
    }

    /// <summary>
    /// Whether this machine can compute the hash the token used. The SHA-3 family comes from the
    /// platform's cryptographic library, so a token using one may be unverifiable here and perfectly
    /// verifiable elsewhere.
    /// </summary>
    internal bool IsDigestAvailable => Hash([]).Length > 0;

    private byte[] Hash(ReadOnlySpan<byte> data) => Digest.Name switch
    {
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        "SHA3-256" => SHA3_256.IsSupported ? SHA3_256.HashData(data) : [],
        "SHA3-384" => SHA3_384.IsSupported ? SHA3_384.HashData(data) : [],
        "SHA3-512" => SHA3_512.IsSupported ? SHA3_512.HashData(data) : [],
        _ => SHA256.HashData(data),
    };

    private static int DigestLength(HashAlgorithmName digest) => digest.Name switch
    {
        "SHA384" or "SHA3-384" => 48,
        "SHA512" or "SHA3-512" => 64,
        _ => 32,
    };
}
