using Inkwright.IO;

namespace Inkwright.Cos;

/// <summary>
/// The developer extensions of ISO 32000-2 §7.12. A technical specification that adds something to
/// PDF 2.0 says so here, so that a reader meeting a construct it does not know can find out which
/// document defines it rather than guessing.
/// </summary>
internal static class PdfDeveloperExtensions
{
    /// <summary>The prefix under which ISO technical specifications declare themselves.</summary>
    private static readonly PdfName IsoPrefix = PdfName.Get("ISO_");

    /// <summary>
    /// Declares that the document uses a construct from an ISO technical specification.
    /// </summary>
    /// <param name="document">The document to mark.</param>
    /// <param name="level">The number of the specification, such as 32003.</param>
    /// <param name="revision">The revision identifier, whose leading colon is part of it.</param>
    /// <param name="url">Where the specification can be found.</param>
    internal static void DeclareIso(PdfDocument document, int level, string revision, string url)
    {
        document.Version = PdfVersion.Max(document.Version, PdfVersion.Pdf20);
        PdfDictionary extensions = Ensure(document, document.Catalog, PdfName.Get("Extensions"));

        var declaration = new PdfDictionary(5)
        {
            [PdfNames.Type] = PdfValue.Name(PdfName.Get("DeveloperExtensions")),
            [PdfName.Get("BaseVersion")] = PdfValue.Name(PdfName.Get("2.0")),
            [PdfName.Get("ExtensionLevel")] = PdfValue.Integer(level),
            [PdfName.Get("ExtensionRevision")] = PdfValue.TextString(revision),
            [PdfName.Get("URL")] = PdfValue.TextString(url),
        };

        // Several specifications may apply at once, so the prefix carries an array of them.
        PdfArray declared = document.Resolve(extensions.Get(IsoPrefix)).AsArray ?? new PdfArray();

        for (int i = 0; i < declared.Count; i++)
        {
            if (document.Resolve(declared[i]).AsDictionary is { } existing &&
                existing.GetInteger(PdfName.Get("ExtensionLevel")) == level)
            {
                return;
            }
        }

        declared.Add(PdfValue.Dictionary(declaration));
        extensions.Set(IsoPrefix, PdfValue.Array(declared));
        document.MarkChanged(document.CatalogId);
    }

    private static PdfDictionary Ensure(PdfDocument document, PdfDictionary parent, PdfName key)
    {
        if (document.Resolve(parent.Get(key)).AsDictionary is { } existing)
            return existing;

        var created = new PdfDictionary(2);
        parent.Set(key, PdfValue.Dictionary(created));
        return created;
    }
}
