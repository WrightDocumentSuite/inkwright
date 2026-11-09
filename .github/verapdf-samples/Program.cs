using Inkwright;
using Inkwright.Fonts;
using Inkwright.Layout;

// Writes one small file for each conformance claim Inkwright makes, so that veraPDF can check the
// claims with rules this repository did not write. Text only: a file that needs a font embedded
// exercises the part of the standard most likely to go wrong.
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: VeraPdfSamples <output directory>");
    return 2;
}

string output = Directory.CreateDirectory(args[0]).FullName;
string fontPath = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans")
    ?? throw new InvalidOperationException("An embeddable font is required and none was found.");
int problems = 0;

foreach ((string name, PdfAConformance level) in new[]
{
    ("pdfa-1b", PdfAConformance.A1B),
    ("pdfa-2b", PdfAConformance.A2B),
    ("pdfa-2u", PdfAConformance.A2U),
    ("pdfa-3b", PdfAConformance.A3B),
    ("pdfa-3u", PdfAConformance.A3U),
    ("pdfa-4", PdfAConformance.A4),
})
{
    using PdfDocument document = Build(fontPath, tagged: false);
    PdfAProfile.Apply(document, level, new PdfAOptions { Language = "en" });
    problems += SaveAndCheck(document, Path.Combine(output, name + ".pdf"), level);
}

foreach ((string name, PdfAConformance level) in new[]
{
    ("pdfa-1a", PdfAConformance.A1A),
    ("pdfa-2a", PdfAConformance.A2A),
    ("pdfa-3a", PdfAConformance.A3A),
})
{
    using PdfDocument document = Build(fontPath, tagged: true);
    PdfAProfile.Apply(document, level, new PdfAOptions { Language = "en" });
    problems += SaveAndCheck(document, Path.Combine(output, name + ".pdf"), level);
}

foreach ((string name, PdfUaConformance level) in new[]
{
    ("pdfua-1", PdfUaConformance.Ua1),
    ("pdfua-2", PdfUaConformance.Ua2),
})
{
    using PdfDocument document = Build(fontPath, tagged: true);
    PdfUaProfile.Declare(document, level, "A sample with a title");
    foreach (PdfUaProblem problem in PdfUaProfile.Violations(PdfUaProfile.Validate(document, level)))
    {
        Console.Error.WriteLine($"{name}: {problem}");
        problems++;
    }

    document.Save(Path.Combine(output, name + ".pdf"));
}

Console.WriteLine($"Wrote samples to {output}");
return problems == 0 ? 0 : 1;

// A sample the library itself rejects would only tell veraPDF what is already known.
static int SaveAndCheck(PdfDocument document, string path, PdfAConformance level)
{
    document.Save(path);
    int found = 0;
    foreach (PdfAProblem problem in PdfAProfile.Validate(File.ReadAllBytes(path), level))
    {
        Console.Error.WriteLine($"{Path.GetFileName(path)}: {problem}");
        found++;
    }

    return found;
}

static PdfDocument Build(string fontPath, bool tagged)
{
    var document = PdfDocument.Create();
    PdfFont font = document.Fonts.Embed(fontPath);
    document.Info.Title = "Conformance sample";
    document.Info.Author = "Inkwright";

    Report.Create()
        .Page(page =>
        {
            if (tagged)
                page.Tagged("en");

            page.DefaultTextStyle(TextStyle.Default.Font(font));
            page.Content().Column(column =>
            {
                column.Item().Text("A document that intends to outlive its author.");
                column.Item().Text("A second paragraph, so the structure has more than one element.");
            });
        })
        .Render(document);

    return document;
}
