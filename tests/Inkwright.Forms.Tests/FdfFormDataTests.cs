using Inkwright.Annotations;
using Inkwright.Cos;
using Xunit;

namespace Inkwright.Forms.Tests;

/// <summary>
/// The form half of FDF (ISO 32000-1 §12.7.8.3.2): exporting field values as FDF and taking them
/// back in, the COS counterpart of the XFDF form exchange.
/// </summary>
public class FdfFormDataTests
{
    [Fact]
    public void WritesFieldValuesAsAnFdfFieldTree()
    {
        using PdfDocument document = AddressLabel("Name", "Street");
        PdfForm form = PdfForm.Open(document)!;
        form.Set("Name", "Ada Lovelace");
        form.Set("Street", "12 Analytical Way");

        byte[] fdf = document.ExportFdf(PdfExchangeContent.Fields);

        // The file is real FDF and holds a Fields array, not an Annots one.
        Assert.StartsWith("%FDF", System.Text.Encoding.ASCII.GetString(fdf.AsSpan(0, 4)));

        using PdfDocument parsed = PdfDocument.Load(fdf, new IO.PdfReadOptions { OpenLockedDocuments = true });
        PdfDictionary fdfDictionary = parsed.Catalog.GetDictionary(PdfName.Get("FDF"))!;
        PdfArray fields = fdfDictionary.GetArray(PdfName.Get("Fields"))!;

        Assert.Equal(2, fields.Count);
        Assert.Equal(["Name", "Street"], Names(parsed, fields));
    }

    [Fact]
    public void ReadsBackTheValuesItWrote()
    {
        using PdfDocument filled = AddressLabel("Name", "Street", "CityState");
        PdfForm source = PdfForm.Open(filled)!;
        source.Set("Name", "Grace Hopper");
        source.Set("Street", "1 Compiler Lane");

        byte[] fdf = filled.ExportFdf(PdfExchangeContent.Fields);

        using PdfDocument blank = AddressLabel("Name", "Street", "CityState");
        PdfReviewImportResult result = blank.ImportFdf(fdf);

        Assert.Equal(2, result.FieldsFilled);

        PdfForm target = PdfForm.Open(blank)!;
        Assert.Equal("Grace Hopper", target["Name"]!.Value);
        Assert.Equal("1 Compiler Lane", target["Street"]!.Value);
    }

    [Fact]
    public void ImportedValuesSurviveAnIncrementalSave()
    {
        using PdfDocument source = AddressLabel("Name");
        PdfForm.Open(source)!.Set("Name", "Incremental Ada");
        byte[] fdf = source.ExportFdf(PdfExchangeContent.Fields);

        byte[] original;
        using (PdfDocument fresh = AddressLabel("Name"))
            original = fresh.ToArray(new PdfSaveOptions { UseObjectStreams = false });

        byte[] updated;
        using (PdfDocument loaded = PdfDocument.Load(original))
        {
            Assert.Equal(1, loaded.ImportFdf(fdf).FieldsFilled);
            updated = loaded.ToIncrementalArray(new PdfSaveOptions { UseObjectStreams = false });
        }

        using PdfDocument reopened = PdfDocument.Load(updated);
        Assert.Equal("Incremental Ada", PdfForm.Open(reopened)!["Name"]!.Value);
    }

    [Fact]
    public void NestsHierarchicalFieldsByName()
    {
        using PdfDocument filled = AddressLabel("Address.Name", "Address.Street");
        PdfForm.Open(filled)!.Set("Address.Name", "Adobe Systems, Inc.");

        byte[] fdf = filled.ExportFdf(PdfExchangeContent.Fields);
        using PdfDocument parsed = PdfDocument.Load(fdf, new IO.PdfReadOptions { OpenLockedDocuments = true });

        PdfArray fields = parsed.Catalog.GetDictionary(PdfName.Get("FDF"))!.GetArray(PdfName.Get("Fields"))!;

        // In PDF the name is spelled with full stops; in FDF the same tree is spelled by nesting.
        PdfDictionary address = parsed.Resolve(fields.Get(0)).AsDictionary!;
        Assert.Equal("Address", address.GetTextString(PdfName.Get("T")));

        PdfArray kids = address.GetArray(PdfName.Get("Kids"))!;
        Assert.Equal(["Name", "Street"], KidNames(parsed, kids));

        // Round-trips back into a fresh document by fully qualified name.
        using PdfDocument blank = AddressLabel("Address.Name", "Address.Street");
        Assert.Equal(1, blank.ImportFdf(fdf).FieldsFilled);
        Assert.Equal("Adobe Systems, Inc.", PdfForm.Open(blank)!["Address.Name"]!.Value);
    }

