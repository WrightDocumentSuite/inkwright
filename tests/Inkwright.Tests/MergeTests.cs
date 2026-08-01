using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Fonts;
using Xunit;

namespace Inkwright.Tests;

public class MergeTests
{
    [Fact]
    public void JoinsDocumentsInTheOrderGiven()
    {
        using PdfDocument first = Build("first", pages: 2);
        using PdfDocument second = Build("second", pages: 3);

        using PdfDocument merged = PdfDocument.Merge(first, second);

        Assert.Equal(5, merged.Pages.Count);
        Assert.Contains("first 1", merged.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Contains("second 1", merged.Pages[2].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsTheBookmarksOfBothDocuments()
    {
        using PdfDocument first = Build("first", pages: 2, outline: true);
        using PdfDocument second = Build("second", pages: 2, outline: true);

        using PdfDocument merged = PdfDocument.Merge(first, second);
        using PdfDocument reloaded = PdfDocument.Load(merged.ToArray());

        List<string> titles = [.. Titles(reloaded)];
        Assert.Equal(["first page 1", "first page 2", "second page 1", "second page 2"], titles);

        // Each bookmark still leads to its own page rather than to whatever was at that index.
        Assert.Equal(reloaded.Pages[2].Id, DestinationPage(reloaded, "second page 1"));
    }

    [Fact]
    public void DropsBookmarksWhosePagesWereLeftBehind()
    {
        using PdfDocument source = Build("book", pages: 4, outline: true);

        using PdfDocument extracted = source.ExtractPages(0, 2);
        using PdfDocument reloaded = PdfDocument.Load(extracted.ToArray());

        Assert.Equal(2, reloaded.Pages.Count);
        Assert.Equal(["book page 1", "book page 3"], Titles(reloaded));
    }

    [Fact]
    public void RenamesDestinationsThatWouldCollide()
    {
        using PdfDocument first = Build("first", pages: 1, destination: "start");
        using PdfDocument second = Build("second", pages: 1, destination: "start");

        using PdfDocument merged = PdfDocument.Merge(first, second);
        using PdfDocument reloaded = PdfDocument.Load(merged.ToArray());

        PdfDictionary? tree = reloaded.Catalog.GetDictionary(PdfNames.Names)?.GetDictionary(PdfNames.Dests);
        List<string> names = [.. PdfNameTree.Enumerate(tree).Select(entry => entry.Key)];

        Assert.Equal(["start", "start (2)"], names);
        Assert.Equal(reloaded.Pages[0].Id, Page(PdfNameTree.Find(tree, "start")));
        Assert.Equal(reloaded.Pages[1].Id, Page(PdfNameTree.Find(tree, "start (2)")));
    }

    [Fact]
    public void ShiftsPageLabelsToWhereThePagesLanded()
    {
        using PdfDocument first = Build("first", pages: 2);
        first.PageLabels.Set([new PageLabelRange(0, PageLabelStyle.LowercaseRoman)]);

        using PdfDocument second = Build("second", pages: 2);
        second.PageLabels.Set([new PageLabelRange(0, PageLabelStyle.Decimal, "B-")]);

        using PdfDocument merged = PdfDocument.Merge(first, second);
        using PdfDocument reloaded = PdfDocument.Load(merged.ToArray());

        Assert.Equal(["i", "ii", "B-1", "B-2"], reloaded.PageLabels.All());
    }

    [Fact]
    public void CarriesAttachmentsAcrossAndRenamesTheClashes()
    {
        using PdfDocument first = Build("first", pages: 1);
        first.Attachments.Add("data.xml", "<first/>"u8.ToArray());

        using PdfDocument second = Build("second", pages: 1);
        second.Attachments.Add("data.xml", "<second/>"u8.ToArray());

        using PdfDocument merged = PdfDocument.Merge(first, second);
        using PdfDocument reloaded = PdfDocument.Load(merged.ToArray());

        Assert.Equal(["data.xml", "data.xml (2)"], reloaded.Attachments.Names.Order(StringComparer.Ordinal));
        Assert.Equal("<first/>"u8.ToArray(), reloaded.Attachments.Read("data.xml"));
        Assert.Equal("<second/>"u8.ToArray(), reloaded.Attachments.Read("data.xml (2)"));
    }

    [Fact]
    public void KeepsHiddenLayersHidden()
    {
        using PdfDocument first = Build("first", pages: 1);
        using PdfDocument second = Build("second", pages: 1, hiddenLayer: true);

        using PdfDocument merged = PdfDocument.Merge(first, second);
        using PdfDocument reloaded = PdfDocument.Load(merged.ToArray());

        PdfLayer layer = Assert.Single(reloaded.OptionalContent.Layers);
        Assert.Equal("Draft", layer.Name);
        Assert.False(layer.IsVisible);
    }

    [Fact]
    public void RenamesFormFieldsThatWouldCollide()
    {
        using PdfDocument first = BuildForm("first");
        using PdfDocument second = BuildForm("second");

        using PdfDocument merged = PdfDocument.Merge(first, second);
        using PdfDocument reloaded = PdfDocument.Load(merged.ToArray());

        PdfArray fields = reloaded.Catalog.GetDictionary(PdfNames.AcroForm)!.GetArray(PdfNames.Fields)!;
        List<string?> names = [];
        for (int i = 0; i < fields.Count; i++)
            names.Add(fields.GetDictionary(i)?.GetTextString(PdfNames.T));

        Assert.Equal(["name", "name (2)"], names);

        // Each field still holds what its own document put in it.
        Assert.Equal("first", fields.GetDictionary(0)!.GetTextString(PdfName.Get("V")));
        Assert.Equal("second", fields.GetDictionary(1)!.GetTextString(PdfName.Get("V")));
    }

    [Fact]
    public void ShiftsStructureKeysSoBothTreesSurvive()
    {
        using PdfDocument first = BuildTagged("first");
        using PdfDocument second = BuildTagged("second");

        using PdfDocument merged = PdfDocument.Merge(first, second);
        using PdfDocument reloaded = PdfDocument.Load(merged.ToArray());

        PdfDictionary root = reloaded.Catalog.GetDictionary(PdfNames.StructTreeRoot)!;

        // The second document's page can no longer claim the key the first one already used.
        Assert.NotEqual(
            reloaded.Pages[0].Dictionary.GetInteger(PdfNames.StructParents),
            reloaded.Pages[1].Dictionary.GetInteger(PdfNames.StructParents));

        List<int> keys = [.. PdfNumberTree.Enumerate(root.GetDictionary(PdfNames.ParentTree)).Select(entry => entry.Key)];
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(2, root.GetArray(PdfNames.K)!.Count);
    }

    [Fact]
    public void LeavesTheSourceDocumentsAlone()
    {
        using PdfDocument first = Build("first", pages: 2, outline: true);
        using PdfDocument second = Build("second", pages: 1);

        using (PdfDocument merged = PdfDocument.Merge(first, second))
            Assert.Equal(3, merged.Pages.Count);

        Assert.Equal(2, first.Pages.Count);
        Assert.Equal(["first page 1", "first page 2"], Titles(first));
    }

    private static IEnumerable<string> Titles(PdfDocument document)
    {
        PdfDictionary? item = document.Catalog.GetDictionary(PdfNames.Outlines)?.GetDictionary(PdfNames.First);

        while (item is not null)
        {
            if (item.GetTextString(PdfNames.Title) is { } title)
                yield return title;

            item = item.GetDictionary(PdfNames.Next);
        }
    }

    private static PdfObjectId DestinationPage(PdfDocument document, string title)
    {
        PdfDictionary? item = document.Catalog.GetDictionary(PdfNames.Outlines)?.GetDictionary(PdfNames.First);

        while (item is not null)
        {
            if (item.GetTextString(PdfNames.Title) == title)
                return Page(item.Get(PdfNames.Dest));

            item = item.GetDictionary(PdfNames.Next);
        }

        return default;
    }

    private static PdfObjectId Page(PdfValue destination) =>
        destination.AsArray is { Count: > 0 } array && array[0].IsReference ? array[0].AsReference : default;

    private static PdfDocument Build(
        string label, int pages, bool outline = false, string? destination = null, bool hiddenLayer = false)
    {
        var document = PdfDocument.Create();
        PdfFont font = document.Fonts.Helvetica;
        List<PdfPage> added = [];

        for (int i = 0; i < pages; i++)
        {
            PdfPage page = document.Pages.Add();
            added.Add(page);

            using ContentCanvas canvas = ContentCanvas.ForPage(page);
            canvas.DrawText($"{label} {i + 1}", 72, 700, font, 18);
            canvas.Commit();
        }

        if (outline)
            AddOutline(document, label, added);

        if (destination is not null)
            AddDestination(document, destination, added[0]);

        if (hiddenLayer)
            AddHiddenLayer(document);

        return document;
    }

    private static void AddOutline(PdfDocument document, string label, List<PdfPage> pages)
    {
        var root = new PdfDictionary(3);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.Outlines));
        PdfObjectId rootId = document.AddObject(PdfValue.Dictionary(root));

        List<(PdfDictionary Item, PdfObjectId Id)> items = [];

        for (int i = 0; i < pages.Count; i++)
        {
            var destination = new PdfArray(2);
            destination.Add(PdfValue.Reference(pages[i].Id));
            destination.Add(PdfValue.Name(PdfNames.Fit));

            var item = new PdfDictionary(4);
            item.Set(PdfNames.Title, PdfValue.TextString($"{label} page {i + 1}"));
            item.Set(PdfNames.Dest, PdfValue.Array(destination));
            item.Set(PdfNames.Parent, PdfValue.Reference(rootId));

            items.Add((item, document.AddObject(PdfValue.Dictionary(item))));
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
                items[i].Item.Set(PdfNames.Prev, PdfValue.Reference(items[i - 1].Id));

            if (i < items.Count - 1)
                items[i].Item.Set(PdfNames.Next, PdfValue.Reference(items[i + 1].Id));
        }

        root.Set(PdfNames.First, PdfValue.Reference(items[0].Id));
        root.Set(PdfNames.Last, PdfValue.Reference(items[^1].Id));
        root.Set(PdfNames.Count, PdfValue.Integer(items.Count));
        document.Catalog.Set(PdfNames.Outlines, PdfValue.Reference(rootId));
    }

    private static void AddDestination(PdfDocument document, string name, PdfPage page)
    {
        var destination = new PdfArray(2);
        destination.Add(PdfValue.Reference(page.Id));
        destination.Add(PdfValue.Name(PdfNames.Fit));

        var entries = new PdfArray(2);
        entries.Add(PdfValue.TextString(name));
        entries.Add(PdfValue.Array(destination));

        var tree = new PdfDictionary(1);
        tree.Set(PdfNames.Names, PdfValue.Array(entries));

        var names = new PdfDictionary(1);
        names.Set(PdfNames.Dests, PdfValue.Dictionary(tree));

        document.Attach(PdfValue.Dictionary(names));
        document.Catalog.Set(PdfNames.Names, PdfValue.Dictionary(names));
    }

    private static void AddHiddenLayer(PdfDocument document)
    {
        var group = new PdfDictionary(2);
        group.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("OCG")));
        group.Set(PdfName.Get("Name"), PdfValue.TextString("Draft"));
        PdfValue reference = PdfValue.Reference(document.AddObject(PdfValue.Dictionary(group)));

