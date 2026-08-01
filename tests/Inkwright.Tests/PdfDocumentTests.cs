using Inkwright.Cos;
using Inkwright.IO;
using Inkwright.Security;
using Xunit;

namespace Inkwright.Tests;

public class PdfDocumentTests
{
    [Fact]
    public void CreatesAndReloadsAnEmptyDocument()
    {
        using var document = PdfDocument.Create();
        document.Info.Title = "Пример";
        document.Info.Author = "Inkwright";
        document.Pages.Add(PageSize.A4);
        document.Pages.Add(PageSize.Letter.Landscape);

        byte[] bytes = document.ToArray();

        using PdfDocument reloaded = PdfDocument.Load(bytes);
        Assert.Equal(2, reloaded.Pages.Count);
        Assert.Equal("Пример", reloaded.Info.Title);
        Assert.Equal("Inkwright", reloaded.Info.Author);
        Assert.StartsWith("Inkwright", reloaded.Info.Producer, StringComparison.Ordinal);
        Assert.Equal(PageSize.A4.Width, reloaded.Pages[0].Width, 1);
        Assert.Equal(792, reloaded.Pages[1].Width, 1);
        Assert.True(reloaded.Diagnostics.IsClean, string.Join("; ", reloaded.Diagnostics));
    }

    [Fact]
    public void WritesWithoutObjectStreamsWhenAsked()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add();

        byte[] bytes = document.ToArray(new PdfSaveOptions { UseObjectStreams = false });

