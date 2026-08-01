using System.Globalization;
using System.Text;
using Inkwright.Cos;
using Inkwright.Forms;
using Xunit;

namespace Inkwright.Forms.Tests;

/// <summary>The shape of an invoice form, mapped by the source generator.</summary>
[PdfForm]
public partial record InvoiceForm(
    [property: PdfField("customer.name")] string Customer,
    [property: PdfField("invoice.number")] int Number,
    [property: PdfField("invoice.total", Format = "0.00")] decimal Total,
    [property: PdfField("invoice.paid")] bool Paid,
    [property: PdfField("invoice.date")] DateOnly Date);

public class FormTests
{
    [Fact]
    public void BuildsFillsAndReadsAForm()
    {
        byte[] bytes = BuildForm();

        using PdfDocument document = PdfDocument.Load(bytes);
        PdfForm form = PdfForm.Open(document)!;

        Assert.Equal(4, form.Count);
        Assert.Equal(PdfFieldType.Text, form["customer.name"]!.Type);
        Assert.Equal(PdfFieldType.CheckBox, form["invoice.paid"]!.Type);
        Assert.Equal(PdfFieldType.ComboBox, form["invoice.currency"]!.Type);
        Assert.Equal(["EUR", "GBP", "USD"], form["invoice.currency"]!.Options);

        form.Set("customer.name", "Ada Lovelace");
        form.Set("invoice.paid", true);
        form.Set("invoice.currency", "GBP");

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfForm reopened = PdfForm.Open(reloaded)!;

        Assert.Equal("Ada Lovelace", reopened["customer.name"]!.Value);
        Assert.True(reopened["invoice.paid"]!.IsChecked);
        Assert.Equal("GBP", reopened["invoice.currency"]!.Value);
    }

    [Fact]
    public void GeneratesAnAppearanceSoEveryViewerShowsTheValue()
    {
        byte[] bytes = BuildForm();

        using PdfDocument document = PdfDocument.Load(bytes);
        PdfForm form = PdfForm.Open(document)!;
        form.Set("customer.name", "Grace Hopper");

        PdfDictionary widget = form["customer.name"]!.Widgets[0];
        PdfStream appearance = widget.GetDictionary(PdfName.Get("AP"))!.GetStream(PdfName.Get("N"))!;
        string content = Encoding.ASCII.GetString(appearance.DecodedSpan());

        Assert.Contains("/Tx BMC", content, StringComparison.Ordinal);
        Assert.Contains("(Grace Hopper) Tj", content, StringComparison.Ordinal);
        Assert.Contains("Form", appearance.Dictionary.GetName(PdfName.Get("Subtype"))!.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void FlattensIntoOrdinaryPageContent()
    {
        byte[] bytes = BuildForm();

        using PdfDocument document = PdfDocument.Load(bytes);
        PdfForm form = PdfForm.Open(document)!;
        form.Set("customer.name", "Katherine Johnson");
        form.Set("invoice.paid", true);
        form.Flatten();

        using PdfDocument flattened = PdfDocument.Load(document.ToArray());

        Assert.Null(PdfForm.Open(flattened));
        Assert.False(flattened.Pages[0].Dictionary.ContainsKey(PdfName.Get("Annots")));
        Assert.Contains("Katherine Johnson", flattened.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void MapsATypedRecordOntoTheFields()
    {
        byte[] bytes = BuildInvoiceForm();

        using PdfDocument document = PdfDocument.Load(bytes);
        PdfForm form = PdfForm.Open(document)!;

        Assert.Empty(form.MissingFields<InvoiceForm>());

        var invoice = new InvoiceForm("Ada Lovelace", 2026014, 1234.50m, true, new DateOnly(2026, 7, 26));
        form.Fill(invoice);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());
        PdfForm reopened = PdfForm.Open(reloaded)!;

        Assert.Equal("1234.50", reopened["invoice.total"]!.Value);
        Assert.Equal("2026-07-26", reopened["invoice.date"]!.Value);

        InvoiceForm read = reopened.Read<InvoiceForm>();
        Assert.Equal(invoice, read);
    }

    [Fact]
    public void NamesTheFieldsTheSchemaExpects()
    {
        Assert.Equal(
            ["customer.name", "invoice.number", "invoice.total", "invoice.paid", "invoice.date"],
            InvoiceForm.FieldNames);
    }

    [Fact]
    public void ReadsFormsFromRealDocuments()
    {
        int documentsWithForms = 0;
        int fieldsRead = 0;
        var failures = new List<string>();

        foreach (string path in FindFormFiles())
        {
            try
            {
                // A sweep meets locked files; it should look at their structure and move on.
                using PdfDocument document = PdfDocument.Load(
                    File.ReadAllBytes(path), new IO.PdfReadOptions { OpenLockedDocuments = true });

                if (document.WasEncrypted || PdfForm.Open(document) is not { Count: > 0 } form)
                    continue;

                documentsWithForms++;

                foreach (PdfFormField field in form)
                {
                    _ = field.Type;
                    _ = field.Value;
                    _ = field.Options;
                    _ = field.Bounds;
                    fieldsRead++;
                }

                Dictionary<string, string?> values = form.ReadValues();
                Assert.NotNull(values);
            }
            catch (Diagnostics.PdfFormatException)
            {
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.True(documentsWithForms > 20, $"Only {documentsWithForms} documents with forms were found.");
        Assert.True(fieldsRead > 100, $"Only {fieldsRead} fields were read.");
        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(20)));
    }

    private static IEnumerable<string> FindFormFiles()
    {
        string root = @"E:\PDFLib";
        if (!Directory.Exists(root))
            yield break;

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory) == "Inkwright")
                continue;

            foreach (string file in Directory.EnumerateFiles(directory, "*.pdf", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.Contains("form", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("field", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("acro", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private static byte[] BuildForm()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfForm form = PdfForm.OpenOrCreate(document);

        form.AddTextField(page, PdfRectangle.FromSize(60, 700, 260, 22), "customer.name", "");
        form.AddTextField(page, PdfRectangle.FromSize(60, 620, 260, 60), "customer.address", multiline: true);
        form.AddCheckBox(page, PdfRectangle.FromSize(60, 580, 16, 16), "invoice.paid");
        form.AddComboBox(page, PdfRectangle.FromSize(60, 540, 120, 22), "invoice.currency", ["EUR", "GBP", "USD"]);

        return document.ToArray();
    }

    private static byte[] BuildInvoiceForm()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfForm form = PdfForm.OpenOrCreate(document);

        double y = 720;
        foreach (string name in InvoiceForm.FieldNames)
        {
            if (name == "invoice.paid")
                form.AddCheckBox(page, PdfRectangle.FromSize(60, y, 16, 16), name);
            else
                form.AddTextField(page, PdfRectangle.FromSize(60, y, 240, 22), name);

            y -= 40;
        }

        return document.ToArray();
    }

    private static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
