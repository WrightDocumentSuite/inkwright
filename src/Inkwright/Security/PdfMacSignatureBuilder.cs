using System.Formats.Asn1;

namespace Inkwright.Security;

/// <summary>
/// Attaches an integrity token to a signature. In a signed revision the token cannot go in the
/// trailer, because writing it there would change bytes the signature covers; ISO/TS 32004 §6.5.2
/// therefore puts it among the signature's unsigned attributes, the one part of a signed container
/// that may still be written after signing.
/// </summary>
internal static class PdfMacSignatureBuilder
{
    private const string SignedDataOid = "1.2.840.113549.1.7.2";

    /// <summary>
    /// Inserts a token into a signature's unsigned attributes, returning the rebuilt signature.
    /// </summary>
    /// <param name="signature">The signature bytes to extend.</param>
    /// <param name="token">The token to attach.</param>
    /// <returns>The signature with the token attached.</returns>
    /// <exception cref="InvalidOperationException">The CMS signature cannot carry the token.</exception>
    internal static byte[] Attach(byte[] signature, byte[] token)
    {
        try
        {
            return Rebuild(signature, token)
                ?? throw new InvalidOperationException("The CMS signature is not a SignedData container.");
        }
        catch (AsnContentException exception)
        {
            throw new InvalidOperationException("The CMS signature could not be rebuilt with a PDF MAC attribute.", exception);
        }
    }

    /// <summary>
    /// The raw signature value of the first signer, which the token has to record a digest of
    /// (§6.6.3) so that the code covers the signature as well as the document.
    /// </summary>
    /// <param name="signature">The signature bytes.</param>
    /// <returns>The signature value, or an empty array when it cannot be found.</returns>
    internal static byte[] ReadSignatureValue(byte[] signature)
    {
        try
        {
            AsnReader? signer = FirstSigner(signature);
            if (signer is null)
                return [];

            SkipToSignatureValue(signer);
            return signer.ReadOctetString();
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

    private static byte[]? Rebuild(byte[] signature, byte[] token)
    {
        int end = signature.Length;
        while (end > 0 && signature[end - 1] == 0)
            end--;

        var outer = new AsnReader(signature.AsMemory(0, end), AsnEncodingRules.BER);
        AsnReader info = outer.ReadSequence();

        if (info.ReadObjectIdentifier() != SignedDataOid)
            return null;

        AsnReader wrapper = info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        AsnReader signed = wrapper.ReadSequence();

        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(SignedDataOid);

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                using (writer.PushSequence())
                {
                    // Everything up to the signer information is copied through unchanged.
                    writer.WriteEncodedValue(signed.ReadEncodedValue().Span);
                    writer.WriteEncodedValue(signed.ReadEncodedValue().Span);
                    writer.WriteEncodedValue(signed.ReadEncodedValue().Span);

                    for (int tag = 0; tag <= 1; tag++)
                    {
                        if (signed.HasData &&
                            signed.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, tag)))
                        {
                            writer.WriteEncodedValue(signed.ReadEncodedValue().Span);
                        }
                    }

                    AsnReader signers = signed.ReadSetOf();

                    using (writer.PushSetOf())
                    {
                        bool attached = false;

                        while (signers.HasData)
                        {
                            AsnReader signer = signers.ReadSequence();
                            WriteSigner(writer, signer, attached ? [] : token);
                            attached = true;
                        }
                    }
                }
            }
        }

        return writer.Encode();
    }

    private static void WriteSigner(AsnWriter writer, AsnReader signer, byte[] token)
    {
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(signer.ReadEncodedValue().Span);
            writer.WriteEncodedValue(signer.ReadEncodedValue().Span);
            writer.WriteEncodedValue(signer.ReadEncodedValue().Span);

            if (signer.HasData && signer.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                writer.WriteEncodedValue(signer.ReadEncodedValue().Span);

            writer.WriteEncodedValue(signer.ReadEncodedValue().Span);
            writer.WriteEncodedValue(signer.ReadEncodedValue().Span);

            var unsignedTag = new Asn1Tag(TagClass.ContextSpecific, 1);
            bool hasExisting = signer.HasData && signer.PeekTag().HasSameClassAndValue(unsignedTag);

            if (!hasExisting && token.Length == 0)
                return;

            using (writer.PushSetOf(unsignedTag))
            {
                if (hasExisting)
                {
                    AsnReader existing = signer.ReadSetOf(unsignedTag);
                    while (existing.HasData)
                        writer.WriteEncodedValue(existing.ReadEncodedValue().Span);
                }

                if (token.Length == 0)
                    return;

                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(PdfMacSignatureAttribute.AttributeOid);
                    using (writer.PushSetOf())
                        writer.WriteEncodedValue(token);
                }
            }
        }
    }

    private static AsnReader? FirstSigner(byte[] signature)
    {
        int end = signature.Length;
        while (end > 0 && signature[end - 1] == 0)
            end--;

        var outer = new AsnReader(signature.AsMemory(0, end), AsnEncodingRules.BER);
        AsnReader info = outer.ReadSequence();

        if (info.ReadObjectIdentifier() != SignedDataOid)
            return null;

        AsnReader wrapper = info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        AsnReader signed = wrapper.ReadSequence();

        signed.ReadInteger();
        signed.ReadSetOf();
        signed.ReadSequence();

        for (int tag = 0; tag <= 1; tag++)
        {
            if (signed.HasData && signed.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, tag)))
                signed.ReadEncodedValue();
        }

        AsnReader signers = signed.ReadSetOf();
        return signers.HasData ? signers.ReadSequence() : null;
    }

    private static void SkipToSignatureValue(AsnReader signer)
    {
        signer.ReadInteger();
        signer.ReadEncodedValue();
        signer.ReadSequence();

        if (signer.HasData && signer.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            signer.ReadEncodedValue();

        signer.ReadSequence();
    }
}
