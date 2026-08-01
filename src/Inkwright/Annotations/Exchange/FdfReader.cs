using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.IO;

namespace Inkwright.Annotations;

/// <summary>
/// Reads a round of review out of an FDF file (ISO 32000-1 §12.7.7). FDF is PDF syntax with a
/// simpler skeleton, so the ordinary reader parses it as it stands — including the common case of
/// a hand-written file with no cross-reference table, which recovery finds by scanning. What is
/// left is to lift the annotations out of the sender's object graph and into ours.
/// </summary>
internal static class FdfReader
{
    private static readonly PdfName Fdf = PdfName.Get("FDF");
    private static readonly PdfName Annots = PdfName.Get("Annots");
    private static readonly PdfName Page = PdfName.Get("Page");

    /// <summary>What an FDF file carried: a round of review and any form field values.</summary>
    /// <param name="Annotations">The comments to merge.</param>
    /// <param name="Fields">The field values to write into a form.</param>
    internal readonly record struct Payload(List<IncomingAnnotation> Annotations, List<IncomingField> Fields);

    internal static Payload Read(PdfDocument target, byte[] fdf, List<string> warnings)
    {
        using PdfDocument source = Open(fdf);

        if (FindFdfDictionary(source) is not { } root)
        {
            warnings.Add("The file has no FDF dictionary, so it carries no round of review.");
            return new Payload([], []);
        }

        ReportWhatIsNotImported(root, warnings);

        List<IncomingField> fields = FdfFields.Read(source, root);
        List<IncomingAnnotation> incoming = ReadAnnotations(target, source, root, warnings);

        return new Payload(incoming, fields);
    }

    private static List<IncomingAnnotation> ReadAnnotations(
        PdfDocument target, PdfDocument source, PdfDictionary root, List<string> warnings)
    {
        if (root.GetArray(Annots) is not { Count: > 0 } annotations)
            return [];

        Dictionary<PdfDictionary, string> names = Names(source, annotations);
        var importer = new PdfObjectImporter(source, target);
        List<IncomingAnnotation> incoming = [];

        for (int i = 0; i < annotations.Count; i++)
        {
            if (source.Resolve(annotations[i]).AsDictionary is not { } annotation)
                continue;

            // Pop-up windows travel as part of the comment they belong to; one standing on its own
            // in the array would be adopted twice.
            if (annotation.GetName(AnnotationNames.Subtype) == AnnotationNames.Popup)
                continue;

            incoming.Add(Lift(importer, source, annotation, names, warnings));
        }

        return incoming;
    }

    private static PdfDocument Open(byte[] fdf)
    {
        try
        {
            return PdfDocument.Load(fdf, new PdfReadOptions { OpenLockedDocuments = true });
        }
        catch (PdfFormatException exception)
        {
            throw new PdfFormatException(
                "The FDF could not be read; it has neither a trailer naming its catalogue nor " +
                $"objects that could be found by scanning. {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Detaches one annotation from the sender's graph and copies it into ours. Three entries are
    /// taken off first: the page reference, the pop-up and the tie to the comment it answers. All
    /// three point at objects that mean nothing here, and copying them would drag the sender's
    /// whole graph across along with duplicates of comments we already hold.
    /// </summary>
    private static IncomingAnnotation Lift(
        PdfObjectImporter importer,
        PdfDocument source,
        PdfDictionary annotation,
        Dictionary<PdfDictionary, string> names,
        List<string> warnings)
    {
        int pageIndex = ReadPageIndex(annotation, warnings);
        PdfDictionary? popup = source.Resolve(annotation.Get(AnnotationNames.Popup)).AsDictionary;

        string? parent = null;
        if (source.Resolve(annotation.Get(AnnotationNames.IRT)).AsDictionary is { } answered)
            names.TryGetValue(answered, out parent);

        bool grouped = annotation.GetName(AnnotationNames.RT) == AnnotationNames.Group;

        annotation.Remove(AnnotationNames.P);
        annotation.Remove(AnnotationNames.Popup);
        annotation.Remove(AnnotationNames.IRT);
        annotation.Remove(AnnotationNames.RT);
        annotation.Remove(Page);

        return new IncomingAnnotation
        {
            PageIndex = pageIndex,
            Dictionary = importer.Import(PdfValue.Dictionary(annotation)).AsDictionary!,
            InReplyTo = parent,
            IsGroupedWithParent = grouped,
            WantsPopup = popup is not null,
            PopupBounds = popup is null ? null : PdfRectangle.FromArray(popup.GetArray(AnnotationNames.Rect)),
            PopupOpen = popup?.GetBoolean(AnnotationNames.Open) ?? false,
        };
    }

    private static int ReadPageIndex(PdfDictionary annotation, List<string> warnings)
    {
        if (annotation.ContainsKey(Page))
            return (int)annotation.GetInteger(Page);

        warnings.Add("An annotation in the FDF says nothing about which page it belongs on; it went on the first.");
        return 0;
    }

    /// <summary>
    /// The name each annotation goes under, so that a reply pointing at an object number can be
    /// turned into a reply naming a comment — the only reference that survives the crossing.
    /// </summary>
    private static Dictionary<PdfDictionary, string> Names(PdfDocument source, PdfArray annotations)
    {
        Dictionary<PdfDictionary, string> names = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < annotations.Count; i++)
        {
            if (source.Resolve(annotations[i]).AsDictionary is not { } annotation)
                continue;

            if (annotation.GetTextString(AnnotationNames.NM) is { Length: > 0 } name)
                names[annotation] = name;
        }

        return names;
    }

    /// <summary>
    /// Looks for the FDF dictionary where the trailer says it is, and failing that anywhere in the
    /// file. A producer that writes a trailer pointing somewhere else still wrote a usable review.
    /// </summary>
    private static PdfDictionary? FindFdfDictionary(PdfDocument source)
    {
        if (source.Catalog.GetDictionary(Fdf) is { } declared)
            return declared;

        if (source.Reader is not { } reader)
            return null;

        foreach (int number in reader.ObjectNumbers.ToArray())
        {
            if (source.GetObject(number).AsDictionary?.GetDictionary(Fdf) is { } found)
                return found;
        }

        return null;
    }

    private static void ReportWhatIsNotImported(PdfDictionary root, List<string> warnings)
    {
        Report("Differences", "a record of the sender's own edits, which cannot be replayed here");
        Report("JavaScript", "document-level scripts, which are not imported");
        Report("Pages", "template pages, which are not imported");

        void Report(string key, string what)
        {
            if (root.ContainsKey(PdfName.Get(key)))
                warnings.Add($"The FDF also carries {what}.");
        }
    }
}
