using System.Security.Cryptography;
using Inkwright.Images;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Corpus regressions that need fixture-specific metadata. Keep these separate from the generic
/// JBIG2 sweep: opening an encrypted document without its known password produces encrypted stream
/// bytes and measures the password harness, not the decoder.
/// </summary>
public class CorpusJbig2MetricsTests
{
    [Fact]
    public void DecodesEveryJbig2ImageInTheCorpusWithKnownCredentials()
    {
        Corpus.Require();
        int total = 0;
        int decoded = 0;
        var failures = new List<string>();

        foreach (string path in Corpus.Files)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan().IndexOf("/JBIG2Decode"u8) < 0)
                continue;

            try
            {
                var options = Path.GetFileName(path).Equals("issue3371.pdf", StringComparison.OrdinalIgnoreCase)
                    ? new IO.PdfReadOptions { Password = "ELXRTQWS" }
                    : Corpus.ReadOptions;

                using PdfDocument document = PdfDocument.Load(bytes, options);
                foreach (PdfPageImage image in document.Pages
                             .SelectMany(page => page.Images)
                             .Where(image => image.Codec == "JBIG2Decode"))
                {
                    total++;
                    try
                    {
                        _ = image.Decode();
                        decoded++;
                    }
                    catch (Exception error)
                    {
                        failures.Add($"{Path.GetFileName(path)}: {error.GetType().Name}: {error.Message}");
                    }
                }
            }
            catch (Exception error)
            {
                failures.Add($"{Path.GetFileName(path)}: document {error.GetType().Name}: {error.Message}");
            }
        }

        Assert.Empty(failures);
        Assert.Equal(142, total);
        Assert.Equal(total, decoded);
    }

    [Fact]
    public void DecodesPasswordProtectedIssue3371WithItsManifestPassword()
    {
        string? path = Corpus.Matching("issue3371.pdf", 1).FirstOrDefault();
        Assert.SkipUnless(path is not null, "The pdf.js issue3371 fixture is not on this machine.");

        // pdf.js test/test_manifest.json records this password for issue3371.pdf.
        using PdfDocument document = PdfDocument.Load(
            File.ReadAllBytes(path),
            new IO.PdfReadOptions { Password = "ELXRTQWS" });

        PdfPageImage image = Assert.Single(
            document.Pages.SelectMany(page => page.Images),
            candidate => candidate.Codec == "JBIG2Decode");
        PdfImageData decoded = image.Decode();

        Assert.Equal(2480, decoded.Width);
        Assert.Equal(3507, decoded.Height);
        Assert.Equal(34_789_440, decoded.Pixels.Length);
        Assert.Equal(
            "d79737ab7d0da63484f342923720180e1cfb2686b0f2fbbcdf1cbf68fe4f3ff6",
            Convert.ToHexString(SHA256.HashData(decoded.Pixels)).ToLowerInvariant());
    }
}
