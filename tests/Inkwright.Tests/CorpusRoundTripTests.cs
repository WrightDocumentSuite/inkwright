using System.Globalization;
using System.Text;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// The corpus tests are the ones that matter: thousands of files written by every producer in the
/// wild, read, rewritten and read again. A regression here means real documents break, which no
/// hand-written fixture would have caught.
/// </summary>
public class CorpusRoundTripTests
{
    private static int SampleSize => Corpus.Files.Count;

    [Fact]
    public void CorpusIsPresent()
    {
        Corpus.Require();
        Assert.True(Corpus.Files.Count > 1000, $"Only {Corpus.Files.Count} files were found.");
    }

    [Fact]
    public void ReadsAndRewritesRealFiles()
    {
        Corpus.Require();

        var failures = new List<string>();
        var unreadable = new List<string>();
        int opened = 0;
        int rewritten = 0;
        int pages = 0;
        int empty = 0;
        int readFailures = 0;

        foreach (string path in Corpus.Sample(SampleSize))
        {
            byte[] original;
            try
            {
                original = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                readFailures++;
                continue;
            }

            if (original.Length == 0)
            {
                empty++;
                continue;
            }

            int pageCount;
            byte[] rewrite;

            try
            {
                using PdfDocument document = PdfDocument.Load(original, Corpus.ReadOptions);
                pageCount = document.Pages.Count;
                pages += pageCount;
                opened++;

                if (document.WasEncrypted && document.Diagnostics.Contains(Diagnostics.PdfWarningCode.UnsupportedSecurityHandler))
                    continue;

                rewrite = document.ToArray();
            }
            catch (Diagnostics.PdfFormatException)
            {
                // The documented outcome for input that holds no readable document at all.
                unreadable.Add(Describe(path));
                continue;
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                failures.Add($"load {Describe(path)}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            try
            {
                using PdfDocument reloaded = PdfDocument.Load(rewrite);
                if (reloaded.Pages.Count != pageCount)
                {
                    failures.Add($"pages {Describe(path)}: {pageCount} became {reloaded.Pages.Count}");
                    continue;
                }

                rewritten++;
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                failures.Add($"reload {Describe(path)}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.True(opened > SampleSize / 2, $"Only {opened} of the sampled files opened at all.");
        Assert.True(pages > 50_000, $"Only {pages} pages were found in the whole corpus.");
        Assert.True(
            unreadable.Count <= 10,
            $"{unreadable.Count} files were rejected outright:\n{string.Join('\n', unreadable.Take(20))}");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of {opened} files failed to round-trip ({rewritten} succeeded):\n" +
            string.Join('\n', failures.Take(40)));

        // Keep the published snapshot reproducible while still allowing developers to point the
        // tests at a different corpus. When this exact snapshot is present, its headline numbers
        // are part of the regression contract rather than prose that can silently go stale.
        if (SampleSize == 7807)
        {
            Assert.Equal(7800, opened);
            Assert.Equal(5, unreadable.Count);
            Assert.Equal(2, empty);
            Assert.Equal(0, readFailures);
            Assert.Equal(SampleSize, opened + unreadable.Count + empty + readFailures);
        }
    }

    [Fact]
    public void KeepsPageGeometryThroughARewrite()
    {
        Corpus.Require();

        var failures = new List<string>();

        foreach (string path in Corpus.Sample(250))
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

            List<(double Width, double Height, int Rotation)> before;

            byte[] rewrite;
            try
            {
                using PdfDocument document = PdfDocument.Load(original, Corpus.ReadOptions);
                if (document.WasEncrypted)
                    continue;

                before = [.. document.Pages.Select(p => (p.Width, p.Height, p.Rotation))];
                rewrite = document.ToArray();
            }
            catch (Diagnostics.PdfFormatException)
            {
                continue;
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                failures.Add($"{Describe(path)}: {ex.Message}");
                continue;
            }

            List<(double Width, double Height, int Rotation)> after;
            try
            {
                using PdfDocument reloaded = PdfDocument.Load(rewrite);
                after = [.. reloaded.Pages.Select(p => (p.Width, p.Height, p.Rotation))];
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                failures.Add($"reload {Describe(path)}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            if (before.Count != after.Count)
            {
                failures.Add($"{Describe(path)}: page count changed");
                continue;
            }

            for (int i = 0; i < before.Count; i++)
            {
                if (Math.Abs(before[i].Width - after[i].Width) > 0.01 ||
                    Math.Abs(before[i].Height - after[i].Height) > 0.01 ||
                    before[i].Rotation != after[i].Rotation)
                {
                    failures.Add($"{Describe(path)} page {i}: {before[i]} became {after[i]}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(30)));
    }

    [Fact]
    public void ParsesEveryContentStreamItMeets()
    {
        Corpus.Require();

        var failures = new List<string>();
        long operations = 0;
        int pages = 0;

        foreach (string path in Corpus.Sample(400))
        {
            try
            {
                using PdfDocument document = PdfDocument.Load(File.ReadAllBytes(path), Corpus.ReadOptions);
                if (document.WasEncrypted)
                    continue;

                foreach (PdfPage page in document.Pages.Take(20))
                {
                    Content.ContentOperations content = Content.ContentOperations.Parse(page.GetContent(), document);
                    operations += content.Count;
                    pages++;

                    for (int i = 0; i < content.Count; i++)
                        _ = content.GetOperands(i);
                }
            }
            catch (Diagnostics.PdfFormatException)
            {
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                failures.Add($"{Describe(path)}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.True(pages > 500, $"Only {pages} pages had content.");
        Assert.True(operations > 100_000, $"Only {operations} operations were parsed.");
        Assert.True(failures.Count == 0, Summarize(failures, pages));
    }

    [Fact]
    public void ReportsDamageInsteadOfThrowing()
    {
        byte[] good = MinimalPdf.OnePage();
        var random = new Random(20260726);
        int recovered = 0;

        for (int trial = 0; trial < 200; trial++)
        {
            byte[] damaged = (byte[])good.Clone();
            for (int i = 0; i < 8; i++)
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);

            try
            {
                using PdfDocument document = PdfDocument.Load(damaged);
                _ = document.Pages.Count;
                recovered++;
            }
            catch (Diagnostics.PdfFormatException)
            {
                // The one exception the library is allowed to raise for unreadable input.
            }
        }

        Assert.True(recovered > 100, $"Only {recovered} of 200 damaged files survived.");
    }

    internal static string Describe(string path)
    {
        string relative = Path.GetRelativePath(Corpus.Root, path);
        return relative.Length > 90 ? "…" + relative[^90..] : relative;
    }

    internal static string Summarize(IReadOnlyCollection<string> failures, int total)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{failures.Count} of {total} failed:");
        foreach (string failure in failures.Take(25))
            text.Append(CultureInfo.InvariantCulture, $"\n  {failure}");

        return text.ToString();
    }
}
