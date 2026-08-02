using System.Formats.Asn1;

namespace Inkwright.Security;

/// <summary>
/// Finds an integrity token hidden inside a signature. When a revision is signed, attaching the
/// token to the trailer would change the bytes the signature covers, so ISO/TS 32004 §6.5.2 puts it
/// among the signature's unsigned attributes instead — the one place in a signed container that can
/// still be written to afterwards.
/// </summary>
internal static class PdfMacSignatureAttribute
{
    /// <summary>The attribute type the standard assigns the token.</summary>
    internal const string AttributeOid = "1.0.32004.1.2";

    /// <summary>The content type of a signed data structure (RFC 5652 §5.1).</summary>
    private const string SignedDataOid = "1.2.840.113549.1.7.2";

    /// <summary>Extracts the token from a signature's contents.</summary>
    /// <param name="signature">The bytes of the signature dictionary's contents entry.</param>
    /// <returns>The token, or an empty array when the signature carries none.</returns>
    internal static byte[] Extract(byte[] signature)
    {
        try
        {
            return Search(signature) ?? [];
        }
        catch (AsnContentException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Separates one complete CMS value from the zero padding of a fixed-width PDF string.
    /// </summary>
    /// <remarks>
    /// The ASN.1 length identifies the boundary. Trimming zero bytes is unsafe because a valid CMS
    /// value may itself end in zero, including the last byte of a signature or a BER end marker.
    /// </remarks>
    internal static ReadOnlyMemory<byte> ReadPaddedCmsValue(ReadOnlyMemory<byte> signature)
    {
        var envelope = new AsnReader(signature, AsnEncodingRules.BER);
        ReadOnlyMemory<byte> encoded = envelope.ReadEncodedValue();

        if (signature.Span[encoded.Length..].IndexOfAnyExcept((byte)0) >= 0)
            throw new AsnContentException("CMS is followed by non-zero data.");

        return encoded;
    }

    private static byte[]? Search(byte[] signature)
    {
        var outer = new AsnReader(ReadPaddedCmsValue(signature), AsnEncodingRules.BER);
        AsnReader info = outer.ReadSequence();

        if (info.ReadObjectIdentifier() != SignedDataOid)
            return null;

        AsnReader wrapper = info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        AsnReader signed = wrapper.ReadSequence();

        signed.ReadInteger();
        signed.ReadSetOf();
        signed.ReadSequence();

        // Certificates and revocation lists are optional and come before the signer information.
        for (int tag = 0; tag <= 1; tag++)
        {
            if (signed.HasData && signed.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, tag)))
                signed.ReadEncodedValue();
        }

        AsnReader signers = signed.ReadSetOf();

        while (signers.HasData)
        {
            if (FindInSigner(signers.ReadSequence()) is { } token)
                return token;
        }

        return null;
    }

    private static byte[]? FindInSigner(AsnReader signer)
    {
        signer.ReadInteger();
        signer.ReadEncodedValue();
        signer.ReadSequence();

        // The signed attributes, if present, come before the signature algorithm.
        if (signer.HasData && signer.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            signer.ReadEncodedValue();

        signer.ReadSequence();
        signer.ReadOctetString();

        var unsignedTag = new Asn1Tag(TagClass.ContextSpecific, 1);
        if (!signer.HasData || !signer.PeekTag().HasSameClassAndValue(unsignedTag))
            return null;

        AsnReader attributes = signer.ReadSetOf(unsignedTag);

        while (attributes.HasData)
        {
            AsnReader attribute = attributes.ReadSequence();

            if (attribute.ReadObjectIdentifier() != AttributeOid)
                continue;

            AsnReader values = attribute.ReadSetOf();
            if (values.HasData)
                return values.ReadEncodedValue().ToArray();
        }

        return null;
    }
}
