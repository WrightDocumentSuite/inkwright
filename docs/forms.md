# Forms

## Reading and filling

```csharp
using Inkwright.Forms;

using PdfDocument document = PdfDocument.Load("application.pdf");
PdfForm? form = PdfForm.Open(document);

foreach (PdfFormField field in form!)
    Console.WriteLine($"{field.Type} {field.FullName} = {field.Value}");

form.Set("applicant.name", "Ada Lovelace");
form.Set("applicant.remote", true);
form["applicant.country"]!.SetValue("GB");

document.Save("application-filled.pdf");
```

Fields are addressed by full name—the way someone filling the form thinks about them: the field tree is walked once and name parts are joined with dots.

`PdfFormField` knows the type (`Text`, `CheckBox`, `RadioButton`, `PushButton`, `ComboBox`, `ListBox`, `Signature`), flags, tooltip, length limit, option list, toggle states, and rectangle on the page.

## Appearance streams

The specification allows a reader to build field appearance from the `/DA` string, but many readers do not. So the library builds appearance itself: after each value write, text fields, lists, and combo boxes get a fresh XObject with text clipped to the frame and aligned per `/Q`. Toggle buttons already carry an appearance for each state—only the selection changes.

Because of this, a filled form looks filled everywhere, not only in one viewer.

## Formatting like Acrobat

Acrobat stores field format not as data but as a call to one of its built-in scripts in the `/AA /F` action. A document showing `$1,234.50` actually carries the string `AFNumber_Format(2, 0, 0, 0, "$", true)`.

The library reads these calls without running any JavaScript and bakes the result into the appearance stream. So the field looks correct even in a viewer without a script engine.

```csharp
PdfFormField total = form["invoice.total"]!;

total.SetValue("1234.5");
Console.WriteLine(total.Value);          // 1234.5   — what was stored
Console.WriteLine(total.DisplayValue);   // $1,234.50 — what is shown
```

`AFNumber_Format`, `AFPercent_Format`, `AFDate_Format`, `AFDate_FormatEx`, `AFTime_Format`, `AFTime_FormatEx`, and `AFSpecial_Format` (zip, zip+4, phone, social security number) are parsed. Separator styles, negative number styles, and currency sign position—all as in Acrobat’s table.

## Calculated fields

Arithmetic is stored the same way: `AFSimple_Calculate("SUM", ["net", "tax"])` in the `/AA /C` action.

```csharp
form.Set("net", "1200");
form.Set("tax", "34.5");
form.Recalculate();                       // returns how many fields changed

Console.WriteLine(form["total"]!.Value);  // 1234.5
```

Order comes from the form’s `/CO` array, and that matters: a grand total that sums subtotals is correct only if subtotals were computed first. Fields not named in the array are calculated after all named ones.

SUM, AVG, PRD, MIN, and MAX are supported. Numbers are read leniently: a value that already carries separators, a currency symbol, or trailing text still yields the number inside it.

## Data exchange: XFDF and FDF

A filled form is sent back not as the whole file but as its values. There are two formats—XFDF (ISO 19444-1 §6.3) and the older FDF (ISO 32000-1 §12.7.8.3.2, PDF syntax itself); both carry values and comments.

```csharp
// Send values
string xfdf = document.ExportFormData(href: "application.pdf");   // XFDF
byte[] fdf = document.ExportFormDataFdf();                         // FDF

// Import into a blank template—the format is detected automatically
PdfReviewImportResult fromXfdf = blank.ImportXfdf(xfdf);
PdfReviewImportResult fromFdf = blank.ImportFdf(fdf);
Console.WriteLine($"{fromXfdf.FieldsFilled} fields filled");

// Bake values into appearance so any viewer draws them
PdfForm.Open(blank)!.RegenerateAppearances();
```

`ExportXfdf` and `ExportFdf` write both halves—values and comments—by default; `PdfExchangeContent.Fields` keeps only the first; `ExportFormData`/`ExportFormDataFdf` is the shorter equivalent. Both sides of exchange—read and write—are now symmetric in both formats; previously FDF carried only comments.

### Name hierarchy

In PDF the full field name uses dots; in XFDF the same hierarchy is expressed by nesting. The field `Address.Name` travels like this:

```xml
<fields>
  <field name="Address">
    <field name="Name"><value>Adobe Systems, Inc.</value></field>
  </field>
</fields>
```

The dot is a separator, so §12.7.3.2 forbids a dot inside a partial name. `AddTextField` with the name `Address.Name` builds the `Address` branch and attaches the field to it, rather than writing the dot into `/T`. The field is still accessed by full name.

### What exchange cannot do

Import changes only values of fields that already exist in the document (§5.6.1). A field name not present in the document is added to `Warnings` and is not created: neither XFDF nor FDF can specify what type such a field would be or where it would live.

Signature fields do not export values (§6.3.3.1, §12.7.8.3.2)—what they contain is meaningless outside the signed file.

A multi-select list travels as several `<value>` elements; a line break in a multiline field travels as a single newline; a checkbox travels as the name of its on state.

### Appearance after import

Import writes values directly into dictionaries and sets `NeedAppearances`, because what was drawn no longer matches what was stored. A viewer with its own generator will redraw fields itself; `RegenerateAppearances()` does this upfront so the form looks filled everywhere.

## Creating fields

```csharp
PdfForm form = PdfForm.OpenOrCreate(document);

form.AddTextField(page, PdfRectangle.FromSize(200, 700, 300, 22), "applicant.name");
form.AddTextField(page, PdfRectangle.FromSize(200, 640, 300, 60), "applicant.address", multiline: true);
form.AddCheckBox(page, PdfRectangle.FromSize(200, 600, 16, 16), "applicant.remote");
form.AddComboBox(page, PdfRectangle.FromSize(200, 560, 120, 22), "applicant.currency", ["EUR", "GBP", "USD"]);
```

## Flattening

```csharp
form.Flatten();
```

Each field’s appearance is stamped into page content, annotations are removed, and `AcroForm` disappears from the catalog. Display is preserved down to the pixel because the same XObject the viewer showed is drawn; this cannot be undone—that is the point.

## Typed mapping

```csharp
[PdfForm]
public partial record Invoice(
    [property: PdfField("customer.name")] string Customer,
    [property: PdfField("invoice.number")] int Number,
    [property: PdfField("invoice.total", Format = "0.00")] decimal Total,
    [property: PdfField("invoice.paid")] bool Paid,
    [property: PdfField("invoice.date")] DateOnly Date);
```

The source generator implements `IPdfFormSchema<Invoice>` with `static abstract` members:

```csharp
form.Fill(new Invoice("Ada Lovelace", 2026014, 1234.50m, true, DateOnly.FromDateTime(DateTime.Today)));

Invoice read = form.Read<Invoice>();
IReadOnlyList<string> missing = form.MissingFields<Invoice>();
```

No reflection: field names are checked by the compiler, the code runs under Native AOT and survives trimming. Supported types are strings, integers, floating-point, `decimal`, `bool`, enums, `DateTime`, `DateOnly`, `DateTimeOffset`, and `Guid`; a type that does not map to a field value produces warning `IWF002`, and a non-partial type—error `IWF001`. A type nested inside another is supported if all enclosing types are declared `partial`.
