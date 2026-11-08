using System.Security.Cryptography.X509Certificates;
using Inkwright.Cos;
using Inkwright.Forms;
using Xunit;

namespace Inkwright.Signing.Tests;

/// <summary>
/// Certification signatures (ISO 32000-2 §12.8.2.2) and field locks (§12.8.2.4): the author's
/// signature that says what may still change, and the per-signature freeze of named form fields.
/// </summary>
public class CertificationTests
{
    [Fact]
    public void CertifiesADocument()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Author");

        byte[] certified = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Certification",
            CertificationLevel = PdfCertificationLevel.FormFilling,
        });

        PdfSignatureInfo info = Assert.Single(PdfSignatureVerifier.Verify(certified));
        Assert.True(info.IsIntact, info.Problem);
        Assert.Equal(PdfCertificationLevel.FormFilling, info.CertificationLevel);
    }

    [Fact]
    public void WiresTheDictionariesTheStandardAsksFor()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Author");

        byte[] certified = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Certification",
            CertificationLevel = PdfCertificationLevel.NoChanges,
        });

        using PdfDocument document = PdfDocument.Load(certified);

        PdfDictionary signature = PdfForm.Open(document)!["Certification"]!
            .Inherited(PdfName.Get("V")).AsDictionary!;

        // The signature carries one reference dictionary with the DocMDP transform.
        PdfArray references = signature.GetArray(PdfName.Get("Reference"))!;
        PdfDictionary reference = references.GetDictionary(0)!;
        Assert.Equal("SigRef", reference.GetName(PdfName.Get("Type"))!.Value);
        Assert.Equal("DocMDP", reference.GetName(PdfName.Get("TransformMethod"))!.Value);
        Assert.Equal("SHA256", reference.GetName(PdfName.Get("DigestMethod"))!.Value);

        PdfDictionary parameters = reference.GetDictionary(PdfName.Get("TransformParams"))!;
        Assert.Equal(1, parameters.GetInteger(PdfName.Get("P")));
        Assert.Equal("1.2", parameters.GetName(PdfName.Get("V"))!.Value);

        // The permissions dictionary points back at the signature, which is what turns the
        // restriction from advisory into enforceable (Table 263).
        PdfDictionary permissions = document.Catalog.GetDictionary(PdfName.Get("Perms"))!;
        Assert.True(permissions.TryGetRaw(PdfName.Get("DocMDP"), out PdfValue reference2) && reference2.IsReference);
        Assert.Same(signature, document.Resolve(reference2).AsDictionary);
    }

    [Fact]
    public void RefusesASecondCertification()
    {
        using X509Certificate2 author = TestDocuments.CreateCertificate("CN=Author");
        using X509Certificate2 rival = TestDocuments.CreateCertificate("CN=Rival");

        byte[] certified = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = author,
            CertificationLevel = PdfCertificationLevel.FormFilling,
        });

        Assert.Throws<InvalidOperationException>(() => TestDocuments.Sign(certified, new PdfSignatureOptions
        {
            Certificate = rival,
            FieldName = "Second",
            CertificationLevel = PdfCertificationLevel.NoChanges,
        }));
    }

    [Fact]
    public void AllowsAnOrdinaryApprovalAfterCertification()
    {
        using X509Certificate2 author = TestDocuments.CreateCertificate("CN=Author");
        using X509Certificate2 reviewer = TestDocuments.CreateCertificate("CN=Reviewer");

        byte[] certified = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = author,
            FieldName = "Certification",
            CertificationLevel = PdfCertificationLevel.FormFillingAndAnnotations,
        });

        byte[] approved = TestDocuments.Sign(certified, new PdfSignatureOptions
        {
            Certificate = reviewer,
            FieldName = "Approval",
        });

        IReadOnlyList<PdfSignatureInfo> results = PdfSignatureVerifier.Verify(approved);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsIntact, r.Problem));

        Assert.Equal(
            PdfCertificationLevel.FormFillingAndAnnotations,
            results.Single(r => r.FieldName == "Certification").CertificationLevel);
        Assert.Null(results.Single(r => r.FieldName == "Approval").CertificationLevel);
    }

    [Fact]
    public void LocksNamedFieldsWhenSigning()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Recipient");

        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Recipient",
            FieldLock = PdfFieldLock.Include("customer.name", "customer.total"),
        });

        using PdfDocument document = PdfDocument.Load(signed);
        PdfFormField field = PdfForm.Open(document)!["Recipient"]!;

        // The lock dictionary must hang off the field as an indirect reference (Table 235).
        Assert.True(field.Dictionary.TryGetRaw(PdfName.Get("Lock"), out PdfValue lockReference) && lockReference.IsReference);
        PdfDictionary lockDictionary = document.Resolve(lockReference).AsDictionary!;
        Assert.Equal("SigFieldLock", lockDictionary.GetName(PdfName.Get("Type"))!.Value);
        Assert.Equal("Include", lockDictionary.GetName(PdfName.Get("Action"))!.Value);
        Assert.Equal(2, lockDictionary.GetArray(PdfName.Get("Fields"))!.Count);

        // The FieldMDP reference repeats the same set, because a byte range signature must keep
        // its dictionary values direct and cannot point at the lock.
        PdfDictionary signature = field.Inherited(PdfName.Get("V")).AsDictionary!;
        PdfArray references = signature.GetArray(PdfName.Get("Reference"))!;
        PdfDictionary reference = references.GetDictionary(0)!;
        Assert.Equal("FieldMDP", reference.GetName(PdfName.Get("TransformMethod"))!.Value);

        PdfDictionary parameters = reference.GetDictionary(PdfName.Get("TransformParams"))!;
        Assert.Equal("Include", parameters.GetName(PdfName.Get("Action"))!.Value);
        Assert.Equal(2, parameters.GetArray(PdfName.Get("Fields"))!.Count);

        Assert.True(reference.TryGetRaw(PdfName.Get("Data"), out PdfValue data) && data.IsReference);

        PdfSignatureInfo info = Assert.Single(PdfSignatureVerifier.Verify(signed));
        Assert.True(info.IsIntact, info.Problem);
    }

    [Fact]
    public void WritesTheLockPermissionOfPdf20()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Recipient");

        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Recipient",
            FieldLock = PdfFieldLock.All() with { Permissions = PdfCertificationLevel.NoChanges },
        });

        using PdfDocument document = PdfDocument.Load(signed);
        PdfFormField field = PdfForm.Open(document)!["Recipient"]!;
        PdfDictionary lockDictionary = document.Resolve(field.Dictionary.Get(PdfName.Get("Lock"))).AsDictionary!;

        Assert.Equal("All", lockDictionary.GetName(PdfName.Get("Action"))!.Value);
        Assert.False(lockDictionary.ContainsKey(PdfName.Get("Fields")));
        Assert.Equal(1, lockDictionary.GetInteger(PdfName.Get("P")));
        Assert.Equal(IO.PdfVersion.Pdf20, document.Version);
    }

    [Fact]
    public void RefusesAnEmptyNamedFieldLock()
    {
        Assert.Throws<ArgumentException>(() => PdfFieldLock.Include());
        Assert.Throws<ArgumentException>(() => PdfFieldLock.Exclude([]));
        Assert.Throws<ArgumentException>(() => PdfFieldLock.Include(""));
    }

    [Fact]
    public void NamesASha3DigestInTheCertificationReference()
    {
        Assert.SkipUnless(
            PdfSignatureAlgorithms.IsSupported(PdfSignatureAlgorithms.Sha3_256),
            "This machine cannot compute SHA3-256.");

        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=SHA3 Author");
        byte[] certified = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            CertificationLevel = PdfCertificationLevel.NoChanges,
            HashAlgorithm = PdfSignatureAlgorithms.Sha3_256,
        });

        using PdfDocument document = PdfDocument.Load(certified);
        PdfDictionary signature = PdfForm.Open(document)!.First().Inherited(PdfName.Get("V")).AsDictionary!;
        PdfDictionary reference = signature.GetArray(PdfName.Get("Reference"))!.GetDictionary(0)!;
        Assert.Equal("SHA3-256", reference.GetName(PdfName.Get("DigestMethod"))!.Value);
    }

    [Fact]
    public void DocMdpNoChanges_FlagsSubsequentApprovalSignature()
    {
        using X509Certificate2 author = TestDocuments.CreateCertificate("CN=Author");
        using X509Certificate2 reviewer = TestDocuments.CreateCertificate("CN=Reviewer");

        byte[] certified = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = author,
            FieldName = "Certification",
            CertificationLevel = PdfCertificationLevel.NoChanges,
        });

        byte[] modified = TestDocuments.Sign(certified, new PdfSignatureOptions
        {
            Certificate = reviewer,
            FieldName = "Approval",
        });

        IReadOnlyList<PdfSignatureInfo> results = PdfSignatureVerifier.Verify(modified);
        PdfSignatureInfo certInfo = results.Single(r => r.FieldName == "Certification");
        Assert.True(certInfo.IsIntact);
        Assert.False(certInfo.AreSubsequentChangesPermitted);
        Assert.NotEmpty(certInfo.ModificationViolations);
    }

    [Fact]
    public void DocMdpFormFilling_FlagsAddedCommentButPermitsFormFill()
    {
        using X509Certificate2 author = TestDocuments.CreateCertificate("CN=Author");

        byte[] basePdf;
        using (var doc = PdfDocument.Create())
        {
            PdfPage page = doc.Pages.Add(PageSize.A4);
            PdfForm form = PdfForm.OpenOrCreate(doc);
            form.AddTextField(page, PdfRectangle.FromSize(72, 700, 150, 20), "customer.name", "Alice");
            basePdf = doc.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        }

        byte[] certified = TestDocuments.Sign(basePdf, new PdfSignatureOptions
        {
            Certificate = author,
            FieldName = "Certification",
            CertificationLevel = PdfCertificationLevel.FormFilling,
        });

        byte[] filled;
        using (var doc = PdfDocument.Load(certified))
        {
            PdfForm.Open(doc)!.Set("customer.name", "Bob");
            filled = doc.ToIncrementalArray(new PdfSaveOptions { UseObjectStreams = false });
        }

        PdfSignatureInfo afterFill = Assert.Single(PdfSignatureVerifier.Verify(filled));
        Assert.True(afterFill.IsIntact);
        Assert.True(afterFill.AreSubsequentChangesPermitted);

        byte[] commented;
        using (var doc = PdfDocument.Load(certified))
        {
            doc.Pages[0].Annotations.AddNote(new PdfPoint(100, 600), "Unauthorized comment");
            commented = doc.ToIncrementalArray(new PdfSaveOptions { UseObjectStreams = false });
        }

        PdfSignatureInfo afterComment = Assert.Single(PdfSignatureVerifier.Verify(commented));
        Assert.True(afterComment.IsIntact);
        Assert.False(afterComment.AreSubsequentChangesPermitted);
        Assert.Contains(afterComment.ModificationViolations, v => v.Contains("annotations", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FieldMdp_DetectsLockedFieldModificationAndAllowsUnlockedField()
    {
        using X509Certificate2 signer = TestDocuments.CreateCertificate("CN=Signer");

        byte[] basePdf;
        using (var doc = PdfDocument.Create())
        {
            PdfPage page = doc.Pages.Add(PageSize.A4);
            PdfForm form = PdfForm.OpenOrCreate(doc);
            form.AddTextField(page, PdfRectangle.FromSize(72, 700, 150, 20), "customer.total", "100.00");
            form.AddTextField(page, PdfRectangle.FromSize(72, 660, 150, 20), "customer.notes", "Initial");
            basePdf = doc.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        }

        byte[] signed = TestDocuments.Sign(basePdf, new PdfSignatureOptions
        {
            Certificate = signer,
            FieldName = "Sig1",
            FieldLock = PdfFieldLock.Include("customer.total"),
        });

        byte[] unlockedEdited;
        using (var doc = PdfDocument.Load(signed))
        {
            PdfForm.Open(doc)!.Set("customer.notes", "Updated notes");
            unlockedEdited = doc.ToIncrementalArray(new PdfSaveOptions { UseObjectStreams = false });
        }

        PdfSignatureInfo okInfo = Assert.Single(PdfSignatureVerifier.Verify(unlockedEdited));
        Assert.NotNull(okInfo.FieldLock);
        Assert.True(okInfo.FieldLock!.IsFieldLocked("customer.total"));
        Assert.False(okInfo.FieldLock.IsFieldLocked("customer.notes"));
        Assert.True(okInfo.AreSubsequentChangesPermitted);

        byte[] lockedEdited;
        using (var doc = PdfDocument.Load(signed))
        {
            PdfForm.Open(doc)!.Set("customer.total", "999.99");
            lockedEdited = doc.ToIncrementalArray(new PdfSaveOptions { UseObjectStreams = false });
        }

        PdfSignatureInfo badInfo = Assert.Single(PdfSignatureVerifier.Verify(lockedEdited));
        Assert.True(badInfo.IsIntact);
        Assert.False(badInfo.AreSubsequentChangesPermitted);
        Assert.Contains(badInfo.ModificationViolations, v => v.Contains("customer.total", StringComparison.Ordinal));
    }
}
