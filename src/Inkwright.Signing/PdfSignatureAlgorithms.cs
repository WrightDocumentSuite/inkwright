using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Inkwright.Signing;

/// <summary>
/// The digest and signature algorithms a PDF signature may use, including the ones the technical
/// specifications add to PDF 2.0: the SHA-3 family from ISO/TS 32001 and the Edwards curves from
/// ISO/TS 32002.
/// </summary>
public static class PdfSignatureAlgorithms
{
    /// <summary>SHA3-256, admitted by ISO/TS 32001.</summary>
    public static HashAlgorithmName Sha3_256 { get; } = new("SHA3-256");

    /// <summary>SHA3-384, admitted by ISO/TS 32001.</summary>
    public static HashAlgorithmName Sha3_384 { get; } = new("SHA3-384");

    /// <summary>SHA3-512, admitted by ISO/TS 32001.</summary>
    public static HashAlgorithmName Sha3_512 { get; } = new("SHA3-512");

    /// <summary>
    /// SHAKE256 at the fixed length ISO/TS 32001 pins it to. It is the digest ISO/TS 32002 pairs
    /// with Ed448.
    /// </summary>
    public static HashAlgorithmName Shake256 { get; } = new("SHAKE256");

    /// <summary>
    /// Whether this machine can compute a digest with the named algorithm. The SHA-3 family is
    /// only available where the underlying cryptographic library provides it, which on Windows
    /// means a recent enough build.
    /// </summary>
    /// <param name="algorithm">The algorithm to test.</param>
    public static bool IsSupported(HashAlgorithmName algorithm) => CmsBuilder.IsSupported(algorithm);

    /// <summary>Every digest algorithm this machine can actually use for a signature.</summary>
    public static IReadOnlyList<HashAlgorithmName> Available =>
    [
        .. new[]
        {
            HashAlgorithmName.SHA256, HashAlgorithmName.SHA384, HashAlgorithmName.SHA512,
            Sha3_256, Sha3_384, Sha3_512, Shake256,
        }.Where(IsSupported),
    ];

    /// <summary>
    /// The digest ISO/TS 32002 requires with a given Edwards curve. The pairing is fixed by the
    /// specification rather than chosen: Ed25519 goes with SHA-512, Ed448 with SHAKE256.
    /// </summary>
    /// <param name="certificate">The signing certificate.</param>
    /// <returns>The required digest, or <see langword="null"/> when the key is not an Edwards one.</returns>
    public static HashAlgorithmName? RequiredDigestForEdwardsCurve(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return certificate.PublicKey.Oid.Value switch
        {
            Ed25519Oid => HashAlgorithmName.SHA512,
            Ed448Oid => Shake256,
            _ => null,
        };
    }

    /// <summary>
    /// The digest algorithms ISO/TS 32002 permits for the named elliptic curve in a certificate.
    /// An empty result means the key is not elliptic-curve based or its curve is not admitted.
    /// </summary>
    public static IReadOnlyList<HashAlgorithmName> AllowedDigestsForCurve(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (certificate.PublicKey.Oid.Value == Ed25519Oid)
            return [HashAlgorithmName.SHA512];
        if (certificate.PublicKey.Oid.Value == Ed448Oid)
            return [Shake256];

        return AllowedDigestsForNamedCurve(ReadNamedCurveOid(certificate));
    }

    internal static IReadOnlyList<HashAlgorithmName> AllowedDigestsForNamedCurve(string? curveOid) => curveOid switch
    {
        P256Oid => [HashAlgorithmName.SHA256, Sha3_256],
        P384Oid => [HashAlgorithmName.SHA384, Sha3_384],
        P521Oid => [HashAlgorithmName.SHA512, Sha3_512],
        BrainpoolP256r1Oid =>
            [HashAlgorithmName.SHA256, HashAlgorithmName.SHA384, HashAlgorithmName.SHA512, Sha3_256, Sha3_384, Sha3_512],
        BrainpoolP384r1Oid =>
            [HashAlgorithmName.SHA384, HashAlgorithmName.SHA512, Sha3_384, Sha3_512],
        BrainpoolP512r1Oid => [HashAlgorithmName.SHA512, Sha3_512],
        _ => [],
    };

    internal static bool IsEllipticCurve(X509Certificate2 certificate) =>
        certificate.PublicKey.Oid.Value is EcPublicKeyOid or Ed25519Oid or Ed448Oid;

    internal static bool IsAllowedForCurve(X509Certificate2 certificate, HashAlgorithmName digest) =>
        AllowedDigestsForCurve(certificate).Any(
            allowed => string.Equals(allowed.Name, digest.Name, StringComparison.OrdinalIgnoreCase));

    internal static bool RequiresIso32002Declaration(X509Certificate2 certificate)
    {
        if (IsEdwardsCurve(certificate))
            return true;

        return RequiresIso32002DeclarationForNamedCurve(ReadNamedCurveOid(certificate));
    }

    internal static bool RequiresIso32002DeclarationForNamedCurve(string? curveOid) =>
        curveOid is BrainpoolP256r1Oid or BrainpoolP384r1Oid or BrainpoolP512r1Oid;

    internal static string CurveName(X509Certificate2 certificate) =>
        certificate.PublicKey.Oid.Value switch
        {
            Ed25519Oid => "Ed25519",
            Ed448Oid => "Ed448",
            _ => ReadNamedCurveOid(certificate) switch
            {
                P256Oid => "P-256",
                P384Oid => "P-384",
                P521Oid => "P-521",
                BrainpoolP256r1Oid => "brainpoolP256r1",
                BrainpoolP384r1Oid => "brainpoolP384r1",
                BrainpoolP512r1Oid => "brainpoolP512r1",
                _ => "an unsupported or explicitly parameterised elliptic curve",
            },
        };

    private static string? ReadNamedCurveOid(X509Certificate2 certificate)
    {
        if (certificate.PublicKey.Oid?.Value != EcPublicKeyOid)
            return null;

        try
        {
            byte[]? encodedParameters = certificate.PublicKey.EncodedParameters?.RawData;
            if (encodedParameters is not { Length: > 0 })
                return null;

            var reader = new AsnReader(encodedParameters, AsnEncodingRules.DER);
            string oid = reader.ReadObjectIdentifier();
            return reader.HasData ? null : oid;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    /// <summary>Whether a certificate carries an Edwards-curve key, which ISO/TS 32002 admits.</summary>
    /// <param name="certificate">The certificate to inspect.</param>
    public static bool IsEdwardsCurve(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.PublicKey.Oid.Value is Ed25519Oid or Ed448Oid;
    }

    /// <summary>The object identifier of an Ed25519 key, from RFC 8410.</summary>
    internal const string Ed25519Oid = "1.3.101.112";

    /// <summary>The object identifier of an Ed448 key, from RFC 8410.</summary>
    internal const string Ed448Oid = "1.3.101.113";

    internal const string EcPublicKeyOid = "1.2.840.10045.2.1";
    internal const string P256Oid = "1.2.840.10045.3.1.7";
    internal const string P384Oid = "1.3.132.0.34";
    internal const string P521Oid = "1.3.132.0.35";
    internal const string BrainpoolP256r1Oid = "1.3.36.3.3.2.8.1.1.7";
    internal const string BrainpoolP384r1Oid = "1.3.36.3.3.2.8.1.1.11";
    internal const string BrainpoolP512r1Oid = "1.3.36.3.3.2.8.1.1.13";
}
