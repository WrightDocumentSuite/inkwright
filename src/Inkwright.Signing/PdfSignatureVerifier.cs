using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Formats.Asn1;
using Inkwright.Cos;
using Inkwright.Forms;

namespace Inkwright.Signing;

/// <summary>What checking one signature found.</summary>
/// <param name="FieldName">The name of the signature field.</param>
/// <param name="IsIntact">Whether the bytes the signature covers still hash to what it says.</param>
/// <param name="CoversWholeDocument">Whether the signature covers the file right to its last byte.</param>
/// <param name="SignerCertificate">The certificate the signature was made with.</param>
/// <param name="SigningTime">When the signature says it was made.</param>
/// <param name="TimestampTime">When a trusted timestamp says the signature existed, if there is one.</param>
/// <param name="Reason">Why the document was signed.</param>
/// <param name="Problem">What went wrong, when the signature could not be checked at all.</param>
public sealed record PdfSignatureInfo(
    string FieldName,
    bool IsIntact,
    bool CoversWholeDocument,
    X509Certificate2? SignerCertificate,
    DateTimeOffset? SigningTime,
    DateTimeOffset? TimestampTime,
    string? Reason,
    string? Problem)
{
    /// <summary>Whether the signature is intact and covers the whole file.</summary>
    public bool IsValid => IsIntact && CoversWholeDocument && Problem is null;

    /// <summary>
    /// Whether this is a document timestamp (ISO 32000-2 §12.8.5) rather than someone's signature:
    /// an RFC 3161 token vouching for when the file existed, with no signer of its own.
    /// </summary>
    public bool IsDocumentTimestamp { get; init; }

    /// <summary>
    /// Whether the embedded timestamp token checks out cryptographically — its own signature and
    /// its binding to this signature both. Nothing when there is no timestamp at all.
    /// </summary>
    public bool? TimestampIntact { get; init; }

    /// <summary>
    /// What this signature certifies, when it is the author's certification signature carrying a
    /// DocMDP transform (§12.8.2.2). Nothing for an ordinary approval signature.
    /// </summary>
    public PdfCertificationLevel? CertificationLevel { get; init; }
}

/// <summary>
/// Checks the signatures a document carries. Verification answers two separate questions — whether
/// the covered bytes are unchanged, and whether the signature covers all of them — because a
/// signature that is cryptographically perfect over the first half of a file is worth nothing.
/// </summary>
public static class PdfSignatureVerifier
{
    private const string SignatureTimestampOid = "1.2.840.113549.1.9.16.2.14";

    /// <summary>Checks every signature in a file.</summary>
    /// <param name="file">The complete file, exactly as it was stored.</param>
    public static IReadOnlyList<PdfSignatureInfo> Verify(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);

