using Inkwright.Annotations;
using Inkwright.Cos;
using Inkwright.Tagging;
using Xunit;

namespace Inkwright.Tests;

public class StructureTreeAnnotationTests
{
    [Fact]
    public void PreservesContentAnnotationContentOrderInTheKArray()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "Between two runs.");

        var builder = new StructureTreeBuilder(document);
        StructureElement owner = builder.AddRoot("Document").Add("P");
        owner.AddContent(page.Id, builder.NextMarkedContentId(page));
        owner.AddAnnotation(note);
        owner.AddContent(page.Id, builder.NextMarkedContentId(page));
        builder.Build();

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfStructureNode paragraph = Assert.Single(Assert.Single(reloaded.Structure).Children);
        PdfAnnotation annotation = Assert.Single(reloaded.Pages[0].Annotations.Comments);
        PdfArray kids = Assert.IsType<PdfArray>(paragraph.Dictionary.Get(PdfNames.K).AsArray);

        Assert.Equal(3, kids.Count);
        Assert.Equal(0, kids.GetInteger(0, -1));

        PdfDictionary objectReference = Assert.IsType<PdfDictionary>(kids.Get(1).AsDictionary);
        Assert.Equal("OBJR", objectReference.GetName(PdfNames.Type)?.Value);
        Assert.Equal(annotation.Id, objectReference[PdfName.Get("Obj")].AsReference);

        Assert.Equal(1, kids.GetInteger(2, -1));
        Assert.Collection(
            paragraph.Kids,
            first => Assert.Equal(0, Assert.IsType<PdfStructureContentKid>(first).Reference.MarkedContentId),
            second => Assert.Equal(annotation.Id, Assert.IsType<PdfStructureObjectKid>(second).Reference.Object),
            third => Assert.Equal(1, Assert.IsType<PdfStructureContentKid>(third).Reference.MarkedContentId));
    }

    [Fact]
    public void WritesCrossPageContentAsAnMcrAndReadsEachKidOnItsOwnPage()
    {
        using var document = PdfDocument.Create();
        PdfPage firstPage = document.Pages.Add(PageSize.A4);
        PdfPage secondPage = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = firstPage.Annotations.AddNote(
            new PdfPoint(72, 700), "Between the two page fragments.");

        var builder = new StructureTreeBuilder(document);
        StructureElement owner = builder.AddRoot("Document").Add("P");
        owner.AddContent(firstPage.Id, builder.NextMarkedContentId(firstPage));
        owner.AddAnnotation(note);
        owner.AddContent(secondPage.Id, builder.NextMarkedContentId(secondPage));
        builder.Build();

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfStructureNode paragraph = Assert.Single(Assert.Single(reloaded.Structure).Children);
        PdfAnnotation annotation = Assert.Single(reloaded.Pages[0].Annotations.Comments);
        PdfArray kids = Assert.IsType<PdfArray>(paragraph.Dictionary.Get(PdfNames.K).AsArray);

        Assert.Equal(reloaded.Pages[0].Id, paragraph.Dictionary[PdfNames.Pg].AsReference);
        Assert.Equal(0, kids.GetInteger(0, -1));
        Assert.Equal(annotation.Id, Assert.IsType<PdfDictionary>(kids.Get(1).AsDictionary)
            [PdfName.Get("Obj")].AsReference);

        PdfDictionary mcr = Assert.IsType<PdfDictionary>(kids.Get(2).AsDictionary);
        Assert.Equal("MCR", mcr.GetName(PdfNames.Type)?.Value);
        Assert.Equal(reloaded.Pages[1].Id, mcr[PdfNames.Pg].AsReference);
        Assert.Equal(0, mcr.GetInteger(PdfNames.MCID, -1));

        Assert.Collection(
            paragraph.Kids,
            first => Assert.Equal(
                reloaded.Pages[0].Id,
                Assert.IsType<PdfStructureContentKid>(first).Reference.Page),
            second => Assert.Equal(
                annotation.Id,
                Assert.IsType<PdfStructureObjectKid>(second).Reference.Object),
            third => Assert.Equal(
                reloaded.Pages[1].Id,
                Assert.IsType<PdfStructureContentKid>(third).Reference.Page));

        AssertParentTreeOwner(reloaded, reloaded.Pages[0], paragraph.Id, 0);
        AssertParentTreeOwner(reloaded, reloaded.Pages[1], paragraph.Id, 0);
    }

    [Fact]
    public void PreservesChildInsertionAmongContentAndAnnotations()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "After the span.");

        var builder = new StructureTreeBuilder(document);
        StructureElement paragraph = builder.AddRoot("Document").Add("P");
        paragraph.AddContent(page.Id, builder.NextMarkedContentId(page));
        StructureElement span = paragraph.Add("Span");
        span.AddContent(page.Id, builder.NextMarkedContentId(page));
        paragraph.AddAnnotation(note);
        paragraph.AddContent(page.Id, builder.NextMarkedContentId(page));
        builder.Build();

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfStructureNode owner = Assert.Single(Assert.Single(reloaded.Structure).Children);
        PdfObjectId annotationId = Assert.Single(reloaded.Pages[0].Annotations.Comments).Id;

        Assert.Collection(
            owner.Kids,
            first => Assert.Equal(0, Assert.IsType<PdfStructureContentKid>(first).Reference.MarkedContentId),
            second => Assert.Equal("Span", Assert.IsType<PdfStructureElementKid>(second).Element.Tag),
            third => Assert.Equal(annotationId, Assert.IsType<PdfStructureObjectKid>(third).Reference.Object),
            fourth => Assert.Equal(2, Assert.IsType<PdfStructureContentKid>(fourth).Reference.MarkedContentId));
    }

    [Fact]
    public void WritesBidirectionalAnnotationStructureThatSurvivesRoundTrip()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "Review this paragraph.");

        var builder = new StructureTreeBuilder(document);
        StructureElement owner = builder.AddRoot("Document").Add("Annot");
        owner.AddAnnotation(note);
        builder.Build();

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfAnnotation annotation = Assert.Single(reloaded.Pages[0].Annotations.Comments);
        PdfStructureNode annotationElement = Assert.Single(Assert.Single(reloaded.Structure).Children);
        int parentKey = (int)annotation.Dictionary.GetInteger(PdfNames.StructParent, -1);

        Assert.Equal("Annot", annotationElement.Tag);
        Assert.Equal(annotation.Id, Assert.Single(annotationElement.Objects).Object);
        Assert.Equal(annotationElement.Id, ParentValue(reloaded, parentKey).AsReference);
        Assert.Equal("S", reloaded.Pages[0].Dictionary.GetName(PdfName.Get("Tabs"))?.Value);
    }

    [Fact]
    public void AllocatesDistinctParentKeysForAnnotationsOnSeveralPages()
    {
        using var document = PdfDocument.Create();
        PdfPage firstPage = document.Pages.Add(PageSize.A4);
        PdfPage secondPage = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation first = firstPage.Annotations.AddNote(new PdfPoint(72, 700), "First note");
        PdfTextAnnotation second = secondPage.Annotations.AddNote(new PdfPoint(72, 700), "Second note");

        var builder = new StructureTreeBuilder(document);
        StructureElement root = builder.AddRoot("Document");
        root.Add("Annot").AddAnnotation(first);
        root.Add("Annot").AddAnnotation(second);
        builder.Build();

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfAnnotation firstReloaded = Assert.Single(reloaded.Pages[0].Annotations.Comments);
        PdfAnnotation secondReloaded = Assert.Single(reloaded.Pages[1].Annotations.Comments);
        int firstKey = (int)firstReloaded.Dictionary.GetInteger(PdfNames.StructParent, -1);
        int secondKey = (int)secondReloaded.Dictionary.GetInteger(PdfNames.StructParent, -1);
        PdfDictionary structureRoot = reloaded.Catalog.GetDictionary(PdfNames.StructTreeRoot)!;

        Assert.NotEqual(firstKey, secondKey);
        Assert.True(ParentValue(reloaded, firstKey).IsReference);
        Assert.True(ParentValue(reloaded, secondKey).IsReference);
        Assert.Equal(2, structureRoot.GetInteger(PdfNames.ParentTreeNextKey));
        Assert.Equal(2, Assert.Single(reloaded.Structure).Children.Count);
    }

    [Fact]
    public void AppendsToAnExistingElementAndPreservesItsParentTreeEntry()
    {
        using var original = PdfDocument.Create();
        PdfPage page = original.Pages.Add(PageSize.A4);

        var initial = new StructureTreeBuilder(original);
        StructureElement documentElement = initial.AddRoot("Document");
        StructureElement paragraph = documentElement.Add("P");
        paragraph.AddContent(page.Id, initial.NextMarkedContentId(page));
        initial.Build();

        using var editable = PdfDocument.Load(original.ToArray());
        PdfValue structureRootReference = editable.Catalog[PdfNames.StructTreeRoot];
        PdfDictionary structureRoot = editable.Catalog.GetDictionary(PdfNames.StructTreeRoot)!;
        structureRoot.Set(PdfNames.ParentTreeNextKey, PdfValue.Integer(0)); // stale producer hint

        PdfStructureNode existingDocument = Assert.Single(editable.Structure);
        PdfStructureNode existingElement = Assert.Single(existingDocument.Children);
        PdfTextAnnotation note = editable.Pages[0].Annotations.AddNote(
            new PdfPoint(96, 650), "Appended after layout.");

        var update = new StructureTreeBuilder(editable);
        update.AddAnnotation(existingElement, note);
        update.Build();

        using PdfDocument reloaded = PdfDocument.Load(editable.ToIncrementalArray());
        PdfStructureNode reloadedDocument = Assert.Single(reloaded.Structure);
        PdfStructureNode reloadedElement = Assert.Single(reloadedDocument.Children);
        PdfStructureNode annotationElement = Assert.Single(reloadedElement.Children);
        PdfAnnotation reloadedNote = Assert.Single(reloaded.Pages[0].Annotations.Comments);
        int annotationKey = (int)reloadedNote.Dictionary.GetInteger(PdfNames.StructParent, -1);

        Assert.Equal(structureRootReference.AsReference, reloaded.Catalog[PdfNames.StructTreeRoot].AsReference);
        Assert.Equal(0, Assert.Single(reloadedElement.Content).MarkedContentId);
        Assert.Empty(reloadedElement.Objects);
        Assert.Equal("Annot", annotationElement.Tag);
        Assert.Equal(reloadedElement.Id, annotationElement.Dictionary[PdfNames.P].AsReference);
        Assert.Equal(reloadedNote.Id, Assert.Single(annotationElement.Objects).Object);
        Assert.Collection(
            reloadedElement.Kids,
            first => Assert.IsType<PdfStructureContentKid>(first),
            second => Assert.Equal(
                "Annot",
                Assert.IsType<PdfStructureElementKid>(second).Element.Tag));
        Assert.True(reloaded.Resolve(ParentValue(reloaded, 0)).AsArray is { Count: 1 });
        Assert.Equal(annotationElement.Id, ParentValue(reloaded, annotationKey).AsReference);
        Assert.Equal(1, annotationKey);
        Assert.Equal(2, reloaded.Catalog.GetDictionary(PdfNames.StructTreeRoot)!
            .GetInteger(PdfNames.ParentTreeNextKey));
    }

    [Fact]
    public void InsertsSemanticAnnotationChildrenAtIndexesAndAroundExistingAnchors()
    {
        using var original = PdfDocument.Create();
        PdfPage page = original.Pages.Add(PageSize.A4);

        var initial = new StructureTreeBuilder(original);
        StructureElement paragraph = initial.AddRoot("Document").Add("P");
        paragraph.AddContent(page.Id, initial.NextMarkedContentId(page));
        paragraph.Add("Span").AddContent(page.Id, initial.NextMarkedContentId(page));
        paragraph.AddContent(page.Id, initial.NextMarkedContentId(page));
        initial.Build();

        using var editable = PdfDocument.Load(original.ToArray());
        PdfStructureNode existingParagraph = Assert.Single(Assert.Single(editable.Structure).Children);
        PdfStructureKid lastContent = existingParagraph.Kids[2];
        PdfTextAnnotation indexed = editable.Pages[0].Annotations.AddNote(
            new PdfPoint(72, 700), "At index one.");
        PdfTextAnnotation before = editable.Pages[0].Annotations.AddNote(
            new PdfPoint(96, 700), "Before the last run.");
        PdfTextAnnotation after = editable.Pages[0].Annotations.AddNote(
            new PdfPoint(120, 700), "After the last run.");

        var update = new StructureTreeBuilder(editable);
        update.AddAnnotation(existingParagraph, indexed, 1);
        update.AddAnnotationBefore(existingParagraph, before, lastContent);
        update.AddAnnotationAfter(existingParagraph, after, lastContent);
        update.Build();

        using PdfDocument reloaded = PdfDocument.Load(editable.ToIncrementalArray());
        PdfStructureNode reloadedParagraph = Assert.Single(Assert.Single(reloaded.Structure).Children);

        Assert.Collection(
            reloadedParagraph.Kids,
            first => Assert.IsType<PdfStructureContentKid>(first),
            second => AssertAnnotationElement(second, "At index one.", reloaded),
            third => Assert.Equal("Span", Assert.IsType<PdfStructureElementKid>(third).Element.Tag),
            fourth => AssertAnnotationElement(fourth, "Before the last run.", reloaded),
            fifth => Assert.IsType<PdfStructureContentKid>(fifth),
            sixth => AssertAnnotationElement(sixth, "After the last run.", reloaded));

        PdfArray rawKids = Assert.IsType<PdfArray>(reloadedParagraph.Dictionary.Get(PdfNames.K).AsArray);
        PdfDictionary inserted = Assert.IsType<PdfDictionary>(reloaded.GetObject(rawKids[1].AsReference).AsDictionary);
        Assert.Equal("Annot", inserted.GetName(PdfNames.S)?.Value);
        Assert.Equal(reloadedParagraph.Id, inserted[PdfNames.P].AsReference);
        Assert.Equal("OBJR", inserted.GetDictionary(PdfNames.K)?.GetName(PdfNames.Type)?.Value);
    }

    [Fact]
    public void WrapsLinksAndWidgetsInTheirSemanticStructureElements()
    {
        using var original = PdfDocument.Create();
        PdfPage page = original.Pages.Add(PageSize.A4);
        var initial = new StructureTreeBuilder(original);
        initial.AddRoot("Document").Add("P");
        initial.Build();

        using var editable = PdfDocument.Load(original.ToArray());
        PdfPage editablePage = editable.Pages[0];
        PdfStructureNode paragraph = Assert.Single(Assert.Single(editable.Structure).Children);
        PdfLinkAnnotation link = editablePage.Annotations.AddLink(
            new PdfRectangle(72, 650, 180, 670), "https://example.org");
        PdfAnnotation widget = AddWidget(editable, editablePage, new PdfRectangle(72, 610, 180, 635));

        var update = new StructureTreeBuilder(editable);
        update.AddAnnotation(paragraph, link);
        update.AddAnnotation(paragraph, widget);
        update.Build();

        using PdfDocument reloaded = PdfDocument.Load(editable.ToArray());
        PdfStructureNode reloadedParagraph = Assert.Single(Assert.Single(reloaded.Structure).Children);
        Assert.Collection(
            reloadedParagraph.Children,
            child => Assert.Equal("Link", child.Tag),
            child => Assert.Equal("Form", child.Tag));
        Assert.All(reloadedParagraph.Children, child =>
        {
            Assert.Equal("StructElem", child.Dictionary.GetName(PdfNames.Type)?.Value);
            Assert.Equal(reloadedParagraph.Id, child.Dictionary[PdfNames.P].AsReference);
            PdfDictionary objectReference = Assert.IsType<PdfDictionary>(child.Dictionary.Get(PdfNames.K).AsDictionary);
            Assert.Equal("OBJR", objectReference.GetName(PdfNames.Type)?.Value);

            PdfObjectId annotationId = Assert.Single(child.Objects).Object;
            PdfAnnotation annotation = Assert.Single(
                reloaded.AllAnnotations,
                candidate => candidate.Id == annotationId);
            int parentKey = (int)annotation.Dictionary.GetInteger(PdfNames.StructParent, -1);
            Assert.Equal(child.Id, ParentValue(reloaded, parentKey).AsReference);
        });
    }

    [Fact]
    public void UsesAlreadySemanticAnnotReferenceAndFormElementsAsDirectOwners()
    {
        using var original = PdfDocument.Create();
        PdfPage page = original.Pages.Add(PageSize.A4);
        var initial = new StructureTreeBuilder(original);
        StructureElement documentElement = initial.AddRoot("Document");
        documentElement.Add("Annot");
        documentElement.Add("Reference");
        documentElement.Add("Form");
        initial.Build();

        using var editable = PdfDocument.Load(original.ToArray());
        PdfPage editablePage = editable.Pages[0];
        PdfStructureNode root = Assert.Single(editable.Structure);
        PdfTextAnnotation note = editablePage.Annotations.AddNote(new PdfPoint(72, 700), "Direct note.");
        PdfLinkAnnotation link = editablePage.Annotations.AddLink(
            new PdfRectangle(72, 650, 180, 670), "https://example.org/reference");
        PdfAnnotation widget = AddWidget(editable, editablePage, new PdfRectangle(72, 610, 180, 635));

        var update = new StructureTreeBuilder(editable);
        update.AddAnnotation(root.Children[0], note);
        update.AddAnnotation(root.Children[1], link);
        update.AddAnnotation(root.Children[2], widget);
        update.Build();

        using PdfDocument reloaded = PdfDocument.Load(editable.ToArray());
        PdfStructureNode reloadedRoot = Assert.Single(reloaded.Structure);
        Assert.Collection(
            reloadedRoot.Children,
            child => AssertDirectOwner(child, "Annot", PdfAnnotationType.Text, reloaded),
            child => AssertDirectOwner(child, "Reference", PdfAnnotationType.Link, reloaded),
            child => AssertDirectOwner(child, "Form", PdfAnnotationType.Widget, reloaded));
    }

    [Fact]
    public void PreflightsParentTreeCapacityBeforeMutatingAnExistingOwner()
    {
        using var original = PdfDocument.Create();
        PdfPage page = original.Pages.Add(PageSize.A4);
        var initial = new StructureTreeBuilder(original);
        StructureElement paragraph = initial.AddRoot("Document").Add("P");
        paragraph.AddContent(page.Id, initial.NextMarkedContentId(page));
        initial.Build();

        using var editable = PdfDocument.Load(original.ToArray());
        PdfStructureNode existingParagraph = Assert.Single(Assert.Single(editable.Structure).Children);
        PdfDictionary root = editable.Catalog.GetDictionary(PdfNames.StructTreeRoot)!;
        PdfArray numbers = root.GetDictionary(PdfNames.ParentTree)!.GetArray(PdfNames.Nums)!;
        numbers.Add(PdfValue.Integer(int.MaxValue));
        numbers.Add(PdfValue.Reference(existingParagraph.Id));
        root.Set(PdfNames.ParentTreeNextKey, PdfValue.Integer(int.MaxValue));

        PdfTextAnnotation note = editable.Pages[0].Annotations.AddNote(
            new PdfPoint(72, 700), "Retry after preflight.");
        var update = new StructureTreeBuilder(editable);
        update.AddAnnotation(existingParagraph, note);

        Assert.Throws<InvalidOperationException>(() => update.Build());
        Assert.Equal(0, existingParagraph.Dictionary.GetInteger(PdfNames.K, -1));
        Assert.True(note.Dictionary.Get(PdfNames.StructParent).IsNull);

        numbers.RemoveAt(numbers.Count - 1);
        numbers.RemoveAt(numbers.Count - 1);
        root.Set(PdfNames.ParentTreeNextKey, PdfValue.Integer(1));
        update.Build();

        Assert.False(note.Dictionary.Get(PdfNames.StructParent).IsNull);
        Assert.Equal(2, Assert.IsType<PdfArray>(existingParagraph.Dictionary.Get(PdfNames.K).AsArray).Count);
    }

    [Fact]
    public void PreflightsNewContentConflictBeforeMutationAndAllowsRetryAfterRepair()
    {
        using var original = PdfDocument.Create();
        PdfPage page = original.Pages.Add(PageSize.A4);
        var initial = new StructureTreeBuilder(original);
        StructureElement originalParagraph = initial.AddRoot("Document").Add("P");
        originalParagraph.AddContent(page.Id, 0);
        initial.Build();

        using var editable = PdfDocument.Load(original.ToArray());
        PdfPage editablePage = editable.Pages[0];
        PdfStructureNode existingDocument = Assert.Single(editable.Structure);
        PdfStructureNode existingParagraph = Assert.Single(existingDocument.Children);
        PdfDictionary structureRoot = editable.Catalog.GetDictionary(PdfNames.StructTreeRoot)!;
        PdfValue originalRootKids = structureRoot.Get(PdfNames.K);

        var update = new StructureTreeBuilder(editable);
        StructureElement pendingParagraph = update.AddRoot("Sect").Add("P");
        pendingParagraph.AddContent(editablePage.Id, 0);

        Assert.Throws<InvalidOperationException>(() => update.Build());
        Assert.Equal(originalRootKids, structureRoot.Get(PdfNames.K));
        Assert.Equal(0, existingParagraph.Dictionary.GetInteger(PdfNames.K, -1));

        int pageKey = (int)editablePage.Dictionary.GetInteger(PdfNames.StructParents, -1);
        PdfValue parentArrayValue = ParentValue(editable, pageKey);
        PdfArray parentArray = Assert.IsType<PdfArray>(editable.Resolve(parentArrayValue).AsArray);
        parentArray[0] = PdfValue.Null;
        editable.MarkChanged(parentArrayValue.AsReference);
        existingParagraph.Dictionary.Remove(PdfNames.K);
        editable.MarkChanged(existingParagraph.Id);

        update.Build();

        PdfArray updatedRootKids = Assert.IsType<PdfArray>(structureRoot.Get(PdfNames.K).AsArray);
        Assert.Equal(2, updatedRootKids.Count);
        Assert.False(parentArrayValue.IsNull);
    }

    [Fact]
    public void RejectsDuplicatePendingMcidBeforeCreatingAStructureRoot()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        var builder = new StructureTreeBuilder(document);
        builder.AddRoot("P").AddContent(page.Id, 0);
        builder.AddRoot("P").AddContent(page.Id, 0);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("occurs more than once", exception.Message, StringComparison.Ordinal);
        Assert.True(document.Catalog.Get(PdfNames.StructTreeRoot).IsNull);
        Assert.True(document.Catalog.Get(PdfNames.MarkInfo).IsNull);
        _ = builder.AddRoot("Sect"); // A failed preflight does not consume the builder.
    }

    [Fact]
    public void PreflightsPageKeyCollisionWithAnObjectParentEntry()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "Owns key zero.");
        var initial = new StructureTreeBuilder(document);
        initial.AddRoot("Annot").AddAnnotation(note);
        initial.Build();

        PdfDictionary structureRoot = document.Catalog.GetDictionary(PdfNames.StructTreeRoot)!;
        PdfValue originalRootKids = structureRoot.Get(PdfNames.K);
        page.Dictionary.Set(PdfNames.StructParents, PdfValue.Integer(0));

        var update = new StructureTreeBuilder(document);
        update.AddRoot("P").AddContent(page.Id, 0);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => update.Build());

        Assert.Contains("non-page content item", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalRootKids, structureRoot.Get(PdfNames.K));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReservesStatedPageKeysBeforeAllocatingKeysForUnkeyedPages(bool keyedPageFirst)
    {
        using var document = PdfDocument.Create();
        PdfPage first = document.Pages.Add(PageSize.A4);
        PdfPage second = document.Pages.Add(PageSize.A4);
        PdfPage keyed = keyedPageFirst ? first : second;
        PdfPage unkeyed = keyedPageFirst ? second : first;
        keyed.Dictionary.Set(PdfNames.StructParents, PdfValue.Integer(0));

        var builder = new StructureTreeBuilder(document);
        StructureElement root = builder.AddRoot("Document");
        root.Add("P").AddContent(first.Id, 0);
        root.Add("P").AddContent(second.Id, 0);
        builder.Build();

        Assert.Equal(0, keyed.Dictionary.GetInteger(PdfNames.StructParents, -1));
        Assert.Equal(1, unkeyed.Dictionary.GetInteger(PdfNames.StructParents, -1));
        Assert.Equal(2, document.Catalog.GetDictionary(PdfNames.StructTreeRoot)!
            .GetInteger(PdfNames.ParentTreeNextKey, -1));
    }

    [Fact]
    public void RejectsAnInvalidIndexBeforeRegisteringTheAnnotation()
    {
        using var original = PdfDocument.Create();
        original.Pages.Add(PageSize.A4);
        var initial = new StructureTreeBuilder(original);
        initial.AddRoot("Document").Add("P");
        initial.Build();

        using var editable = PdfDocument.Load(original.ToArray());
        PdfStructureNode paragraph = Assert.Single(Assert.Single(editable.Structure).Children);
        PdfTextAnnotation note = editable.Pages[0].Annotations.AddNote(new PdfPoint(72, 700), "Still usable.");
        var update = new StructureTreeBuilder(editable);

        Assert.Throws<ArgumentOutOfRangeException>(() => update.AddAnnotation(paragraph, note, 1));
        update.AddAnnotation(paragraph, note);
        update.Build();

        PdfStructureNode updatedParagraph = Assert.Single(Assert.Single(editable.Structure).Children);
        Assert.Equal("Annot", Assert.Single(updatedParagraph.Children).Tag);
    }

    [Fact]
    public void RejectsAnAnnotationThatAlreadyHasAStructureParent()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "One owner only.");

        var first = new StructureTreeBuilder(document);
        first.AddRoot("Annot").AddAnnotation(note);
        first.Build();

        var second = new StructureTreeBuilder(document);
        StructureElement other = second.AddRoot("Annot");

        Assert.Throws<InvalidOperationException>(() => other.AddAnnotation(note));
    }

    private static void AssertAnnotationElement(
        PdfStructureKid kid,
        string contents,
        PdfDocument document)
    {
        PdfStructureNode element = Assert.IsType<PdfStructureElementKid>(kid).Element;
        Assert.Equal("Annot", element.Tag);
        PdfObjectId annotationId = Assert.Single(element.Objects).Object;
        Assert.Equal(contents, Assert.Single(document.AllAnnotations, annotation => annotation.Id == annotationId).Contents);
    }

    private static void AssertDirectOwner(
        PdfStructureNode element,
        string tag,
        PdfAnnotationType annotationType,
        PdfDocument document)
    {
        Assert.Equal(tag, element.Tag);
        Assert.Empty(element.Children);
        PdfObjectId annotationId = Assert.Single(element.Objects).Object;
        PdfAnnotation annotation = Assert.Single(document.AllAnnotations, candidate => candidate.Id == annotationId);
        Assert.Equal(annotationType, annotation.Type);

        PdfDictionary objectReference = Assert.IsType<PdfDictionary>(element.Dictionary.Get(PdfNames.K).AsDictionary);
        Assert.Equal("OBJR", objectReference.GetName(PdfNames.Type)?.Value);
        int parentKey = (int)annotation.Dictionary.GetInteger(PdfNames.StructParent, -1);
        Assert.Equal(element.Id, ParentValue(document, parentKey).AsReference);
    }

    private static PdfAnnotation AddWidget(
        PdfDocument document,
        PdfPage page,
        PdfRectangle rectangle)
    {
        var widget = new PdfDictionary(4);
        widget.Set(PdfNames.Type, PdfValue.Name("Annot"));
        widget.Set(PdfNames.Subtype, PdfValue.Name("Widget"));
        widget.Set(PdfName.Get("Rect"), PdfValue.Array(rectangle.ToArray()));
        PdfObjectId id = document.AddObject(PdfValue.Dictionary(widget));
        page.Annotations.RawArray.Add(PdfValue.Reference(id));
        return Assert.Single(page.Annotations, annotation => annotation.Id == id);
    }

    private static PdfValue ParentValue(PdfDocument document, int key)
    {
        PdfDictionary root = document.Catalog.GetDictionary(PdfNames.StructTreeRoot)!;
        PdfDictionary tree = root.GetDictionary(PdfNames.ParentTree)!;
        PdfArray numbers = tree.GetArray(PdfNames.Nums)!;

        for (int i = 0; i + 1 < numbers.Count; i += 2)
        {
            if (numbers.GetInteger(i, -1) == key)
                return numbers[i + 1];
        }

        return PdfValue.Null;
    }

    private static void AssertParentTreeOwner(
        PdfDocument document,
        PdfPage page,
        PdfObjectId expectedOwner,
        int mcid)
    {
        int key = (int)page.Dictionary.GetInteger(PdfNames.StructParents, -1);
        PdfArray parents = Assert.IsType<PdfArray>(document.Resolve(ParentValue(document, key)).AsArray);

        Assert.True(mcid < parents.Count);
        Assert.Equal(expectedOwner, parents[mcid].AsReference);
    }
}
