using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Inkwright.Signing.Tests;

/// <summary>
/// A timestamp authority that lives inside the test process: it answers RFC 3161 requests with
/// properly signed tokens, so the tests exercise the real request, response and verification code
/// without asking anything of the network.
/// </summary>
internal sealed class TestTimestampAuthority : ITimestampAuthority, IDisposable
{
    private const string TstInfoOid = "1.2.840.113549.1.9.16.1.4";
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";

    private readonly X509Certificate2 _certificate;
    private readonly bool _omitNonce;
    private readonly bool _corruptSignature;
    private int _serial;

    internal TestTimestampAuthority(bool omitNonce = false, bool corruptSignature = false)
    {
        _omitNonce = omitNonce;
        _corruptSignature = corruptSignature;
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Inkwright Test TSA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        // RFC 3161 §2.3: the authority's certificate must carry exactly the timestamping purpose,
        // marked critical; the platform's token validation refuses anything else.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.8")], critical: true));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        _certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }

    /// <summary>How many requests were answered.</summary>
    internal int Requests { get; private set; }

    /// <summary>The nonce carried by the last request.</summary>
    internal ReadOnlyMemory<byte>? LastNonce { get; private set; }

    public byte[] Send(ReadOnlyMemory<byte> request)
    {
        if (!Rfc3161TimestampRequest.TryDecode(request, out Rfc3161TimestampRequest? decoded, out _) || decoded is null)
            throw new InvalidOperationException("The request could not be decoded.");

        Requests++;
        LastNonce = decoded.GetNonce();

        byte[] token = BuildToken(decoded);

        var response = new AsnWriter(AsnEncodingRules.DER);
        using (response.PushSequence())
        {
            // PKIStatusInfo with status zero: granted.
            using (response.PushSequence())
                response.WriteInteger(0);

            response.WriteEncodedValue(token);
        }

        return response.Encode();
    }

    private byte[] BuildToken(Rfc3161TimestampRequest request)
    {
        byte[] tstInfo = BuildTstInfo(request);

        var cms = new SignedCms(new ContentInfo(new Oid(TstInfoOid), tstInfo), detached: false);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };

        signer.SignedAttributes.Add(BuildSigningCertificateV2());
        cms.ComputeSignature(signer, silent: true);
        byte[] encoded = cms.Encode();
        if (!_corruptSignature)
            return encoded;

        byte[] signature = cms.SignerInfos[0].GetSignature();
        int offset = encoded.AsSpan().LastIndexOf(signature);
        if (offset < 0)
            throw new InvalidOperationException("The test token signature could not be located.");

        encoded[offset + signature.Length / 2] ^= 1;
        return encoded;
    }

    private byte[] BuildTstInfo(Rfc3161TimestampRequest request)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(1);
            writer.WriteObjectIdentifier("1.3.6.1.4.1.601.10.3.1");

            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(request.HashAlgorithmId.Value!);
                    writer.WriteNull();
                }

                writer.WriteOctetString(request.GetMessageHash().Span);
            }

            writer.WriteInteger(++_serial);
            writer.WriteGeneralizedTime(DateTimeOffset.UtcNow, omitFractionalSeconds: true);

            if (!_omitNonce && request.GetNonce() is { } nonce)
                writer.WriteInteger(nonce.Span);
        }

        return writer.Encode();
    }

    /// <summary>The ESS certificate binding RFC 3161 requires in the signed attributes.</summary>
    private AsnEncodedData BuildSigningCertificateV2()
    {
        byte[] digest = SHA256.HashData(_certificate.RawData);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                    writer.WriteOctetString(digest);
            }
        }

        return new AsnEncodedData(new Oid(SigningCertificateV2Oid), writer.Encode());
    }

    public void Dispose() => _certificate.Dispose();
}
