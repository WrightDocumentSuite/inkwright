using Inkwright.Cos;

namespace Inkwright;

/// <summary>
/// An output intent (ISO 32000-1 §14.11.5): the colour characterisation of the device the document
/// was prepared for, carried as an embedded ICC profile. PDF 2.0 lets an intent live on a single
/// page as well as on the whole document, which is what a file assembled from pages meant for
/// different devices needs.
/// </summary>
public sealed class PdfOutputIntent
{
    private PdfOutputIntent(byte[] iccProfile, int components, string subtype, string conditionIdentifier)
    {
        IccProfile = iccProfile;
        Components = components;
        Subtype = subtype;
        OutputConditionIdentifier = conditionIdentifier;
    }

    /// <summary>The embedded ICC profile that characterises the colour space.</summary>
    public byte[] IccProfile { get; }

    /// <summary>How many colour components the profile has: 1 grey, 3 RGB, 4 CMYK.</summary>
    public int Components { get; }

    /// <summary>The intent subtype, such as <c>GTS_PDFA1</c> or <c>GTS_PDFX</c>.</summary>
    public string Subtype { get; }

    /// <summary>A name identifying the intended output device or production condition.</summary>
    public string OutputConditionIdentifier { get; }

    /// <summary>A human-readable description of the intended output condition.</summary>
    public string? OutputCondition { get; init; }

    /// <summary>The registry the condition identifier is drawn from.</summary>
    public string? RegistryName { get; init; }

    /// <summary>Builds an output intent from an embedded ICC profile.</summary>
    /// <param name="iccProfile">The DER-free ICC profile bytes to embed.</param>
    /// <param name="components">How many colour components the profile has.</param>
    /// <param name="subtype">The intent subtype; <c>GTS_PDFA1</c> when omitted.</param>
    /// <param name="conditionIdentifier">A name for the output condition.</param>
    public static PdfOutputIntent FromIccProfile(
        byte[] iccProfile, int components, string subtype = "GTS_PDFA1", string conditionIdentifier = "Custom")
    {
        ArgumentNullException.ThrowIfNull(iccProfile);
        return new PdfOutputIntent(iccProfile, components, subtype, conditionIdentifier);
    }

    /// <summary>The standard sRGB intent, using the profile the library generates.</summary>
    /// <param name="subtype">The intent subtype; <c>GTS_PDFA1</c> when omitted.</param>
    public static PdfOutputIntent SRgb(string subtype = "GTS_PDFA1") =>
        new(Metadata.IccProfile.CreateSRgb(), 3, subtype, "sRGB IEC61966-2.1")
        {
            OutputCondition = "sRGB IEC61966-2.1",
            RegistryName = "http://www.color.org",
        };

    /// <summary>Writes the intent as an object and returns the reference to put in an array.</summary>
    internal PdfValue Build(PdfDocument document)
    {
        var stream = PdfStream.FromData(IccProfile);
        stream.Dictionary.Set(PdfName.Get("N"), PdfValue.Integer(Components));
        PdfObjectId profileId = document.AddObject(PdfValue.Stream(stream));

        var intent = new PdfDictionary(6);
        intent.Set(PdfName.Get("Type"), PdfValue.Name("OutputIntent"));
        intent.Set(PdfName.Get("S"), PdfValue.Name(Subtype));
        intent.Set(PdfName.Get("OutputConditionIdentifier"), PdfValue.TextString(OutputConditionIdentifier));

        if (OutputCondition is { } condition)
            intent.Set(PdfName.Get("OutputCondition"), PdfValue.TextString(condition));

        if (RegistryName is { } registry)
            intent.Set(PdfName.Get("RegistryName"), PdfValue.TextString(registry));

        intent.Set(PdfName.Get("DestOutputProfile"), PdfValue.Reference(profileId));

        PdfObjectId intentId = document.AddObject(PdfValue.Dictionary(intent));
        return PdfValue.Reference(intentId);
    }
}
