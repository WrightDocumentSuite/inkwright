using System.Xml.Linq;
using Inkwright.Annotations;
using Inkwright.Cos;
using Xunit;

namespace Inkwright.Forms.Tests;

/// <summary>
/// The form half of ISO 19444-1: exporting field values as XFDF and taking them back in. The
/// examples of §5.6.2 and §5.6.3 are used as they are printed in the standard.
/// </summary>
public class XfdfFormDataTests
{
    private static readonly XNamespace Ns = "http://ns.adobe.com/xfdf/";

    [Fact]
    public void WritesTheAddressLabelOfClauseFiveSixTwo()
    {
        using PdfDocument document = AddressLabel("Name", "Street", "CityState");
        PdfForm form = PdfForm.Open(document)!;

        form.Set("Name", "Adobe Systems, Inc.");
        form.Set("Street", "345 Park Ave.");
        form.Set("CityState", "San Jose, CA 95110");

        XElement root = XDocument.Parse(document.ExportXfdf(PdfExchangeContent.Fields)).Root!;
        XElement fields = Assert.Single(root.Elements(Ns + "fields"));

        Assert.Equal(
            ["Name", "Street", "CityState"],
            fields.Elements(Ns + "field").Select(field => field.Attribute("name")!.Value));

        Assert.Equal(
            ["Adobe Systems, Inc.", "345 Park Ave.", "San Jose, CA 95110"],
            fields.Elements(Ns + "field").Select(field => field.Element(Ns + "value")!.Value));
    }

    [Fact]
    public void NestsHierarchicalFieldsAsClauseFiveSixThreeShows()
    {
        using PdfDocument document = AddressLabel("Address.Name", "Address.Street", "Address.CityState");
        PdfForm form = PdfForm.Open(document)!;
        form.Set("Address.Name", "Adobe Systems, Inc.");

        XElement fields = XDocument.Parse(document.ExportXfdf(PdfExchangeContent.Fields))
            .Root!.Element(Ns + "fields")!;

        // In PDF the name is spelled with full stops; in XFDF the same tree is spelled by nesting.
        XElement address = Assert.Single(fields.Elements(Ns + "field"));
        Assert.Equal("Address", address.Attribute("name")!.Value);

        Assert.Equal(
            ["Name", "Street", "CityState"],
            address.Elements(Ns + "field").Select(field => field.Attribute("name")!.Value));

        Assert.Equal("Adobe Systems, Inc.", address.Elements(Ns + "field").First().Element(Ns + "value")!.Value);
    }

    [Fact]
    public void ReadsBackTheValuesItWrote()
    {
        using PdfDocument filled = AddressLabel("Name", "Street", "CityState");
        PdfForm source = PdfForm.Open(filled)!;
        source.Set("Name", "Ada Lovelace");
        source.Set("Street", "12 Analytical Way");

        string xfdf = filled.ExportXfdf(PdfExchangeContent.Fields);

        using PdfDocument blank = AddressLabel("Name", "Street", "CityState");
        PdfReviewImportResult result = blank.ImportXfdf(xfdf);

        Assert.Equal(2, result.FieldsFilled);
        Assert.Empty(result.Warnings);

        PdfForm target = PdfForm.Open(blank)!;
        Assert.Equal("Ada Lovelace", target["Name"]!.Value);
        Assert.Equal("12 Analytical Way", target["Street"]!.Value);
    }

