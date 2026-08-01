using System.Globalization;
using Inkwright.Cos;

namespace Inkwright.Content;

/// <summary>
/// Names the resources a content stream refers to. Adding the same object twice returns the same
/// name, so a canvas can hand over a font or an image without tracking whether it did so already.
/// </summary>
public sealed class PdfResources
{
    private readonly PdfDocument _document;
    private readonly PdfDictionary _dictionary;
    private readonly Dictionary<(string Category, PdfValue Value), PdfName> _byValue = [];

    internal PdfResources(PdfDocument document, PdfDictionary dictionary)
    {
        _document = document;
        _dictionary = dictionary;
    }

    /// <summary>The resource dictionary being filled in.</summary>
    public PdfDictionary Dictionary => _dictionary;

    /// <summary>Registers a font and returns the name a <c>Tf</c> operator should use.</summary>
    /// <param name="font">A reference to the font dictionary.</param>
    public PdfName AddFont(PdfValue font) => Add(PdfNames.Font, "F", font);

    /// <summary>Registers an external object and returns the name a <c>Do</c> operator should use.</summary>
    /// <param name="xObject">A reference to the form or image XObject.</param>
    public PdfName AddXObject(PdfValue xObject) => Add(PdfNames.XObject, "X", xObject);

    /// <summary>Registers a graphics state dictionary and returns the name a <c>gs</c> operator should use.</summary>
    /// <param name="state">The state dictionary, which is stored as an indirect object.</param>
    public PdfName AddExtGState(PdfDictionary state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Add(PdfNames.ExtGState, "GS", PdfValue.Reference(_document.AddObject(PdfValue.Dictionary(state))));
    }

    /// <summary>Registers a colour space and returns the name a <c>cs</c> operator should use.</summary>
    /// <param name="colorSpace">The colour space object.</param>
    public PdfName AddColorSpace(PdfValue colorSpace) => Add(PdfNames.ColorSpace, "CS", colorSpace);

    /// <summary>Registers a shading and returns the name an <c>sh</c> operator should use.</summary>
    /// <param name="shading">The shading dictionary or stream.</param>
    public PdfName AddShading(PdfValue shading) => Add(PdfNames.Shading, "Sh", shading);

    /// <summary>Registers a pattern and returns the name a colour operator should use.</summary>
    /// <param name="pattern">The pattern stream.</param>
    public PdfName AddPattern(PdfValue pattern) => Add(PdfNames.Pattern, "P", pattern);

    /// <summary>Registers a marked-content property list and returns the name a <c>BDC</c> operator should use.</summary>
    /// <param name="properties">The property dictionary.</param>
    public PdfName AddProperties(PdfDictionary properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return Add(PdfNames.Properties, "MC", PdfValue.Reference(_document.AddObject(PdfValue.Dictionary(properties))));
    }

    private PdfName Add(PdfName category, string prefix, PdfValue value)
    {
        if (_byValue.TryGetValue((prefix, value), out PdfName? existing))
            return existing;

        PdfDictionary bucket = _dictionary.GetDictionary(category) ?? CreateBucket(category);

        // Reuse the name the file already gave this object, so rewriting a page keeps its own names.
        foreach (KeyValuePair<PdfName, PdfValue> entry in bucket)
        {
            if (entry.Value == value)
            {
                _byValue[(prefix, value)] = entry.Key;
                return entry.Key;
            }
        }

        int index = bucket.Count + 1;
        PdfName name;
        do
        {
            name = PdfName.Get(string.Create(CultureInfo.InvariantCulture, $"{prefix}{index++}"));
        }
        while (bucket.ContainsKey(name));

        bucket.Set(name, value);
        _byValue[(prefix, value)] = name;
        return name;
    }

    private PdfDictionary CreateBucket(PdfName category)
    {
        var bucket = new PdfDictionary { Resolver = _document };
        _dictionary.Set(category, PdfValue.Dictionary(bucket));
        return bucket;
    }
}
