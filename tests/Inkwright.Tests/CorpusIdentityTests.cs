using System.Text;
using Xunit;

namespace Inkwright.Tests;

public class CorpusIdentityTests
{
    [Fact]
    public void IdentityIsStableAcrossInputOrderAndPlatformSeparators()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("inkwright-corpus-id-");
        try
        {
            string firstPath = Path.Combine(directory.FullName, "first.pdf");
            string secondPath = Path.Combine(directory.FullName, "second.pdf");
            File.WriteAllBytes(firstPath, [0, 1, 2]);
            File.WriteAllText(secondPath, "hello", new UTF8Encoding(false));

            CorpusIdentityResult forward = CorpusIdentity.Compute(
            [
                new CorpusFile("PdfPig-0.1.15\\A.pdf", firstPath),
                new CorpusFile("pdf.js-6.1.200/e\u0301.PDF", secondPath),
            ]);
            CorpusIdentityResult reverse = CorpusIdentity.Compute(
            [
                new CorpusFile("pdf.js-6.1.200/é.PDF", secondPath),
                new CorpusFile("PdfPig-0.1.15/A.pdf", firstPath),
            ]);

            Assert.Equal(forward, reverse);
            Assert.Equal(2, forward.FileCount);
            Assert.Equal(8, forward.TotalBytes);
            Assert.Equal("34067a3e02e7b81cc4e1d360899b2a14ce365f81545b5e22e486fb420edbfd6d", forward.Sha256);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void IdentityRejectsPathsThatCollideAfterNormalization()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("inkwright-corpus-id-");
        try
        {
            string file = Path.Combine(directory.FullName, "fixture.pdf");
            File.WriteAllBytes(file, [42]);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                CorpusIdentity.Compute(
                [
                    new CorpusFile("root/e\u0301.pdf", file),
                    new CorpusFile("root/é.pdf", file),
                ]));

            Assert.Contains("Duplicate normalized corpus path", exception.Message);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
