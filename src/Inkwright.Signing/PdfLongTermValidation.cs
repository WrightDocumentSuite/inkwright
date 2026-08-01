using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inkwright.Cos;
using Inkwright.Forms;
using Inkwright.IO;

namespace Inkwright.Signing;

/// <summary>
/// The validation material a signature will need when someone checks it years from now:
/// certificates for the chains, and the revocation answers — CRLs and OCSP responses — that were
/// current while checking was still possible. The library does not fetch any of it; deciding whom
/// to ask and what to trust is the caller's policy, not the file format's.
/// </summary>
public sealed class PdfValidationData
{
    /// <summary>Certificates used by the signatures and their chains.</summary>
    public IList<X509Certificate2> Certificates { get; } = [];

    /// <summary>DER-encoded certificate revocation lists (RFC 5280).</summary>
    public IList<byte[]> CertificateRevocationLists { get; } = [];

    /// <summary>DER-encoded OCSP responses (RFC 6960).</summary>
    public IList<byte[]> OcspResponses { get; } = [];

    /// <summary>Whether there is anything here at all.</summary>
    public bool IsEmpty =>
        Certificates.Count == 0 && CertificateRevocationLists.Count == 0 && OcspResponses.Count == 0;
}

/// <summary>
/// Writes validation material into the document security store (ISO 32000-2 §12.8.4.3), the
/// dictionary a long-term verifiable signature keeps its collateral in. The store always goes in as
/// an incremental update after the signatures it serves, which is the ordering the standard
/// requires and the only one that leaves the signed bytes alone. When the caller has validated the
/// supplied material, this store and a document timestamp are the structural additions for B-LT.
/// </summary>
public static class PdfLongTermValidation
{
    /// <summary>Adds validation material to the security store.</summary>
    /// <param name="file">The complete signed file; its bytes survive untouched at the front of the result.</param>
    /// <param name="data">The certificates and revocation answers to store.</param>
    public static byte[] Add(byte[] file, PdfValidationData data) => Add(file, signatureFieldName: null, data);

    /// <summary>
    /// Adds validation material and records which signature it belongs to, through a VRI entry
    /// keyed by the signature's digest (§12.8.4.4).
    /// </summary>
    /// <param name="file">The complete signed file; its bytes survive untouched at the front of the result.</param>
    /// <param name="signatureFieldName">The fully qualified name of the signature field this material validates.</param>
    /// <param name="data">The certificates and revocation answers to store.</param>
    public static byte[] Add(byte[] file, string? signatureFieldName, PdfValidationData data)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(data);

        if (data.IsEmpty)
            throw new ArgumentException("There is no validation material to add.", nameof(data));

        using PdfDocument document = PdfDocument.Load(file);
        document.Version = PdfVersion.Max(document.Version, PdfVersion.Pdf20);
        PdfDictionary? existing = document.Catalog.GetDictionary(PdfName.Get("DSS"));

        var store = new PdfDictionary(5);
        store.Set(PdfName.Get("Type"), PdfValue.Name("DSS"));
        document.Attach(PdfValue.Dictionary(store));

        (PdfArray certificates, PdfArray usedCertificates) = MergeStreams(
            document, existing, "Certs", data.Certificates.Select(certificate => certificate.RawData));
        (PdfArray crls, PdfArray usedCrls) = MergeStreams(document, existing, "CRLs", data.CertificateRevocationLists);
        (PdfArray ocsps, PdfArray usedOcsps) = MergeStreams(document, existing, "OCSPs", data.OcspResponses);

        if (certificates.Count > 0)
            store.Set(PdfName.Get("Certs"), PdfValue.Array(certificates));

        if (ocsps.Count > 0)
            store.Set(PdfName.Get("OCSPs"), PdfValue.Array(ocsps));

        if (crls.Count > 0)
            store.Set(PdfName.Get("CRLs"), PdfValue.Array(crls));

        PdfDictionary related = CopyRelated(document, existing);

        if (signatureFieldName is not null)
        {
            related.Set(
                PdfName.Get(RelatedKey(document, signatureFieldName)),
                PdfValue.Dictionary(BuildRelatedEntry(document, usedCertificates, usedCrls, usedOcsps)));
        }

