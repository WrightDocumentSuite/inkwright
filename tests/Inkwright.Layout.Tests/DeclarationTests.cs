using Inkwright.Layout;
using Inkwright.Metadata;
using Xunit;

namespace Inkwright.Layout.Tests;

/// <summary>
/// The PDF Declarations mechanism, by which a document claims conformance with something outside
/// PDF itself. A declaration is an assertion by the author, not evidence: the specification is
/// explicit that its presence guarantees nothing.
/// </summary>
public class DeclarationTests
{
    [Fact]
    public void ReadsTheExampleTheSpecificationPrints()
    {
        const string Xmp = """
            <?xml version="1.0" encoding="utf-8"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:pdfd="http://pdfa.org/declarations/">
                <pdfd:declarations>
                  <rdf:Bag>
                    <rdf:li rdf:parseType="Resource">
                      <pdfd:conformsTo>http://pdfa.org/declarations/#hipaa</pdfd:conformsTo>
                      <pdfd:claimData>
                        <rdf:Bag>
                          <rdf:li rdf:parseType="Resource">
                            <pdfd:claimBy>Validator company</pdfd:claimBy>
                            <pdfd:claimDate>2019-01-17</pdfd:claimDate>
                            <pdfd:claimCredentials>Validation credentials</pdfd:claimCredentials>
                            <pdfd:claimReport>http://pdfa.org/report67890.html</pdfd:claimReport>
                          </rdf:li>
                        </rdf:Bag>
                      </pdfd:claimData>
                    </rdf:li>
                  </rdf:Bag>
                </pdfd:declarations>
              </rdf:Description>
            </rdf:RDF>
            """;

        PdfDeclaration declaration = Assert.Single(PdfDeclarations.Parse(Xmp));

        Assert.Equal("http://pdfa.org/declarations/#hipaa", declaration.ConformsTo);
        Assert.Equal("Validator company", declaration.ClaimedBy);
        Assert.Equal(new DateOnly(2019, 1, 17), DateOnly.FromDateTime(declaration.ClaimedOn!.Value.Date));
        Assert.Equal("Validation credentials", declaration.Credentials);
        Assert.Equal("http://pdfa.org/report67890.html", declaration.Report);
    }

    [Fact]
    public void ReadsBackWhatItWrites()
    {
        PdfDeclaration[] written =
        [
            new("http://example.org/profile/one"),
            new("http://example.org/profile/two")
            {
                ClaimedBy = "A tool",
                ClaimedOn = new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero),
                Credentials = "Some authority",
                Report = "http://example.org/report",
            },
        ];

        string block = PdfDeclarations.Build(written);
        string xmp = $"""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">{block}</rdf:RDF>
            """;

        IReadOnlyList<PdfDeclaration> read = PdfDeclarations.Parse(xmp);

        Assert.Equal(2, read.Count);
        Assert.Equal("http://example.org/profile/one", read[0].ConformsTo);
        Assert.Null(read[0].ClaimedBy);

        Assert.Equal("A tool", read[1].ClaimedBy);
        Assert.Equal("Some authority", read[1].Credentials);
        Assert.Equal("http://example.org/report", read[1].Report);
    }

    [Fact]
    public void EscapesWhatWouldOtherwiseBreakTheMarkup()
    {
        PdfDeclaration[] written = [new("http://example.org/a?x=1&y=2<3")];

        string xmp = $"""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">{PdfDeclarations.Build(written)}</rdf:RDF>
            """;

        PdfDeclaration read = Assert.Single(PdfDeclarations.Parse(xmp));
        Assert.Equal("http://example.org/a?x=1&y=2<3", read.ConformsTo);
    }

    [Fact]
    public void FindsNothingInADocumentThatDeclaresNothing()
    {
        using PdfDocument document = Blank();
        Assert.Empty(PdfDeclarations.Read(document));
    }

    [Fact]
    public void DeclaresAWellTaggedLevelAndReadsItBack()
    {
        using PdfDocument document = Blank();

        WellTaggedPdf.Declare(document, WellTaggedLevel.Accessibility, "Inkwright tests");

        Assert.True(WellTaggedPdf.Declares(document, WellTaggedLevel.Accessibility));
        Assert.False(WellTaggedPdf.Declares(document, WellTaggedLevel.Reuse));

        PdfDeclaration declaration = Assert.Single(PdfDeclarations.Read(document));
        Assert.Equal(WellTaggedPdf.AccessibilityIdentifier, declaration.ConformsTo);
        Assert.Equal("Inkwright tests", declaration.ClaimedBy);
    }

    [Fact]
    public void KeepsBothLevelsWhenBothAreClaimed()
    {
        using PdfDocument document = Blank();

        // The levels are not exclusive, and the specification expects a file to claim several
        // things at once.
        WellTaggedPdf.Declare(document, WellTaggedLevel.Reuse);
        WellTaggedPdf.Declare(document, WellTaggedLevel.Accessibility);

        Assert.True(WellTaggedPdf.Declares(document, WellTaggedLevel.Reuse));
        Assert.True(WellTaggedPdf.Declares(document, WellTaggedLevel.Accessibility));
        Assert.Equal(2, PdfDeclarations.Read(document).Count);
    }

    [Fact]
    public void ClaimsTheAccessibilityStandardTheLevelIsDefinedToMeet()
    {
        using PdfDocument document = Blank();
        WellTaggedPdf.Declare(document, WellTaggedLevel.Accessibility);

        // The accessibility level subsumes ISO 14289-2, so the identification schema goes in too.
        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);
        Assert.DoesNotContain(problems, problem => problem.Clause == "5");
    }

    [Fact]
    public void SurvivesBeingSavedAndReadBack()
    {
        using PdfDocument document = Blank();
        WellTaggedPdf.Declare(document, WellTaggedLevel.Reuse, "A tool");

        using PdfDocument reopened = PdfDocument.Load(document.ToArray());

        PdfDeclaration declaration = Assert.Single(PdfDeclarations.Read(reopened));
        Assert.Equal(WellTaggedPdf.ReuseIdentifier, declaration.ConformsTo);
    }

    private static PdfDocument Blank()
    {
        PdfDocument document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);
        return document;
    }
}