        using PdfDocument document = PdfDocument.Load(file);
        return Verify(document, file);
    }

    /// <summary>Checks every signature in a file on disk.</summary>
    /// <param name="path">The file to check.</param>
    public static IReadOnlyList<PdfSignatureInfo> VerifyFile(string path) => Verify(File.ReadAllBytes(path));

    /// <summary>Checks every signature of a document against the bytes it was read from.</summary>
    /// <param name="document">The document, opened from <paramref name="file"/>.</param>
    /// <param name="file">The complete file.</param>
    public static IReadOnlyList<PdfSignatureInfo> Verify(PdfDocument document, byte[] file)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(file);

        List<PdfSignatureInfo> results = [];

        if (PdfForm.Open(document) is not { } form)
            return results;

        foreach (PdfFormField field in form)
        {
            if (field.Type != PdfFieldType.Signature)
                continue;

            if (field.Inherited(PdfName.Get("V")).AsDictionary is not { } signature)
                continue;

            results.Add(Check(field.FullName, signature, file));
        }

        return results;
    }

    private static PdfSignatureInfo Check(string fieldName, PdfDictionary signature, byte[] file)
    {
        DateTimeOffset? signingTime = PdfDate.TryParse(signature.GetTextString(PdfName.Get("M")), out DateTimeOffset moment)
            ? moment
            : null;

        string? reason = signature.GetTextString(PdfName.Get("Reason"));

        if (signature.GetArray(PdfName.Get("ByteRange")) is not { } ranges)
            return new PdfSignatureInfo(fieldName, false, false, null, signingTime, null, reason, "The signature has no byte range.");

        if (!TryCollectCoveredBytes(ranges, file, out byte[] covered, out List<(long Offset, long Length)> parts,
                out string? rangeProblem))
        {
            return new PdfSignatureInfo(fieldName, false, false, null, signingTime, null, reason, rangeProblem);
        }

        bool coversEverything = CoversEverythingButContents(parts, file);

        ReadOnlySpan<byte> contents = signature.Get(PdfName.Get("Contents")).AsStringBytes;
        if (contents.IsEmpty)
            return new PdfSignatureInfo(fieldName, false, coversEverything, null, signingTime, null, reason, "The signature is empty.");

        if (IsDocumentTimestamp(signature))
            return CheckDocumentTimestamp(fieldName, covered, coversEverything, ReadEncodedValue(contents));

        try
        {
            byte[] encoded = ReadEncodedValue(contents);

            // Windows SignedCms rejects Ed25519/Ed448 while decoding the structure, before a
            // custom signature verifier can run. Select the signer certificate from ASN.1 first
            // and keep the whole EdDSA path independent of the platform CMS implementation.
            if (EdwardsCms.TryGetSignerCertificate(encoded, out X509Certificate2? edwardsCertificate) &&
                edwardsCertificate is not null &&
                PdfSignatureAlgorithms.IsEdwardsCurve(edwardsCertificate))
            {
                bool edwardsIntact;
                string? edwardsProblem = null;

                if (edwardsCertificate.PublicKey.Oid.Value == PdfSignatureAlgorithms.Ed448Oid &&
                    !EdwardsCurve.Ed448Available)
                {
                    edwardsIntact = false;
                    edwardsProblem = "Checking an Ed448 signature needs SHAKE256, which this machine cannot compute.";
                }
                else
                {
                    edwardsIntact = EdwardsCms.Verify(encoded, covered, edwardsCertificate);
                }

                return new PdfSignatureInfo(
                    fieldName,
                    edwardsIntact,
                    coversEverything,
                    edwardsCertificate,
                    EdwardsCms.ReadSigningTime(encoded) ?? signingTime,
                    null,
                    reason,
                    edwardsProblem)
                {
                    CertificationLevel = ReadCertificationLevel(signature),
                };
            }

            var cms = new SignedCms(new ContentInfo(covered), detached: true);
            cms.Decode(encoded);

            SignerInfo signer = cms.SignerInfos[0];
            bool intact = CheckSignerIntact(cms, signer, encoded, covered, out string? problem);
            (DateTimeOffset? timestampTime, bool? timestampIntact) = ReadTimestamp(signer);

            return new PdfSignatureInfo(
                fieldName,
                intact,
                coversEverything,
                signer.Certificate,
                ReadSigningTime(signer) ?? signingTime,
                timestampTime,
                reason,
                problem)
            {
                TimestampIntact = timestampIntact,
                CertificationLevel = ReadCertificationLevel(signature),
            };
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return new PdfSignatureInfo(fieldName, false, coversEverything, null, signingTime, null, reason, ex.Message);
        }
    }

    private static bool IsDocumentTimestamp(PdfDictionary signature) =>
        signature.GetName(PdfName.Get("SubFilter"))?.Value == "ETSI.RFC3161" ||
        signature.GetName(PdfName.Get("Type"))?.Value == "DocTimeStamp";

    /// <summary>
    /// A document timestamp is checked the way §12.8.5.2 lays it out: the token's message imprint
    /// must be the digest of the covered bytes, and the token's own signature must hold, which the
    /// platform checks along with the timestamping key usage of the authority's certificate.
    /// </summary>
    private static PdfSignatureInfo CheckDocumentTimestamp(
        string fieldName, byte[] covered, bool coversEverything, byte[] encoded)
    {
        if (!Rfc3161TimestampToken.TryDecode(encoded, out Rfc3161TimestampToken? token, out _) || token is null)
        {
            return new PdfSignatureInfo(fieldName, false, coversEverything, null, null, null, null,
                "The timestamp token could not be decoded.")
            { IsDocumentTimestamp = true };
        }

        if (HashName(token.TokenInfo.HashAlgorithmId.Value) is not { } algorithm ||
            !PdfSignatureAlgorithms.IsSupported(algorithm))
        {
            return new PdfSignatureInfo(
                fieldName, false, coversEverything, null, null, token.TokenInfo.Timestamp, null,
                $"The timestamp digest {token.TokenInfo.HashAlgorithmId.Value} cannot be computed here.")
            { IsDocumentTimestamp = true, TimestampIntact = false };
        }

        byte[] digest = CmsBuilder.Hash(covered, algorithm);
        bool intact = token.VerifySignatureForHash(digest, token.TokenInfo.HashAlgorithmId, out X509Certificate2? authority);

        return new PdfSignatureInfo(
            fieldName, intact, coversEverything, authority, null, token.TokenInfo.Timestamp, null, null)
        {
            IsDocumentTimestamp = true,
            TimestampIntact = intact,
        };
    }

    /// <summary>
    /// Whether the signer's signature holds. The platform handles everything it knows; an Edwards
    /// curve, which ISO/TS 32002 admits and the platform does not carry, is checked by the
    /// library's own arithmetic.
    /// </summary>
    private static bool CheckSignerIntact(
        SignedCms cms, SignerInfo signer, byte[] encoded, byte[] covered, out string? problem)
    {
        problem = null;

        if (signer.Certificate is { } certificate && PdfSignatureAlgorithms.IsEdwardsCurve(certificate))
        {
            if (certificate.PublicKey.Oid.Value == PdfSignatureAlgorithms.Ed448Oid && !EdwardsCurve.Ed448Available)
            {
                problem = "Checking an Ed448 signature needs SHAKE256, which this machine cannot compute.";
                return false;
            }

            return EdwardsCms.Verify(encoded, covered, certificate);
        }

        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static PdfCertificationLevel? ReadCertificationLevel(PdfDictionary signature)
    {
        if (signature.GetArray(PdfName.Get("Reference")) is not { } references)
            return null;

        for (int i = 0; i < references.Count; i++)
        {
            if (references.GetDictionary(i) is not { } reference ||
                reference.GetName(PdfName.Get("TransformMethod"))?.Value != "DocMDP")
            {
                continue;
            }

            long permission = reference.GetDictionary(PdfName.Get("TransformParams"))
                ?.GetInteger(PdfName.Get("P"), 2) ?? 2;

            return permission is >= 1 and <= 3 ? (PdfCertificationLevel)permission : PdfCertificationLevel.FormFilling;
        }

        return null;
    }

    private static HashAlgorithmName? HashName(string? oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        "2.16.840.1.101.3.4.2.8" => PdfSignatureAlgorithms.Sha3_256,
        "2.16.840.1.101.3.4.2.9" => PdfSignatureAlgorithms.Sha3_384,
        "2.16.840.1.101.3.4.2.10" => PdfSignatureAlgorithms.Sha3_512,
        "2.16.840.1.101.3.4.2.18" => PdfSignatureAlgorithms.Shake256,
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        _ => null,
    };

    private static DateTimeOffset? ReadSigningTime(SignerInfo signer)
    {
        foreach (CryptographicAttributeObject attribute in signer.SignedAttributes)
        {
            if (attribute.Oid?.Value == "1.2.840.113549.1.9.5" && attribute.Values.Count > 0 &&
                attribute.Values[0] is Pkcs9SigningTime time)
            {
                return time.SigningTime;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the signature timestamp and checks it properly: the token's message imprint must be
    /// the digest of this signature's value, and the token's own signature must hold. A time from
    /// a token that fails either check is still reported, marked as not to be trusted.
    /// </summary>
    private static (DateTimeOffset? Time, bool? Intact) ReadTimestamp(SignerInfo signer)
    {
        foreach (CryptographicAttributeObject attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid?.Value != SignatureTimestampOid || attribute.Values.Count == 0)
                continue;

            try
            {
                if (!Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData, out Rfc3161TimestampToken? token, out _) ||
                    token is null)
                {
                    return (null, false);
                }

                bool intact = token.VerifySignatureForSignerInfo(signer, out _);
                return (token.TokenInfo.Timestamp, intact);
            }
            catch (CryptographicException)
            {
                return (null, false);
            }
        }

        return (null, null);
    }

    internal static byte[] ReadEncodedValue(ReadOnlySpan<byte> contents)
    {
        // /Contents reserves more space than most CMS objects need. Read the exact top-level ASN.1
        // value instead of trimming zero bytes: a DER signature is allowed to end in 00 itself.
        var reader = new AsnReader(contents.ToArray(), AsnEncodingRules.BER);
        return reader.ReadEncodedValue().ToArray();
    }

    private static bool TryCollectCoveredBytes(
        PdfArray ranges,
        byte[] file,
        out byte[] covered,
        out List<(long Offset, long Length)> parts,
        out string? problem)
    {
        covered = [];
        parts = [];
        problem = null;

        if (ranges.Count < 2 || (ranges.Count & 1) != 0)
        {
            problem = "The signature byte range is not an array of offset-length pairs.";
            return false;
        }

        long total = 0;
        long previousEnd = 0;

        for (int i = 0; i < ranges.Count; i += 2)
        {
            long offset = ranges.GetInteger(i);
            long length = ranges.GetInteger(i + 1);
            long end;

            try
            {
                end = checked(offset + length);
                total = checked(total + length);
            }
            catch (OverflowException)
            {
                problem = "The signature byte range overflows the file address space.";
                return false;
            }

            if (offset < 0 || length < 0 || end > file.Length)
            {
                problem = "The signature byte range lies outside the file.";
                return false;
            }

            if (i > 0 && offset < previousEnd)
            {
                problem = "The signature byte ranges overlap or are out of order.";
                return false;
            }

            parts.Add((offset, length));
            previousEnd = end;
        }

        if (total > int.MaxValue)
        {
            problem = "The signature byte range is too large to verify on this platform.";
            return false;
        }

        covered = new byte[(int)total];
        int destination = 0;
        foreach ((long offset, long length) in parts)
        {
            file.AsSpan((int)offset, (int)length).CopyTo(covered.AsSpan(destination));
            destination += (int)length;
        }

        return true;
    }

    private static bool CoversEverythingButContents(
        IReadOnlyList<(long Offset, long Length)> parts,
        ReadOnlySpan<byte> file)
    {
        if (parts.Count != 2 || parts[0].Offset != 0)
            return false;

        long firstEnd = parts[0].Length;
        long secondEnd = parts[1].Offset + parts[1].Length;
        if (firstEnd >= parts[1].Offset || secondEnd != file.Length ||
            firstEnd > int.MaxValue || parts[1].Offset > int.MaxValue)
        {
            return false;
        }

        int start = (int)firstEnd;
        int end = (int)parts[1].Offset;
        ReadOnlySpan<byte> gap = file[start..end];
        bool stringValue = gap.Length >= 2 &&
            ((gap[0] == (byte)'<' && gap[^1] == (byte)'>' && gap[1] != (byte)'<') ||
             (gap[0] == (byte)'(' && gap[^1] == (byte)')'));
        if (!stringValue)
            return false;

        int at = start - 1;
        while (at >= 0 && file[at] is 0 or 9 or 10 or 12 or 13 or 32)
            at--;

        ReadOnlySpan<byte> key = "/Contents"u8;
        int keyStart = at - key.Length + 1;
        return keyStart >= 0 && file.Slice(keyStart, key.Length).SequenceEqual(key);
    }
}
