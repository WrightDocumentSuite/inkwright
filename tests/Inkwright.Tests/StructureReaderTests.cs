using System.Text;
using Inkwright.Cos;
using Inkwright.Tagging;
using Inkwright.Text;
using Xunit;

namespace Inkwright.Tests;

public class StructureReaderTests
{
    [Fact]
    public void SaysNothingAboutAnUntaggedDocument()
    {
        using var document = PdfDocument.Create();
        document.Pages.Add();

        Assert.Empty(document.Structure);
    }

    [Fact]
    public void ReadsBackTheTreeItWrote()
    {
        using PdfDocument document = PdfDocument.Load(BuildTwoColumns());

        PdfStructureNode root = Assert.Single(document.Structure);
        Assert.Equal("Document", root.Tag);
        Assert.Equal(3, root.Children.Count);
        Assert.Equal(["H1", "P", "P"], root.Children.Select(child => child.Tag));
        Assert.Equal("A heading", root.Children[0].Title);
    }

    [Fact]
    public void PointsEachNodeAtTheContentItClaims()
    {
        using PdfDocument document = PdfDocument.Load(BuildTwoColumns());

        PdfStructureNode heading = document.Structure[0].Children[0];
        PdfContentReference reference = Assert.Single(heading.Content);

        Assert.Equal(document.Pages[0].Id.Number, reference.Page.Number);
        Assert.Equal(0, reference.MarkedContentId);
    }

    [Fact]
    public void ReadsAlternateTextOffTheTree()
    {
        using PdfDocument document = PdfDocument.Load(BuildTwoColumns());

        Assert.Equal("A chart of sales", document.Structure[0].Children[2].AlternateText);
    }

    [Fact]
    public void TagsEachGlyphWithTheSequenceItWasPaintedIn()
    {
        using PdfDocument document = PdfDocument.Load(BuildTwoColumns());

        IReadOnlyList<PdfLetter> letters = document.Pages[0].ExtractLetters();

        Assert.All(letters, letter => Assert.NotNull(letter.MarkedContentId));
        Assert.Contains(letters, letter => letter.MarkedContentId == 0);
        Assert.Contains(letters, letter => letter.MarkedContentId == 2);
    }

