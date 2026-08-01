using Inkwright.Diagnostics;
using Inkwright.Markdown;
using Xunit;

namespace Inkwright.Tests;

public sealed class MarkdownCorpusTests
{
    private static readonly byte[][] FeatureMarkers =
    [
        "/ActualText"u8.ToArray(),
        "/RoleMapNS"u8.ToArray(),
        "/ClassMap"u8.ToArray(),
        "/Stm"u8.ToArray(),
        "/Formula"u8.ToArray(),
    ];

    [Fact]
    public void FeatureTaggedCorpusSampleIsDeterministicAndReportsValidMetrics()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "The reference corpus is not present.");

        var failures = new List<string>();
        int candidates = 0;
        int exported = 0;
        long referenced = 0;
        long resolved = 0;

        foreach (string path in FeatureTaggedCandidates(limit: 24))
        {
            candidates++;
            try
            {
                using PdfDocument document = PdfDocument.Load(File.ReadAllBytes(path), Corpus.ReadOptions);
                if (document.WasEncrypted || document.Structure.Count == 0)
                    continue;

                var options = new MarkdownExportOptions { IncludeImages = false };
                MarkdownDocument first = document.ToMarkdown(options);
                MarkdownDocument second = document.ToMarkdown(options);

                if (first.Text != second.Text ||
                    !first.Diagnostics.SequenceEqual(second.Diagnostics) ||
                    first.Metrics != second.Metrics)
                {
                    failures.Add($"nondeterministic {Describe(path)}");
                    continue;
                }

                Validate(first, document.Pages.Count, path, failures);
                referenced += first.Metrics.MarkedContentReferences;
                resolved += first.Metrics.ResolvedMarkedContentReferences;
                exported++;
            }
            catch (PdfFormatException)
            {
                // Corpus files with no recoverable PDF object graph are outside export scope.
            }
            catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
            {
                failures.Add($"{Describe(path)}: {exception.GetType().Name} {exception.Message}");
            }
        }

        Assert.True(candidates >= 12, $"Only {candidates} feature-tagged candidates were found.");
        Assert.True(exported >= 8, $"Only {exported} feature-tagged PDFs exported.");
        Assert.True(resolved <= referenced, $"Resolved {resolved} of only {referenced} MCID references.");
        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(25)));
    }

    private static IEnumerable<string> FeatureTaggedCandidates(int limit)
    {
        int found = 0;
        foreach (string path in Corpus.Files)
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 16 * 1024 * 1024)
                continue;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }

            ReadOnlySpan<byte> bytes = data;
            if (bytes.IndexOf("/StructTreeRoot"u8) < 0 ||
                !HasFeatureMarker(bytes))
            {
                continue;
            }

            yield return path;
            if (++found >= limit)
                yield break;
        }
    }

    private static bool HasFeatureMarker(ReadOnlySpan<byte> bytes)
    {
        foreach (byte[] marker in FeatureMarkers)
        {
            if (bytes.IndexOf(marker) >= 0)
                return true;
        }

        return false;
    }

    private static void Validate(
        MarkdownDocument markdown,
        int sourcePages,
        string path,
        List<string> failures)
    {
        string name = Describe(path);
        if (markdown.Text.Contains('\r') || markdown.Text.Contains('\0') ||
            !markdown.Text.EndsWith('\n') || markdown.Text.EndsWith("\n\n", StringComparison.Ordinal))
        {
            failures.Add($"text invariants {name}");
        }

        MarkdownExportMetrics metrics = markdown.Metrics;
        if (metrics.SourcePages != sourcePages ||
            metrics.OutputCharacters != markdown.Text.Length ||
            metrics.Warnings != markdown.Diagnostics.Count ||
            metrics.ResolvedMarkedContentReferences > metrics.MarkedContentReferences ||
            metrics.MarkedContentCoverage is < 0 or > 1 ||
            metrics.Images != markdown.Images.Count)
        {
            failures.Add($"metrics {name}: {metrics}");
        }
    }

    private static string Describe(string path) => CorpusRoundTripTests.Describe(path);
}
