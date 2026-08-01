using System.Security.Cryptography;
using Inkwright.Cos;
using Inkwright.Security;
using Xunit;

namespace Inkwright.Tests;

public sealed class CryptFilterTests
{
    [Fact]
    public void ExplicitIdentityCryptFilterLeavesAStreamInTheClear()
    {
        StandardSecurityHandler handler = Handler();
        byte[] clear = "clear stream"u8.ToArray();
        var parameters = new PdfDictionary
        {
            [PdfNames.Name] = PdfValue.Name(PdfNames.Identity),
        };
        var stream = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfNames.Crypt),
            [PdfNames.DecodeParms] = PdfValue.Dictionary(parameters),
        };

        Assert.Equal(clear, handler.DecryptStream(new PdfObjectId(7, 0), clear, stream));
    }

    [Fact]
    public void ExplicitNamedCryptFilterSelectsItsDeclaredCipher()
    {
        StandardSecurityHandler handler = Handler();
        var id = new PdfObjectId(8, 0);
        byte[] clear = "named encrypted stream"u8.ToArray();
        byte[] cipher = handler.EncryptStream(id, clear);
        var parameters = new PdfDictionary
        {
            [PdfNames.Name] = PdfValue.Name(PdfNames.StdCF),
        };
        var stream = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfNames.Crypt),
            [PdfNames.DecodeParms] = PdfValue.Dictionary(parameters),
        };

        Assert.Equal(clear, handler.DecryptStream(id, cipher, stream));
    }

    [Fact]
    public void EmbeddedFileUsesEffInsteadOfTheDefaultStreamFilter()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        StandardSecurityHandler handler = StandardSecurityHandler.FromFileKey(
            key,
            CryptMethod.AesV3,
            CryptMethod.AesV3,
            encryptMetadata: true,
            revision: 6,
            ownerAccess: true,
            permissionBits: -1,
            embeddedFileMethod: CryptMethod.None,
            cryptFilters: new Dictionary<PdfName, CryptMethod>());
        byte[] clear = "unencrypted attachment"u8.ToArray();
        var stream = new PdfDictionary
        {
            [PdfNames.Type] = PdfValue.Name(PdfNames.EmbeddedFile),
        };

        Assert.Equal(clear, handler.DecryptStream(new PdfObjectId(9, 0), clear, stream));
    }

    private static StandardSecurityHandler Handler()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        return StandardSecurityHandler.FromFileKey(
            key,
            CryptMethod.AesV3,
            CryptMethod.AesV3,
            encryptMetadata: true,
            revision: 6,
            ownerAccess: true,
            permissionBits: -1,
            cryptFilters: new Dictionary<PdfName, CryptMethod>
            {
                [PdfNames.StdCF] = CryptMethod.AesV3,
            });
    }
}
