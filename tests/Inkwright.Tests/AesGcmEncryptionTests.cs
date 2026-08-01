using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Security;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Encryption in Galois/Counter mode, which ISO/TS 32003 adds to PDF 2.0. Unlike cipher block
/// chaining it authenticates what it conceals, so a file somebody has meddled with refuses to
/// decrypt instead of decrypting to something plausible and wrong.
/// </summary>
public class AesGcmEncryptionTests
{
    [Fact]
    public void WritesTheVersionAndMethodTheSpecificationNames()
    {
        byte[] file = Encrypted(gcm: true);

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "secret" });
        PdfDictionary encrypt = reopened.Resolve(reopened.Trailer.Get(PdfNames.Encrypt)).AsDictionary!;

        Assert.Equal(6, encrypt.GetInteger(PdfNames.V));
        Assert.Equal(7, encrypt.GetInteger(PdfNames.R));

        PdfDictionary filters = reopened.Resolve(encrypt.Get(PdfNames.CF)).AsDictionary!;
        PdfDictionary standard = reopened.Resolve(filters.Get(PdfNames.StdCF)).AsDictionary!;

        Assert.Equal("AESV4", standard.GetName(PdfNames.CFM)?.Value);
        Assert.Equal(32, standard.GetInteger(PdfName.Get("Length")));
    }

    [Fact]
    public void KeepsWritingTheWidelyReadableModeByDefault()
    {
        byte[] file = Encrypted(gcm: false);

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "secret" });
        PdfDictionary encrypt = reopened.Resolve(reopened.Trailer.Get(PdfNames.Encrypt)).AsDictionary!;

        Assert.Equal(5, encrypt.GetInteger(PdfNames.V));
        Assert.Equal(6, encrypt.GetInteger(PdfNames.R));
    }

    [Fact]
    public void ReadsBackWhatItWrote()
    {
        byte[] file = Encrypted(gcm: true);

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "secret" });

        Assert.True(reopened.WasEncrypted);
        Assert.Equal("A confidential heading", reopened.Info.Title);
        Assert.Contains("confidential", reopened.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void OpensWithTheOwnerPasswordToo()
    {
        byte[] file = Encrypted(gcm: true);

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "master" });

        Assert.Contains("confidential", reopened.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTheWrongPassword()
    {
        byte[] file = Encrypted(gcm: true);

        Assert.Throws<PdfPasswordException>(
            () => PdfDocument.Load(file, new IO.PdfReadOptions { Password = "wrong" }));
    }

    [Fact]
    public void DeclaresTheSpecificationThatAddsTheMode()
    {
        byte[] file = Encrypted(gcm: true);

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "secret" });
        PdfDictionary extensions = reopened.Resolve(
            reopened.Catalog.Get(PdfName.Get("Extensions"))).AsDictionary!;

        PdfArray declared = reopened.Resolve(extensions.Get(PdfName.Get("ISO_"))).AsArray!;
        PdfDictionary entry = reopened.Resolve(declared[0]).AsDictionary!;

        Assert.Equal("DeveloperExtensions", entry.GetName(PdfNames.Type)?.Value);
        Assert.Equal("2.0", entry.GetName(PdfName.Get("BaseVersion"))?.Value);
        Assert.Equal(32003, entry.GetInteger(PdfName.Get("ExtensionLevel")));

        // The leading colon is part of the revision identifier, not a separator.
        Assert.Equal(":2023", entry.GetTextString(PdfName.Get("ExtensionRevision")));
    }

    [Fact]
    public void NoticesWhenSomebodyHasTamperedWithTheCiphertext()
    {
        StandardSecurityHandler handler = StandardSecurityHandler.CreateAes256(
            "secret", string.Empty, PdfPermissions.All, encryptMetadata: true, out _, galoisCounterMode: true);

        var id = new PdfObjectId(4, 0);
        byte[] cipher = handler.EncryptStream(id, "the quick brown fox"u8.ToArray());

        // Flip one bit in the middle of the ciphertext. This is the whole point of the mode: the
        // tag no longer matches, so decryption refuses rather than producing altered plaintext.
        byte[] altered = [.. cipher];
        altered[16] ^= 0x01;

        Assert.Throws<PdfTamperedException>(() => handler.DecryptStream(id, altered));

        // Meddling with the tag itself is caught just the same.
        byte[] retagged = [.. cipher];
        retagged[^1] ^= 0x01;

        Assert.Throws<PdfTamperedException>(() => handler.DecryptStream(id, retagged));

        // So is a truncated object, which is too short to hold a vector and a tag at all.
        Assert.Throws<PdfTamperedException>(() => handler.DecryptStream(id, cipher.AsSpan(0, 8).ToArray()));

        // The untouched ciphertext still comes back, so the check is not simply always failing.
        Assert.Equal("the quick brown fox"u8.ToArray(), handler.DecryptStream(id, cipher));
    }

    [Fact]
    public void SerialisesTheNonceAndTagWhereTheSpecificationSaysToPutThem()
    {
        StandardSecurityHandler handler = StandardSecurityHandler.CreateAes256(
            "secret", "master", PdfPermissions.All, encryptMetadata: true, out _, galoisCounterMode: true);

        byte[] plain = "the quick brown fox"u8.ToArray();
        byte[] cipher = handler.EncryptStream(new PdfObjectId(1, 0), plain);

        // Twelve bytes of nonce, then the ciphertext, then a sixteen-byte tag (clause 5.2).
        Assert.Equal(plain.Length + 12 + 16, cipher.Length);
        Assert.Equal(plain, handler.DecryptStream(new PdfObjectId(1, 0), cipher));
    }

    [Fact]
    public void NeverRepeatsAnInitialisationVector()
    {
        StandardSecurityHandler handler = StandardSecurityHandler.CreateAes256(
            "secret", string.Empty, PdfPermissions.All, encryptMetadata: true, out _, galoisCounterMode: true);

        // The key is shared across objects, so a repeated nonce would give away the key stream.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        byte[] plain = "same text every time"u8.ToArray();

        for (int i = 0; i < 500; i++)
        {
            byte[] cipher = handler.EncryptStream(new PdfObjectId(i + 1, 0), plain);
            Assert.True(seen.Add(Convert.ToHexString(cipher.AsSpan(0, 12))), "An initialisation vector repeated.");
        }
    }

    private static byte[] Encrypted(bool gcm)
    {
        using var document = PdfDocument.Create();
        document.Info.Title = "A confidential heading";

        PdfPage page = document.Pages.Add(PageSize.A4);
        using (Content.ContentCanvas canvas = Content.ContentCanvas.ForPage(page))
        {
            canvas.DrawText("This text is confidential.", 72, 700, document.Fonts.Helvetica, 12);
            canvas.Commit();
        }

        using var buffer = new MemoryStream();
        document.Save(buffer, new PdfSaveOptions
        {
            Encryption = new PdfEncryptionSettings
            {
                UserPassword = "secret",
                OwnerPassword = "master",
                UseGaloisCounterMode = gcm,
            },
        });

        return buffer.ToArray();
    }
}
