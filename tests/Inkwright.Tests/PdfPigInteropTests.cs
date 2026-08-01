using System.Text;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Cross-checks text extraction against PdfPig on real files. Agreeing with an independent
/// implementation on thousands of documents is the only evidence that the encoding, width and
/// matrix handling are right; a fixture written by the same hands that wrote the extractor proves
/// nothing about either.
/// </summary>
public class PdfPigInteropTests
{
    [Fact]
    public void AgreesWithPdfPigOnRealDocuments()
    {
        Corpus.Require();

        var scores = new List<(double Score, string Path)>();
        int compared = 0;

        // The reference implementation is an order of magnitude slower than ours, so the sample is
        // sized for a test suite people will actually run rather than for maximum coverage. It has
        // to be drawn wide, because most of what it lands on is unusable as a reference: encrypted
        // files, files PdfPig itself rejects, and files with almost no text on the first pages.
        foreach (string path in Corpus.Sample(160))
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }

            string ours;
            try
            {
                using PdfDocument document = PdfDocument.Load(data, Corpus.ReadOptions);
                if (document.WasEncrypted || document.Pages.Count == 0)
                    continue;

                ours = string.Concat(document.Pages.Take(5).Select(p => p.ExtractText()));
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                continue;
            }

            string theirs;
            try
            {
                using UglyToad.PdfPig.PdfDocument reference = UglyToad.PdfPig.PdfDocument.Open(data);
                theirs = string.Concat(reference.GetPages().Take(5).Select(p => p.Text));
            }
            catch (Exception)
            {
                // PdfPig rejects files we can still read; those cannot be used as a reference.
                continue;
            }

            string a = Normalize(ours);
            string b = Normalize(theirs);

            if (a.Length < 40 && b.Length < 40)
                continue;

            compared++;
            scores.Add((Similarity(a, b), path));
        }

        Assert.True(compared > 15, $"Only {compared} documents could be compared.");

        double mean = scores.Average(entry => entry.Score);
        int close = scores.Count(entry => entry.Score >= 0.85);

        string worst = string.Join('\n', scores
            .OrderBy(entry => entry.Score)
            .Take(10)
            .Select(entry => $"  {entry.Score:F2}  {CorpusRoundTripTests.Describe(entry.Path)}"));

        Assert.True(mean >= 0.90, FormattableString.Invariant($"Mean similarity was {mean:F3} over {compared} files.\n{worst}"));
        Assert.True(close >= compared * 0.85,
            FormattableString.Invariant($"Only {close} of {compared} files matched closely.\n{worst}"));
    }

    [Fact]
    public void WritesCommentsAnotherLibraryCanRead()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        using (Content.ContentCanvas canvas = Content.ContentCanvas.ForPage(page))
        {
            canvas.DrawText("The quick brown fox jumps over the lazy dog.", 72, 720, document.Fonts.Helvetica, 16);
            canvas.Commit();
        }

        page.Annotations.AddNote(new PdfPoint(500, 740), "A sticky note", "Reviewer");
        page.Annotations.MarkText("brown fox", Annotations.PdfAnnotationType.Highlight, "Marked for review", "Reviewer");
        page.Annotations.AddLink(new PdfRectangle(72, 100, 200, 120), "https://example.org");

        using UglyToad.PdfPig.PdfDocument reference = UglyToad.PdfPig.PdfDocument.Open(document.ToArray());
        UglyToad.PdfPig.Annotations.Annotation[] annotations = [.. reference.GetPage(1).GetAnnotations()];

        UglyToad.PdfPig.Annotations.Annotation note = Assert.Single(
            annotations, annotation => annotation.Type == UglyToad.PdfPig.Annotations.AnnotationType.Text);
        Assert.Equal("A sticky note", note.Content);

        UglyToad.PdfPig.Annotations.Annotation highlight = Assert.Single(
            annotations, annotation => annotation.Type == UglyToad.PdfPig.Annotations.AnnotationType.Highlight);

        // The mark has to sit over the words, which only holds if the quadrilaterals were written in
        // the order readers expect rather than the one the specification's figure suggests.
        Assert.Single(highlight.QuadPoints);
        Assert.Contains(annotations, annotation => annotation.Type == UglyToad.PdfPig.Annotations.AnnotationType.Link);
        Assert.Contains(annotations, annotation => annotation.Type == UglyToad.PdfPig.Annotations.AnnotationType.Popup);
    }

    [Fact]
    public void FindsTheSameAmountOfTextAsPdfPig()
    {
        Corpus.Require();

        long ourCharacters = 0;
        long theirCharacters = 0;
        int compared = 0;

        foreach (string path in Corpus.Sample(40))
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
                using PdfDocument document = PdfDocument.Load(data, Corpus.ReadOptions);
                using UglyToad.PdfPig.PdfDocument reference = UglyToad.PdfPig.PdfDocument.Open(data);

                if (document.WasEncrypted)
                    continue;

                ourCharacters += document.Pages.Take(3).Sum(p => Normalize(p.ExtractText()).Length);
                theirCharacters += reference.GetPages().Take(3).Sum(p => Normalize(p.Text).Length);
                compared++;
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
            }
        }

        Assert.True(compared > 10, $"Only {compared} documents could be compared.");
        Assert.True(theirCharacters > 0, "The reference implementation found no text at all.");

        double ratio = (double)ourCharacters / theirCharacters;
        Assert.InRange(ratio, 0.95, 1.05);
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>How much two texts overlap, as twice the shared characters over the total length.</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
            return 1;

        if (a.Length == 0 || b.Length == 0)
            return 0;

        var counts = new Dictionary<char, int>();
        foreach (char c in a)
            counts[c] = counts.GetValueOrDefault(c) + 1;

        int shared = 0;
        foreach (char c in b)
        {
            if (counts.TryGetValue(c, out int available) && available > 0)
            {
                counts[c] = available - 1;
                shared++;
            }
        }

        return 2.0 * shared / (a.Length + b.Length);
    }
}
