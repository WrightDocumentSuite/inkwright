using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Inkwright.Signing;

/// <summary>
/// Builds the CMS blob that goes into a signature dictionary. Everything here comes from the base
/// class library: <see cref="SignedCms"/> for the structure, <see cref="AsnWriter"/> for the two
/// PAdES attributes it does not model, and <see cref="Rfc3161TimestampRequest"/> for the timestamp.
/// A PDF signature needs no third-party cryptography stack.
/// </summary>
internal static class CmsBuilder
{
    private const int TimestampNonceSize = 16;
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const string SignatureTimestampOid = "1.2.840.113549.1.9.16.2.14";

    internal static byte[] Build(ReadOnlyMemory<byte> content, PdfSignatureOptions options, IExternalSigner signer)
    {
        if ((signer is not CertificateSigner || options.HashAlgorithm == PdfSignatureAlgorithms.Shake256) &&
            options.Level != PdfSignatureLevel.Timestamped)
        {
            return DetachedCms.Build(content.Span, options, signer);
        }

        SignedCms signedData = BuildSignedData(content, options, signer);

        if (options.Level == PdfSignatureLevel.Timestamped)
            AddTimestamp(signedData, options);

        return signedData.Encode();
    }

    internal static async ValueTask<byte[]> BuildAsync(
        ReadOnlyMemory<byte> content,
        PdfSignatureOptions options,
        IExternalSigner signer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if ((signer is not CertificateSigner || options.HashAlgorithm == PdfSignatureAlgorithms.Shake256) &&
            options.Level != PdfSignatureLevel.Timestamped)
        {
            return DetachedCms.Build(content.Span, options, signer);
        }

        SignedCms signedData = BuildSignedData(content, options, signer);

        if (options.Level == PdfSignatureLevel.Timestamped)
            await AddTimestampAsync(signedData, options, cancellationToken).ConfigureAwait(false);

        return signedData.Encode();
    }

    private static SignedCms BuildSignedData(
        ReadOnlyMemory<byte> content,
        PdfSignatureOptions options,
        IExternalSigner signer)
    {
        // A key the platform can use goes through SignedCms; anything else is encoded here, because
        // an external signer only ever answers with a signature over a digest. Adding an unsigned
        // timestamp attribute needs no private key, so the BCL can reopen that manually encoded CMS.
        if (signer is not CertificateSigner)
        {
            byte[] encoded = DetachedCms.Build(content.Span, options, signer);
            var externalSignedData = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
            externalSignedData.Decode(encoded);
            return externalSignedData;
        }

        var signedData = new SignedCms(new ContentInfo(content.ToArray()), detached: true);

        var cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signer.Certificate)
        {
            DigestAlgorithm = DigestOid(options.HashAlgorithm),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };

        foreach (X509Certificate2 extra in signer.Chain)
        {
            if (!extra.Equals(signer.Certificate))
                cmsSigner.Certificates.Add(extra);
        }

        cmsSigner.SignedAttributes.Add(new Pkcs9SigningTime(options.SigningTime.UtcDateTime));
        cmsSigner.SignedAttributes.Add(BuildSigningCertificateV2(signer.Certificate, options.HashAlgorithm));

