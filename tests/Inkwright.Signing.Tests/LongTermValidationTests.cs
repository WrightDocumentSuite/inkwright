using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inkwright.Cos;
using Xunit;

namespace Inkwright.Signing.Tests;

/// <summary>
/// The document security store of ISO 32000-2 §12.8.4: certificates and revocation answers stored
/// next to the signatures they will one day validate, in an incremental update that leaves the
/// signed bytes alone.
/// </summary>
public class LongTermValidationTests
{
    [Fact]
    public void StoresValidationMaterialWithoutBreakingTheSignature()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=LTV Signer");
        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Approval",
        });

        var data = new PdfValidationData();
        data.Certificates.Add(certificate);
        data.CertificateRevocationLists.Add([1, 2, 3, 4]);

        byte[] extended = PdfLongTermValidation.Add(signed, "Approval", data);

        Assert.True(extended.AsSpan(0, signed.Length).SequenceEqual(signed),
            "Validation data must append, never rewrite: the signature covers the original bytes.");

        PdfSignatureInfo info = Assert.Single(PdfSignatureVerifier.Verify(extended));
        Assert.True(info.IsIntact, info.Problem);
    }

    [Fact]
    public void LaysTheStoreOutTheWayTheStandardDraws()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=DSS Layout");
        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Approval",
        });

        var data = new PdfValidationData();
        data.Certificates.Add(certificate);
        data.OcspResponses.Add([9, 9, 9]);

        byte[] extended = PdfLongTermValidation.Add(signed, "Approval", data);
        using PdfDocument document = PdfDocument.Load(extended);

        PdfDictionary store = document.Catalog.GetDictionary(PdfName.Get("DSS"))!;
        Assert.Equal("DSS", store.GetName(PdfName.Get("Type"))!.Value);
        Assert.Equal(IO.PdfVersion.Pdf20, document.Version);

        // The certificate went in as a stream of its DER bytes.
        PdfArray certificates = store.GetArray(PdfName.Get("Certs"))!;
        PdfStream stored = document.Resolve(certificates[0]).AsStream!;
        Assert.True(stored.DecodedSpan().SequenceEqual(certificate.RawData));

        // The VRI key is the uppercase SHA-1 of the signature's Contents value, padding included.
        PdfDictionary signature = Forms.PdfForm.Open(document)!["Approval"]!
            .Inherited(PdfName.Get("V")).AsDictionary!;
        string expectedKey = Convert.ToHexString(
            SHA1.HashData(signature.Get(PdfName.Get("Contents")).AsStringBytes));

        PdfDictionary related = store.GetDictionary(PdfName.Get("VRI"))!;
        PdfDictionary entry = related.GetDictionary(PdfName.Get(expectedKey))!;
        Assert.Equal("VRI", entry.GetName(PdfName.Get("Type"))!.Value);
        Assert.Single(entry.GetArray(PdfName.Get("Cert"))!);
        Assert.Single(entry.GetArray(PdfName.Get("OCSP"))!);
        Assert.False(entry.ContainsKey(PdfName.Get("CRL")), "An empty CRL selection is omitted (Table 262).");
        Assert.NotNull(entry.GetTextString(PdfName.Get("TU")));
    }

    [Fact]
    public void StoresASharedBlobOnlyOnce()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Shared Chain");
        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "First",
        });

        var data = new PdfValidationData();
        data.Certificates.Add(certificate);

        byte[] once = PdfLongTermValidation.Add(signed, "First", data);
        byte[] twice = PdfLongTermValidation.Add(once, "First", data);

        using PdfDocument document = PdfDocument.Load(twice);
        PdfDictionary store = document.Catalog.GetDictionary(PdfName.Get("DSS"))!;

        Assert.Single(store.GetArray(PdfName.Get("Certs"))!);

        // The VRI entry of the second round still points at the shared stream.
        PdfDictionary related = store.GetDictionary(PdfName.Get("VRI"))!;
        foreach (KeyValuePair<PdfName, PdfValue> entry in related)
        {
            PdfArray certs = document.Resolve(entry.Value).AsDictionary!.GetArray(PdfName.Get("Cert"))!;
            Assert.Single(certs);
        }
    }

    [Fact]
    public void RefusesToStoreNothing()
    {
        byte[] file = TestDocuments.Build();
        Assert.Throws<ArgumentException>(() => PdfLongTermValidation.Add(file, new PdfValidationData()));
    }

    [Fact]
    public void RefusesAFieldThatIsNotSigned()
    {
        byte[] file = TestDocuments.Build();

        var data = new PdfValidationData();
        data.CertificateRevocationLists.Add([1]);

        Assert.Throws<ArgumentException>(() => PdfLongTermValidation.Add(file, "Nowhere", data));
    }

    [Fact]
    public void ReachesLongTermArchivalByComposition()
    {
        // The B-LTA structure is a pipeline: sign with a timestamp, store caller-validated
        // collateral, and seal the whole thing with a document timestamp.
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Archival Signer");
        using var authority = new TestTimestampAuthority();

        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Contract",
            Level = PdfSignatureLevel.Timestamped,
            TimestampClient = authority,
        });

        var data = new PdfValidationData();
        data.Certificates.Add(certificate);
        byte[] extended = PdfLongTermValidation.Add(signed, "Contract", data);

        byte[] archived = PdfTimestamper.Timestamp(extended, new PdfTimestampOptions { Client = authority });

        IReadOnlyList<PdfSignatureInfo> results = PdfSignatureVerifier.Verify(archived);
        Assert.Equal(2, results.Count);

        PdfSignatureInfo signature = results.Single(r => !r.IsDocumentTimestamp);
        Assert.True(signature.IsIntact, signature.Problem);
        Assert.True(signature.TimestampIntact);

        PdfSignatureInfo seal = results.Single(r => r.IsDocumentTimestamp);
        Assert.True(seal.IsIntact, seal.Problem);
        Assert.True(seal.CoversWholeDocument);

        using PdfDocument document = PdfDocument.Load(archived);
        Assert.NotNull(document.Catalog.GetDictionary(PdfName.Get("DSS")));
    }
}
