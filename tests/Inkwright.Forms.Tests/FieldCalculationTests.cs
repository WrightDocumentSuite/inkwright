using Inkwright.Cos;
using Xunit;

namespace Inkwright.Forms.Tests;

public class FieldCalculationTests
{
    [Theory]
    [InlineData("AFSimple_Calculate(\"SUM\", [\"a\", \"b\"])", FieldCalculationKind.Sum)]
    [InlineData("AFSimple_Calculate('AVG', ['a', 'b'])", FieldCalculationKind.Average)]
    [InlineData("AFSimple_Calculate(\"PRD\", new Array(\"a\", \"b\"))", FieldCalculationKind.Product)]
    [InlineData("AFSimple_Calculate(\"MIN\", [\"a\", \"b\"])", FieldCalculationKind.Minimum)]
    [InlineData("AFSimple_Calculate(\"MAX\", [\"a\", \"b\"])", FieldCalculationKind.Maximum)]
    public void ReadsEveryFunctionAndItsFields(string script, FieldCalculationKind expected)
    {
        FieldCalculation calculation = FieldCalculation.Parse(script);

        Assert.Equal(expected, calculation.Kind);
        Assert.Equal(["a", "b"], calculation.Sources);
    }

    [Fact]
    public void LeavesFieldsWithNoCalculateActionAlone()
    {
        Assert.False(FieldCalculation.Parse(null).IsDefined);
        Assert.False(FieldCalculation.Parse("event.value = 1;").IsDefined);
        Assert.False(FieldCalculation.Parse("AFSimple_Calculate(\"NOPE\", [\"a\"])").IsDefined);
    }

    [Theory]
    [InlineData(FieldCalculationKind.Sum, 6)]
    [InlineData(FieldCalculationKind.Average, 2)]
    [InlineData(FieldCalculationKind.Product, 6)]
    [InlineData(FieldCalculationKind.Minimum, 1)]
    [InlineData(FieldCalculationKind.Maximum, 3)]
    public void ComputesEachFunction(FieldCalculationKind kind, double expected) =>
        Assert.Equal(expected, new FieldCalculation(kind, ["a", "b", "c"]).Compute([1, 2, 3])!.Value, 6);

    [Fact]
    public void AddsUpAFormAndFormatsTheTotal()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfForm form = PdfForm.OpenOrCreate(document);

        form.AddTextField(page, PdfRectangle.FromSize(60, 700, 120, 22), "net");
        form.AddTextField(page, PdfRectangle.FromSize(60, 660, 120, 22), "tax");
        form.AddTextField(page, PdfRectangle.FromSize(60, 620, 120, 22), "total");

        PdfFormField total = form["total"]!;
        FieldFormatTests.SetCalculateAction(total, "AFSimple_Calculate(\"SUM\", [\"net\", \"tax\"]);");
        FieldFormatTests.SetFormatAction(total, "AFNumber_Format(2, 0, 0, 0, \"$\", true);");

        form.Set("net", "1200");
        form.Set("tax", "34.5");

        Assert.Equal(1, form.Recalculate());
        Assert.Equal("1234.5", total.Value);
        Assert.Equal("$1,234.50", total.DisplayValue);

        // Nothing changed the second time round, so nothing is rewritten.
        Assert.Equal(0, form.Recalculate());
    }

    [Fact]
    public void WorksSubtotalsOutBeforeTheGrandTotalThatUsesThem()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        PdfForm form = PdfForm.OpenOrCreate(document);

        foreach (string name in new[] { "grand", "sub", "a", "b" })
            form.AddTextField(page, PdfRectangle.FromSize(60, 700, 120, 22), name);

        FieldFormatTests.SetCalculateAction(form["sub"]!, "AFSimple_Calculate(\"SUM\", [\"a\", \"b\"]);");
        FieldFormatTests.SetCalculateAction(form["grand"]!, "AFSimple_Calculate(\"PRD\", [\"sub\", \"sub\"]);");

        // The fields were declared with the grand total first, so only the order named in /CO can
        // give the right answer.
        var order = new PdfArray(2);
        order.Add(PdfValue.Reference(form["sub"]!.Id));
        order.Add(PdfValue.Reference(form["grand"]!.Id));
        form.Root.Set(PdfName.Get("CO"), PdfValue.Array(order));

        form.Set("a", "3");
        form.Set("b", "4");
        form.Recalculate();

        Assert.Equal("7", form["sub"]!.Value);
        Assert.Equal("49", form["grand"]!.Value);
    }
}
