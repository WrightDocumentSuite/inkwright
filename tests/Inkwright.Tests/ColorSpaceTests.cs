using System.Text;
using Inkwright.Color;
using Inkwright.Cos;
using Xunit;

namespace Inkwright.Tests;

public class ColorSpaceTests
{
    [Fact]
    public void ConvertsTheDeviceSpaces()
    {
        Assert.Equal(new PdfRgb(128, 128, 128), PdfColorSpace.DeviceGray.ToRgb([0.502]));
        Assert.Equal(new PdfRgb(255, 0, 0), PdfColorSpace.DeviceRgb.ToRgb([1, 0, 0]));

        // Cyan ink takes red away; black ink takes everything away.
        Assert.Equal(new PdfRgb(0, 255, 255), PdfColorSpace.DeviceCmyk.ToRgb([1, 0, 0, 0]));
        Assert.Equal(new PdfRgb(0, 0, 0), PdfColorSpace.DeviceCmyk.ToRgb([0, 0, 0, 1]));
        Assert.Equal(new PdfRgb(255, 255, 255), PdfColorSpace.DeviceCmyk.ToRgb([0, 0, 0, 0]));
    }

    [Theory]
    [InlineData("DeviceGray", 1)]
    [InlineData("DeviceRGB", 3)]
    [InlineData("DeviceCMYK", 4)]
    [InlineData("G", 1)]
    [InlineData("RGB", 3)]
    [InlineData("CMYK", 4)]
    public void ReadsTheDeviceSpacesByName(string name, int components) =>
        Assert.Equal(components, PdfColorSpace.Read(PdfValue.Name(PdfName.Get(name)))!.ComponentCount);

    [Fact]
    public void FallsBackToTheAlternateOfAnIccProfile()
    {
        var stream = new PdfStream(new PdfDictionary(2), System.Array.Empty<byte>());
        stream.Dictionary.Set(PdfName.Get("N"), PdfValue.Integer(4));

        // With no alternate stated, the component count decides which device space stands in.
        PdfColorSpace space = PdfColorSpace.Read(Array("ICCBased", PdfValue.Stream(stream)))!;
        Assert.Equal(4, space.ComponentCount);
        Assert.Equal(new PdfRgb(0, 255, 255), space.ToRgb([1, 0, 0, 0]));

        stream.Dictionary.Set(PdfName.Get("Alternate"), PdfValue.Name(PdfName.Get("DeviceGray")));
        Assert.Equal(1, PdfColorSpace.Read(Array("ICCBased", PdfValue.Stream(stream)))!.ComponentCount);
    }

    [Fact]
    public void LooksColoursUpInAPalette()
    {
        // A three-entry palette of red, green and blue.
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255];

        PdfColorSpace space = PdfColorSpace.Read(Array(
            "Indexed",
            PdfValue.Name(PdfName.Get("DeviceRGB")),
            PdfValue.Integer(2),
            PdfValue.String(palette)))!;

        Assert.True(space.IsIndexed);
        Assert.Equal(1, space.ComponentCount);
        Assert.Equal(new PdfRgb(255, 0, 0), space.ToRgb([0]));
        Assert.Equal(new PdfRgb(0, 255, 0), space.ToRgb([1]));
        Assert.Equal(new PdfRgb(0, 0, 255), space.ToRgb([2]));

        // An index past the end of the palette is clipped rather than read out of bounds.
        Assert.Equal(new PdfRgb(0, 0, 255), space.ToRgb([99]));

