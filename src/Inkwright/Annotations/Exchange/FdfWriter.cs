using Inkwright.Cos;
using Inkwright.IO;

namespace Inkwright.Annotations;

/// <summary>
/// Writes the comments of a document as an FDF file (ISO 32000-1 §12.7.7). FDF is PDF syntax
/// carrying a much smaller skeleton — a catalogue, an array of annotations, and each of them
/// saying which page it belongs on — so the comments are copied into a document of their own and
/// only what that catalogue can reach is written out. Appearance streams travel along, which is
/// the reason to prefer FDF over XFDF: the far end sees the drawing rather than a reconstruction.
/// </summary>
internal static class FdfWriter
{
    private static readonly PdfName Fdf = PdfName.Get("FDF");
    private static readonly PdfName Annots = PdfName.Get("Annots");
    private static readonly PdfName Page = PdfName.Get("Page");

    internal static void Write(PdfDocument document, Stream output, PdfExchangeContent content = PdfExchangeContent.All)
    {
        using PdfDocument container = PdfDocument.Create();
        var importer = new PdfObjectImporter(document, container);

        var fdf = new PdfDictionary(2);

        if (content.HasFlag(PdfExchangeContent.Annotations))
        {
            var annotations = new PdfArray();

            for (int i = 0; i < document.Pages.Count; i++)
            {
                foreach (PdfMarkupAnnotation comment in document.Pages[i].Annotations.Comments)
                {
                    foreach (PdfMarkupAnnotation member in comment.Thread)
                        Copy(container, importer, member, i, annotations);
                }
            }

            fdf.Set(Annots, PdfValue.Array(annotations));
        }

        if (content.HasFlag(PdfExchangeContent.Fields) && FdfFields.Write(document, importer) is { } fields)
        {
            container.Attach(PdfValue.Array(fields));
            fdf.Set(PdfName.Get("Fields"), PdfValue.Array(fields));
        }

        var root = new PdfDictionary(1);
        root.Set(Fdf, PdfValue.Dictionary(fdf));

        PdfObjectId rootId = container.AddObject(PdfValue.Dictionary(root));
        Serialize(container, rootId, output);
    }

    /// <summary>
    /// Copies one annotation into the container. The copy goes by reference so that the importer
    /// keeps its own record of what it has already brought across: a reply and the comment it
    /// answers must end up pointing at one object, not at two copies of the same one.
    /// </summary>
    private static void Copy(
        PdfDocument container,
        PdfObjectImporter importer,
        PdfMarkupAnnotation annotation,
        int pageIndex,
        PdfArray annotations)
    {
        PdfValue copied = annotation.Id.IsValid
            ? importer.Import(PdfValue.Reference(annotation.Id))
            : PdfValue.Reference(container.AddObject(importer.Import(PdfValue.Dictionary(annotation.Dictionary))));

        if (container.Resolve(copied).AsDictionary is not { } body)
            return;

        // Page references belong to the document the comments came off; in an FDF the page is an
        // ordinal instead (table 251).
        body.Remove(AnnotationNames.P);
        body.Set(Page, PdfValue.Integer(pageIndex));

        if (container.Resolve(body.Get(AnnotationNames.Popup)).AsDictionary is { } popup)
            popup.Remove(AnnotationNames.P);

        annotations.Add(copied);
    }

    private static void Serialize(PdfDocument container, PdfObjectId root, Stream output)
    {
        SortedDictionary<int, PdfValue> objects = Reachable(container, root);

        using var writer = new Utf8Writer(output);
        var serializer = new PdfObjectSerializer(writer);
        var offsets = new SortedDictionary<int, long>();

        writer.Write("%FDF-1.2\n"u8);
        writer.Write("%\u00E2\u00E3\u00CF\u00D3\n"u8);

        foreach ((int number, PdfValue value) in objects)
        {
            offsets[number] = writer.Length;
            serializer.WriteIndirectObject(new PdfObjectId(number, 0), value);
        }

        long start = writer.Length;
        int size = objects.Count == 0 ? 1 : objects.Keys.Max() + 1;
        WriteCrossReferences(writer, offsets, size);

        var trailer = new PdfDictionary(2);
        trailer.Set(PdfNames.Root, PdfValue.Reference(root));
        trailer.Set(PdfNames.Size, PdfValue.Integer(size));

        writer.Write("trailer\n"u8);
        serializer.CurrentObject = default;
        serializer.WriteValue(PdfValue.Dictionary(trailer));

        writer.Write("\nstartxref\n"u8);
        writer.Write(start);
        writer.Write("\n%%EOF\n"u8);
    }

    private static void WriteCrossReferences(Utf8Writer writer, SortedDictionary<int, long> offsets, int size)
    {
        writer.Write("xref\n"u8);
        writer.Write(0);
        writer.WriteSpace();
        writer.Write(size);
        writer.WriteNewLine();

        for (int number = 0; number < size; number++)
        {
            if (number == 0 || !offsets.TryGetValue(number, out long offset))
            {
                writer.Write("0000000000 65535 f \n"u8);
                continue;
            }

            writer.WritePadded(offset, 10);
            writer.Write(" 00000 n \n"u8);
        }
    }

    /// <summary>
    /// The objects the catalogue can actually reach. The container was made as an ordinary
    /// document, so it also holds a page tree and an information dictionary that have nothing to do
    /// with the review; writing those would put a document inside a message about a document.
    /// </summary>
    private static SortedDictionary<int, PdfValue> Reachable(PdfDocument container, PdfObjectId root)
    {
        SortedDictionary<int, PdfValue> objects = [];
        Stack<PdfValue> pending = new();
        pending.Push(PdfValue.Reference(root));

        while (pending.Count > 0)
        {
            PdfValue value = pending.Pop();

            switch (value.Kind)
            {
                case PdfValueKind.Reference:
                    {
                        int number = value.AsReference.Number;
                        if (objects.ContainsKey(number))
                            break;

                        PdfValue body = container.GetObject(number);
                        objects[number] = body;
                        pending.Push(body);
                        break;
                    }

                case PdfValueKind.Array:
                    {
                        PdfArray array = value.AsArray!;
                        for (int i = 0; i < array.Count; i++)
                            pending.Push(array[i]);

                        break;
                    }

                case PdfValueKind.Dictionary:
                case PdfValueKind.Stream:
                    {
                        foreach ((PdfName _, PdfValue entry) in value.AsDictionary!)
                            pending.Push(entry);

                        break;
                    }

                default:
                    break;
            }
        }

        return objects;
    }
}