        var groups = new PdfArray(1);
        groups.Add(reference);

        var off = new PdfArray(1);
        off.Add(reference);

        var configuration = new PdfDictionary(1);
        configuration.Set(PdfName.Get("OFF"), PdfValue.Array(off));

        var properties = new PdfDictionary(2);
        properties.Set(PdfName.Get("OCGs"), PdfValue.Array(groups));
        properties.Set(PdfName.Get("D"), PdfValue.Dictionary(configuration));

        document.Attach(PdfValue.Dictionary(properties));
        document.Catalog.Set(PdfName.Get("OCProperties"), PdfValue.Dictionary(properties));
    }

    private static PdfDocument BuildForm(string value)
    {
        var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var widget = new PdfDictionary(8);
        widget.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Annot")));
        widget.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Widget")));
        widget.Set(PdfName.Get("FT"), PdfValue.Name(PdfName.Get("Tx")));
        widget.Set(PdfNames.T, PdfValue.TextString("name"));
        widget.Set(PdfName.Get("V"), PdfValue.TextString(value));
        widget.Set(PdfName.Get("Rect"), PdfValue.Array(PdfArray.OfNumbers(60, 700, 260, 722)));
        widget.Set(PdfName.Get("P"), PdfValue.Reference(page.Id));

        PdfObjectId widgetId = document.AddObject(PdfValue.Dictionary(widget));

        var annotations = new PdfArray(1);
        annotations.Add(PdfValue.Reference(widgetId));
        page.Dictionary.Set(PdfNames.Annots, PdfValue.Array(annotations));

        var fields = new PdfArray(1);
        fields.Add(PdfValue.Reference(widgetId));

        var form = new PdfDictionary(1);
        form.Set(PdfNames.Fields, PdfValue.Array(fields));
        document.Attach(PdfValue.Dictionary(form));
        document.Catalog.Set(PdfNames.AcroForm, PdfValue.Dictionary(form));

        return document;
    }

    private static PdfDocument BuildTagged(string label)
    {
        var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        page.Dictionary.Set(PdfNames.StructParents, PdfValue.Integer(0));

        var root = new PdfDictionary(3);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        PdfObjectId rootId = document.AddObject(PdfValue.Dictionary(root));

        var element = new PdfDictionary(4);
        element.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("StructElem")));
        element.Set(PdfNames.S, PdfValue.Name(PdfName.Get("P")));
        element.Set(PdfName.Get("Alt"), PdfValue.TextString(label));
        element.Set(PdfNames.P, PdfValue.Reference(rootId));
        element.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
        element.Set(PdfNames.K, PdfValue.Integer(0));

        PdfObjectId elementId = document.AddObject(PdfValue.Dictionary(element));

        var children = new PdfArray(1);
        children.Add(PdfValue.Reference(elementId));
        root.Set(PdfNames.K, PdfValue.Array(children));

        var numbers = new PdfArray(2);
        numbers.Add(PdfValue.Integer(0));
        numbers.Add(PdfValue.Reference(elementId));

        var parentTree = new PdfDictionary(1);
        parentTree.Set(PdfNames.Nums, PdfValue.Array(numbers));
        document.Attach(PdfValue.Dictionary(parentTree));

        root.Set(PdfNames.ParentTree, PdfValue.Dictionary(parentTree));
        root.Set(PdfNames.ParentTreeNextKey, PdfValue.Integer(1));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));

        return document;
    }
}
