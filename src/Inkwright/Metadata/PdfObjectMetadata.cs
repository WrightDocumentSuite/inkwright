using System.Text;
using Inkwright.Cos;

namespace Inkwright.Metadata;

/// <summary>
/// Metadata attached to one object rather than to the whole document. PDF 2.0 allows a
/// <c>Metadata</c> entry in any dictionary, and PDF 2.0 Application Note 003 sets out where doing so
/// is understood by other software: pages, image and form objects, colour profiles, embedded fonts,
/// patterns, structure elements, threads and embedded files.
/// </summary>
/// <remarks>
/// Metadata is only worth writing where a reader will look for it. Attaching a licence to an image
/// object is understood; attaching one to a dictionary nobody inspects is a private note.
/// </remarks>
public static class PdfObjectMetadata
{
    /// <summary>Attaches a metadata stream to an object.</summary>
    /// <param name="document">The document the object belongs to.</param>
    /// <param name="target">The dictionary to attach to, which may be a stream's dictionary.</param>
    /// <param name="xmp">The metadata, as an XMP packet.</param>
    /// <returns>A reference to the metadata stream.</returns>
    public static PdfValue Attach(PdfDocument document, PdfDictionary target, string xmp)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(xmp);

        var stream = PdfStream.FromData(Encoding.UTF8.GetBytes(xmp));
        stream.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Metadata")));
        stream.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("XML")));

        // A metadata stream is read by tools that scan the file without decompressing it, so it is
        // written plain. ISO 32000-2 14.3.2 says as much for the document-level one.
        PdfObjectId id = document.AddObject(PdfValue.Stream(stream));
        var reference = PdfValue.Reference(id);

        target.Set(PdfName.Get("Metadata"), reference);
        return reference;
    }

    /// <summary>Reads the metadata attached to an object.</summary>
    /// <param name="document">The document the object belongs to.</param>
    /// <param name="target">The dictionary to read from.</param>
    /// <returns>The XMP packet, or <see langword="null"/> when there is none.</returns>
    public static string? Read(PdfDocument document, PdfDictionary target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);

        if (document.Resolve(target.Get(PdfName.Get("Metadata"))).AsStream is not { } stream)
            return null;

        try
        {
            return Encoding.UTF8.GetString(stream.Decode().Span);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether attaching metadata to this kind of object is one of the placements the standard
    /// names, and so one another program is likely to look at.
    /// </summary>
    /// <param name="document">The document the object belongs to.</param>
    /// <param name="target">The dictionary to judge.</param>
    public static bool IsRecognisedPlacement(PdfDocument document, PdfDictionary target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);

        string? type = document.Resolve(target.Get(PdfNames.Type)).AsName?.Value;
        string? subtype = document.Resolve(target.Get(PdfNames.Subtype)).AsName?.Value;

        return type switch
        {
            "Catalog" or "Page" or "Pages" or "Thread" or "Filespec" or "StructElem" or "Font" => true,
            "XObject" => subtype is "Image" or "Form",
            "Pattern" => true,
            _ => subtype is "Type1C" or "CIDFontType0C" or "OpenType",
        };
    }
}
