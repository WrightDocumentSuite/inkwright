using Inkwright.Cos;
using Inkwright.Markdown;
using Xunit;

namespace Inkwright.Tests;

public class MarkdownStructureTests
{
    [Fact]
    public void RendersOrderedListsFromListNumbering()
    {
        var listAttributes = Attributes("List", ("ListNumbering", PdfValue.Name("Decimal")));
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("L", Children:
            [
                new Node("LI", Children: [new Node("LBody", "one")]),
                new Node("LI", Children: [new Node("LBody", "two")]),
            ], Attributes: listAttributes),
        ]));

        using PdfDocument document = PdfDocument.Load(pdf);
        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("1. one\n2. two\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void RendersSimpleTablesAsGfm()
    {
        byte[] pdf = Build(Table());
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal(
            "| Name | Value |\n| --- | --- |\n| Alpha | 1 |\n",
            markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void UsesHtmlForCommonMarkTablesAndSpans()
    {
        byte[] pdf = Build(Table(columnSpan: 2));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown(new MarkdownExportOptions
        {
            Flavor = MarkdownFlavor.CommonMark,
        });

        Assert.Contains("<table>", markdown.Text, StringComparison.Ordinal);
        Assert.Contains("colspan=\"2\"", markdown.Text, StringComparison.Ordinal);
        Assert.Contains(markdown.Diagnostics, warning =>
            warning.Kind == MarkdownExportWarningKind.HtmlFallbackUsed && warning.Subject == "Table");
    }

    [Fact]
    public void RendersSafeTaggedLinks()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("P", Children: [new Node("Link", "OpenAI", LinkUri: "https://openai.com/a b")]),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("[OpenAI](<https://openai.com/a b>)\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void DropsPotentiallyExecutableLinkTargets()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("P", Children: [new Node("Link", "Run", LinkUri: "javascript:alert(1)")]),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("Run\n", markdown.Text);
        MarkdownExportWarning warning = Assert.Single(markdown.Diagnostics);
        Assert.Equal(MarkdownExportWarningKind.UnsafeLinkSkipped, warning.Kind);
        Assert.Equal("javascript", warning.Subject);
    }

    [Theory]
    [InlineData("javascript&#x3a;alert(1)", "javascript")]
    [InlineData("java&#x73;cript:alert(1)", "javascript")]
    [InlineData("vbscript&colon;msgbox(1)", "vbscript")]
    public void DropsEntityObfuscatedExecutableLinkTargets(string destination, string scheme)
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("P", Children: [new Node("Link", "Run", LinkUri: destination)]),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("Run\n", markdown.Text);
        MarkdownExportWarning warning = Assert.Single(markdown.Diagnostics);
        Assert.Equal(MarkdownExportWarningKind.UnsafeLinkSkipped, warning.Kind);
        Assert.Equal(scheme, warning.Subject);
    }

    [Fact]
    public void EscapesRelativeLinkDestinationStartingWithAngleBracket()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("P", Children: [new Node("Link", "Open", LinkUri: "<relative>")]),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("[Open](\\<relative\\>)\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void ResolvesRelativeLinkAgainstTheDocumentBaseUri()
    {
        byte[] pdf = Build(
            new Node("Document", Children:
            [
                new Node("P", Children: [new Node("Link", "Chapter", LinkUri: "chapter.html")]),
            ]),
            baseUri: "https://example.com/manual/");
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("[Chapter](https://example.com/manual/chapter.html)\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void ConsumesLinkObjectReferenceWithoutReportingItAsSkipped()
    {
        byte[] pdf = Build(
            new Node("Document", Children:
            [
                new Node("P", Children:
                [
                    new Node("Link", MarkedContentId: 0, LinkUri: "https://openai.com"),
                ]),
            ]),
            pageContent: "/P <</MCID 0>> BDC BT /F1 12 Tf 60 700 Td (OpenAI) Tj ET EMC\n");
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("[OpenAI](https://openai.com)\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void StructureActualTextPreservesWhitespaceAndLineBreaks()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("P", "one  two\nthree"),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("one  two  \nthree\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void ActualTextOnContainerReplacesItsChildren()
    {
        byte[] pdf = Build(new Node(
            "Document",
            "replacement\nline",
            Children: [new Node("P", "ignored")]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("replacement  \nline\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void ConsecutiveActualTextFragmentsDoNotGainAWordBreak()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("P", Children:
            [
                new Node("Span", "hel"),
                new Node("Strong", "lo"),
            ]),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("hel**lo**\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void FigureActualTextIsAReplacementRatherThanAnImageDescription()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("Figure", "exact replacement"),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("exact replacement\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
        Assert.Empty(markdown.Images);
    }

    [Fact]
    public void DoesNotTreatAnUnmappedCustomLinkAsAStandardPdfLink()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("P", Children:
            [
                new Node(
                    "Link",
                    "visible label",
                    LinkUri: "https://example.com",
                    NamespaceUri: "urn:example:custom"),
            ]),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("visible label\n", markdown.Text);
        Assert.Contains(markdown.Diagnostics, warning =>
            warning.Kind == MarkdownExportWarningKind.StructureApproximated &&
            warning.Subject == "urn:example:custom#Link");
    }

    [Fact]
    public void RecognizesTagsInTheExplicitPdf20Namespace()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("P", Children:
            [
                new Node(
                    "Link",
                    "standard link",
                    LinkUri: "https://example.com",
                    NamespaceUri: "http://iso.org/pdf2/ssn"),
            ]),
        ], NamespaceUri: "http://iso.org/pdf2/ssn"));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("[standard link](https://example.com)\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void ReadsListAttributesFromClassMap()
    {
        var classMap = new PdfDictionary(1);
        classMap.Set(
            PdfName.Get("OrderedList"),
            Attributes("List", ("ListNumbering", PdfValue.Name("Decimal"))));
        byte[] pdf = Build(
            new Node("Document", Children:
            [
                new Node("L", Children:
                [
                    new Node("LI", Children: [new Node("LBody", "one")]),
                    new Node("LI", Children: [new Node("LBody", "two")]),
                ], AttributeClass: "OrderedList"),
            ]),
            classMap: classMap);
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("1. one\n2. two\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void DirectAttributesOverrideClassMapAttributes()
    {
        var classMap = new PdfDictionary(1);
        classMap.Set(
            PdfName.Get("OrderedList"),
            Attributes("List", ("ListNumbering", PdfValue.Name("Decimal"))));
        byte[] pdf = Build(
            new Node("Document", Children:
            [
                new Node("L", Children:
                [
                    new Node("LI", Children: [new Node("LBody", "one")]),
                ],
                Attributes: Attributes("List", ("ListNumbering", PdfValue.Name("None"))),
                AttributeClass: "OrderedList"),
            ]),
            classMap: classMap);
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("- one\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void RendersMultilineCodeWithAFenceThatCannotCloseEarly()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("Code", "alpha\n```\nomega"),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("~~~\nalpha\n```\nomega\n~~~\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void PreservesInlineFormattingInsideGfmTableCells()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("Table", Children:
            [
                new Node("TR", Children: [new Node("TH", Children: [new Node("Strong", "Name")])]),
                new Node("TR", Children: [new Node("TD", Children: [new Node("Em", "value")])]),
            ]),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("| **Name** |\n| --- |\n| *value* |\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void ProjectsPresentationMathMlChildrenOfFormula()
    {
        const string mathMl = "http://www.w3.org/1998/Math/MathML";
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("Formula", Children:
            [
                new Node("math", Children:
                [
                    new Node("mi", "x ", NamespaceUri: mathMl),
                    new Node("mo", "+ ", NamespaceUri: mathMl),
                    new Node("mn", "1", NamespaceUri: mathMl),
                ], NamespaceUri: mathMl),
            ]),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("x + 1\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void ExportsTheValueOfAnInteractiveWidgetClaimedByForm()
    {
        var widget = new PdfDictionary(6);
        widget.Set(PdfNames.Type, PdfValue.Name("Annot"));
        widget.Set(PdfNames.Subtype, PdfValue.Name("Widget"));
        widget.Set(PdfName.Get("FT"), PdfValue.Name("Tx"));
        widget.Set(PdfName.Get("T"), PdfValue.TextString("customer"));
        widget.Set(PdfName.Get("TU"), PdfValue.TextString("Customer name"));
        widget.Set(PdfName.Get("V"), PdfValue.TextString("Ada"));
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("Form", ObjectDictionary: widget),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("Customer name: Ada\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void ExportsNonInteractivePrintFieldAttributes()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node(
                "Form",
                Children: [new Node("Span", "42")],
                Attributes: Attributes(
                    "PrintField",
                    ("Role", PdfValue.Name("tv")),
                    ("Desc", PdfValue.TextString("Account")))),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("Account: 42\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void PreservesExplicitNonDecimalListLabelsAndDiagnosesTheApproximation()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("L", Children:
            [
                new Node("LI", Children:
                [
                    new Node("Lbl", "IV."),
                    new Node("LBody", "item"),
                ]),
            ], Attributes: Attributes(
                "List",
                ("ListNumbering", PdfValue.Name("UpperRoman")),
                ("ContinuedList", PdfValue.True))),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("- IV. item\n", markdown.Text);
        Assert.Contains(markdown.Diagnostics, warning =>
            warning.Kind == MarkdownExportWarningKind.StructureApproximated &&
            warning.Subject == "UpperRoman list");
        Assert.Contains(markdown.Diagnostics, warning =>
            warning.Kind == MarkdownExportWarningKind.StructureApproximated &&
            warning.Subject == "Continued list");
    }

    [Fact]
    public void UsesHtmlWhenGfmCannotRepresentRowHeaderCells()
    {
        byte[] pdf = Build(new Node("Document", Children:
        [
            new Node("Table", Children:
            [
                new Node("TR", Children: [new Node("TD", "Name"), new Node("TD", "Value")]),
                new Node("TR", Children: [new Node("TH", "A"), new Node("TD", "1")]),
            ]),
        ]));
        using PdfDocument document = PdfDocument.Load(pdf);

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Contains("<th>A</th>", markdown.Text, StringComparison.Ordinal);
        Assert.Contains("<td>1</td>", markdown.Text, StringComparison.Ordinal);
        Assert.Contains(markdown.Diagnostics, warning =>
            warning.Kind == MarkdownExportWarningKind.HtmlFallbackUsed &&
            warning.Subject == "Table");
    }

    private static Node Table(int columnSpan = 1)
    {
        PdfDictionary? span = columnSpan > 1
            ? Attributes("Table", ("ColSpan", PdfValue.Integer(columnSpan)))
            : null;

        return new Node("Document", Children:
        [
            new Node("Table", Children:
            [
                new Node("TR", Children:
                [
                    new Node("TH", "Name", Attributes: span),
                    new Node("TH", "Value"),
                ]),
                new Node("TR", Children:
                [
                    new Node("TD", "Alpha"),
                    new Node("TD", "1"),
                ]),
            ]),
        ]);
    }

    private static PdfDictionary Attributes(string owner, params (string Name, PdfValue Value)[] values)
    {
        var attributes = new PdfDictionary(values.Length + 1);
        attributes.Set(PdfName.Get("O"), PdfValue.Name(owner));
        foreach ((string name, PdfValue value) in values)
            attributes.Set(PdfName.Get(name), value);
        return attributes;
    }

    private static byte[] Build(
        Node rootNode,
        string? pageContent = null,
        string? baseUri = null,
        PdfDictionary? classMap = null)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        if (pageContent is not null)
        {
            var font = new PdfDictionary(3);
            font.Set(PdfNames.Type, PdfValue.Name("Font"));
            font.Set(PdfNames.Subtype, PdfValue.Name("Type1"));
            font.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));
            var fonts = new PdfDictionary(1);
            fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(font)));
            page.Resources.Set(PdfName.Get("Font"), fonts);
            page.SetContent(System.Text.Encoding.ASCII.GetBytes(pageContent));
        }

        if (baseUri is not null)
        {
            var uri = new PdfDictionary(1);
            uri.Set(PdfName.Get("Base"), PdfValue.TextString(baseUri));
            document.Catalog.Set(PdfName.Get("URI"), uri);
        }

        var root = new PdfDictionary(2);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        if (classMap is not null)
            root.Set(PdfName.Get("ClassMap"), classMap);
        PdfObjectId rootId = document.AddObject(root);
        PdfObjectId nodeId = AddNode(document, page, rootId, rootNode);
        root.Set(PdfNames.K, PdfValue.Reference(nodeId));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        return document.ToArray();
    }

    private static PdfObjectId AddNode(PdfDocument document, PdfPage page, PdfObjectId parent, Node node)
    {
        PdfObjectId id = document.AddObject(PdfValue.Null);
        var dictionary = new PdfDictionary(8);
        dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        dictionary.Set(PdfNames.S, PdfValue.Name(node.Tag));
        dictionary.Set(PdfNames.P, PdfValue.Reference(parent));
        dictionary.Set(PdfNames.Pg, PdfValue.Reference(page.Id));

        if (node.Text is not null)
            dictionary.Set(PdfNames.ActualText, PdfValue.TextString(node.Text));
        if (node.Attributes is not null)
            dictionary.Set(PdfName.Get("A"), node.Attributes);
        if (node.AttributeClass is not null)
            dictionary.Set(PdfName.Get("C"), PdfValue.Name(node.AttributeClass));
        if (node.NamespaceUri is not null)
        {
            var structureNamespace = new PdfDictionary(2);
            structureNamespace.Set(PdfNames.Type, PdfValue.Name("Namespace"));
            structureNamespace.Set(PdfName.Get("NS"), PdfValue.TextString(node.NamespaceUri));
            dictionary.Set(
                PdfName.Get("NS"),
                PdfValue.Reference(document.AddObject(structureNamespace)));
        }

        var kids = new PdfArray();
        foreach (Node child in node.Children ?? [])
            kids.Add(PdfValue.Reference(AddNode(document, page, id, child)));

        if (node.MarkedContentId is { } mcid)
            kids.Add(PdfValue.Integer(mcid));

        if (node.LinkUri is not null)
        {
            var action = new PdfDictionary(2);
            action.Set(PdfName.Get("S"), PdfValue.Name("URI"));
            action.Set(PdfName.Get("URI"), PdfValue.TextString(node.LinkUri));
            var annotation = new PdfDictionary(3);
            annotation.Set(PdfNames.Type, PdfValue.Name("Annot"));
            annotation.Set(PdfNames.Subtype, PdfValue.Name("Link"));
            annotation.Set(PdfName.Get("A"), action);
            PdfObjectId annotationId = document.AddObject(annotation);

            var objectReference = new PdfDictionary(3);
            objectReference.Set(PdfNames.Type, PdfValue.Name("OBJR"));
            objectReference.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
            objectReference.Set(PdfName.Get("Obj"), PdfValue.Reference(annotationId));
            kids.Add(objectReference);
        }

        if (node.ObjectDictionary is not null)
        {
            PdfObjectId objectId = document.AddObject(node.ObjectDictionary);
            var objectReference = new PdfDictionary(3);
            objectReference.Set(PdfNames.Type, PdfValue.Name("OBJR"));
            objectReference.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
            objectReference.Set(PdfName.Get("Obj"), PdfValue.Reference(objectId));
            kids.Add(objectReference);
        }

        if (kids.Count == 1)
            dictionary.Set(PdfNames.K, kids[0]);
        else if (kids.Count > 1)
            dictionary.Set(PdfNames.K, kids);

        document.SetObject(id, dictionary);
        return id;
    }

    private sealed record Node(
        string Tag,
        string? Text = null,
        Node[]? Children = null,
        PdfDictionary? Attributes = null,
        string? LinkUri = null,
        int? MarkedContentId = null,
        string? NamespaceUri = null,
        string? AttributeClass = null,
        PdfDictionary? ObjectDictionary = null);
}
