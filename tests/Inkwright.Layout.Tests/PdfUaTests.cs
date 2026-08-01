using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Layout;
using Inkwright.Metadata;
using System.Text;
using Xunit;

namespace Inkwright.Layout.Tests;

/// <summary>
/// The accessibility profile of ISO 14289. Most of what the standard asks for is a judgement no
/// program can make, so these tests are about the part that can be decided from the file: whether
/// the structures a reader depends on are present and shaped the way the standard says.
/// </summary>
public class PdfUaTests
{
    [Fact]
    public void ComplainsAboutADocumentThatIsNotTaggedAtAll()
    {
        using PdfDocument document = Blank();

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.Contains(problems, p => p.Clause == "7.1" && p.Message.Contains("no structure tree", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Clause == "5");
        Assert.All(PdfUaProfile.Violations(problems), p => Assert.Equal(PdfUaSeverity.Violation, p.Severity));
    }

    [Fact]
    public void AcceptsTheDeclarationItWritesItself()
    {
        using PdfDocument document = Blank();
        PdfUaProfile.Declare(document, PdfUaConformance.Ua1, "A document with a name");

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        // The identification, the title and the display preference are all satisfied by Declare.
        Assert.DoesNotContain(problems, p => p.Clause == "5");
        Assert.DoesNotContain(problems, p => p.Message.Contains("carries no title", StringComparison.Ordinal));
        Assert.DoesNotContain(
            problems,
            p => p.Message.Contains("document title to be displayed", StringComparison.Ordinal));
    }

    [Fact]
    public void WritesAndValidatesTheCompleteUa2IdentificationSchema()
    {
        using PdfDocument document = Blank();
        PdfUaProfile.Declare(document, PdfUaConformance.Ua2, "An accessible document");

        PdfStream metadata = document.Resolve(document.Catalog.Get(PdfName.Get("Metadata"))).AsStream!;
        string packet = Encoding.UTF8.GetString(metadata.Decode().Span);
        Assert.Contains("<pdfuaid:part>2</pdfuaid:part>", packet, StringComparison.Ordinal);
        Assert.Contains("<pdfuaid:rev>2024</pdfuaid:rev>", packet, StringComparison.Ordinal);

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);
        Assert.DoesNotContain(problems, problem => problem.Clause == "5");
    }

    [Fact]
    public void RejectsTheWrongPartAndRevisionForUa2()
    {
        using PdfDocument document = Tagged(["P"]);
        new XmpMetadata { PdfUaPart = 1, PdfUaRevision = 2023 }.ApplyTo(document);

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);

