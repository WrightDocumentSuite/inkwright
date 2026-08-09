using System.Text;
using Inkwright.Annotations;
using Inkwright.Cos;
using Inkwright.Tagging;
using Xunit;

namespace Inkwright.Tests;

public class AnnotationStructureUnlinkTests
{
    [Fact]
    public void DeletingATaggedThreadAndAttachmentRemovesEveryStructuralAndPrivateReference()
    {
        const string RootText = "private-root-comment-4f6821";
        const string ReplyText = "private-reply-comment-4f6821";
        const string StateText = "private-state-comment-4f6821";
        const string AttachmentText = "private-attachment-caption-4f6821";
        byte[] payload = Encoding.ASCII.GetBytes("private-attachment-payload-4f6821");

        using var authored = PdfDocument.Create();
        PdfPage page = authored.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), RootText, "Reviewer");
        PdfTextAnnotation reply = note.Reply(ReplyText, "Author");
        PdfTextAnnotation state = note.SetReviewState(PdfReviewState.Completed, "Reviewer", StateText);
        PdfFileAttachmentAnnotation attachment = page.Annotations.AddAttachment(
            new PdfPoint(72, 650), "private-4f6821.txt", payload, AttachmentText, "Reviewer");

        var builder = new StructureTreeBuilder(authored);
        StructureElement root = builder.AddRoot("Document");
        root.Add("Annot").AddAnnotation(note);
        root.Add("Annot").AddAnnotation(reply);
        root.Add("Annot").AddAnnotation(state);
        root.Add("Annot").AddAnnotation(attachment);
        builder.Build();

        using PdfDocument editable = PdfDocument.Load(authored.ToArray());
        PdfPage editablePage = editable.Pages[0];
        PdfMarkupAnnotation editableNote = editablePage.Annotations.Comments
            .Single(annotation => annotation.Contents == RootText);
        Assert.Equal(2, editableNote.Replies.Count);
        PdfFileAttachmentAnnotation editableAttachment = Assert.IsType<PdfFileAttachmentAnnotation>(
            editablePage.Annotations.Comments.Single(annotation => annotation.Type == PdfAnnotationType.FileAttachment));

        PdfAnnotation[] removedAnnotations = [.. editablePage.Annotations];
        PdfObjectId payloadId = AttachmentPayloadId(editableAttachment);

        editableNote.Delete();
        editableAttachment.Delete();

        Assert.Empty(editablePage.Annotations);
        Assert.Empty(editable.Attachments.Names);
        Assert.All(removedAnnotations, annotation =>
            Assert.False(annotation.Dictionary.ContainsKey(PdfNames.StructParent)));
        Assert.Empty(ParentKeys(editable));

        PdfStructureNode documentElement = Assert.Single(editable.Structure);
        Assert.Equal("Document", documentElement.Tag);
        Assert.Empty(documentElement.Children);
        Assert.Empty(documentElement.Objects);

        byte[] sanitized = editable.ToArray(PrivacySaveOptions());
        string raw = Encoding.Latin1.GetString(sanitized);

        Assert.DoesNotContain(RootText, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplyText, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(StateText, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(AttachmentText, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("private-4f6821.txt", raw, StringComparison.Ordinal);

        using PdfDocument reloaded = PdfDocument.Load(sanitized);
        Assert.Empty(reloaded.Pages[0].Annotations);
        Assert.Empty(reloaded.Attachments.Names);
        Assert.Empty(ParentKeys(reloaded));
        Assert.All(removedAnnotations, annotation => Assert.True(reloaded.GetObject(annotation.Id).IsNull));
        Assert.True(reloaded.GetObject(payloadId).IsNull);
        Assert.Empty(Assert.Single(reloaded.Structure).Children);
    }

    [Fact]
    public void FlatteningATaggedCommentUnlinksItsThreadButKeepsThePaintedMark()
    {
        const string RootText = "flattened-root-private-9d3c";
        const string ReplyText = "flattened-reply-private-9d3c";

        using var authored = PdfDocument.Create();
        PdfPage page = authored.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), RootText, "Reviewer");
        PdfTextAnnotation reply = note.Reply(ReplyText, "Author");

        var builder = new StructureTreeBuilder(authored);
        StructureElement root = builder.AddRoot("Document");
        root.Add("Annot").AddAnnotation(note);
        root.Add("Annot").AddAnnotation(reply);
        builder.Build();

        using PdfDocument editable = PdfDocument.Load(authored.ToArray());
        PdfMarkupAnnotation editableNote = Assert.Single(editable.Pages[0].Annotations.Comments);
        PdfMarkupAnnotation editableReply = Assert.Single(editableNote.Replies);
        PdfObjectId noteId = editableNote.Id;
        PdfObjectId replyId = editableReply.Id;

        Assert.Equal(1, editable.FlattenComments());

        Assert.Empty(editable.Pages[0].Annotations);
        Assert.Empty(ParentKeys(editable));
        Assert.Empty(Assert.Single(editable.Structure).Children);
        Assert.NotNull(editable.Pages[0].Resources.GetDictionary(PdfName.Get("XObject")));
        Assert.Contains("/Artifact BMC", Encoding.ASCII.GetString(editable.Pages[0].GetContent()),
            StringComparison.Ordinal);

        byte[] sanitized = editable.ToArray(PrivacySaveOptions());
        string raw = Encoding.Latin1.GetString(sanitized);
        Assert.DoesNotContain(RootText, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplyText, raw, StringComparison.Ordinal);

        using PdfDocument reloaded = PdfDocument.Load(sanitized);
        Assert.Empty(reloaded.Pages[0].Annotations);
        Assert.True(reloaded.GetObject(noteId).IsNull);
        Assert.True(reloaded.GetObject(replyId).IsNull);
        Assert.NotNull(reloaded.Pages[0].Resources.GetDictionary(PdfName.Get("XObject")));
    }

    [Fact]
    public void DeletingOneOfTwoObjectsKeepsTheirSharedAnnotationElementAndOtherParentEntry()
    {
        using var authored = PdfDocument.Create();
        PdfPage page = authored.Pages.Add(PageSize.A4);
        PdfTextAnnotation first = page.Annotations.AddNote(new PdfPoint(72, 700), "remove-me-72a1");
        PdfTextAnnotation second = page.Annotations.AddNote(new PdfPoint(96, 700), "keep-me-72a1");

        var builder = new StructureTreeBuilder(authored);
        StructureElement owner = builder.AddRoot("Document").Add("Annot");
        owner.AddAnnotation(first);
        owner.AddAnnotation(second);
        builder.Build();

        using PdfDocument editable = PdfDocument.Load(authored.ToArray());
        PdfMarkupAnnotation removed = editable.Pages[0].Annotations.Comments
            .Single(annotation => annotation.Contents == "remove-me-72a1");
        PdfMarkupAnnotation kept = editable.Pages[0].Annotations.Comments
            .Single(annotation => annotation.Contents == "keep-me-72a1");
        int removedKey = (int)removed.Dictionary.GetInteger(PdfNames.StructParent, -1);
        int keptKey = (int)kept.Dictionary.GetInteger(PdfNames.StructParent, -1);

        removed.Delete();

        PdfStructureNode annotationElement = Assert.Single(Assert.Single(editable.Structure).Children);
        Assert.Equal("Annot", annotationElement.Tag);
        Assert.Equal(kept.Id, Assert.Single(annotationElement.Objects).Object);
        Assert.DoesNotContain(removedKey, ParentKeys(editable));
        Assert.Contains(keptKey, ParentKeys(editable));
        Assert.False(removed.Dictionary.ContainsKey(PdfNames.StructParent));
        Assert.True(kept.Dictionary.ContainsKey(PdfNames.StructParent));

        using PdfDocument reloaded = PdfDocument.Load(editable.ToIncrementalArray());
        Assert.Equal("keep-me-72a1", Assert.Single(reloaded.Pages[0].Annotations.Comments).Contents);
        Assert.Equal(keptKey, Assert.Single(ParentKeys(reloaded)));
        Assert.Single(Assert.Single(Assert.Single(reloaded.Structure).Children).Objects);
    }

    [Fact]
    public void DeletionRecoversAParentEntryWhoseObjrWasAlreadyMissing()
    {
        using var authored = PdfDocument.Create();
        PdfPage page = authored.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "malformed-link-3a91");

        var builder = new StructureTreeBuilder(authored);
        builder.AddRoot("Document").Add("Annot").AddAnnotation(note);
        builder.Build();

        PdfStructureNode annotationElement = Assert.Single(Assert.Single(authored.Structure).Children);
        int parentKey = (int)note.Dictionary.GetInteger(PdfNames.StructParent, -1);

        // Simulate a producer that removed only the forward OBJR and left both reverse links.
        annotationElement.Dictionary.Remove(PdfNames.K);
        authored.MarkChanged(annotationElement.Id);

        using PdfDocument editable = PdfDocument.Load(authored.ToArray());
        PdfMarkupAnnotation malformed = Assert.Single(editable.Pages[0].Annotations.Comments);
        Assert.Contains(parentKey, ParentKeys(editable));

        malformed.Delete();

        Assert.False(malformed.Dictionary.ContainsKey(PdfNames.StructParent));
        Assert.Empty(ParentKeys(editable));
        Assert.Empty(Assert.Single(editable.Structure).Children);
    }

    [Fact]
    public void RecoveryDoesNotRemoveAParentKeyClaimedByAnotherLiveAnnotation()
    {
        using var authored = PdfDocument.Create();
        PdfPage page = authored.Pages.Add(PageSize.A4);
        PdfTextAnnotation first = page.Annotations.AddNote(new PdfPoint(72, 700), "ambiguous-first-b817");
        PdfTextAnnotation second = page.Annotations.AddNote(new PdfPoint(96, 700), "ambiguous-second-b817");

        var builder = new StructureTreeBuilder(authored);
        StructureElement root = builder.AddRoot("Document");
        root.Add("Annot").AddAnnotation(first);
        root.Add("Annot").AddAnnotation(second);
        builder.Build();

        int sharedKey = (int)first.Dictionary.GetInteger(PdfNames.StructParent, -1);
        PdfStructureNode firstOwner = Assert.Single(
            Assert.Single(authored.Structure).Children,
            element => element.Objects.Any(reference => reference.Object == first.Id));

        firstOwner.Dictionary.Remove(PdfNames.K);
        second.Dictionary.Set(PdfNames.StructParent, PdfValue.Integer(sharedKey));
        authored.MarkChanged(firstOwner.Id);
        authored.MarkChanged(second.Id);

        using PdfDocument editable = PdfDocument.Load(authored.ToArray());
        PdfMarkupAnnotation removed = editable.Pages[0].Annotations.Comments
            .Single(annotation => annotation.Contents == "ambiguous-first-b817");
        PdfMarkupAnnotation survivor = editable.Pages[0].Annotations.Comments
            .Single(annotation => annotation.Contents == "ambiguous-second-b817");

        removed.Delete();

        Assert.Contains(sharedKey, ParentKeys(editable));
        Assert.Equal(sharedKey, survivor.Dictionary.GetInteger(PdfNames.StructParent, -1));
    }

    [Fact]
    public void DeletingAnnotationsPrunesOnlyLibraryCreatedLinkAndFormWrappers()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        var initial = new StructureTreeBuilder(document);
        initial.AddRoot("Document").Add("P");
        initial.Build();

        PdfStructureNode paragraph = Assert.Single(Assert.Single(document.Structure).Children);
        PdfLinkAnnotation link = page.Annotations.AddLink(
            new PdfRectangle(72, 650, 180, 670), "https://example.org/unlink");
        PdfAnnotation widget = AddWidget(document, page, new PdfRectangle(72, 610, 180, 635));
        var update = new StructureTreeBuilder(document);
        update.AddAnnotation(paragraph, link);
        update.AddAnnotation(paragraph, widget);
        update.Build();

        Assert.Collection(
            Assert.Single(document.Structure).Children.Single().Children,
            child => Assert.Equal("Link", child.Tag),
            child => Assert.Equal("Form", child.Tag));

        link.Delete();
        widget.Delete();

        PdfStructureNode updatedParagraph = Assert.Single(Assert.Single(document.Structure).Children);
        Assert.Empty(updatedParagraph.Children);
        Assert.Empty(ParentKeys(document));
    }

    [Fact]
    public void FlatteningPrunesTheLibraryCreatedAnnotWrapperButKeepsItsUserParent()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        var initial = new StructureTreeBuilder(document);
        initial.AddRoot("Document").Add("P");
        initial.Build();

        PdfStructureNode paragraph = Assert.Single(Assert.Single(document.Structure).Children);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "Flatten auto wrapper.");
        var update = new StructureTreeBuilder(document);
        update.AddAnnotation(paragraph, note);
        update.Build();

        Assert.Equal("Annot", Assert.Single(Assert.Single(document.Structure).Children).Children.Single().Tag);
        Assert.Equal(1, document.FlattenComments());

        PdfStructureNode updatedParagraph = Assert.Single(Assert.Single(document.Structure).Children);
        Assert.Equal("P", updatedParagraph.Tag);
        Assert.Empty(updatedParagraph.Children);
        Assert.Empty(ParentKeys(document));
    }

    [Fact]
    public void DeletingFromANewTreeAnnotPrunesItsEmptyAnnotationOwner()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfTextAnnotation note = page.Annotations.AddNote(new PdfPoint(72, 700), "Preserve my owner.");
        var builder = new StructureTreeBuilder(document);
        builder.AddRoot("Document").Add("Annot").AddAnnotation(note);
        builder.Build();

        note.Delete();

        Assert.Empty(Assert.Single(document.Structure).Children);
        Assert.Empty(ParentKeys(document));
    }

    [Fact]
    public void DeletingFromAuthoredLinkAndFormOwnersRetainsTheirSemanticMetadata()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        var initial = new StructureTreeBuilder(document);
        StructureElement root = initial.AddRoot("Document");
        StructureElement linkOwner = root.Add("Link");
        linkOwner.AlternateText = "Authored link owner";
        StructureElement formOwner = root.Add("Form");
        formOwner.AlternateText = "Authored form owner";
        initial.Build();

        PdfStructureNode authoredRoot = Assert.Single(document.Structure);
        PdfLinkAnnotation link = page.Annotations.AddLink(
            new PdfRectangle(72, 650, 180, 670), "https://example.org/preserve-owner");
        PdfAnnotation widget = AddWidget(document, page, new PdfRectangle(72, 610, 180, 635));
        var update = new StructureTreeBuilder(document);
        update.AddAnnotation(authoredRoot.Children[0], link);
        update.AddAnnotation(authoredRoot.Children[1], widget);
        update.Build();

        link.Delete();
        widget.Delete();

        PdfStructureNode reloadedRoot = Assert.Single(document.Structure);
        Assert.Collection(
            reloadedRoot.Children,
            owner =>
            {
                Assert.Equal("Link", owner.Tag);
                Assert.Equal("Authored link owner", owner.AlternateText);
                Assert.Empty(owner.Kids);
            },
            owner =>
            {
                Assert.Equal("Form", owner.Tag);
                Assert.Equal("Authored form owner", owner.AlternateText);
                Assert.Empty(owner.Kids);
            });
    }

    private static IReadOnlyList<int> ParentKeys(PdfDocument document)
    {
        PdfDictionary? root = document.Catalog.GetDictionary(PdfNames.StructTreeRoot);
        return [.. PdfNumberTree.Enumerate(root?.GetDictionary(PdfNames.ParentTree)).Select(entry => entry.Key)];
    }

    private static PdfObjectId AttachmentPayloadId(PdfFileAttachmentAnnotation attachment)
    {
        PdfDictionary specification = attachment.Dictionary.GetDictionary(PdfName.Get("FS"))!;
        PdfDictionary embedded = specification.GetDictionary(PdfName.Get("EF"))!;
        Assert.True(embedded.TryGetRaw(PdfName.Get("F"), out PdfValue payload));
        Assert.True(payload.IsReference);
        return payload.AsReference;
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

    private static PdfSaveOptions PrivacySaveOptions() => new()
    {
        RemoveUnusedObjects = true,
        UseObjectStreams = false,
        Compress = false,
    };
}