        Assert.Contains("xref"u8.ToArray(), Windows(bytes, 4));
        using PdfDocument reloaded = PdfDocument.Load(bytes);
        Assert.Single(reloaded.Pages);
    }

    [Fact]
    public void KeepsPageContentThroughARoundTrip()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        page.SetContent("1 0 0 RG 10 10 m 200 200 l S"u8.ToArray());

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        string content = System.Text.Encoding.ASCII.GetString(reloaded.Pages[0].GetContent());
        Assert.Contains("200 200 l", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReordersAndRemovesPages()
    {
        using var document = PdfDocument.Create();
        for (int i = 0; i < 4; i++)
            document.Pages.Add().SetContent(System.Text.Encoding.ASCII.GetBytes($"% page {i}"));

        document.Pages.Move(0, 3);
        document.Pages.RemoveAt(0);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        // 0 1 2 3 becomes 1 2 3 0 after the move, then 2 3 0 once the first page is dropped.
        Assert.Equal(3, reloaded.Pages.Count);
        Assert.Contains("page 2", System.Text.Encoding.ASCII.GetString(reloaded.Pages[0].GetContent()), StringComparison.Ordinal);
        Assert.Contains("page 0", System.Text.Encoding.ASCII.GetString(reloaded.Pages[2].GetContent()), StringComparison.Ordinal);
    }

    [Fact]
    public void ImportsPagesFromAnotherDocument()
    {
        using var source = PdfDocument.Create();
        source.Pages.Add(PageSize.A5).SetContent("% imported"u8.ToArray());

        using var target = PdfDocument.Create();
        target.Pages.Add();
        target.Pages.ImportAll(source);

        using PdfDocument reloaded = PdfDocument.Load(target.ToArray());
        Assert.Equal(2, reloaded.Pages.Count);
        Assert.Equal(PageSize.A5.Height, reloaded.Pages[1].Height, 1);
        Assert.Contains("imported", System.Text.Encoding.ASCII.GetString(reloaded.Pages[1].GetContent()), StringComparison.Ordinal);
    }

    [Fact]
    public void PrunesUnreachableObjects()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add();
        document.AddObject(PdfValue.Stream(PdfStream.FromData(new byte[4096])));

        byte[] withGarbage = document.ToArray();
        byte[] pruned = document.ToArray(new PdfSaveOptions { RemoveUnusedObjects = true });

        Assert.True(pruned.Length < withGarbage.Length);
        using PdfDocument reloaded = PdfDocument.Load(pruned);
        Assert.Single(reloaded.Pages);
    }

    [Fact]
    public void RoundTripsEncryptedDocuments()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add().SetContent("% secret"u8.ToArray());
        document.Info.Title = "Секрет";

        byte[] bytes = document.ToArray(new PdfSaveOptions
        {
            Encryption = new PdfEncryptionSettings
            {
                UserPassword = "open-me",
                OwnerPassword = "owner",
                Permissions = PdfPermissions.Print,
            },
        });

        using PdfDocument reopened = PdfDocument.Load(bytes, new PdfReadOptions { Password = "open-me" });
        Assert.True(reopened.WasEncrypted);
        Assert.Equal("Секрет", reopened.Info.Title);
        Assert.Contains("secret", System.Text.Encoding.ASCII.GetString(reopened.Pages[0].GetContent()), StringComparison.Ordinal);

        using PdfDocument asOwner = PdfDocument.Load(bytes, new PdfReadOptions { Password = "owner" });
        Assert.Equal("Секрет", asOwner.Info.Title);

        // The permission bits carry two reserved ones and everything above the twelfth; reporting
        // them raw would say -3900 where the file grants printing and nothing else.
        Assert.Equal(PdfPermissions.Print, reopened.SourcePermissions);
    }

    [Fact]
    public void LeavesTheMetadataReadableWhenTheFileSaysItIs()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add();
        new Metadata.XmpMetadata { Title = "Indexable" }.ApplyTo(document);

        byte[] sealed_ = document.ToArray(new PdfSaveOptions
        {
            Encryption = new PdfEncryptionSettings { UserPassword = "open", EncryptMetadata = false },
        });

        // Leaving the XMP in the clear is the whole point of the option: a search indexer reads it
        // without ever being told the password.
        string raw = System.Text.Encoding.Latin1.GetString(sealed_);
        Assert.Contains("<?xpacket", raw, StringComparison.Ordinal);
        Assert.Contains("Indexable", raw, StringComparison.Ordinal);

        byte[] closed = document.ToArray(new PdfSaveOptions
        {
            Encryption = new PdfEncryptionSettings { UserPassword = "open" },
        });

        Assert.DoesNotContain("<?xpacket", System.Text.Encoding.Latin1.GetString(closed), StringComparison.Ordinal);

        using PdfDocument reopened = PdfDocument.Load(sealed_, new PdfReadOptions { Password = "open" });
        Assert.Contains(
            "Indexable",
            System.Text.Encoding.UTF8.GetString(reopened.Catalog.GetStream(PdfName.Get("Metadata"))!.DecodedSpan()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsTheAesGcmEncryptionOfPdf2()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "The reference corpus is not present.");

        // AES-GCM is the one flavour a reader can mistake for AES-CBC: both are named by a crypt
        // filter, and guessing wrong produces plausible-looking rubbish instead of a complaint.
        string? plainPath = FindGcmSample("simpleDocument.pdf");
        string? sealedPath = FindGcmSample("encryptedDocument.pdf");

        Assert.SkipWhen(plainPath is null || sealedPath is null, "No AES-GCM sample in the corpus.");

        using PdfDocument plain = PdfDocument.Load(File.ReadAllBytes(plainPath!));
        using PdfDocument opened = PdfDocument.Load(
            File.ReadAllBytes(sealedPath!), new PdfReadOptions { Password = "secret" });

        Assert.True(opened.WasEncrypted);
        Assert.Equal(Drawings(plain), Drawings(opened));

        // A tampered stream fails its authentication tag, and GCM is the only scheme that can tell:
        // AES-CBC would hand back noise and call it content. Saying so is the whole reason to
        // choose the mode, so the file is refused rather than opened with the damage hidden.
        // Streams are decrypted when they are read, so where the complaint surfaces depends on
        // where the damage is: in the cross-reference data it shows at load, in page content when
        // the content is asked for. What matters is that it surfaces, never as quiet nonsense.
        foreach (string path in Corpus.Matching("encryptedDocumentTampered", 8)
                     .Where(p => p.Contains("AesGcm", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] damaged = File.ReadAllBytes(path);

            Assert.Throws<Inkwright.Diagnostics.PdfTamperedException>(() =>
            {
                using PdfDocument opened = PdfDocument.Load(damaged, new PdfReadOptions { Password = "secret" });
                _ = Drawings(opened);
            });
        }
    }

    private static string? FindGcmSample(string fileName) => Corpus.Files.FirstOrDefault(
        path => string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal) &&
                path.Contains("AesGcm", StringComparison.OrdinalIgnoreCase));

    /// <summary>The length of every form the first page draws, keyed by its resource name.</summary>
    private static Dictionary<string, int> Drawings(PdfDocument document)
    {
        Dictionary<string, int> lengths = [];
        if (document.Pages[0].Resources.GetDictionary(PdfName.Get("XObject")) is not { } forms)
            return lengths;

        foreach (PdfName name in forms.Keys.ToArray())
            lengths[name.Value] = forms.GetStream(name) is { } stream ? stream.DecodedSpan().Length : -1;

        return lengths;
    }

    [Fact]
    public void RejectsTheWrongPassword()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add();
        byte[] bytes = document.ToArray(new PdfSaveOptions
        {
            Encryption = new PdfEncryptionSettings { UserPassword = "right" },
        });

        Diagnostics.PdfPasswordException failure = Assert.Throws<Diagnostics.PdfPasswordException>(
            () => PdfDocument.Load(bytes, new PdfReadOptions { Password = "wrong" }));

        Assert.True(failure.WasPasswordSupplied);

        // Loading without a password at all is the same failure, told apart for the caller who has
        // to decide whether to ask for one.
        Assert.False(Assert.Throws<Diagnostics.PdfPasswordException>(
            () => PdfDocument.Load(bytes)).WasPasswordSupplied);

        // A tool that only sorts files by whether they are locked can still look inside the wrapper.
        using PdfDocument locked = PdfDocument.Load(bytes, new PdfReadOptions { OpenLockedDocuments = true });
        Assert.True(locked.WasEncrypted);
        Assert.Contains(locked.Diagnostics, w => w.Code == Diagnostics.PdfWarningCode.UnsupportedSecurityHandler);
    }

    private static IEnumerable<byte[]> Windows(byte[] data, int size)
    {
        for (int i = 0; i + size <= data.Length; i++)
            yield return data[i..(i + size)];
    }
}