        Assert.Contains(problems, problem =>
            problem.Clause == "5" && problem.Message.Contains("value 2", StringComparison.Ordinal));
        Assert.Contains(problems, problem =>
            problem.Clause == "5" && problem.Message.Contains("2024", StringComparison.Ordinal));
    }

    [Fact]
    public void CatchesAHeadingSequenceThatSkipsALevel()
    {
        using PdfDocument document = Tagged(["H1", "H3"]);

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.Contains(problems, p => p.Clause == "7.4.2" && p.Message.Contains("skips from H1 to H3", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsAHeadingSequenceThatDescendsOneStepAtATime()
    {
        using PdfDocument document = Tagged(["H1", "H2", "H3", "H3", "H2"]);

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.DoesNotContain(problems, p => p.Clause == "7.4.2");
    }

    [Fact]
    public void CatchesAHeadingSequenceThatDoesNotStartAtTheTop()
    {
        using PdfDocument document = Tagged(["H2", "H3"]);

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.Contains(problems, p => p.Clause == "7.4.2" && p.Message.Contains("rather than H1", StringComparison.Ordinal));
    }

    [Fact]
    public void WantsAlternativeTextOnFiguresAndFormulae()
    {
        using PdfDocument document = Tagged(["Figure", "Formula"]);

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.Contains(problems, p => p.Clause == "7.3");
        Assert.Contains(problems, p => p.Clause == "7.7");
    }

    [Fact]
    public void AcceptsAFigureThatDescribesItself()
    {
        using PdfDocument document = Tagged(["Figure"], node =>
            node.Set(PdfName.Get("Alt"), PdfValue.TextString("A photograph of a bridge")));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.DoesNotContain(problems, p => p.Clause == "7.3");
    }

    [Fact]
    public void RefusesToLetTheRoleMapRedefineAStandardTag()
    {
        using PdfDocument document = Tagged(["P"]);
        PdfDictionary root = StructureRoot(document);

        var roleMap = new PdfDictionary(2);
        roleMap.Set(PdfName.Get("P"), PdfValue.Name(PdfName.Get("Span")));
        roleMap.Set(PdfName.Get("Sidebar"), PdfValue.Name(PdfName.Get("Nonsense")));
        root.Set(PdfName.Get("RoleMap"), PdfValue.Dictionary(roleMap));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.Contains(problems, p => p.Message.Contains("remaps the standard type P", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Message.Contains("never reaches a standard structure type", StringComparison.Ordinal));
    }

    [Fact]
    public void FollowsARoleMapThroughSeveralSteps()
    {
        using PdfDocument document = Tagged(["P"]);
        PdfDictionary root = StructureRoot(document);

        var roleMap = new PdfDictionary(2);
        roleMap.Set(PdfName.Get("Sidebar"), PdfValue.Name(PdfName.Get("Callout")));
        roleMap.Set(PdfName.Get("Callout"), PdfValue.Name(PdfName.Get("Aside")));
        root.Set(PdfName.Get("RoleMap"), PdfValue.Dictionary(roleMap));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.DoesNotContain(problems, p => p.Message.Contains("never reaches", StringComparison.Ordinal));
    }

    [Fact]
    public void WantsHeaderCellsToSayWhatTheyCover()
    {
        using PdfDocument document = Tagged(["Table"], table =>
        {
            var header = new PdfDictionary(2);
            header.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("StructElem")));
            header.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get("TH")));
            table.Set(PdfName.Get("K"), PdfValue.Dictionary(header));
        });

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.Contains(problems, p => p.Clause == "7.5" && p.Message.Contains("neither a scope", StringComparison.Ordinal));
    }

    [Fact]
    public void WantsTabOrderSetOnPagesThatCarryAnnotations()
    {
        using PdfDocument document = Blank();
        PdfPage page = document.Pages[0];

        var annotation = new PdfDictionary(4);
        annotation.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Annot")));
        annotation.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Text")));
        page.Dictionary.Set(PdfName.Get("Annots"), PdfValue.Array(new PdfArray(PdfValue.Dictionary(annotation))));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.Contains(problems, p => p.Clause == "7.18.3");
        Assert.Contains(problems, p => p.Clause == "7.18.1" && p.Message.Contains("no description", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesATrappingNetworkAnnotation()
    {
        using PdfDocument document = Blank();
        PdfPage page = document.Pages[0];

        var annotation = new PdfDictionary(4);
        annotation.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("TrapNet")));
        annotation.Set(PdfNames.Contents, PdfValue.TextString("trapping"));
        page.Dictionary.Set(PdfName.Get("Annots"), PdfValue.Array(new PdfArray(PdfValue.Dictionary(annotation))));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.Contains(problems, p => p.Clause == "7.18.2");
    }

    [Fact]
    public void LeavesHiddenAndPopupAnnotationsAlone()
    {
        using PdfDocument document = Blank();
        PdfPage page = document.Pages[0];

        var hidden = new PdfDictionary(4);
        hidden.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Text")));
        hidden.Set(PdfName.Get("F"), PdfValue.Integer(2));

        var popup = new PdfDictionary(2);
        popup.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Popup")));

        page.Dictionary.Set(
            PdfName.Get("Annots"),
            PdfValue.Array(new PdfArray(PdfValue.Dictionary(hidden), PdfValue.Dictionary(popup))));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.DoesNotContain(problems, p => p.Clause.StartsWith("7.18", StringComparison.Ordinal));
    }

    [Fact]
    public void WantsBothFileNamesOnAnAttachment()
    {
        using PdfDocument document = Blank();
        document.Attachments.Add("notes.txt", "some notes"u8.ToArray());

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        // The library writes both names, so only the description should be missing.
        Assert.DoesNotContain(problems, p => p.Message.Contains("no file name", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Message.Contains("no Unicode file name", StringComparison.Ordinal));
    }

    [Fact]
    public void ChecksNamespacesOnlyForTheSecondPart()
    {
        using PdfDocument document = Tagged(["P"]);

        IReadOnlyList<PdfUaProblem> one = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);
        IReadOnlyList<PdfUaProblem> two = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);

        Assert.DoesNotContain(one, p => p.Message.Contains("namespace", StringComparison.Ordinal));
        Assert.Contains(two, p => p.Message.Contains("PDF 1.7 default", StringComparison.Ordinal));
    }

    [Fact]
    public void TellsAdviceApartFromRequirements()
    {
        using PdfDocument document = Blank();
        for (int i = 0; i < 12; i++)
            document.Pages.Add(PageSize.A4);

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        PdfUaProblem outline = Assert.Single(problems, p => p.Clause == "7.17");
        Assert.Equal(PdfUaSeverity.Advice, outline.Severity);
        Assert.DoesNotContain(PdfUaProfile.Violations(problems), p => p.Clause == "7.17");
    }

    /// <summary>
    /// ISO 14289-2 §8.6: a text string a person is meant to read must not lean on the Unicode
    /// private use area, whose code points mean nothing outside the font that defined them.
    /// </summary>
    [Fact]
    public void RejectsPrivateUseCharactersInAnAlternateDescription()
    {
        using PdfDocument document = Tagged(["Figure"], element =>
            element.Set(PdfName.Get("Alt"), PdfValue.TextString("Logo ")));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);

        Assert.Contains(problems, problem =>
            problem.Clause == "8.6" && problem.Severity == PdfUaSeverity.Violation);
    }

    [Fact]
    public void RejectsSupplementaryPrivateUseCharactersInAnOutlineTitle()
    {
        using PdfDocument document = Tagged(["P"]);
        AddOutlineItem(document, "Chapter \U000F0001");

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);

        Assert.Contains(problems, problem => problem.Clause == "8.6");
    }

    [Fact]
    public void AcceptsOrdinaryTextInTheSamePlaces()
    {
        using PdfDocument document = Tagged(["Figure"], element =>
            element.Set(PdfName.Get("Alt"), PdfValue.TextString("The company logo")));
        AddOutlineItem(document, "Chapter one — введение");

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);

        Assert.DoesNotContain(problems, problem => problem.Clause == "8.6");
    }

    /// <summary>Part 1 says nothing about the private use area, so the rule stays out of its way.</summary>
    [Fact]
    public void LeavesPrivateUseCharactersAloneUnderUa1()
    {
        using PdfDocument document = Tagged(["Figure"], element =>
            element.Set(PdfName.Get("Alt"), PdfValue.TextString("Logo ")));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.DoesNotContain(problems, problem => problem.Clause == "8.6");
    }

    private static void AddOutlineItem(PdfDocument document, string title)
    {
        var item = new PdfDictionary(2);
        item.Set(PdfNames.Title, PdfValue.TextString(title));
        item.Set(PdfName.Get("Dest"), PdfValue.Array(StructureDestination(document)));

        var outlines = new PdfDictionary(2);
        outlines.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Outlines")));
        outlines.Set(PdfName.Get("First"), PdfValue.Dictionary(item));
        document.Catalog.Set(PdfName.Get("Outlines"), PdfValue.Dictionary(outlines));
    }

    /// <summary>
    /// ISO 14289-2 §8.8: a destination that stays inside the document has to name a structure
    /// element, so that the target survives the content being reflowed.
    /// </summary>
    [Fact]
    public void RejectsAPageDestinationOnALinkUnderUa2()
    {
        using PdfDocument document = Tagged(["P"]);
        AddLink(document, PdfValue.Array(PageDestination(document)));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);

        Assert.Contains(problems, problem =>
            problem.Clause == "8.8" && problem.Severity == PdfUaSeverity.Violation);
    }

    [Fact]
    public void AcceptsAStructureDestinationOnALinkUnderUa2()
    {
        using PdfDocument document = Tagged(["P"]);
        AddLink(document, PdfValue.Array(StructureDestination(document)));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);

        Assert.DoesNotContain(problems, problem => problem.Clause == "8.8");
    }

    /// <summary>Part 1 predates structure destinations, so the same file is fine as PDF/UA-1.</summary>
    [Fact]
    public void LeavesPageDestinationsAloneUnderUa1()
    {
        using PdfDocument document = Tagged(["P"]);
        AddLink(document, PdfValue.Array(PageDestination(document)));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua1);

        Assert.DoesNotContain(problems, problem => problem.Clause == "8.8");
    }

    /// <summary>A named destination is followed to the array it stands for before being judged.</summary>
    [Fact]
    public void FollowsANamedDestinationBeforeJudgingIt()
    {
        using PdfDocument document = Tagged(["P"]);

        var destinations = new PdfDictionary(1);
        destinations.Set(PdfName.Get("Names"), PdfValue.Array(new PdfArray(
            PdfValue.TextString("chapter-one"),
            PdfValue.Array(PageDestination(document)))));

        var names = new PdfDictionary(1);
        names.Set(PdfName.Get("Dests"), PdfValue.Dictionary(destinations));
        document.Catalog.Set(PdfName.Get("Names"), PdfValue.Dictionary(names));

        AddLink(document, PdfValue.TextString("chapter-one"));

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);

        Assert.Contains(problems, problem => problem.Clause == "8.8");
    }

    /// <summary>A target in another file is not an intra-document destination and is left alone.</summary>
    [Fact]
    public void LeavesRemoteDestinationsAlone()
    {
        using PdfDocument document = Tagged(["P"]);

        var action = new PdfDictionary(3);
        action.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get("GoToR")));
        action.Set(PdfName.Get("F"), PdfValue.TextString("other.pdf"));
        action.Set(PdfName.Get("D"), PdfValue.Array(new PdfArray(
            PdfValue.Integer(0), PdfValue.Name(PdfName.Get("Fit")))));

        AddLinkAction(document, action);

        IReadOnlyList<PdfUaProblem> problems = PdfUaProfile.Validate(document, PdfUaConformance.Ua2);

        Assert.DoesNotContain(problems, problem => problem.Clause == "8.8");
    }

    private static PdfArray PageDestination(PdfDocument document) =>
        new(PdfValue.Reference(document.Pages[0].EnsureId()), PdfValue.Name(PdfName.Get("Fit")));

    private static PdfArray StructureDestination(PdfDocument document)
    {
        PdfDictionary root = StructureRoot(document);
        PdfValue element = document.Resolve(root.Get(PdfName.Get("K"))).AsArray!.Get(0);

        // The element has to be an indirect object for a destination to be able to name it.
        PdfObjectId id = document.AddObject(element);
        return new PdfArray(PdfValue.Reference(id), PdfValue.Name(PdfName.Get("Fit")));
    }

    private static void AddLink(PdfDocument document, PdfValue destination)
    {
        var link = NewLink();
        link.Set(PdfName.Get("Dest"), destination);
        Attach(document, link);
    }

    private static void AddLinkAction(PdfDocument document, PdfDictionary action)
    {
        var link = NewLink();
        link.Set(PdfName.Get("A"), PdfValue.Dictionary(action));
        Attach(document, link);
    }

    private static PdfDictionary NewLink()
    {
        var link = new PdfDictionary(5);
        link.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Annot")));
        link.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Link")));
        link.Set(PdfNames.Contents, PdfValue.TextString("Go to chapter one"));
        link.Set(PdfName.Get("Rect"), PdfValue.Array(PdfArray.OfNumbers(10, 10, 100, 30)));
        return link;
    }

    private static void Attach(PdfDocument document, PdfDictionary annotation)
    {
        PdfPage page = document.Pages[0];
        PdfArray annotations = document.Resolve(page.Dictionary.Get(PdfName.Get("Annots"))).AsArray ?? new PdfArray();
        annotations.Add(PdfValue.Dictionary(annotation));
        page.Dictionary.Set(PdfName.Get("Annots"), PdfValue.Array(annotations));
    }

    private static PdfDocument Blank()
    {
        PdfDocument document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);
        return document;
    }

    /// <summary>Builds a document with a structure tree holding one element per tag named.</summary>
    private static PdfDocument Tagged(string[] tags, Action<PdfDictionary>? decorate = null)
    {
        PdfDocument document = Blank();

        var root = new PdfDictionary(4);
        root.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("StructTreeRoot")));

        var children = new PdfArray();
        foreach (string tag in tags)
        {
            var element = new PdfDictionary(4);
            element.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("StructElem")));
            element.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get(tag)));
            decorate?.Invoke(element);
            children.Add(PdfValue.Dictionary(element));
        }

        root.Set(PdfName.Get("K"), PdfValue.Array(children));
        document.Catalog.Set(PdfName.Get("StructTreeRoot"), PdfValue.Dictionary(root));

        var markInfo = new PdfDictionary(2);
        markInfo.Set(PdfName.Get("Marked"), PdfValue.Boolean(true));
        document.Catalog.Set(PdfName.Get("MarkInfo"), PdfValue.Dictionary(markInfo));

        return document;
    }

    private static PdfDictionary StructureRoot(PdfDocument document) =>
        document.Resolve(document.Catalog.Get(PdfName.Get("StructTreeRoot"))).AsDictionary!;
}
