using Inkwright.Cos;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Layout.Tests;

/// <summary>PDF/A-4 clauses 6.4.1 and 6.6.3: where additional actions may be attached.</summary>
public class PdfAInteractiveActionTests
{
    private static readonly string[] PartFourTriggers = ["E", "X", "D", "U", "Fo", "Bl"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartFourAllowsAdditionalActionsOnFieldAndWidgetDictionaries(bool widget)
    {
        using PdfDocument baseline = FormDocument(widget, entry: null);
        using PdfDocument withAdditionalActions = FormDocument(widget, entry: "AA");

        Assert.Equal(Problems(baseline, PdfAConformance.A4), Problems(withAdditionalActions, PdfAConformance.A4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartFourAllowsTheDidPrintTriggerOnFieldAndWidgetDictionaries(bool widget)
    {
        using PdfDocument baseline = FormDocument(widget, entry: null);
        using PdfDocument withDidPrint = FormDocument(widget, entry: "AA", trigger: "DP");

        Assert.Equal(Problems(baseline, PdfAConformance.A4), Problems(withDidPrint, PdfAConformance.A4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartFourStillForbidsAWidgetOrFieldActionEntry(bool widget)
    {
        using PdfDocument document = FormDocument(widget, entry: "A");

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.4.1" && problem.Message.Contains("A entry", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartFourRejectsUnknownAdditionalActionTriggersOnFieldsAndWidgets(bool widget)
    {
        using PdfDocument document = FormDocument(widget, entry: "AA", trigger: "Bogus");

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.6.3" &&
                       problem.Message.Contains("Bogus", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("page")]
    [InlineData("annotation")]
    public void PartFourAllowsTheSixUserInitiatedTriggersElsewhere(string container)
    {
        using PdfDocument baseline = ContainerDocument(container, triggers: null);
        using PdfDocument withAdditionalActions = ContainerDocument(container, PartFourTriggers);

        Assert.Equal(Problems(baseline, PdfAConformance.A4), Problems(withAdditionalActions, PdfAConformance.A4));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("page")]
    [InlineData("annotation")]
    public void PartFourRejectsEveryOtherTriggerElsewhere(string container)
    {
        using PdfDocument document = ContainerDocument(container, ["WC"]);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.6.3" && problem.Message.Contains("WC", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("page")]
    [InlineData("annotation")]
    public void PartFourRequiresEveryAdditionalActionValueToBeAnActionDictionary(string container)
    {
        using PdfDocument document = ContainerDocument(container, ["E"], actionDictionaries: false);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.6.3" &&
                       problem.Message.Contains("not an action dictionary", StringComparison.Ordinal));
    }

    [Fact]
    public void PartFourReportsForbiddenActionsAgainstItsOwnClause()
    {
        using PdfDocument document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);
        document.Catalog.Set(PdfName.Get("OpenAction"), PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.Type] = PdfValue.Name(PdfName.Get("Action")),
            [PdfName.Get("S")] = PdfValue.Name(PdfName.Get("Launch")),
        }));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.6.1" &&
                       problem.Message.Contains("Launch", StringComparison.Ordinal));
    }

    [Fact]
    public void AReachableNonActionDictionaryIsNotClassifiedByItsSKey()
    {
        using PdfDocument document = PdfDocument.Create();
        document.Pages.Add(PageSize.A4);
        document.Catalog.Set(PdfName.Get("NonActionProbe"), PdfValue.Dictionary(new PdfDictionary
        {
            [PdfName.Get("S")] = PdfValue.Name(PdfName.Get("Launch")),
        }));

        Assert.DoesNotContain(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.6.1" &&
                       problem.Message.Contains("Launch action", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "6.9")]
    [InlineData(PdfAConformance.A2B, "6.4.1")]
    [InlineData(PdfAConformance.A3B, "6.4.1")]
    public void EarlierPartsStillForbidFieldAdditionalActions(PdfAConformance conformance, string clause)
    {
        using PdfDocument document = FormDocument(widget: false, entry: "AA");

        Assert.Contains(
            PdfAProfile.Validate(document, conformance),
            problem => problem.Clause == clause && problem.Message.Contains("may not act", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.A1B, "6.6.2")]
    [InlineData(PdfAConformance.A2B, "6.5.2")]
    [InlineData(PdfAConformance.A3B, "6.5.2")]
    public void EarlierPartsStillForbidCatalogAdditionalActions(PdfAConformance conformance, string clause)
    {
        using PdfDocument document = ContainerDocument("catalog", PartFourTriggers);

        Assert.Contains(PdfAProfile.Validate(document, conformance), problem => problem.Clause == clause);
    }

    [Theory]
    [InlineData(PdfAConformance.A2B)]
    [InlineData(PdfAConformance.A3B)]
    public void PartsTwoAndThreeStillForbidPageAdditionalActions(PdfAConformance conformance)
    {
        using PdfDocument document = ContainerDocument("page", PartFourTriggers);

        Assert.Contains(PdfAProfile.Validate(document, conformance), problem => problem.Clause == "6.5.2");
    }

    [Fact]
    public void PartOneKeepsItsExistingPageAdditionalActionBehavior()
    {
        using PdfDocument baseline = ContainerDocument("page", triggers: null);
        using PdfDocument withAdditionalActions = ContainerDocument("page", PartFourTriggers);

        Assert.Equal(Problems(baseline, PdfAConformance.A1B), Problems(withAdditionalActions, PdfAConformance.A1B));
    }

    private static PdfDocument FormDocument(bool widget, string? entry, string? trigger = null)
    {
        PdfDocument document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        var field = new PdfDictionary(8);
        field.Set(PdfName.Get("FT"), PdfValue.Name(PdfName.Get("Tx")));
        field.Set(PdfName.Get("T"), PdfValue.TextString(widget ? "widget" : "field"));

        if (widget)
        {
            field.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("Annot")));
            field.Set(PdfName.Get("Subtype"), PdfValue.Name(PdfName.Get("Widget")));
            field.Set(PdfName.Get("Rect"), PdfValue.Array(PdfArray.OfNumbers(0, 0, 0, 0)));
            field.Set(PdfName.Get("F"), PdfValue.Integer(4));
        }

        if (entry == "A")
            field.Set(PdfName.Get("A"), PdfValue.Dictionary(Action()));
        else if (entry == "AA")
        {
            field.Set(
                PdfName.Get("AA"),
                PdfValue.Dictionary(AdditionalActions([trigger ?? (widget ? "E" : "K")])));
        }

        PdfObjectId fieldId = document.AddObject(PdfValue.Dictionary(field));
        var form = new PdfDictionary(1);
        form.Set(PdfName.Get("Fields"), PdfValue.Array(new PdfArray(PdfValue.Reference(fieldId))));
        document.Catalog.Set(PdfName.Get("AcroForm"), PdfValue.Dictionary(form));

        if (widget)
            page.Annotations.RawArray.Add(PdfValue.Reference(fieldId));

        return document;
    }

    private static PdfDocument ContainerDocument(
        string container,
        IReadOnlyList<string>? triggers,
        bool actionDictionaries = true)
    {
        PdfDocument document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        if (triggers is null)
            return document;

        PdfValue additionalActions = PdfValue.Dictionary(AdditionalActions(triggers, actionDictionaries));

        switch (container)
        {
            case "catalog":
                document.Catalog.Set(PdfName.Get("AA"), additionalActions);
                break;

            case "page":
                page.Dictionary.Set(PdfName.Get("AA"), additionalActions);
                break;

            case "annotation":
                var annotation = new PdfDictionary(6);
                annotation.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("Annot")));
                annotation.Set(PdfName.Get("Subtype"), PdfValue.Name(PdfName.Get("Link")));
                annotation.Set(PdfName.Get("Rect"), PdfValue.Array(PdfArray.OfNumbers(0, 0, 0, 0)));
                annotation.Set(PdfName.Get("F"), PdfValue.Integer(4));
                annotation.Set(PdfName.Get("AA"), additionalActions);
                page.Annotations.RawArray.Add(PdfValue.Reference(document.AddObject(PdfValue.Dictionary(annotation))));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(container));
        }

        return document;
    }

    private static PdfDictionary AdditionalActions(
        IEnumerable<string> triggers,
        bool actionDictionaries = true)
    {
        var dictionary = new PdfDictionary(6);
        foreach (string trigger in triggers)
        {
            dictionary.Set(
                PdfName.Get(trigger),
                actionDictionaries ? PdfValue.Dictionary(Action()) : PdfValue.Integer(42));
        }

        return dictionary;
    }

    private static PdfDictionary Action()
    {
        var action = new PdfDictionary(2);
        action.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("Action")));
        action.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get("GoTo")));
        return action;
    }

    private static string[] Problems(PdfDocument document, PdfAConformance conformance) =>
        [.. PdfAProfile.Validate(document, conformance).Select(problem => problem.ToString())];
}