    [Fact]
    public void FillsHierarchicalFieldsFromNestedElements()
    {
        using PdfDocument document = AddressLabel("Address.Name", "Address.Street", "Address.CityState");

        PdfReviewImportResult result = document.ImportXfdf(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
            <fields>
            <field name="Address">
            <field name="Name"><value>Adobe Systems, Inc.</value></field>
            <field name="CityState"><value>San Jose, CA 95110</value></field>
            </field>
            </fields>
            </xfdf>
            """);

        Assert.Equal(2, result.FieldsFilled);

        PdfForm form = PdfForm.Open(document)!;
        Assert.Equal("Adobe Systems, Inc.", form["Address.Name"]!.Value);
        Assert.Equal("San Jose, CA 95110", form["Address.CityState"]!.Value);
    }

    [Fact]
    public void RefusesToCreateFieldsTheDocumentDoesNotHave()
    {
        using PdfDocument document = AddressLabel("Name");

        // §5.6.1: XFDF can only change the value of a field that already exists.
        PdfReviewImportResult result = document.ImportXfdf(
            """
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
            <fields><field name="Nonesuch"><value>x</value></field></fields>
            </xfdf>
            """);

        Assert.Equal(0, result.FieldsFilled);
        Assert.Contains(result.Warnings, warning => warning.Contains("Nonesuch", StringComparison.Ordinal));
        Assert.Null(PdfForm.Open(document)!["Nonesuch"]);
    }

    [Fact]
    public void CarriesCheckBoxesAsTheirOnState()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfForm form = PdfForm.OpenOrCreate(document);
        form.AddCheckBox(page, PdfRectangle.FromSize(60, 700, 16, 16), "agreed");
        form.Set("agreed", true);

        string xfdf = document.ExportXfdf(PdfExchangeContent.Fields);
        Assert.Contains("<value>Yes</value>", xfdf, StringComparison.Ordinal);

        using var target = PdfDocument.Create();
        PdfPage targetPage = target.Pages.Add(PageSize.A4);
        PdfForm targetForm = PdfForm.OpenOrCreate(target);
        targetForm.AddCheckBox(targetPage, PdfRectangle.FromSize(60, 700, 16, 16), "agreed");

        Assert.Equal(1, target.ImportXfdf(xfdf).FieldsFilled);
        Assert.True(PdfForm.Open(target)!["agreed"]!.IsChecked);
    }

    [Fact]
    public void CarriesEveryValueOfAListThatAllowsSeveral()
    {
        using PdfDocument document = AddressLabel("colours");

        PdfFormField field = PdfForm.Open(document)!["colours"]!;
        var chosen = new PdfArray(2);
        chosen.Add(PdfValue.TextString("red"));
        chosen.Add(PdfValue.TextString("blue"));
        field.Dictionary.Set(PdfName.Get("V"), PdfValue.Array(chosen));

        XElement element = XDocument.Parse(document.ExportXfdf(PdfExchangeContent.Fields))
            .Root!.Element(Ns + "fields")!.Elements(Ns + "field").Single();

        // The content model allows a value element for each selection.
        Assert.Equal(["red", "blue"], element.Elements(Ns + "value").Select(value => value.Value));

        using PdfDocument target = AddressLabel("colours");
        Assert.Equal(1, target.ImportXfdf(document.ExportXfdf(PdfExchangeContent.Fields)).FieldsFilled);

        PdfArray written = PdfForm.Open(target)!["colours"]!.Dictionary.GetArray(PdfName.Get("V"))!;
        Assert.Equal(2, written.Count);
        Assert.Equal("blue", written.Get(1).AsTextString);
    }

    [Fact]
    public void ExportsNoValueForASignatureField()
    {
        using PdfDocument document = AddressLabel("Name");

        PdfFormField name = PdfForm.Open(document)!["Name"]!;
        name.Dictionary.Set(PdfName.Get("FT"), PdfValue.Name(PdfName.Get("Sig")));
        name.Dictionary.Set(PdfName.Get("V"), PdfValue.TextString("not really a signature"));

        XElement element = XDocument.Parse(document.ExportXfdf(PdfExchangeContent.Fields))
            .Root!.Element(Ns + "fields")!.Elements(Ns + "field").Single();

        // §6.3.3.1: a signature field exports no value.
        Assert.Empty(element.Elements(Ns + "value"));
    }

    [Fact]
    public void WritesLineBreaksInAMultilineFieldAsSingleLineFeeds()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfForm form = PdfForm.OpenOrCreate(document);
        form.AddTextField(page, PdfRectangle.FromSize(60, 620, 260, 60), "address", multiline: true);

        form["address"]!.Dictionary.Set(PdfName.Get("V"), PdfValue.TextString("line one\r\nline two\rline three"));

        string value = XDocument.Parse(document.ExportXfdf(PdfExchangeContent.Fields))
            .Root!.Element(Ns + "fields")!.Elements(Ns + "field").Single().Element(Ns + "value")!.Value;

        Assert.Equal("line one\nline two\nline three", value);
    }

    [Fact]
    public void NamesTheFileThePayloadCameFrom()
    {
        using PdfDocument document = AddressLabel("Name");

        XElement root = XDocument.Parse(
            document.ExportXfdf(PdfExchangeContent.Fields, href: "samples/AddressLabel.pdf")).Root!;

        Assert.Equal("samples/AddressLabel.pdf", root.Element(Ns + "f")!.Attribute("href")!.Value);
        Assert.Null(XDocument.Parse(document.ExportXfdf(PdfExchangeContent.Fields)).Root!.Element(Ns + "f"));
    }

    [Fact]
    public void WritesOnlyTheHalfThatWasAskedFor()
    {
        using PdfDocument document = AddressLabel("Name");
        PdfForm.Open(document)!.Set("Name", "Ada");

        Assert.DoesNotContain("<annots", document.ExportXfdf(PdfExchangeContent.Fields), StringComparison.Ordinal);
        Assert.DoesNotContain("<fields", document.ExportXfdf(PdfExchangeContent.Annotations), StringComparison.Ordinal);
        Assert.Contains("<fields", document.ExportXfdf(), StringComparison.Ordinal);
    }

    [Fact]
    public void BakesImportedValuesIntoAppearancesOnRequest()
    {
        using PdfDocument document = AddressLabel("Name");

        document.ImportXfdf(
            """
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
            <fields><field name="Name"><value>Grace Hopper</value></field></fields>
            </xfdf>
            """);

        PdfForm form = PdfForm.Open(document)!;

        // Importing says the appearances are stale; regenerating them makes it no longer true.
        Assert.True(form.NeedAppearances);
        Assert.Equal(1, form.RegenerateAppearances());
        Assert.False(form.NeedAppearances);

        PdfStream appearance = form["Name"]!.Widgets[0]
            .GetDictionary(PdfName.Get("AP"))!.GetStream(PdfName.Get("N"))!;

        Assert.Contains(
            "(Grace Hopper) Tj",
            System.Text.Encoding.ASCII.GetString(appearance.DecodedSpan()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildsAFieldTreeRatherThanPuttingFullStopsInPartialNames()
    {
        using PdfDocument built = AddressLabel("Address.Name", "Address.CityState", "Signature");
        PdfForm.Open(built)!.Set("Address.Name", "Ada Lovelace");

        using PdfDocument document = PdfDocument.Load(built.ToArray());
        PdfForm form = PdfForm.Open(document)!;

        // The names callers use are unchanged, and they survive a save and a reload.
        Assert.Equal("Ada Lovelace", form["Address.Name"]!.Value);
        Assert.NotNull(form["Address.CityState"]);
        Assert.NotNull(form["Signature"]);

        // §12.7.3.2: a partial name shall not contain a full stop, because that is the separator.
        foreach (PdfFormField field in form)
            Assert.DoesNotContain('.', field.PartialName);

        // The root holds one branch for the shared prefix and one field beside it.
        PdfArray roots = document.Catalog.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
        Assert.Equal(2, roots.Count);
        Assert.Equal("Address", roots.GetDictionary(0)!.GetTextString(PdfName.Get("T")));
        Assert.Equal(2, roots.GetDictionary(0)!.GetArray(PdfName.Get("Kids"))!.Count);
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