    [Fact]
    public void ReportsAFieldTheDocumentDoesNotCarry()
    {
        using PdfDocument filled = AddressLabel("Name");
        PdfForm.Open(filled)!.Set("Name", "Ada");
        byte[] fdf = filled.ExportFdf(PdfExchangeContent.Fields);

        using PdfDocument other = AddressLabel("Different");
        PdfReviewImportResult result = other.ImportFdf(fdf);

        Assert.Equal(0, result.FieldsFilled);
        Assert.Contains(result.Warnings, warning => warning.Contains("Name", StringComparison.Ordinal));
    }

    [Fact]
    public void WritesOnlyTheHalfThatWasAskedFor()
    {
        using PdfDocument document = AddressLabel("Name");
        PdfForm.Open(document)!.Set("Name", "Ada");

        string fieldsOnly = System.Text.Encoding.Latin1.GetString(document.ExportFdf(PdfExchangeContent.Fields));
        Assert.Contains("/Fields", fieldsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("/Annots", fieldsOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void CopiesReferencedFieldValuesIntoTheFdfObjectGraph()
    {
        using PdfDocument document = AddressLabel("Attachment");
        PdfFormField field = PdfForm.Open(document)!["Attachment"]!;

        byte[] payload = [1, 3, 3, 7];
        PdfObjectId streamId = document.AddObject(PdfValue.Stream(PdfStream.FromData(payload)));
        var embedded = new PdfDictionary { [PdfName.Get("F")] = PdfValue.Reference(streamId) };
        var fileSpec = new PdfDictionary
        {
            [PdfName.Get("Type")] = PdfValue.Name("Filespec"),
            [PdfName.Get("F")] = PdfValue.TextString("value.bin"),
            [PdfName.Get("EF")] = PdfValue.Dictionary(embedded),
        };
        field.Dictionary.Set(PdfNames.V, PdfValue.Reference(document.AddObject(PdfValue.Dictionary(fileSpec))));
        document.MarkChanged(field.Id);

        byte[] fdf = document.ExportFdf(PdfExchangeContent.Fields);
        using PdfDocument parsed = PdfDocument.Load(fdf, new IO.PdfReadOptions { OpenLockedDocuments = true });
        PdfDictionary node = parsed.Catalog.GetDictionary(PdfName.Get("FDF"))!
            .GetArray(PdfName.Get("Fields"))!.GetDictionary(0)!;
        PdfDictionary copiedSpec = parsed.Resolve(node.Get(PdfNames.V)).AsDictionary!;
        PdfStream copiedStream = parsed.Resolve(copiedSpec.GetDictionary(PdfName.Get("EF"))!.Get(PdfName.Get("F"))).AsStream!;

        Assert.Equal("value.bin", copiedSpec.GetTextString(PdfName.Get("F")));
        Assert.True(copiedStream.DecodedSpan().SequenceEqual(payload));
    }

    private static IEnumerable<string> Names(PdfDocument document, PdfArray fields)
    {
        for (int i = 0; i < fields.Count; i++)
            yield return document.Resolve(fields.Get(i)).AsDictionary!.GetTextString(PdfName.Get("T"))!;
    }

    private static IEnumerable<string> KidNames(PdfDocument document, PdfArray kids)
    {
        for (int i = 0; i < kids.Count; i++)
            yield return document.Resolve(kids.Get(i)).AsDictionary!.GetTextString(PdfName.Get("T"))!;
    }

    private static PdfDocument AddressLabel(params string[] names)
    {
        var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfForm form = PdfForm.OpenOrCreate(document);

        double y = 700;
        foreach (string name in names)
        {
            form.AddTextField(page, PdfRectangle.FromSize(60, y, 260, 22), name);
            y -= 40;
        }

        return document;
    }
}
