using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Security;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Public-key encryption of ISO 32000-2 §7.6.5: the file key is wrapped for named certificate
/// holders instead of being derived from a password. Only a recipient's private key opens it.
/// </summary>
public class PublicKeyEncryptionTests
{
    [Fact]
    public void EncryptsForARecipientAndReadsItBack()
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Recipient");
        byte[] file = EncryptFor(recipient);

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { DecryptionCertificate = recipient });

        Assert.True(reopened.WasEncrypted);
        Assert.Equal("A confidential heading", reopened.Info.Title);
        Assert.Contains("confidential", reopened.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void WritesThePublicKeyDictionaryTheStandardDescribes()
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Recipient");
        byte[] file = EncryptFor(recipient);

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { DecryptionCertificate = recipient });
        PdfDictionary encrypt = reopened.Resolve(reopened.Trailer.Get(PdfNames.Encrypt)).AsDictionary!;

        Assert.Equal("Adobe.PubSec", encrypt.GetName(PdfNames.Filter)!.Value);
        Assert.Equal("adbe.pkcs7.s5", encrypt.GetName(PdfNames.SubFilter)!.Value);
        Assert.Equal(5, encrypt.GetInteger(PdfNames.V));

        // The recipient list lives in the crypt filter dictionary for the s5 sub-filter.
        PdfDictionary filters = reopened.Resolve(encrypt.Get(PdfNames.CF)).AsDictionary!;
        PdfDictionary filter = reopened.Resolve(filters.Get(PdfName.Get("DefaultCryptFilter"))).AsDictionary!;
        Assert.Equal("AESV3", filter.GetName(PdfNames.CFM)!.Value);
        PdfArray recipients = filter.GetArray(PdfName.Get("Recipients"))!;
        Assert.Single(recipients);
        Assert.False(recipients.Get(0).AsStringBytes.IsEmpty);
    }

    [Fact]
    public void OpensForAnyOneOfSeveralRecipients()
    {
        using X509Certificate2 first = CreateRecipient("CN=First Recipient");
        using X509Certificate2 second = CreateRecipient("CN=Second Recipient");

        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4).SetContent("BT /F1 12 Tf 72 700 Td (Shared) Tj ET"u8.ToArray());

        byte[] file = document.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            Encryption = new PdfEncryptionSettings { Recipients = [first, second] },
        });

        // Either recipient's private key unwraps the same file key.
        using PdfDocument asFirst = PdfDocument.Load(file, new IO.PdfReadOptions { DecryptionCertificate = first });
        using PdfDocument asSecond = PdfDocument.Load(file, new IO.PdfReadOptions { DecryptionCertificate = second });

        Assert.True(asFirst.WasEncrypted);
        Assert.True(asSecond.WasEncrypted);
    }

    [Fact]
    public void RefusesAStrangerCertificate()
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Recipient");
        using X509Certificate2 stranger = CreateRecipient("CN=Stranger");
        byte[] file = EncryptFor(recipient);

        // A certificate the document was not encrypted for cannot open it, and with recovery off
        // that is an exception rather than a silent pile of ciphertext.
        Assert.Throws<Diagnostics.PdfPasswordException>(() =>
            PdfDocument.Load(file, new IO.PdfReadOptions { DecryptionCertificate = stranger }));
    }

    [Fact]
    public void RoundTripsTheContentUnchanged()
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Recipient");
        byte[] file = EncryptFor(recipient);

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { DecryptionCertificate = recipient });

        // Re-saving in the clear must give a readable document: proof the streams decrypted.
        byte[] clear = reopened.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        using PdfDocument plain = PdfDocument.Load(clear);
        Assert.False(plain.WasEncrypted);
        Assert.Contains("confidential", plain.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesThePermissionsFromTheMatchingRecipientEnvelope()
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Restricted Recipient");
        PdfPermissions requested = PdfPermissions.Print | PdfPermissions.FillForms;
        byte[] file = EncryptFor(recipient, requested);

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { DecryptionCertificate = recipient });

        Assert.Equal(requested | PdfPermissions.ExtractForAccessibility, reopened.SourcePermissions);
    }

    [Fact]
    public void TakesEncryptMetadataFromTheReferencedCryptFilter()
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Recipient");
        byte[] file = EncryptFor(recipient, encryptMetadata: false);

        // Successful opening proves key derivation used the filter-level false value: omitting its
        // four-byte marker would produce a different file key and make every object unreadable.
        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { DecryptionCertificate = recipient });
        PdfDictionary encrypt = reopened.Resolve(reopened.Trailer.Get(PdfNames.Encrypt)).AsDictionary!;
        PdfDictionary filter = encrypt.GetDictionary(PdfNames.CF)!.GetDictionary(PdfNames.DefaultCryptFilter)!;
        Assert.False(filter.GetBoolean(PdfNames.EncryptMetadata, true));
        Assert.False(encrypt.ContainsKey(PdfNames.EncryptMetadata));
    }

    [Fact]
    public void CarriesTheMacRequirementInsideTheRecipientEnvelope()
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Protected Recipient");
        byte[] file = EncryptFor(recipient, addIntegrityCode: true);

        using PdfDocument reopened = PdfDocument.Load(
            file, new IO.PdfReadOptions { DecryptionCertificate = recipient });
        PdfMacIntegrity integrity = PdfMacIntegrity.Read(reopened);

        Assert.True(reopened.Reader!.Security!.RequiresMac);
        Assert.True(integrity.IsRequired);
        Assert.True(integrity.IsVerified, integrity.VerificationNote);
    }

    [Fact]
    public void OpensAttachmentOnlyEncryptionThroughEff()
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Attachment Recipient");
        _ = PublicKeySecurityHandler.Create(
            [recipient], PdfPermissions.All, encryptMetadata: true, out PdfDictionary encrypt);
        encrypt.Set(PdfNames.StmF, PdfValue.Name(PdfNames.Identity));
        encrypt.Set(PdfNames.StrF, PdfValue.Name(PdfNames.Identity));
        encrypt.Set(PdfNames.EFF, PdfValue.Name(PdfNames.DefaultCryptFilter));

        Assert.True(PublicKeySecurityHandler.TryOpen(
            encrypt, recipient, out StandardSecurityHandler? opened, out string? failure), failure);
        Assert.NotNull(opened);
        Assert.Equal(CryptMethod.None, opened.StreamMethod);
        Assert.Equal(CryptMethod.None, opened.StringMethod);
        Assert.Equal(CryptMethod.AesV3, opened.EmbeddedFileMethod);
    }

    [Fact]
    public void ResolvesAnIndirectCryptFilterDefinition()
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Indirect Filter Recipient");
        _ = PublicKeySecurityHandler.Create(
            [recipient], PdfPermissions.All, encryptMetadata: true, out PdfDictionary encrypt);
        PdfDictionary filters = encrypt.GetDictionary(PdfNames.CF)!;
        PdfDictionary definition = filters.GetDictionary(PdfNames.DefaultCryptFilter)!;

        using var owner = PdfDocument.Create();
        PdfObjectId definitionId = owner.AddObject(PdfValue.Dictionary(definition));
        filters.Set(PdfNames.DefaultCryptFilter, PdfValue.Reference(definitionId));
        owner.Attach(PdfValue.Dictionary(encrypt));

        Assert.True(PublicKeySecurityHandler.TryOpen(
            encrypt, recipient, out StandardSecurityHandler? opened, out string? failure), failure);
        Assert.Equal(CryptMethod.AesV3, opened!.StreamMethod);
    }

    /// <summary>
    /// ISO 32000-2 Table 20 fixes V=1 at 40 bits. For V=2 and V=3, Length defaults to 40 and may
    /// select any multiple of eight from 40 through 128. A tolerant reader falls back to 40 when a
    /// non-conforming producer writes any other value.
    /// </summary>
    [Theory]
    [InlineData(1, null, 40)]
    [InlineData(1, 128, 40)]
    [InlineData(2, null, 40)]
    [InlineData(3, null, 40)]
    [InlineData(2, 56, 56)]
    [InlineData(3, 128, 128)]
    [InlineData(2, 55, 40)]
    [InlineData(3, 32, 40)]
    [InlineData(2, 136, 40)]
    public void OpensLegacyPublicKeyEncryptionWithTheSpecifiedKeyLength(
        int version,
        int? declaredLength,
        int expectedLength)
    {
        using X509Certificate2 recipient = CreateRecipient("CN=Legacy Recipient");
        byte[] seed = Enumerable.Range(1, 20).Select(value => (byte)value).ToArray();
        byte[] content = new byte[24];
        seed.CopyTo(content, 0);
        BinaryPrimitives.WriteInt32BigEndian(content.AsSpan(20), unchecked((int)0xFFFFF0C2));

        var envelope = new EnvelopedCms(new ContentInfo(content));
        envelope.Encrypt(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, recipient));
        byte[] recipientCms = envelope.Encode();

        var encrypt = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfName.Get("Adobe.PubSec")),
            [PdfNames.SubFilter] = PdfValue.Name(PdfName.Get("adbe.pkcs7.s3")),
            [PdfNames.V] = PdfValue.Integer(version),
            [PdfName.Get("Recipients")] = PdfValue.Array(new PdfArray(PdfValue.String(recipientCms))),
        };
        if (declaredLength is not null)
            encrypt.Set(PdfNames.Length, PdfValue.Integer(declaredLength.Value));

        Assert.True(PublicKeySecurityHandler.TryOpen(
            encrypt, recipient, out StandardSecurityHandler? opened, out string? failure), failure);

        byte[] expected = SHA1.HashData([.. seed, .. recipientCms])[..(expectedLength / 8)];
        Assert.Equal(expected, opened!.FileKey.ToArray());
        Assert.Equal(CryptMethod.Rc4, opened.StreamMethod);
    }

    private static byte[] EncryptFor(
        X509Certificate2 recipient,
        PdfPermissions permissions = PdfPermissions.All,
        bool encryptMetadata = true,
        bool addIntegrityCode = false)
    {
        using var document = PdfDocument.Create();
        document.Info.Title = "A confidential heading";

        PdfPage page = document.Pages.Add(PageSize.A4);
        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("This text is confidential.", 72, 700, document.Fonts.Helvetica, 12);
            canvas.Commit();
        }

        return document.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            AddIntegrityCode = addIntegrityCode,
            Encryption = new PdfEncryptionSettings
            {
                Recipients = [recipient],
                Permissions = permissions,
                EncryptMetadata = encryptMetadata,
            },
        });
    }

    private static X509Certificate2 CreateRecipient(string subject)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }
}
