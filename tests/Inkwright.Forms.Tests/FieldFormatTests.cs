using System.Text;
using Inkwright.Cos;
using Xunit;

namespace Inkwright.Forms.Tests;

public class FieldFormatTests
{
    [Theory]
    [InlineData("AFNumber_Format(2, 0, 0, 0, \"$\", true)", "1234.5", "$1,234.50")]
    [InlineData("AFNumber_Format(2, 0, 0, 0, \"\", true)", "1234.5", "1,234.50")]
    [InlineData("AFNumber_Format(0, 1, 0, 0, \"\", true)", "1234.5", "1235")]
    [InlineData("AFNumber_Format(2, 2, 0, 0, \" EUR\", false)", "1234.5", "1.234,50 EUR")]
    [InlineData("AFNumber_Format(2, 4, 0, 0, \"\", true)", "1234567", "1'234'567.00")]
    [InlineData("AFNumber_Format(2, 0, 0, 0, \"$\", true)", "-99", "-$99.00")]
    [InlineData("AFNumber_Format(2, 0, 2, 0, \"$\", true)", "-99", "($99.00)")]
    [InlineData("AFNumber_Format(2, 0, 3, 0, \"$\", true)", "-99", "($99.00)")]
    public void FormatsNumbersTheWayAcrobatDoes(string script, string value, string expected) =>
        Assert.Equal(expected, FieldFormat.Parse(script).Apply(value));

    [Theory]
    [InlineData("AFPercent_Format(2, 0)", "0.125", "12.50%")]
    [InlineData("AFPercent_Format(0, 0)", "0.5", "50%")]
    [InlineData("AFPercent_Format(1, 2)", "0.125", "12,5%")]
    [InlineData("AFPercent_Format(2, 0, true)", "0.125", "%12.50")]
    public void FormatsPercentages(string script, string value, string expected) =>
        Assert.Equal(expected, FieldFormat.Parse(script).Apply(value));

    [Theory]
    [InlineData("AFDate_FormatEx(\"mm/dd/yyyy\")", "2026-07-26", "07/26/2026")]
    [InlineData("AFDate_FormatEx(\"d-mmm-yy\")", "2026-07-26", "26-Jul-26")]
    [InlineData("AFDate_FormatEx(\"mmmm d, yyyy\")", "2026-07-26", "July 26, 2026")]
    [InlineData("AFDate_Format(2)", "2026-07-26", "07/26/26")]
    [InlineData("AFDate_Format(7)", "2026-07-26", "26-07-26")]
    [InlineData("AFTime_Format(0)", "2026-07-26T14:30:00", "14:30")]
    [InlineData("AFTime_Format(1)", "2026-07-26T14:30:00", "2:30 pm")]
    [InlineData("AFTime_Format(3)", "2026-07-26T09:05:07", "9:05:07 am")]
    public void FormatsDatesAndTimes(string script, string value, string expected) =>
        Assert.Equal(expected, FieldFormat.Parse(script).Apply(value));

    [Theory]
    [InlineData(0, "12345", "12345")]
    [InlineData(1, "123456789", "12345-6789")]
    [InlineData(2, "5551234567", "(555) 123-4567")]
    [InlineData(3, "123456789", "123-45-6789")]
    public void FormatsTelephoneAndPostalNumbers(int kind, string value, string expected) =>
        Assert.Equal(expected, FieldFormat.Parse($"AFSpecial_Format({kind})").Apply(value));

    [Fact]
    public void ReadsNumbersOutOfTextThatIsAlreadyFormatted()
    {
        Assert.True(FieldFormat.TryReadNumber("$1,234.50", out double dollars));
        Assert.Equal(1234.50, dollars, 4);

        Assert.True(FieldFormat.TryReadNumber("1.234,50 EUR", out double euros));
        Assert.Equal(1234.50, euros, 4);

        Assert.True(FieldFormat.TryReadNumber("-42", out double negative));
        Assert.Equal(-42, negative, 4);

        Assert.False(FieldFormat.TryReadNumber("not a number", out _));
        Assert.False(FieldFormat.TryReadNumber("", out _));
    }

    [Fact]
    public void LeavesFieldsWithNoFormatActionAlone()
    {
        Assert.False(FieldFormat.Parse(null).IsDefined);
        Assert.False(FieldFormat.Parse("doSomethingElse(1)").IsDefined);
        Assert.Equal("as stored", FieldFormat.None.Apply("as stored"));
    }

    [Fact]
    public void BakesTheFormattedValueIntoTheAppearance()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfForm form = PdfForm.OpenOrCreate(document);
        form.AddTextField(page, PdfRectangle.FromSize(60, 700, 200, 22), "total");

        PdfFormField field = form["total"]!;
        SetFormatAction(field, "AFNumber_Format(2, 0, 0, 0, \"$\", true);");
        field.SetValue("1234.5");

        Assert.Equal("1234.5", field.Value);
        Assert.Equal("$1,234.50", field.DisplayValue);

        PdfStream appearance = field.Widgets[0].GetDictionary(PdfName.Get("AP"))!.GetStream(PdfName.Get("N"))!;
        Assert.Contains("($1,234.50) Tj", Encoding.ASCII.GetString(appearance.DecodedSpan()), StringComparison.Ordinal);
    }

    internal static void SetFormatAction(PdfFormField field, string script) =>
        SetAction(field, "F", script);

    internal static void SetCalculateAction(PdfFormField field, string script) =>
        SetAction(field, "C", script);

    private static void SetAction(PdfFormField field, string key, string script)
    {
        var action = new PdfDictionary(2);
        action.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get("JavaScript")));
        action.Set(PdfName.Get("JS"), PdfValue.TextString(script));

        PdfDictionary actions = field.Dictionary.GetDictionary(PdfName.Get("AA")) ?? new PdfDictionary(1);
        actions.Set(PdfName.Get(key), PdfValue.Dictionary(action));
        field.Dictionary.Set(PdfName.Get("AA"), PdfValue.Dictionary(actions));
    }
}
