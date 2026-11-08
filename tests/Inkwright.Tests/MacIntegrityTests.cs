using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Inkwright.Security;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Integrity protection as ISO/TS 32004 defines it. Encryption alone conceals a document without
/// vouching for it; the message authentication code closes that gap, keyed from the file encryption
/// key, so a code that checks out proves whoever wrote it knew the password.
/// </summary>
public class MacIntegrityTests
{
    /// <summary>The password the reference documents in the corpus were written with.</summary>
    private const string CorpusPassword = "123";

    [Fact]
    public void ReportsNothingForADocumentWithoutIntegrityProtection()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);

        PdfMacIntegrity integrity = PdfMacIntegrity.Read(document);

        Assert.False(integrity.IsPresent);
        Assert.False(integrity.IsVerified);
        Assert.Equal(PdfMacLocation.None, integrity.Location);
    }

    [Fact]
    public void VerifiesTheReferenceDocumentsOfAnotherImplementation()
    {
        int authentic = 0;
        int verified = 0;

        foreach (PdfMacIntegrity integrity in Protected())
        {
            if (integrity.IsAuthentic)
                authentic++;

            if (integrity.IsVerified)
                verified++;
        }

        Assert.SkipUnless(authentic + verified > 0, "The corpus of integrity-protected documents is not present.");

        // Authenticating means the whole chain came out right: the key derivation, the key
        // unwrapping, and the code over the authenticated attributes.
        Assert.True(authentic >= 40, $"Only {authentic} documents authenticated.");
        Assert.True(verified >= 25, $"Only {verified} documents verified completely.");
    }

    [Fact]
    public void FindsTokensInBothOfThePlacesTheStandardAllows()
    {
        var places = new HashSet<PdfMacLocation>();

        foreach (PdfMacIntegrity integrity in Protected())
        {
            if (integrity.IsAuthentic)
                places.Add(integrity.Location);
        }

        Assert.SkipUnless(places.Count > 0, "The corpus of integrity-protected documents is not present.");

        // A standalone token sits in the trailer; a token in a signed revision has to hide among
        // the signature's unsigned attributes so as not to disturb what the signature covers.
        Assert.Contains(PdfMacLocation.Standalone, places);
        Assert.Contains(PdfMacLocation.AttachedToSignature, places);
    }

    [Fact]
    public void SaysWhyWhenADocumentCannotBeChecked()
    {
        // Two of the reference documents are deliberate negatives: one omits the salt the key
        // derivation needs, the other declares protection and attaches no token.
        var reasons = new List<string>();

        foreach (string name in new[] { "noSaltTest.pdf", "signedMacProtectedDocWithoutAttribute.pdf" })
        {
            string? path = Corpus.Matching(name, 1).FirstOrDefault();
            if (path is null)
                continue;

            using PdfDocument document = Open(path);
            PdfMacIntegrity integrity = PdfMacIntegrity.Read(document);

            Assert.True(integrity.IsPresent);
            Assert.False(integrity.IsAuthentic);
            Assert.NotNull(integrity.VerificationNote);
            reasons.Add(integrity.VerificationNote!);
        }

        Assert.SkipUnless(reasons.Count > 0, "The negative reference documents are not present.");
        Assert.All(reasons, reason => Assert.NotEmpty(reason));
    }

    [Fact]
    public void VerifiesTheCodeItWritesItself()
    {
        byte[] file = Sealed();

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "open-me" });
        PdfMacIntegrity integrity = PdfMacIntegrity.Read(reopened);

        Assert.True(integrity.IsPresent);
        Assert.Equal(PdfMacLocation.Standalone, integrity.Location);
        Assert.True(integrity.IsAuthentic, integrity.VerificationNote);
        Assert.True(integrity.MatchesContent, integrity.VerificationNote);
        Assert.True(integrity.CoversWholeFile, integrity.CoverageNote);
        Assert.True(integrity.IsVerified);
    }

    [Fact]
    public void NoticesWhenTheSealedFileIsAlteredAfterwards()
    {
        byte[] file = Sealed();

        // Flip a byte inside the stretch the code actually covers. The space reserved for the code
        // itself is deliberately outside that stretch, so a change there would prove nothing.
        using (PdfDocument original = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "open-me" }))
        {
            (long offset, long length) = PdfMacIntegrity.Read(original).Coverage[0];
            file[offset + (length / 2)] ^= 0x01;
        }

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "open-me" });
        PdfMacIntegrity integrity = PdfMacIntegrity.Read(reopened);

        Assert.True(integrity.IsAuthentic, "The token itself should still be genuine.");
        Assert.False(integrity.MatchesContent);
        Assert.False(integrity.IsVerified);
        Assert.Contains("changed", integrity.VerificationNote!, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAPayloadThatNoLongerMatchesTheAuthenticatedMessageDigest()
    {
        byte[] fileKey = RandomNumberGenerator.GetBytes(32);
        byte[] salt = RandomNumberGenerator.GetBytes(PdfMacWriter.SaltLength);
        byte[] originalDigest = SHA256.HashData("original document"u8);
        byte[] replacementDigest = SHA256.HashData("altered document"u8);
        byte[] token = PdfMacWriter.Build(fileKey, salt, originalDigest, []);

        // The payload sits outside the HMAC input. An attacker can replace its dataDigest without
        // the file key; the mandatory CMS messageDigest is what has to detect that substitution.
        int at = token.AsSpan().IndexOf(originalDigest);
        Assert.True(at >= 0);
        replacementDigest.CopyTo(token.AsSpan(at));

        PdfMacToken parsed = Assert.IsType<PdfMacToken>(PdfMacToken.Read(token));
        Assert.True(parsed.VerifyMac(fileKey, salt), "The HMAC itself was deliberately left untouched.");
        Assert.False(parsed.VerifyContentDigest());
    }

    [Fact]
    public void ReportsWhenARequiredAuthenticationDictionaryWasStripped()
    {
        byte[] file = Sealed();
        int at = file.AsSpan().LastIndexOf("/AuthCode"u8);
        Assert.True(at >= 0);
        file[at + 1] = (byte)'X';

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "open-me" });
        PdfMacIntegrity integrity = PdfMacIntegrity.Read(reopened);

        Assert.True(integrity.IsRequired);
        Assert.False(integrity.IsPresent);
        Assert.False(integrity.IsVerified);
        Assert.Contains("requires", integrity.VerificationNote!, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotCallAMalformedByteRangeWholeFileCoverage()
    {
        byte[] file = Sealed();
        int at = file.AsSpan().LastIndexOf("/ByteRange"u8);
        Assert.True(at >= 0);
        while (at < file.Length && file[at] != (byte)'0')
            at++;
        Assert.True(at < file.Length);
        file[at] = (byte)'1';

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "open-me" });
        PdfMacIntegrity integrity = PdfMacIntegrity.Read(reopened);

        Assert.False(integrity.CoversWholeFile);
        Assert.False(integrity.IsVerified);
        Assert.Contains("exactly", integrity.CoverageNote!, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAPublicPermissionWordThatDisagreesWithEncryptedPerms()
    {
        byte[] file = Sealed();
        string text = Encoding.Latin1.GetString(file);
        int permsAt = text.IndexOf("/Perms", StringComparison.Ordinal);
        Assert.True(permsAt >= 0);
        int permissionAt = text.LastIndexOf("/P ", permsAt, StringComparison.Ordinal);
        Assert.True(permissionAt >= 0);
        Match match = Regex.Match(text[permissionAt..permsAt], @"/P\s+(-?\d+)");
        Assert.True(match.Success);

        int lastDigit = permissionAt + match.Groups[1].Index + match.Groups[1].Length - 1;
        file[lastDigit] = file[lastDigit] == (byte)'0' ? (byte)'1' : (byte)'0';

        Assert.Throws<Diagnostics.PdfPasswordException>(() =>
            PdfDocument.Load(file, new IO.PdfReadOptions { Password = "open-me" }));
    }

    [Fact]
    public void WritesWhatTheSpecificationAsksForAroundTheCode()
    {
        byte[] file = Sealed();

        using PdfDocument reopened = PdfDocument.Load(file, new IO.PdfReadOptions { Password = "open-me" });

        Cos.PdfDictionary encrypt = reopened.Resolve(
            reopened.Trailer.Get(Cos.PdfNames.Encrypt)).AsDictionary!;

        // A thirty-two byte salt for the key derivation.
        Assert.Equal(32, encrypt.Get(Cos.PdfName.Get("KDFSalt")).AsStringBytes.Length);

        // The thirteenth permission bit reads as zero when a code is required, which is the
        // opposite of how permission bits usually read.
        Assert.Equal(0, encrypt.GetInteger(Cos.PdfNames.P) & (1 << 12));
        Assert.True(reopened.Reader!.Security!.RequiresMac);

        Cos.PdfDictionary extensions = reopened.Resolve(
            reopened.Catalog.Get(Cos.PdfName.Get("Extensions"))).AsDictionary!;

        Cos.PdfArray declared = reopened.Resolve(extensions.Get(Cos.PdfName.Get("ISO_"))).AsArray!;
        var levels = new List<long>();

        for (int i = 0; i < declared.Count; i++)
            levels.Add(reopened.Resolve(declared[i]).AsDictionary!.GetInteger(Cos.PdfName.Get("ExtensionLevel")));

        Assert.Contains(32004L, levels);
    }

    [Fact]
    public void LeavesAnUnencryptedFileAlone()
    {
        // There is no key to derive from without encryption, so the request has nothing to act on.
        using var document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);

        byte[] file = document.ToArray(new PdfSaveOptions { AddIntegrityCode = true });

        using PdfDocument reopened = PdfDocument.Load(file);
        Assert.False(PdfMacIntegrity.Read(reopened).IsPresent);
    }

    [Fact]
    public void UnwrapsAKeyTheWayTheKeyWrapStandardSpecifies()
    {
        // The test vector of IETF RFC 3394 section 4.6: a 256-bit key under a 256-bit key.
        byte[] kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F");
        byte[] expected = Convert.FromHexString(
            "28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21");

        byte[] wrapped = AesKeyWrap.Wrap(kek, key);
        Assert.Equal(expected, wrapped);

        Assert.True(AesKeyWrap.TryUnwrap(kek, wrapped, out byte[] unwrapped));
        Assert.Equal(key, unwrapped);
    }

    [Fact]
    public void RefusesToUnwrapWithTheWrongKey()
    {
        byte[] kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F");

        byte[] wrapped = AesKeyWrap.Wrap(kek, key);
        byte[] wrong = [.. kek];
        wrong[0] ^= 0x01;

        // The wrapped form embeds a known constant, so the wrong key is detected rather than
        // producing a key that fails obscurely later.
        Assert.False(AesKeyWrap.TryUnwrap(wrong, wrapped, out _));

        byte[] tampered = [.. wrapped];
        tampered[12] ^= 0x01;
        Assert.False(AesKeyWrap.TryUnwrap(kek, tampered, out _));
    }

    [Fact]
    public void DerivesTheSameWrappingKeyFromTheSameInputs()
    {
        byte[] fileKey = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] salt = Convert.FromHexString("A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7B8B9BABBBCBDBEBF");

        byte[] first = AesKeyWrap.DeriveWrappingKey(fileKey, salt, 32);
        byte[] second = AesKeyWrap.DeriveWrappingKey(fileKey, salt, 32);

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);

        // A different salt has to give a different key, or the salt is doing nothing.
        byte[] other = [.. salt];
        other[0] ^= 0x01;
        Assert.NotEqual(first, AesKeyWrap.DeriveWrappingKey(fileKey, other, 32));
    }

    /// <summary>An encrypted document written with integrity protection.</summary>
    private static byte[] Sealed()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        using (Content.ContentCanvas canvas = Content.ContentCanvas.ForPage(page))
        {
            canvas.DrawText("A document that vouches for itself.", 72, 700, document.Fonts.Helvetica, 12);
            canvas.Commit();
        }

        return document.ToArray(new PdfSaveOptions
        {
            AddIntegrityCode = true,
            Encryption = new PdfEncryptionSettings { UserPassword = "open-me", OwnerPassword = "owner" },
        });
    }

    private static IEnumerable<PdfMacIntegrity> Protected()
    {
        foreach (string path in Corpus.Files)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }

            if (!System.Text.Encoding.Latin1.GetString(bytes).Contains("AuthCode", StringComparison.Ordinal))
                continue;

            PdfDocument document;
            try
            {
                document = PdfDocument.Load(bytes, new IO.PdfReadOptions { Password = CorpusPassword });
            }
            catch (Exception)
            {
                continue;
            }

            using (document)
            {
                PdfMacIntegrity integrity = PdfMacIntegrity.Read(document);

                if (integrity.IsPresent)
                    yield return integrity;
            }
        }
    }

    private static PdfDocument Open(string path) =>
        PdfDocument.Load(File.ReadAllBytes(path), new IO.PdfReadOptions { Password = CorpusPassword });
}
