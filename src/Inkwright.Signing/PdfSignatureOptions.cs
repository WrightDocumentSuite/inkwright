using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Inkwright.Signing;

/// <summary>How much of the PAdES baseline a signature satisfies.</summary>
public enum PdfSignatureLevel
{
    /// <summary>B-B: the signature itself, with the signing certificate bound into it.</summary>
    Baseline,

    /// <summary>B-T: a trusted timestamp is added, proving when the signature existed.</summary>
    Timestamped,
}

/// <summary>
/// Signs a hash with a key the library never sees. This is what a hardware module or a cloud signing
/// service needs: the private key stays where it is and only the digest travels.
/// </summary>
public interface IExternalSigner
{
    /// <summary>The certificate the signature is made with.</summary>
    X509Certificate2 Certificate { get; }

    /// <summary>The chain to embed alongside the certificate, leaf first.</summary>
    IReadOnlyList<X509Certificate2> Chain => [];

    /// <summary>Signs a digest.</summary>
    /// <param name="digest">The bytes to sign, already hashed with <paramref name="hashAlgorithm"/>.</param>
    /// <param name="hashAlgorithm">The algorithm the digest was produced with.</param>
    /// <returns>The signature value, DER-encoded when the algorithm calls for structure (ECDSA).</returns>
    byte[] SignHash(ReadOnlyMemory<byte> digest, HashAlgorithmName hashAlgorithm);

    /// <summary>
    /// Signs a whole message. Only an Edwards-curve signer needs to override this: RFC 8419 has
    /// Ed25519 and Ed448 sign the full DER-encoded attributes rather than their digest. Everything
    /// else keeps the default, which hashes and hands over to <see cref="SignHash"/>.
    /// </summary>
    /// <param name="message">The exact bytes the signature must cover.</param>
    /// <param name="hashAlgorithm">The digest algorithm the signature declares.</param>
    byte[] SignMessage(ReadOnlyMemory<byte> message, HashAlgorithmName hashAlgorithm) =>
        SignHash(CmsBuilder.Hash(message.Span, hashAlgorithm), hashAlgorithm);
}

/// <summary>Everything a signature needs beyond the document itself.</summary>
public sealed class PdfSignatureOptions
{
    /// <summary>The certificate and private key to sign with.</summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>Certificates to embed alongside the signing one, so a verifier can build the chain.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    /// <summary>An external signer, used instead of <see cref="Certificate"/> when the key is not local.</summary>
    public IExternalSigner? ExternalSigner { get; init; }

    /// <summary>The digest algorithm; SHA-256 unless there is a reason to change it.</summary>
    /// <remarks>
    /// Beside the SHA-2 family, ISO/TS 32001 admits <c>SHA3-256</c>, <c>SHA3-384</c>,
    /// <c>SHA3-512</c> and <c>SHAKE256</c>. Whether the runtime can compute them depends on the
    /// platform's cryptographic library; <see cref="PdfSignatureAlgorithms.IsSupported"/> says.
    /// </remarks>
    public HashAlgorithmName HashAlgorithm { get; init; } = HashAlgorithmName.SHA256;

    /// <summary>How much of the PAdES baseline to satisfy.</summary>
    public PdfSignatureLevel Level { get; init; } = PdfSignatureLevel.Baseline;

    /// <summary>The timestamp authority to ask, required for <see cref="PdfSignatureLevel.Timestamped"/>.</summary>
    public Uri? TimestampAuthority { get; init; }

    /// <summary>
    /// How to reach the timestamp authority, when plain HTTP to <see cref="TimestampAuthority"/>
    /// does not fit — a proxy, a private network, a test double.
    /// </summary>
    public ITimestampAuthority? TimestampClient { get; init; }

    /// <summary>
    /// What this signature certifies, when it is the author's certification signature rather than
    /// an ordinary approval (ISO 32000-2 §12.8.2.2). A document may carry only one.
    /// </summary>
    public PdfCertificationLevel CertificationLevel { get; init; } = PdfCertificationLevel.None;

    /// <summary>
    /// The form fields this signature freezes (§12.8.2.4). Their values become part of what the
    /// signature vouches for: changing one afterwards is flagged by any validator that honours
    /// FieldMDP.
    /// </summary>
    public PdfFieldLock? FieldLock { get; init; }

    /// <summary>The name of the signature field to create or fill in.</summary>
    public string FieldName { get; init; } = "Signature1";

    /// <summary>The name shown as the signer, defaulting to the certificate's subject.</summary>
    public string? SignerName { get; init; }

    /// <summary>Why the document was signed.</summary>
    public string? Reason { get; init; }

    /// <summary>Where the document was signed.</summary>
    public string? Location { get; init; }

    /// <summary>How to reach the signer.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>When the signature was made; the current time when omitted.</summary>
    public DateTimeOffset SigningTime { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// Where to create a visible signature, or nothing for an invisible new field. When
    /// <see cref="FieldName"/> selects an existing empty signature field, its widget rectangles
    /// determine the appearance instead.
    /// </summary>
    public PdfRectangle? Appearance { get; init; }

    /// <summary>Which page a visible signature goes on.</summary>
    public int PageIndex { get; init; }

    /// <summary>How many bytes to reserve for the signature; raise it for long certificate chains.</summary>
    public int SignatureSize { get; init; } = 16384;

    /// <summary>
    /// Whether to attach the integrity code of ISO/TS 32004 to the signature. It only applies to an
    /// encrypted document, because the code is keyed from the file encryption key.
    /// </summary>
    /// <remarks>
    /// A signature and a code answer different questions. A signature says who approved the
    /// document; a code says whoever wrote it knew the password. The code also covers the signature
    /// itself, so it can be checked whatever the signature's own validation comes to.
    /// </remarks>
    public bool AddIntegrityCode { get; init; }
}

/// <summary>Signs with a certificate that carries its own private key.</summary>
internal sealed class CertificateSigner(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> chain) : IExternalSigner
{
    public X509Certificate2 Certificate { get; } = certificate;

    public IReadOnlyList<X509Certificate2> Chain { get; } = chain;

    public byte[] SignHash(ReadOnlyMemory<byte> digest, HashAlgorithmName hashAlgorithm)
    {
        if (Certificate.GetRSAPrivateKey() is { } rsa)
            return rsa.SignHash(digest.Span, hashAlgorithm, RSASignaturePadding.Pkcs1);

        if (Certificate.GetECDsaPrivateKey() is { } ecdsa)
            return ecdsa.SignHash(digest.Span);

        // ISO/TS 32002 admits Edwards curves, but .NET carries no primitive for them, so a caller
        // wanting one has to bring the signing itself through IExternalSigner.
        if (PdfSignatureAlgorithms.IsEdwardsCurve(Certificate))
        {
            throw new PlatformNotSupportedException(
                "Signing with an Edwards curve needs an external signer; .NET provides no Ed25519 or Ed448 primitive.");
        }

        throw new InvalidOperationException("The certificate has no usable private key.");
    }
}