        if (related.Count > 0)
            store.Set(PdfName.Get("VRI"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(related))));

        document.Catalog.Set(PdfName.Get("DSS"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(store))));
        document.MarkChanged(document.CatalogId);

        return document.ToIncrementalArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            UpdateModificationDate = false,
            SetProducer = false,
        });
    }

    /// <summary>
    /// Carries the entries of an existing array forward and appends the new material, skipping
    /// blobs the store already holds: certificates shared by several signatures are stored once,
    /// which is the point of having a common store. The second array names the entry each addition
    /// ended up as, whether it was created now or found already there.
    /// </summary>
    private static (PdfArray Merged, PdfArray Used) MergeStreams(
        PdfDocument document, PdfDictionary? existing, string key, IEnumerable<byte[]> additions)
    {
        var merged = new PdfArray();
        var used = new PdfArray();
        document.Attach(PdfValue.Array(merged));
        document.Attach(PdfValue.Array(used));

        List<byte[]> present = [];

        if (existing?.GetArray(PdfName.Get(key)) is { } current)
        {
            for (int i = 0; i < current.Count; i++)
            {
                merged.Add(current[i]);
                present.Add(document.Resolve(current[i]).AsStream?.DecodedSpan().ToArray() ?? []);
            }
        }

        foreach (byte[] addition in additions)
        {
            int known = present.FindIndex(candidate => candidate.AsSpan().SequenceEqual(addition));
            if (known >= 0)
            {
                used.Add(merged[known]);
                continue;
            }

            present.Add(addition);
            PdfObjectId id = document.AddObject(PdfValue.Stream(PdfStream.FromData(addition)));
            merged.Add(PdfValue.Reference(id));
            used.Add(PdfValue.Reference(id));
        }

        return (merged, used);
    }

    private static PdfDictionary CopyRelated(PdfDocument document, PdfDictionary? existing)
    {
        var related = new PdfDictionary(2);
        document.Attach(PdfValue.Dictionary(related));

        if (existing?.GetDictionary(PdfName.Get("VRI")) is { } current)
        {
            foreach (KeyValuePair<PdfName, PdfValue> entry in current)
                related.Set(entry.Key, entry.Value);
        }

        return related;
    }

    /// <summary>
    /// The key a VRI entry hangs from: the SHA-1 of the signature's <c>Contents</c> value, padding
    /// included, in uppercase hexadecimal (§12.8.4.3, Table 261). SHA-1 here is a lookup key fixed
    /// by the standard, not a security boundary.
    /// </summary>
    private static string RelatedKey(PdfDocument document, string signatureFieldName)
    {
        if (PdfForm.Open(document)?[signatureFieldName] is not { } field ||
            field.Inherited(PdfName.Get("V")).AsDictionary is not { } signature)
        {
            throw new ArgumentException($"The document has no signed field called '{signatureFieldName}'.");
        }

        ReadOnlySpan<byte> contents = signature.Get(PdfName.Get("Contents")).AsStringBytes;
        if (contents.IsEmpty)
            throw new ArgumentException($"The field '{signatureFieldName}' carries no signature value.");

        return Convert.ToHexString(SHA1.HashData(contents));
    }

    /// <summary>An empty Cert, CRL or OCSP array is omitted, as Table 262 requires.</summary>
    private static PdfDictionary BuildRelatedEntry(
        PdfDocument document, PdfArray certificates, PdfArray crls, PdfArray ocsps)
    {
        var entry = new PdfDictionary(5);
        entry.Set(PdfName.Get("Type"), PdfValue.Name("VRI"));
        document.Attach(PdfValue.Dictionary(entry));

        if (certificates.Count > 0)
            entry.Set(PdfName.Get("Cert"), PdfValue.Array(certificates));

        if (crls.Count > 0)
            entry.Set(PdfName.Get("CRL"), PdfValue.Array(crls));

        if (ocsps.Count > 0)
            entry.Set(PdfName.Get("OCSP"), PdfValue.Array(ocsps));

        entry.Set(PdfName.Get("TU"), PdfValue.TextString(PdfDate.Format(DateTimeOffset.Now)));
        return entry;
    }
}
