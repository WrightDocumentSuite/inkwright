using System.Text;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Text;
using Xunit;

namespace Inkwright.Tests;

public class OptionalContentTests
{
    [Fact]
    public void ReportsTheLayersADocumentDeclares()
    {
        using PdfDocument document = PdfDocument.Load(Build(hiddenText: "hidden", visibleText: "shown"));

        PdfOptionalContent layers = document.OptionalContent;

        Assert.True(layers.IsDefined);
        Assert.Equal(["Visible layer", "Hidden layer"], layers.Layers.Select(layer => layer.Name));
        Assert.True(layers.Layers[0].IsVisible);
        Assert.False(layers.Layers[1].IsVisible);
    }

    [Fact]
    public void ReadsHiddenTextUnlessAskedToSkipIt()
    {
        using PdfDocument document = PdfDocument.Load(Build(hiddenText: "hidden", visibleText: "shown"));
        PdfPage page = document.Pages[0];

        string all = page.ExtractText();
        Assert.Contains("hidden", all, StringComparison.Ordinal);
        Assert.Contains("shown", all, StringComparison.Ordinal);

        string visible = page.ExtractText(new TextExtractionOptions { SkipHiddenLayers = true });
        Assert.DoesNotContain("hidden", visible, StringComparison.Ordinal);
        Assert.Contains("shown", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void AppliesTheMembershipPolicyOfACombinedGroup()
    {
        using PdfDocument document = PdfDocument.Load(Build(hiddenText: "hidden", visibleText: "shown"));
        PdfOptionalContent layers = document.OptionalContent;

        PdfValue on = layers.Layers[0].Reference;
        PdfValue off = layers.Layers[1].Reference;

        // AnyOn is the default, so one group being on is enough.
        Assert.True(layers.IsVisible(Membership(document, policy: null, on, off)));
        Assert.False(layers.IsVisible(Membership(document, "AllOn", on, off)));
        Assert.True(layers.IsVisible(Membership(document, "AnyOff", on, off)));
        Assert.False(layers.IsVisible(Membership(document, "AllOff", on, off)));
    }

    [Fact]
    public void EvaluatesAVisibilityExpression()
    {
        using PdfDocument document = PdfDocument.Load(Build(hiddenText: "hidden", visibleText: "shown"));
        PdfOptionalContent layers = document.OptionalContent;

        PdfValue on = layers.Layers[0].Reference;
        PdfValue off = layers.Layers[1].Reference;

        Assert.True(layers.IsVisible(Expression(document, Array("Or", on, off))));
        Assert.False(layers.IsVisible(Expression(document, Array("And", on, off))));
        Assert.True(layers.IsVisible(Expression(document, Array("Not", off))));

        // Nested expressions: on AND (NOT off).
        Assert.True(layers.IsVisible(Expression(document, Array("And", on, PdfValue.Array(Array("Not", off))))));
    }

    private static PdfArray Array(string @operator, params PdfValue[] operands)
    {
        var array = new PdfArray(operands.Length + 1);
        array.Add(PdfValue.Name(PdfName.Get(@operator)));
        foreach (PdfValue operand in operands)
            array.Add(operand);

        return array;
    }

    private static PdfValue Membership(PdfDocument document, string? policy, params PdfValue[] groups)
    {
        var array = new PdfArray(groups.Length);
        foreach (PdfValue group in groups)
            array.Add(group);

        var dictionary = new PdfDictionary(3);
        dictionary.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("OCMD")));
        dictionary.Set(PdfName.Get("OCGs"), PdfValue.Array(array));

        if (policy is not null)
            dictionary.Set(PdfName.Get("P"), PdfValue.Name(PdfName.Get(policy)));

        document.Attach(PdfValue.Dictionary(dictionary));
        return PdfValue.Dictionary(dictionary);
    }

    private static PdfValue Expression(PdfDocument document, PdfArray expression)
    {
        var dictionary = new PdfDictionary(2);
        dictionary.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("OCMD")));
        dictionary.Set(PdfName.Get("VE"), PdfValue.Array(expression));

        document.Attach(PdfValue.Dictionary(dictionary));
        return PdfValue.Dictionary(dictionary);
    }

    /// <summary>
    /// A page that paints two lines, each inside a marked-content sequence tagged with a layer. The
    /// document's default configuration turns the second layer off.
    /// </summary>
    private static byte[] Build(string hiddenText, string visibleText)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        PdfValue visible = Group(document, "Visible layer");
        PdfValue hidden = Group(document, "Hidden layer");

        var groups = new PdfArray(2);
        groups.Add(visible);
        groups.Add(hidden);

        var off = new PdfArray(1);
        off.Add(hidden);

        var configuration = new PdfDictionary(2);
        configuration.Set(PdfName.Get("Name"), PdfValue.TextString("Default"));
        configuration.Set(PdfName.Get("OFF"), PdfValue.Array(off));

        var properties = new PdfDictionary(2);
        properties.Set(PdfName.Get("OCGs"), PdfValue.Array(groups));
        properties.Set(PdfName.Get("D"), PdfValue.Dictionary(configuration));
        document.Attach(PdfValue.Dictionary(properties));
        document.Catalog.Set(PdfName.Get("OCProperties"), PdfValue.Dictionary(properties));

        var named = new PdfDictionary(2);
        named.Set(PdfName.Get("L1"), visible);
        named.Set(PdfName.Get("L2"), hidden);
        page.Resources.Set(PdfName.Get("Properties"), PdfValue.Dictionary(named));

        var font = new PdfDictionary(3);
        font.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("Font")));
        font.Set(PdfName.Get("Subtype"), PdfValue.Name(PdfName.Get("Type1")));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name(PdfName.Get("Helvetica")));

        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(font))));
        page.Resources.Set(PdfName.Get("Font"), PdfValue.Dictionary(fonts));

        // Each line sits in its own marked-content sequence, tagged with the layer that governs it.
        page.SetContent(Encoding.Latin1.GetBytes(
            $"/OC /L1 BDC BT /F1 14 Tf 72 700 Td ({visibleText}) Tj ET EMC " +
            $"/OC /L2 BDC BT /F1 14 Tf 72 680 Td ({hiddenText}) Tj ET EMC"));

        return document.ToArray();
    }

    private static PdfValue Group(PdfDocument document, string name)
    {
        var group = new PdfDictionary(2);
        group.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("OCG")));
        group.Set(PdfName.Get("Name"), PdfValue.TextString(name));
        return PdfValue.Reference(document.AddObject(PdfValue.Dictionary(group)));
    }
}
