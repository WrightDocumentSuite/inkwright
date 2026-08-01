using System.Text;
using System.Text.RegularExpressions;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Layout.Tests;

/// <summary>
/// Judges the archival files another implementation produced. A validator can be made to agree with
/// itself by writing the rules twice; the only real test is whether it accepts files it did not
/// write and that a different team believes to be conformant.
/// </summary>
public class PdfAReferenceTests
{
    [Fact]
    public void AcceptsTheArchivalFilesAnotherImplementationWrote()
    {
        string? root = FindReferenceFiles();
        Assert.SkipWhen(root is null, "The reference PDF/A files are not present.");

        int judged = 0;
        var complaints = new List<string>();

        foreach (string path in Directory.EnumerateFiles(root!, "*.pdf", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(path);

            // The set also holds PDF/A-4 files and inputs with no claim at all; neither is ours to
            // judge, and a validator that guesses at the level proves nothing.
            if (Claim(bytes) is not { } level)
                continue;

            judged++;

            foreach (PdfAProblem problem in PdfAProfile.Validate(bytes, level))
                complaints.Add($"{Path.GetFileName(path)} [{level.Name}] {problem}");
        }

        Assert.True(judged > 80, $"only {judged} reference files carried a claim to judge");
        Assert.Empty(complaints.Take(20));
    }

    private static string? FindReferenceFiles()
    {
        string? directory = AppContext.BaseDirectory;

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory, "itext-dotnet-9.7.0", "itext.tests", "itext.pdfa.tests", "resources");

            if (Directory.Exists(candidate))
                return candidate;

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    /// <summary>The level a file claims for itself, read straight out of its metadata.</summary>
    private static PdfAConformance? Claim(byte[] bytes)
    {
        string text = Encoding.Latin1.GetString(bytes);
        Match part = Regex.Match(text, @"pdfaid[:\s]*part[^0-9]{0,20}([1-4])");
        Match level = Regex.Match(text, @"pdfaid[:\s]*conformance[^ABU]{0,20}([ABU])");

        if (!part.Success)
            return null;

        return (part.Groups[1].Value, level.Success ? level.Groups[1].Value : "B") switch
        {
            ("1", "A") => PdfAConformance.A1A,
            ("1", _) => PdfAConformance.A1B,
            ("2", "A") => PdfAConformance.A2A,
            ("2", "U") => PdfAConformance.A2U,
            ("2", _) => PdfAConformance.A2B,
            ("3", "A") => PdfAConformance.A3A,
            ("3", "U") => PdfAConformance.A3U,
            ("3", _) => PdfAConformance.A3B,
            _ => null,
        };
    }
}