    [Fact]
    public void ReadsColumnsInTheOrderTheTreeDeclares()
    {
        using PdfDocument document = PdfDocument.Load(BuildTwoColumns());
        PdfPage page = document.Pages[0];

        // By position the two columns interleave line by line, which is how they were painted.
        string painted = page.ExtractText().ReplaceLineEndings(" ").Trim();
        Assert.Contains("left one right one", painted, StringComparison.Ordinal);

        // The structure tree says the left column is one paragraph and the right another, so the
        // whole of the left column comes out before any of the right.
        string logical = page.ExtractText(new TextExtractionOptions { UseLogicalOrder = true });

        Assert.Equal(
            ["A heading", "left one", "left two", "right one", "right two"],
            logical.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void PreservesTheOriginalOrderOfMixedKids()
    {
        using PdfDocument document = PdfDocument.Load(BuildMixedKids());
        PdfStructureNode root = Assert.Single(document.Structure);

        Assert.Collection(
            root.Kids,
            kid => Assert.Equal(0, Assert.IsType<PdfStructureContentKid>(kid).Reference.MarkedContentId),
            kid => Assert.Equal("P", Assert.IsType<PdfStructureElementKid>(kid).Element.Tag),
            kid => Assert.True(Assert.IsType<PdfStructureObjectKid>(kid).Reference.Object.IsValid));

        Assert.Single(root.Children);
        Assert.Single(root.Content);
        Assert.Single(root.Objects);
        Assert.Equal([0, 1], StructureReader.ReadingOrder(document.Structure, document.Pages[0]));
    }

    [Fact]
    public void ResolvesLegacyRoleMapsTransitively()
    {
        using PdfDocument document = PdfDocument.Load(BuildRoleMappedElement());
        PdfStructureNode heading = Assert.Single(document.Structure);

        Assert.Equal("ChapterTitle", heading.Tag);
        Assert.Equal("H2", heading.ResolvedTag);
        Assert.Null(heading.NamespaceName);
        Assert.Null(heading.ResolvedNamespaceName);
    }

    [Fact]
    public void ResolvesNamespaceRoleMapsIntoAStandardNamespace()
    {
        using PdfDocument document = PdfDocument.Load(BuildNamespacedElement());
        PdfStructureNode heading = Assert.Single(document.Structure);

        Assert.Equal("Lead", heading.Tag);
        Assert.Equal("H1", heading.ResolvedTag);
        Assert.Equal("urn:inkwright:test", heading.NamespaceName);
        Assert.Equal("http://iso.org/pdf2/ssn", heading.ResolvedNamespaceName);
    }

    /// <summary>
    /// A page holding a heading and two columns, painted line by line across the page the way a
    /// typesetter does, with a structure tree that says which column is which.
    /// </summary>
    private static byte[] BuildTwoColumns()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var font = new PdfDictionary(3);
        font.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Font")));
        font.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Type1")));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name(PdfName.Get("Helvetica")));

        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(font))));
        page.Resources.Set(PdfName.Get("Font"), PdfValue.Dictionary(fonts));

        var content = new StringBuilder();
        content.Append("/H1 <</MCID 0>> BDC BT /F1 18 Tf 60 750 Td (A heading) Tj ET EMC\n");
        content.Append("/P <</MCID 1>> BDC BT /F1 12 Tf 60 700 Td (left one) Tj ET EMC\n");
        content.Append("/P <</MCID 2>> BDC BT /F1 12 Tf 320 700 Td (right one) Tj ET EMC\n");
        content.Append("/P <</MCID 1>> BDC BT /F1 12 Tf 60 680 Td (left two) Tj ET EMC\n");
        content.Append("/P <</MCID 2>> BDC BT /F1 12 Tf 320 680 Td (right two) Tj ET EMC\n");

        page.SetContent(Encoding.ASCII.GetBytes(content.ToString()));
        page.Dictionary.Set(PdfNames.StructParents, PdfValue.Integer(0));

        var root = new PdfDictionary(3);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        PdfObjectId rootId = document.AddObject(PdfValue.Dictionary(root));

        PdfObjectId documentId = document.AddObject(PdfValue.Null);

        var children = new PdfArray(3);
        children.Add(PdfValue.Reference(Element(document, documentId, page, "H1", [0], title: "A heading")));
        children.Add(PdfValue.Reference(Element(document, documentId, page, "P", [1])));
        children.Add(PdfValue.Reference(Element(document, documentId, page, "P", [2], alt: "A chart of sales")));

        var top = new PdfDictionary(4);
        top.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("StructElem")));
        top.Set(PdfNames.S, PdfValue.Name(PdfName.Get("Document")));
        top.Set(PdfNames.P, PdfValue.Reference(rootId));
        top.Set(PdfNames.K, PdfValue.Array(children));
        document.SetObject(documentId, PdfValue.Dictionary(top));

        var rootChildren = new PdfArray(1);
        rootChildren.Add(PdfValue.Reference(documentId));
        root.Set(PdfNames.K, PdfValue.Array(rootChildren));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));

        return document.ToArray();
    }

    private static byte[] BuildMixedKids()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var root = new PdfDictionary(2);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        PdfObjectId rootId = document.AddObject(root);

        var child = new PdfDictionary(5);
        child.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        child.Set(PdfNames.S, PdfValue.Name("P"));
        child.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
        child.Set(PdfNames.K, PdfValue.Integer(1));
        PdfObjectId childId = document.AddObject(child);

        var annotation = new PdfDictionary(2);
        annotation.Set(PdfNames.Type, PdfValue.Name("Annot"));
        annotation.Set(PdfNames.Subtype, PdfValue.Name("Link"));
        PdfObjectId annotationId = document.AddObject(annotation);

        var objectReference = new PdfDictionary(3);
        objectReference.Set(PdfNames.Type, PdfValue.Name("OBJR"));
        objectReference.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
        objectReference.Set(PdfName.Get("Obj"), PdfValue.Reference(annotationId));

        var kids = new PdfArray(3);
        kids.Add(PdfValue.Integer(0));
        kids.Add(PdfValue.Reference(childId));
        kids.Add(objectReference);

        var top = new PdfDictionary(6);
        top.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        top.Set(PdfNames.S, PdfValue.Name("Document"));
        top.Set(PdfNames.P, PdfValue.Reference(rootId));
        top.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
        top.Set(PdfNames.K, kids);
        PdfObjectId topId = document.AddObject(top);
        child.Set(PdfNames.P, PdfValue.Reference(topId));

        root.Set(PdfNames.K, PdfValue.Reference(topId));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        return document.ToArray();
    }

    private static byte[] BuildRoleMappedElement()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var roleMap = new PdfDictionary(2);
        roleMap.Set(PdfName.Get("ChapterTitle"), PdfValue.Name("Heading"));
        roleMap.Set(PdfName.Get("Heading"), PdfValue.Name("H2"));

        var root = new PdfDictionary(3);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        root.Set(PdfNames.RoleMap, roleMap);
        PdfObjectId rootId = document.AddObject(root);

        PdfObjectId elementId = AddTopElement(document, page, rootId, "ChapterTitle");
        root.Set(PdfNames.K, PdfValue.Reference(elementId));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        return document.ToArray();
    }

    private static byte[] BuildNamespacedElement()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var standardNamespace = new PdfDictionary(2);
        standardNamespace.Set(PdfNames.Type, PdfValue.Name("Namespace"));
        standardNamespace.Set(PdfName.Get("NS"), PdfValue.TextString("http://iso.org/pdf2/ssn"));
        PdfObjectId standardNamespaceId = document.AddObject(standardNamespace);

        var target = new PdfArray(2);
        target.Add(PdfValue.Name("H1"));
        target.Add(PdfValue.Reference(standardNamespaceId));
        var namespaceRoleMap = new PdfDictionary(1);
        namespaceRoleMap.Set(PdfName.Get("Lead"), target);

        var customNamespace = new PdfDictionary(3);
        customNamespace.Set(PdfNames.Type, PdfValue.Name("Namespace"));
        customNamespace.Set(PdfName.Get("NS"), PdfValue.TextString("urn:inkwright:test"));
        customNamespace.Set(PdfName.Get("RoleMapNS"), namespaceRoleMap);
        PdfObjectId customNamespaceId = document.AddObject(customNamespace);

        var root = new PdfDictionary(3);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        var namespaces = new PdfArray(2);
        namespaces.Add(PdfValue.Reference(customNamespaceId));
        namespaces.Add(PdfValue.Reference(standardNamespaceId));
        root.Set(PdfName.Get("Namespaces"), namespaces);
        PdfObjectId rootId = document.AddObject(root);

        PdfObjectId elementId = AddTopElement(document, page, rootId, "Lead", customNamespaceId);
        root.Set(PdfNames.K, PdfValue.Reference(elementId));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        return document.ToArray();
    }

    private static PdfObjectId AddTopElement(
        PdfDocument document,
        PdfPage page,
        PdfObjectId root,
        string tag,
        PdfObjectId structureNamespace = default)
    {
        var element = new PdfDictionary(6);
        element.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        element.Set(PdfNames.S, PdfValue.Name(tag));
        element.Set(PdfNames.P, PdfValue.Reference(root));
        element.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
        element.Set(PdfNames.K, PdfValue.Integer(0));
        if (structureNamespace.IsValid)
            element.Set(PdfName.Get("NS"), PdfValue.Reference(structureNamespace));

        return document.AddObject(element);
    }

    private static PdfObjectId Element(
        PdfDocument document, PdfObjectId parent, PdfPage page, string tag, int[] mcids,
        string? title = null, string? alt = null)
    {
        var element = new PdfDictionary(6);
        element.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("StructElem")));
        element.Set(PdfNames.S, PdfValue.Name(PdfName.Get(tag)));
        element.Set(PdfNames.P, PdfValue.Reference(parent));
        element.Set(PdfNames.Pg, PdfValue.Reference(page.Id));

        if (title is not null)
            element.Set(PdfNames.Title, PdfValue.TextString(title));

        if (alt is not null)
            element.Set(PdfName.Get("Alt"), PdfValue.TextString(alt));

        if (mcids.Length == 1)
            element.Set(PdfNames.K, PdfValue.Integer(mcids[0]));
        else
        {
            var array = new PdfArray(mcids.Length);
            foreach (int mcid in mcids)
                array.Add(PdfValue.Integer(mcid));

            element.Set(PdfNames.K, PdfValue.Array(array));
        }

        return document.AddObject(PdfValue.Dictionary(element));
    }
}