        // Samples in an indexed space are indices, so the default decode spans the sample width.
        Assert.Equal([0, 255], space.DefaultDecode(8));
        Assert.Equal([0, 15], space.DefaultDecode(4));
    }

    [Fact]
    public void RunsTheTintTransformOfASeparation()
    {
        // A separation of one ink whose full strength is pure cyan.
        PdfColorSpace space = PdfColorSpace.Read(Array(
            "Separation",
            PdfValue.Name(PdfName.Get("Spot")),
            PdfValue.Name(PdfName.Get("DeviceCMYK")),
            PdfValue.Dictionary(Tint())))!;

        Assert.Equal(1, space.ComponentCount);
        Assert.Equal(new PdfRgb(255, 255, 255), space.ToRgb([0]));
        Assert.Equal(new PdfRgb(0, 255, 255), space.ToRgb([1]));
    }

    [Fact]
    public void PaintsNothingForTheNoneColorant()
    {
        PdfColorSpace space = PdfColorSpace.Read(Array(
            "Separation",
            PdfValue.Name(PdfName.Get("None")),
            PdfValue.Name(PdfName.Get("DeviceCMYK")),
            PdfValue.Dictionary(Tint())))!;

        Assert.Equal(new PdfRgb(255, 255, 255), space.ToRgb([1]));
    }

    [Fact]
    public void ConvertsLabThroughItsWhitePoint()
    {
        var dictionary = new PdfDictionary(2);
        dictionary.Set(PdfName.Get("WhitePoint"), PdfValue.Array(PdfArray.OfNumbers(0.9505, 1.0, 1.089)));
        dictionary.Set(PdfName.Get("Range"), PdfValue.Array(PdfArray.OfNumbers(-100, 100, -100, 100)));

        PdfColorSpace space = PdfColorSpace.Read(Array("Lab", PdfValue.Dictionary(dictionary)))!;

        Assert.Equal(3, space.ComponentCount);

        // Lightness 100 with no colour is white; lightness 0 is black.
        Assert.Equal(new PdfRgb(255, 255, 255), space.ToRgb([100, 0, 0]));
        Assert.Equal(new PdfRgb(0, 0, 0), space.ToRgb([0, 0, 0]));

        // Lightness runs to 100 rather than to one, which the default decode has to say.
        Assert.Equal([0, 100, -100, 100, -100, 100], space.DefaultDecode(8));
    }

    [Fact]
    public void AppliesTheCalGrayWhitePointAndGamma()
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.WhitePoint] = PdfValue.Array(PdfArray.OfNumbers(0.95047, 1, 1.08883)),
            [PdfName.Get("Gamma")] = PdfValue.Number(1),
        };

        PdfColorSpace space = PdfColorSpace.Read(Array("CalGray", PdfValue.Dictionary(dictionary)))!;
        PdfRgb middle = space.ToRgb([0.5]);

        Assert.Equal(1, space.ComponentCount);
        Assert.InRange(middle.R, 187, 189);
        Assert.InRange(middle.G, 187, 189);
        Assert.InRange(middle.B, 187, 189);
        Assert.NotEqual(PdfColorSpace.DeviceGray.ToRgb([0.5]), middle);
    }

    [Fact]
    public void AppliesTheCalRgbGammaAndColumnMajorMatrix()
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.WhitePoint] = PdfValue.Array(PdfArray.OfNumbers(0.95047, 1, 1.08883)),
            [PdfName.Get("Gamma")] = PdfValue.Array(PdfArray.OfNumbers(1, 1, 1)),
            [PdfNames.Matrix] = PdfValue.Array(PdfArray.OfNumbers(
                0.4124564, 0.2126729, 0.0193339,
                0.3575761, 0.7151522, 0.1191920,
                0.1804375, 0.0721750, 0.9503041)),
        };

        PdfColorSpace space = PdfColorSpace.Read(Array("CalRGB", PdfValue.Dictionary(dictionary)))!;
        PdfRgb red = space.ToRgb([1, 0, 0]);
        PdfRgb green = space.ToRgb([0, 1, 0]);

        Assert.InRange(red.R, 254, 255);
        Assert.InRange(red.G, 0, 1);
        Assert.InRange(red.B, 0, 1);
        Assert.InRange(green.R, 0, 1);
        Assert.InRange(green.G, 254, 255);
        Assert.InRange(green.B, 0, 1);
    }

    [Fact]
    public void RejectsACalibratedSpaceWithoutItsRequiredWhitePoint()
    {
        Assert.Null(PdfColorSpace.Read(Array("CalGray", PdfValue.Dictionary(new PdfDictionary()))));
        Assert.Null(PdfColorSpace.Read(Array("CalRGB", PdfValue.Dictionary(new PdfDictionary()))));
    }

    [Fact]
    public void AnswersNothingForSpacesItCannotRead()
    {
        Assert.Null(PdfColorSpace.Read(PdfValue.Name(PdfName.Get("Nonesuch"))));
        Assert.Null(PdfColorSpace.Read(PdfValue.Null));
    }

    /// <summary>A type 2 function running from no ink to full cyan.</summary>
    private static PdfDictionary Tint()
    {
        var tint = new PdfDictionary(5);
        tint.Set(PdfName.Get("FunctionType"), PdfValue.Integer(2));
        tint.Set(PdfName.Get("Domain"), PdfValue.Array(PdfArray.OfNumbers(0, 1)));
        tint.Set(PdfName.Get("C0"), PdfValue.Array(PdfArray.OfNumbers(0, 0, 0, 0)));
        tint.Set(PdfName.Get("C1"), PdfValue.Array(PdfArray.OfNumbers(1, 0, 0, 0)));
        return tint;
    }

    private static PdfValue Array(string family, params PdfValue[] rest)
    {
        var array = new PdfArray(rest.Length + 1);
        array.Add(PdfValue.Name(PdfName.Get(family)));
        foreach (PdfValue value in rest)
            array.Add(value);

        return PdfValue.Array(array);
    }
}
