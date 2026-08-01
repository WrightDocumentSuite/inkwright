using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inkwright.Cos;
using Xunit;

namespace Inkwright.Signing.Tests;

/// <summary>
/// Document timestamps of ISO 32000-2 §12.8.5: an RFC 3161 token over the whole file, held by a
/// signature field of its own. The authority answering the requests lives inside the test.
/// </summary>
public class TimestampTests
{
    [Fact]
    public void TimestampsADocument()
    {
        using var authority = new TestTimestampAuthority();
        byte[] file = TestDocuments.Build();

        byte[] stamped = PdfTimestamper.Timestamp(file, new PdfTimestampOptions { Client = authority });

        Assert.Equal(1, authority.Requests);
        Assert.True(stamped.AsSpan(0, file.Length).SequenceEqual(file),
            "A timestamp must append, never rewrite.");

        PdfSignatureInfo info = Assert.Single(PdfSignatureVerifier.Verify(stamped));
        Assert.True(info.IsDocumentTimestamp);
        Assert.True(info.IsIntact, info.Problem);
        Assert.True(info.CoversWholeDocument);
        Assert.True(info.TimestampIntact);
        Assert.NotNull(info.TimestampTime);
        Assert.True((DateTimeOffset.UtcNow - info.TimestampTime.Value).Duration() < TimeSpan.FromMinutes(5));

        using PdfDocument reopened = PdfDocument.Load(stamped);
        Assert.Equal(IO.PdfVersion.Pdf20, reopened.Version);
    }

    [Fact]
    public void WritesTheDictionaryTheStandardAsksFor()
    {
        using var authority = new TestTimestampAuthority();
        byte[] stamped = PdfTimestamper.Timestamp(TestDocuments.Build(), new PdfTimestampOptions { Client = authority });

        using PdfDocument document = PdfDocument.Load(stamped);
        Forms.PdfForm form = Forms.PdfForm.Open(document)!;
        Forms.PdfFormField field = Assert.Single(form.Fields);

        PdfDictionary timestamp = field.Inherited(PdfName.Get("V")).AsDictionary!;
        Assert.Equal("DocTimeStamp", timestamp.GetName(PdfName.Get("Type"))!.Value);
        Assert.Equal("ETSI.RFC3161", timestamp.GetName(PdfName.Get("SubFilter"))!.Value);

        // Table 255 forbids the signer-flavoured entries when the SubFilter is ETSI.RFC3161.
        Assert.False(timestamp.ContainsKey(PdfName.Get("M")));
        Assert.False(timestamp.ContainsKey(PdfName.Get("Name")));
        Assert.False(timestamp.ContainsKey(PdfName.Get("Reason")));
    }

    [Fact]
    public void TimestampsASignedFileWithoutBreakingTheSignature()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Signer Before Timestamp");
        using var authority = new TestTimestampAuthority();

        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            FieldName = "Approval",
        });

        byte[] stamped = PdfTimestamper.Timestamp(signed, new PdfTimestampOptions { Client = authority });

        IReadOnlyList<PdfSignatureInfo> results = PdfSignatureVerifier.Verify(stamped);
        Assert.Equal(2, results.Count);

        PdfSignatureInfo signature = results.Single(r => !r.IsDocumentTimestamp);
        Assert.True(signature.IsIntact, signature.Problem);
        Assert.False(signature.CoversWholeDocument);

        PdfSignatureInfo timestamp = results.Single(r => r.IsDocumentTimestamp);
        Assert.True(timestamp.IsIntact, timestamp.Problem);
        Assert.True(timestamp.CoversWholeDocument);
    }

    [Fact]
    public void NoticesTamperingUnderneathTheTimestamp()
    {
        using var authority = new TestTimestampAuthority();
        byte[] stamped = PdfTimestamper.Timestamp(TestDocuments.Build(), new PdfTimestampOptions { Client = authority });

        byte[] tampered = (byte[])stamped.Clone();
        int index = tampered.AsSpan().IndexOf("Hello"u8);
        Assert.True(index > 0);
        tampered[index] = (byte)'J';

        PdfSignatureInfo info = Assert.Single(PdfSignatureVerifier.Verify(tampered));
        Assert.False(info.IsIntact);
        Assert.False(info.IsValid);
    }

    [Fact]
    public void PicksAFreshFieldNameForEverySuccessiveTimestamp()
    {
        using var authority = new TestTimestampAuthority();

        byte[] once = PdfTimestamper.Timestamp(TestDocuments.Build(), new PdfTimestampOptions { Client = authority });
        byte[] twice = PdfTimestamper.Timestamp(once, new PdfTimestampOptions { Client = authority });

        IReadOnlyList<PdfSignatureInfo> results = PdfSignatureVerifier.Verify(twice);
        Assert.Equal(2, results.Count);
        Assert.Equal(2, results.Select(r => r.FieldName).Distinct().Count());
        Assert.All(results, r => Assert.True(r.IsIntact, r.Problem));
    }

    [Fact]
    public void ChecksTheSignatureTimestampTokenProperly()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Timestamped Signer");
        using var authority = new TestTimestampAuthority();

        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions
        {
            Certificate = certificate,
            Level = PdfSignatureLevel.Timestamped,
            TimestampClient = authority,
        });

        PdfSignatureInfo info = Assert.Single(PdfSignatureVerifier.Verify(signed));
        Assert.True(info.IsIntact, info.Problem);
        Assert.True(info.TimestampIntact, "The token was issued by the test authority and must verify.");
        Assert.NotNull(info.TimestampTime);
    }

    [Fact]
    public void ReportsNoTimestampWhenThereIsNone()
    {
        using X509Certificate2 certificate = TestDocuments.CreateCertificate("CN=Plain Signer");
        byte[] signed = TestDocuments.Sign(TestDocuments.Build(), new PdfSignatureOptions { Certificate = certificate });

        PdfSignatureInfo info = Assert.Single(PdfSignatureVerifier.Verify(signed));
        Assert.Null(info.TimestampIntact);
        Assert.Null(info.TimestampTime);
    }

    [Fact]
    public void KeepsALegitimateZeroAtTheEndOfAnAsnValue()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteOctetString([1, 2, 0]);
        byte[] encoded = writer.Encode();
        byte[] reserved = [.. encoded, .. new byte[16]];

        Assert.Equal(encoded, PdfSignatureVerifier.ReadEncodedValue(reserved));
    }

    [Fact]
    public void RefusesToStampWithoutAnAuthority()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PdfTimestamper.Timestamp(TestDocuments.Build(), new PdfTimestampOptions()));
    }
}