        signedData.ComputeSignature(cmsSigner, silent: true);
        return signedData;
    }

    private static AsnEncodedData BuildSigningCertificateV2(X509Certificate2 certificate, HashAlgorithmName hash)
    {
        byte[] digest = Hash(certificate.RawData, hash);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    // SHA-256 is the default and is left out, exactly as the profile prescribes.
                    if (hash != HashAlgorithmName.SHA256)
                    {
                        using (writer.PushSequence())
                            writer.WriteObjectIdentifier(DigestOid(hash).Value!);
                    }

                    writer.WriteOctetString(digest);
                }
            }
        }

        return new AsnEncodedData(new Oid(SigningCertificateV2Oid), writer.Encode());
    }

    private static void AddTimestamp(SignedCms signedData, PdfSignatureOptions options)
    {
        ITimestampAuthority client = ResolveAuthority(options.TimestampClient, options.TimestampAuthority);

        Rfc3161TimestampRequest request = Rfc3161TimestampRequest.CreateFromSignerInfo(
            signedData.SignerInfos[0],
            options.HashAlgorithm,
            nonce: CreateTimestampNonce(),
            requestSignerCertificates: true);

        byte[] response = client.Send(request.Encode());
        Rfc3161TimestampToken token = ValidateTimestampResponse(request, response);

        signedData.SignerInfos[0].AddUnsignedAttribute(
            new AsnEncodedData(new Oid(SignatureTimestampOid), token.AsSignedCms().Encode()));
    }

    private static async ValueTask AddTimestampAsync(
        SignedCms signedData,
        PdfSignatureOptions options,
        CancellationToken cancellationToken)
    {
        ITimestampAuthority client = ResolveAuthority(options.TimestampClient, options.TimestampAuthority);

        Rfc3161TimestampRequest request = Rfc3161TimestampRequest.CreateFromSignerInfo(
            signedData.SignerInfos[0],
            options.HashAlgorithm,
            nonce: CreateTimestampNonce(),
            requestSignerCertificates: true);

        byte[] response = await client.SendAsync(request.Encode(), cancellationToken).ConfigureAwait(false);
        Rfc3161TimestampToken token = ValidateTimestampResponse(request, response);

        signedData.SignerInfos[0].AddUnsignedAttribute(
            new AsnEncodedData(new Oid(SignatureTimestampOid), token.AsSignedCms().Encode()));
    }

    /// <summary>The way to reach the authority: an explicit client wins, a URI gets plain HTTP.</summary>
    internal static ITimestampAuthority ResolveAuthority(ITimestampAuthority? client, Uri? authority)
    {
        if (client is not null)
            return client;

        if (authority is null)
            throw new InvalidOperationException("A timestamp needs an authority to ask.");

        return new HttpTimestampAuthority(authority);
    }

    /// <summary>
    /// Creates a positive, fixed-width RFC 3161 INTEGER. Keeping the leading bit clear prevents
    /// DER from adding a sign byte, while setting the next bit keeps all 16 bytes significant.
    /// </summary>
    internal static byte[] CreateTimestampNonce()
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(TimestampNonceSize);
        nonce[0] = (byte)((nonce[0] & 0x3F) | 0x40);
        return nonce;
    }

    /// <summary>
    /// Accepts exactly one complete response and verifies the timestamp token's CMS signature.
    /// Chain trust and revocation remain application policy; this check proves only that the token
    /// was not malformed or altered after the authority signed it.
    /// </summary>
    internal static Rfc3161TimestampToken ValidateTimestampResponse(
        Rfc3161TimestampRequest request,
        ReadOnlyMemory<byte> response)
    {
        ArgumentNullException.ThrowIfNull(request);

        Rfc3161TimestampToken token = request.ProcessResponse(response, out int bytesConsumed);
        if (bytesConsumed != response.Length)
        {
            throw new CryptographicException(
                $"The timestamp response contains {response.Length - bytesConsumed} trailing byte(s).");
        }

        token.AsSignedCms().CheckSignature(verifySignatureOnly: true);
        return token;
    }

    /// <summary>
    /// The length ISO/TS 32001 fixes for a SHAKE256 digest. The function can produce any length,
    /// which is what makes it extendable, but a signature needs one agreed length, and the
    /// specification pins it by requiring the fixed-length object identifier.
    /// </summary>
    private const int Shake256Length = 64;

    internal static byte[] Hash(ReadOnlySpan<byte> data, HashAlgorithmName algorithm) => algorithm.Name switch
    {
        "SHA512" => SHA512.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA3-256" => SHA3_256.HashData(data),
        "SHA3-384" => SHA3_384.HashData(data),
        "SHA3-512" => SHA3_512.HashData(data),
        "SHAKE256" => Shake256.HashData(data, Shake256Length),
        _ => SHA256.HashData(data),
    };

    internal static Oid DigestOid(HashAlgorithmName algorithm) => algorithm.Name switch
    {
        "SHA512" => new Oid("2.16.840.1.101.3.4.2.3"),
        "SHA384" => new Oid("2.16.840.1.101.3.4.2.2"),

        // The SHA-3 family, added to PDF by ISO/TS 32001 and defined in FIPS PUB 202.
        "SHA3-256" => new Oid("2.16.840.1.101.3.4.2.8"),
        "SHA3-384" => new Oid("2.16.840.1.101.3.4.2.9"),
        "SHA3-512" => new Oid("2.16.840.1.101.3.4.2.10"),

        // The fixed-length identifier, which is the one the specification requires.
        "SHAKE256" => new Oid("2.16.840.1.101.3.4.2.18"),

        _ => new Oid("2.16.840.1.101.3.4.2.1"),
    };

    /// <summary>Whether this machine can compute a digest with the named algorithm.</summary>
    /// <param name="algorithm">The algorithm to test.</param>
    internal static bool IsSupported(HashAlgorithmName algorithm) => algorithm.Name switch
    {
        "SHA3-256" => SHA3_256.IsSupported,
        "SHA3-384" => SHA3_384.IsSupported,
        "SHA3-512" => SHA3_512.IsSupported,
        "SHAKE256" => Shake256.IsSupported,
        "SHA256" or "SHA384" or "SHA512" => true,
        _ => false,
    };
}
