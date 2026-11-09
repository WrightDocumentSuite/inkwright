using System.Text;
using Inkwright.Cos;
using Inkwright.Images;
using Inkwright.Text;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Redaction has to remove every representation of the selected content and nothing else: the
/// text alternatives that restate it, and none of the content other pages draw from shared objects.
/// </summary>
public sealed class RedactionSemanticsTests
{
    private static readonly TextExtractionOptions WithActualText = new() { UseActualText = true };
    private static readonly PdfSaveOptions FlatRewrite = new()
    {
        RemoveUnusedObjects = true,
        Compress = false,
        UseObjectStreams = false,
    };

    private static readonly PdfName Fm = PdfName.Get("Fm");

    private static PdfValue HelveticaResources()
    {
        var font = new PdfDictionary();
        font.Set(PdfNames.Type, PdfValue.Name(PdfNames.Font));
        font.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Type1")));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name(PdfName.Get("Helvetica")));
        font.Set(PdfNames.Encoding, PdfValue.Name(PdfName.Get("WinAnsiEncoding")));

        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("F1"), PdfValue.Dictionary(font));
        return PdfValue.Dictionary(fonts);
    }

    private static void SetPage(PdfPage page, string content)
    {
        page.Resources.Set(PdfNames.Font, HelveticaResources());
        page.SetContent(Encoding.ASCII.GetBytes(content));
    }

    private static string Text(string value, int y = 700) => $"BT /F1 12 Tf 72 {y} Td ({value}) Tj ET";

    private static PdfObjectId AddForm(PdfDocument document, string content, PdfDictionary? xObjects = null)
    {
        var resources = new PdfDictionary();
        resources.Set(PdfNames.Font, HelveticaResources());
        if (xObjects is not null)
            resources.Set(PdfNames.XObject, PdfValue.Dictionary(xObjects));

        PdfStream form = PdfStream.FromData(Encoding.ASCII.GetBytes(content), compress: false);
        form.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        form.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Form));
        form.Dictionary.Set(PdfName.Get("BBox"), PdfValue.Array(PdfArray.OfNumbers(0, 0, 600, 100)));
        form.Dictionary.Set(PdfNames.Resources, PdfValue.Dictionary(resources));
        return document.AddObject(PdfValue.Stream(form));
    }

    private static void UseForm(PdfPage page, PdfObjectId form, string content)
    {
        var xObjects = new PdfDictionary();
        xObjects.Set(Fm, PdfValue.Reference(form));
        page.Resources.Set(PdfNames.XObject, PdfValue.Dictionary(xObjects));
        SetPage(page, content);
    }

    private static string FormAt(int y) => $"q 1 0 0 1 72 {y} cm /Fm Do Q";

    /// <summary>The whole saved file as text, with every stream decoded, however it was stored.</summary>
    private static string SavedFileText(PdfDocument document)
    {
        byte[] saved = document.ToArray(FlatRewrite);
        var text = new StringBuilder(Encoding.Latin1.GetString(saved));

        using var reloaded = PdfDocument.Load(saved);
        foreach (int number in reloaded.Reader!.LiveObjectNumbers)
        {
            if (reloaded.Reader.GetObject(number).AsStream is { } stream && stream.UndecodedFilter is null)
                text.Append('\n').Append(Encoding.Latin1.GetString(stream.DecodedSpan()));
        }

        return text.ToString();
    }

    private static void AssertNotInFile(PdfDocument document, string secret) =>
        Assert.DoesNotContain(secret, SavedFileText(document), StringComparison.Ordinal);

    private static int Occurrences(string text, string value) => text.Split(value).Length - 1;

    [Fact]
    public void Redact_RemovesActualTextOfFullyRedactedSequence()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        SetPage(page, $"/Span << /ActualText (SECRET) >> BDC {Text("SECRET")} EMC");

        // The check below has to be able to see the secret for its absence to mean anything.
        Assert.Contains("/ActualText (SECRET)", SavedFileText(document), StringComparison.Ordinal);

        PdfRedactionResult result = page.RedactText("SECRET");

        Assert.Equal(6, result.GlyphsRemoved);
        Assert.Equal(1, result.SemanticEntriesRemoved);
        Assert.DoesNotContain("SECRET", page.ExtractText(WithActualText), StringComparison.Ordinal);
        AssertNotInFile(document, "SECRET");

        using var reloaded = PdfDocument.Load(document.ToArray(FlatRewrite));
        Assert.DoesNotContain("SECRET", reloaded.Pages[0].ExtractText(WithActualText), StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_PartialOverlapFallsBackToSurvivingGlyphs()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        SetPage(page, $"/Span << /ActualText (PUBLIC SECRET) /Lang (en) >> BDC {Text("PUBLIC SECRET")} EMC");

        page.RedactText("SECRET");

        string text = page.ExtractText(WithActualText);
        Assert.Contains("PUBLIC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        AssertNotInFile(document, "SECRET");

        // Only the text alternatives go; the rest of the property list still applies.
        Assert.Contains("/Lang", Encoding.ASCII.GetString(page.GetContent()), StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RemovesAltAndExpansionFromEnclosingSequences()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        SetPage(page, $"/Figure << /Alt (SECRET plan) >> BDC /Abbr << /E (SECRET expansion) >> BDC {Text("SECRET")} EMC EMC");

        PdfRedactionResult result = page.RedactText("SECRET");

        Assert.Equal(2, result.SemanticEntriesRemoved);
        AssertNotInFile(document, "SECRET");
    }

    [Fact]
    public void Redact_LeavesTextAlternativesOfUntouchedSequences()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        SetPage(page, $"/Span << /ActualText (KEPT) >> BDC {Text("Other", 500)} EMC {Text("SECRET")}");

        PdfRedactionResult result = page.RedactText("SECRET");

        Assert.Equal(0, result.SemanticEntriesRemoved);
        Assert.Contains("KEPT", page.ExtractText(WithActualText), StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_SequenceAroundFormIsScrubbedWhenFormContentIsRemoved()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        PdfObjectId form = AddForm(document, "BT /F1 12 Tf 0 0 Td (SECRET) Tj ET");
        UseForm(page, form, $"/Span << /ActualText (SECRET) >> BDC {FormAt(700)} EMC");

        PdfRedactionResult result = page.RedactText("SECRET");

        Assert.Equal(1, result.SemanticEntriesRemoved);
        AssertNotInFile(document, "SECRET");
    }

    private static PdfObjectId AddPropertyList(PdfDocument document, string actualText)
    {
        var properties = new PdfDictionary();
        properties.Set(PdfNames.ActualText, PdfValue.TextString(actualText));
        properties.Set(PdfName.Get("Owner"), PdfValue.Name(PdfName.Get("Editor")));
        return document.AddObject(PdfValue.Dictionary(properties));
    }

    [Fact]
    public void Redact_NamedPropertyListIsReplacedAndRemovedWhenUnused()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        var lists = new PdfDictionary();
        lists.Set(PdfName.Get("MC0"), PdfValue.Reference(AddPropertyList(document, "SECRET")));
        page.Resources.Set(PdfNames.Properties, PdfValue.Dictionary(lists));
        SetPage(page, $"/Span /MC0 BDC {Text("SECRET")} EMC");

        PdfRedactionResult result = page.RedactText("SECRET");

        Assert.Equal(1, result.SemanticEntriesRemoved);
        Assert.False(lists.ContainsKey(PdfName.Get("MC0")));
        AssertNotInFile(document, "SECRET");

        // Entries other than the text alternatives stay attached to the sequence.
        Assert.Contains("/Editor", SavedFileText(document), StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_NamedPropertyListStaysWhenAnotherPageStillUsesIt()
    {
        using var document = PdfDocument.Create();
        PdfPage first = document.Pages.Add(PageSize.Letter);
        PdfPage second = document.Pages.Add(PageSize.Letter);

        var lists = new PdfDictionary();
        lists.Set(PdfName.Get("MC0"), PdfValue.Reference(AddPropertyList(document, "SECRET")));
        first.Resources.Set(PdfNames.Properties, PdfValue.Dictionary(lists));
        second.Resources.Set(PdfNames.Properties, PdfValue.Dictionary(lists));
        SetPage(first, $"/Span /MC0 BDC {Text("SECRET")} EMC");
        SetPage(second, $"/Span /MC0 BDC {Text("SECRET")} EMC");

        first.RedactText("SECRET");

        Assert.DoesNotContain("SECRET", first.ExtractText(WithActualText), StringComparison.Ordinal);
        Assert.Contains("SECRET", second.ExtractText(WithActualText), StringComparison.Ordinal);
        Assert.True(lists.ContainsKey(PdfName.Get("MC0")));
    }

    /// <summary>A two-level tree whose elements claim marked content zero and one of the page.</summary>
    private static (PdfDictionary Section, PdfDictionary Hit, PdfDictionary Other) AddStructure(PdfDocument document, PdfPage page)
    {
        var root = new PdfDictionary();
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        PdfObjectId rootId = document.AddObject(PdfValue.Dictionary(root));

        PdfDictionary Element(string tag, PdfValue parent, string? actualText, int? mcid)
        {
            var element = new PdfDictionary();
            element.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("StructElem")));
            element.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get(tag)));
            element.Set(PdfNames.P, parent);
            element.Set(PdfName.Get("Pg"), PdfValue.Reference(page.Id));
            if (actualText is not null)
                element.Set(PdfNames.ActualText, PdfValue.TextString(actualText));
            if (mcid is { } id)
                element.Set(PdfName.Get("K"), PdfValue.Integer(id));
            return element;
        }

        PdfDictionary section = Element("Sect", PdfValue.Reference(rootId), "Section with SECRET", null);
        PdfObjectId sectionId = document.AddObject(PdfValue.Dictionary(section));
        PdfDictionary hit = Element("P", PdfValue.Reference(sectionId), "SECRET", 0);
        hit.Set(PdfNames.Alt, PdfValue.TextString("SECRET alternative"));
        PdfObjectId hitId = document.AddObject(PdfValue.Dictionary(hit));
        PdfDictionary other = Element("P", PdfValue.Reference(rootId), "KEPT", 1);
        PdfObjectId otherId = document.AddObject(PdfValue.Dictionary(other));

        var sectionKids = new PdfArray();
        sectionKids.Add(PdfValue.Reference(hitId));
        section.Set(PdfName.Get("K"), PdfValue.Array(sectionKids));

        var rootKids = new PdfArray();
        rootKids.Add(PdfValue.Reference(sectionId));
        rootKids.Add(PdfValue.Reference(otherId));
        root.Set(PdfName.Get("K"), PdfValue.Array(rootKids));

        var owners = new PdfArray();
        owners.Add(PdfValue.Reference(hitId));
        owners.Add(PdfValue.Reference(otherId));
        var numbers = new PdfArray();
        numbers.Add(PdfValue.Integer(0));
        numbers.Add(PdfValue.Array(owners));
        var parentTree = new PdfDictionary();
        parentTree.Set(PdfNames.Nums, PdfValue.Array(numbers));
        root.Set(PdfNames.ParentTree, PdfValue.Dictionary(parentTree));

        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        page.Dictionary.Set(PdfNames.StructParents, PdfValue.Integer(0));
        return (section, hit, other);
    }

    [Fact]
    public void Redact_ScrubsOwningStructureElementAndAncestorsOnly()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        (PdfDictionary section, PdfDictionary hit, PdfDictionary other) = AddStructure(document, page);
        SetPage(page, $"/P << /MCID 0 >> BDC {Text("SECRET")} EMC /P << /MCID 1 >> BDC {Text("Other", 500)} EMC");

        PdfRedactionResult result = page.RedactText("SECRET");

        Assert.Equal(3, result.SemanticEntriesRemoved);
        Assert.False(hit.ContainsKey(PdfNames.ActualText));
        Assert.False(hit.ContainsKey(PdfNames.Alt));
        Assert.False(section.ContainsKey(PdfNames.ActualText));
        Assert.True(other.ContainsKey(PdfNames.ActualText));
        AssertNotInFile(document, "SECRET");
    }

    [Fact]
    public void Redact_FindsOwningElementWithoutParentTree()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        (_, PdfDictionary hit, PdfDictionary other) = AddStructure(document, page);
        document.Catalog.GetDictionary(PdfNames.StructTreeRoot)!.Remove(PdfNames.ParentTree);
        SetPage(page, $"/P << /MCID 0 >> BDC {Text("SECRET")} EMC /P << /MCID 1 >> BDC {Text("Other", 500)} EMC");

        page.RedactText("SECRET");

        Assert.False(hit.ContainsKey(PdfNames.ActualText));
        Assert.True(other.ContainsKey(PdfNames.ActualText));
    }

    [Fact]
    public void Redact_RefusesFormsNestedBeyondTheLimit()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);

        PdfObjectId current = AddForm(document, "BT /F1 12 Tf 0 0 Td (SECRET) Tj ET");
        for (int i = 0; i < 14; i++)
        {
            var xObjects = new PdfDictionary();
            xObjects.Set(Fm, PdfValue.Reference(current));
            current = AddForm(document, "/Fm Do", xObjects);
        }

        UseForm(page, current, FormAt(700));
        byte[] before = page.GetContent();

        Assert.Throws<PdfRedactionException>(() => page.Redact(new PdfRectangle(60, 690, 300, 720)));
        Assert.Equal(before, page.GetContent());
    }

    [Fact]
    public void Redact_RefusesFormThatDrawsItself()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);

        var xObjects = new PdfDictionary();
        PdfObjectId form = AddForm(document, "/Fm Do", xObjects);
        xObjects.Set(Fm, PdfValue.Reference(form));
        UseForm(page, form, FormAt(700));

        Assert.Throws<PdfRedactionException>(() => page.Redact(new PdfRectangle(60, 690, 300, 720)));
    }

    [Fact]
    public void Redact_RefusesTextInAFontItCannotResolve()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        SetPage(page, "BT /Missing 12 Tf 72 700 Td (SECRET) Tj ET");

        Assert.Throws<PdfRedactionException>(() => page.Redact(new PdfRectangle(60, 690, 300, 720)));
    }

    [Fact]
    public void Redact_MeasuresTextWhoseFontComesFromExtGState()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        SetPage(page, "/GS0 gs BT 72 700 Td (SECRET) Tj ET");

        PdfDictionary fonts = page.Resources.GetDictionary(PdfNames.Font)!;
        var fontEntry = new PdfArray();
        fontEntry.Add(fonts[PdfName.Get("F1")]);
        fontEntry.Add(PdfValue.Number(12));
        var state = new PdfDictionary();
        state.Set(PdfNames.Font, PdfValue.Array(fontEntry));
        var states = new PdfDictionary();
        states.Set(PdfName.Get("GS0"), PdfValue.Dictionary(state));
        page.Resources.Set(PdfNames.ExtGState, PdfValue.Dictionary(states));

        PdfRedactionResult result = page.Redact(new PdfRectangle(60, 690, 300, 720));

        Assert.Equal(6, result.GlyphsRemoved);
    }

    [Fact]
    public void Redact_SharedFormOnTwoPages_LeavesOtherPageUntouched()
    {
        using var document = PdfDocument.Create();
        PdfPage first = document.Pages.Add(PageSize.Letter);
        PdfPage second = document.Pages.Add(PageSize.Letter);
        PdfObjectId form = AddForm(document, "BT /F1 12 Tf 0 0 Td (PUBLIC SECRET) Tj ET");
        UseForm(first, form, FormAt(700));
        UseForm(second, form, FormAt(700));

        first.RedactText("SECRET");

        Assert.DoesNotContain("SECRET", first.ExtractText(), StringComparison.Ordinal);
        Assert.Contains("PUBLIC", first.ExtractText(), StringComparison.Ordinal);
        Assert.Contains("PUBLIC SECRET", second.ExtractText(), StringComparison.Ordinal);

        using var reloaded = PdfDocument.Load(document.ToArray(FlatRewrite));
        Assert.DoesNotContain("SECRET", reloaded.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Contains("PUBLIC SECRET", reloaded.Pages[1].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_FormDrawnTwiceOnOnePage_ChangesOnlyTheSelectedInvocation()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        PdfObjectId form = AddForm(document, "BT /F1 12 Tf 0 0 Td (PUBLIC SECRET) Tj ET");
        UseForm(page, form, $"{FormAt(700)} {FormAt(500)}");

        page.Redact(new PdfRectangle(60, 690, 300, 720));

        using var reloaded = PdfDocument.Load(document.ToArray(FlatRewrite));
        string text = reloaded.Pages[0].ExtractText();
        Assert.Equal(1, Occurrences(text, "SECRET"));
        Assert.Equal(1, Occurrences(text, "PUBLIC SECRET"));
    }

    [Fact]
    public void Redact_FormNothingElseDraws_KeepsObjectNumberAndLeavesNoCopy()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        PdfObjectId form = AddForm(document, "BT /F1 12 Tf 0 0 Td (PUBLIC SECRET) Tj ET");
        UseForm(page, form, FormAt(700));
        Assert.Contains("PUBLIC SECRET", SavedFileText(document), StringComparison.Ordinal);

        page.RedactText("SECRET");

        PdfDictionary xObjects = page.Resources.GetDictionary(PdfNames.XObject)!;
        KeyValuePair<PdfName, PdfValue> entry = Assert.Single(xObjects);
        Assert.Equal(form, entry.Value.AsReference);
        Assert.Contains("PUBLIC", page.ExtractText(), StringComparison.Ordinal);
        AssertNotInFile(document, "SECRET");
    }

    [Fact]
    public void Redact_NestedFormOnlyUsedHere_LeavesNoOriginalBehind()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        PdfObjectId inner = AddForm(document, "BT /F1 12 Tf 0 0 Td (PUBLIC SECRET) Tj ET");
        var xObjects = new PdfDictionary();
        xObjects.Set(Fm, PdfValue.Reference(inner));
        PdfObjectId outer = AddForm(document, "/Fm Do", xObjects);
        UseForm(page, outer, FormAt(700));

        page.RedactText("SECRET");

        Assert.Contains("PUBLIC", page.ExtractText(), StringComparison.Ordinal);
        AssertNotInFile(document, "SECRET");
    }

    [Fact]
    public void Redact_SharedResourcesDictionary_KeepsOriginalFormForOtherPage()
    {
        using var document = PdfDocument.Create();
        PdfPage first = document.Pages.Add(PageSize.Letter);
        PdfPage second = document.Pages.Add(PageSize.Letter);
        PdfObjectId form = AddForm(document, "BT /F1 12 Tf 0 0 Td (PUBLIC SECRET) Tj ET");

        var xObjects = new PdfDictionary();
        xObjects.Set(Fm, PdfValue.Reference(form));
        var shared = new PdfDictionary();
        shared.Set(PdfNames.XObject, PdfValue.Dictionary(xObjects));
        shared.Set(PdfNames.Font, HelveticaResources());
        PdfObjectId sharedId = document.AddObject(PdfValue.Dictionary(shared));
        first.Dictionary.Set(PdfNames.Resources, PdfValue.Reference(sharedId));
        second.Dictionary.Set(PdfNames.Resources, PdfValue.Reference(sharedId));
        first.SetContent(Encoding.ASCII.GetBytes(FormAt(700)));
        second.SetContent(Encoding.ASCII.GetBytes(FormAt(700)));

        first.RedactText("SECRET");

        Assert.Contains("PUBLIC SECRET", second.ExtractText(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", first.ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_SharedResourcesDictionary_KeepsImageOtherPageDraws()
    {
        using var document = PdfDocument.Create();
        PdfPage first = document.Pages.Add(PageSize.Letter);
        PdfPage second = document.Pages.Add(PageSize.Letter);
        PdfImage image = PdfImage.FromPixels(document, 1, 1, [255, 0, 0]);

        var xObjects = new PdfDictionary();
        xObjects.Set(PdfName.Get("Im"), image.Reference);
        var shared = new PdfDictionary();
        shared.Set(PdfNames.XObject, PdfValue.Dictionary(xObjects));
        PdfObjectId sharedId = document.AddObject(PdfValue.Dictionary(shared));
        first.Dictionary.Set(PdfNames.Resources, PdfValue.Reference(sharedId));
        second.Dictionary.Set(PdfNames.Resources, PdfValue.Reference(sharedId));
        const string Draw = "q 80 0 0 80 100 500 cm /Im Do Q";
        first.SetContent(Encoding.ASCII.GetBytes(Draw));
        second.SetContent(Encoding.ASCII.GetBytes(Draw));

        PdfRedactionResult result = first.Redact(new PdfRectangle(90, 490, 200, 600));

        Assert.Equal(1, result.ImagesRemoved);
        Assert.Empty(first.PlacedImages);
        Assert.Single(second.PlacedImages);
        Assert.True(xObjects.ContainsKey(PdfName.Get("Im")));
    }

    [Fact]
    public void Combine_AddsSemanticEntries()
    {
        var left = new PdfRedactionResult(1, 2, 3, 4) { SemanticEntriesRemoved = 5 };
        var right = new PdfRedactionResult(1, 1, 1, 1) { SemanticEntriesRemoved = 2 };

        Assert.Equal(7, left.Combine(right).SemanticEntriesRemoved);
        Assert.True(new PdfRedactionResult(0, 0, 0, 0) { SemanticEntriesRemoved = 1 }.HasChanges);
    }
}
