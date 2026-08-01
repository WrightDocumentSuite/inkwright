using System.Text;
using Inkwright.Cos;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Tests;

public class IncrementalSaveTests
{
    [Fact]
    public void LeavesTheOriginalBytesUntouched()
    {
        byte[] original = BuildTwoPageDocument();

        byte[] updated;
        using (PdfDocument document = PdfDocument.Load(original))
        {
            document.Info.Title = "Changed";
            updated = document.ToIncrementalArray();
        }

        Assert.True(updated.Length > original.Length);
        Assert.True(updated.AsSpan(0, original.Length).SequenceEqual(original),
            "The appended save rewrote part of the original file.");
    }

    [Fact]
    public void AppendsOnlyWhatChanged()
    {
        byte[] original = BuildTwoPageDocument();

        using PdfDocument document = PdfDocument.Load(original);
        document.Pages[0].Rotation = 90;
        byte[] updated = document.ToIncrementalArray();

        // A rotated page, the information dictionary and the catalog: nothing else needs saying.
        Assert.True(updated.Length - original.Length < 1500,
            $"The appended section was {updated.Length - original.Length} bytes.");

        using PdfDocument reloaded = PdfDocument.Load(updated);
        Assert.Equal(2, reloaded.Pages.Count);
        Assert.Equal(90, reloaded.Pages[0].Rotation);
        Assert.Equal(0, reloaded.Pages[1].Rotation);
    }

    [Fact]
    public void ChainsSeveralUpdates()
    {
        byte[] file = BuildTwoPageDocument();

        for (int round = 1; round <= 4; round++)
        {
            using PdfDocument document = PdfDocument.Load(file);
            document.Info.Subject = $"round {round}";
            document.Pages.Add(PageSize.A5);
            file = document.ToIncrementalArray();
        }

        using PdfDocument final = PdfDocument.Load(file);
        Assert.Equal(6, final.Pages.Count);
        Assert.Equal("round 4", final.Info.Subject);
        Assert.Contains("Hello", Encoding.ASCII.GetString(final.Pages[0].GetContent()), StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsAppendingToAnEncryptedDocument()
    {
        using var source = PdfDocument.Create();
        source.Pages.Add().SetContent("% first"u8.ToArray());
        byte[] encrypted = source.ToArray(new PdfSaveOptions
        {
            Encryption = new PdfEncryptionSettings { UserPassword = "pw" },
        });

        byte[] updated;
        using (PdfDocument document = PdfDocument.Load(encrypted, new PdfReadOptions { Password = "pw" }))
        {
            document.Pages.Add().SetContent("% second"u8.ToArray());
            updated = document.ToIncrementalArray();
        }

        using PdfDocument reloaded = PdfDocument.Load(updated, new PdfReadOptions { Password = "pw" });
        Assert.Equal(2, reloaded.Pages.Count);
        Assert.Contains("second", Encoding.ASCII.GetString(reloaded.Pages[1].GetContent()), StringComparison.Ordinal);
        Assert.Contains("first", Encoding.ASCII.GetString(reloaded.Pages[0].GetContent()), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesToAppendToARebuiltFile()
    {
        byte[] broken = MinimalPdf.OnePage();
        int index = broken.AsSpan().LastIndexOf("startxref"u8);
        broken[index + 10] = (byte)'9';
        broken[index + 11] = (byte)'9';

        using PdfDocument document = PdfDocument.Load(broken);
        Assert.Throws<InvalidOperationException>(() => document.ToIncrementalArray());
    }

    [Fact]
    public void RefusesToAppendToADocumentBuiltFromScratch()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add();
        Assert.Throws<InvalidOperationException>(() => document.ToIncrementalArray());
    }

    [Fact]
    public void SurvivesAcrossTheCorpus()
    {
        Corpus.Require();

        var failures = new List<string>();
        int updated = 0;

        foreach (string path in Corpus.Sample(200))
        {
            byte[] original;
            try
            {
                original = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }

            byte[] appended;
            int pages;

            try
            {
                using PdfDocument document = PdfDocument.Load(original, Corpus.ReadOptions);
                if (document.WasEncrypted || document.Diagnostics.Contains(Diagnostics.PdfWarningCode.XrefRebuilt))
                    continue;

                pages = document.Pages.Count;
                document.Info.Keywords = "inkwright";
                appended = document.ToIncrementalArray();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Diagnostics.PdfFormatException)
            {
                continue;
            }

            try
            {
                using PdfDocument reloaded = PdfDocument.Load(appended);
                if (reloaded.Pages.Count != pages)
                    failures.Add($"{CorpusRoundTripTests.Describe(path)}: {pages} pages became {reloaded.Pages.Count}");
                else if (reloaded.Info.Keywords != "inkwright")
                    failures.Add($"{CorpusRoundTripTests.Describe(path)}: the appended keywords did not stick");
                else
                    updated++;
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                failures.Add($"{CorpusRoundTripTests.Describe(path)}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.True(updated > 50, $"Only {updated} files accepted an incremental update.");
        Assert.True(failures.Count == 0, CorpusRoundTripTests.Summarize(failures, updated));
    }

    private static byte[] BuildTwoPageDocument()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add().SetContent("BT /F1 12 Tf 20 20 Td (Hello) Tj ET"u8.ToArray(), compress: false);
        document.Pages.Add();
        document.Info.Title = "Original";
        return document.ToArray();
    }
}
