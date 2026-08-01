using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>
/// The rules ISO 14289 states about the parts of a document a person interacts with: annotations,
/// links, forms, attachments, navigation and the objects a page draws through.
/// </summary>
internal static partial class PdfUaRules
{
    /// <summary>
    /// Checks annotations (part 1 clause 7.18). Hidden annotations, those outside the visible area
    /// and pop-ups are exempt, because none of them is content a reader would announce.
    /// </summary>
    internal static void CheckAnnotations(PdfUaContext context)
    {
        string clause = context.Clause("7.18.1", "8.9.2");
        var pagesWithAnnotations = new HashSet<PdfPage>();

        foreach ((PdfPage page, PdfDictionary annotation) in context.Annotations())
        {
            string subtype = context.Resolve(annotation.Get(PdfNames.Subtype)).AsName?.Value ?? string.Empty;

            if (subtype == "Popup")
                continue;

            // The second bit of the flags is the hidden one.
            if ((annotation.GetInteger(PdfName.Get("F")) & 2) != 0)
                continue;

            pagesWithAnnotations.Add(page);

            if (subtype == "TrapNet")
            {
                context.Violation(
                    context.Clause("7.18.2", "8.9.2"),
                    "The document carries a trapping network annotation, which the standard forbids.");
            }

            if (subtype == "Link")
                CheckLink(context, annotation);

            // Every other visible annotation needs something a reader can announce in place of it.
            if (subtype is not ("Link" or "Widget") &&
                string.IsNullOrWhiteSpace(annotation.GetTextString(PdfNames.Contents)))
            {
                context.Violation(clause, $"A {Describe(subtype)} annotation carries no description.");
            }
        }

        CheckTabOrder(context, pagesWithAnnotations);
    }

    private static void CheckLink(PdfUaContext context, PdfDictionary annotation)
    {
        string clause = context.Clause("7.18.5", "8.9.2.4");

        if (string.IsNullOrWhiteSpace(annotation.GetTextString(PdfNames.Contents)))
            context.Violation(clause, "A link annotation carries no description of where it leads.");

        if (context.Resolve(annotation.Get(PdfName.Get("A"))).AsDictionary is not { } action)
            return;

        // A server-side image map cannot be operated without a pointing device.
        if (action.Get(PdfName.Get("IsMap")).AsBoolean)
        {
            context.Violation(
                clause,
                "A link uses a server-side image map, which cannot be operated without a mouse.");
        }
    }

    /// <summary>
    /// Checks that pages carrying annotations order them by the structure tree rather than by
    /// where they happen to sit on the page (part 1 clause 7.18.3).
    /// </summary>
    private static void CheckTabOrder(PdfUaContext context, HashSet<PdfPage> pages)
    {
        string clause = context.Clause("7.18.3", "8.9.2.2");

        foreach (PdfPage page in pages)
        {
            string? tabs = context.Resolve(page.Dictionary.Get(PdfName.Get("Tabs"))).AsName?.Value;

            if (tabs != "S")
            {
                context.Violation(
                    clause,
                    $"A page with annotations orders them by {(tabs is null ? "nothing" : tabs)} rather than by structure.");
            }
        }
    }

    /// <summary>
    /// Checks that form fields are reachable through the structure tree (part 1 clause 7.18.4).
    /// A widget that is not inside a form element is one a reader cannot describe or label.
    /// </summary>
    internal static void CheckForms(PdfUaContext context)
    {
        string clause = context.Clause("7.18.4", "8.10");

        bool hasWidgets = context.Annotations().Any(entry =>
            context.Resolve(entry.Annotation.Get(PdfNames.Subtype)).AsName?.Value == "Widget");

        if (!hasWidgets)
            return;

        bool hasFormElements = context.AllNodes().Any(node => node.Tag == "Form");

        if (!hasFormElements)
            context.Violation(clause, "The document has form fields but no Form elements in the structure tree.");

        CheckXfa(context);
    }

    /// <summary>
    /// Checks that the form is not a dynamic one (part 1 clause 7.15). A dynamic form is laid out
    /// by the reader rather than by the file, so nothing in the file describes what it will say.
    /// </summary>
    private static void CheckXfa(PdfUaContext context)
    {
        if (context.Resolve(context.Catalog.Get(PdfName.Get("AcroForm"))).AsDictionary is not { } form)
            return;

        PdfValue xfa = context.Resolve(form.Get(PdfName.Get("XFA")));
        if (xfa.IsNull)
            return;

        if (context.Part == 2)
        {
            context.Violation("8.10", "The document is an XFA form, which PDF 2.0 does not carry.");
            return;
        }

        if (IsDynamic(context, xfa))
            context.Violation("7.15", "The document is a dynamic XFA form, which the standard forbids.");
    }

    private static bool IsDynamic(PdfUaContext context, PdfValue xfa)
    {
        var packets = new List<Cos.PdfStream>();

        if (xfa.AsStream is { } single)
            packets.Add(single);

        if (xfa.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (context.Resolve(array[i]).AsStream is { } packet)
                    packets.Add(packet);
            }
        }

        foreach (Cos.PdfStream packet in packets)
        {
            string text;
            try
            {
                text = System.Text.Encoding.UTF8.GetString(packet.Decode().Span);
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
                continue;
            }

            // The form is dynamic when the configuration asks the reader to lay it out.
            int marker = text.IndexOf("dynamicRender", StringComparison.Ordinal);
            if (marker >= 0 && text.AsSpan(marker).Contains("required", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks embedded files (part 1 clause 7.11). Both names have to be there: the older one for
    /// readers that predate Unicode file names, and the newer one for everything since.
    /// </summary>
    internal static void CheckEmbeddedFiles(PdfUaContext context)
    {
        string clause = context.Clause("7.11", "8.14");

        foreach (PdfDictionary specification in FileSpecifications(context))
        {
            if (specification.Get(PdfName.Get("F")).IsNull)
                context.Violation(clause, "An embedded file specification has no file name.");

            if (specification.Get(PdfName.Get("UF")).IsNull)
                context.Violation(clause, "An embedded file specification has no Unicode file name.");

            if (specification.Get(PdfName.Get("Desc")).IsNull)
                context.Advice(clause, "An embedded file specification carries no description.");
        }
    }

    private static IEnumerable<PdfDictionary> FileSpecifications(PdfUaContext context)
    {
        foreach ((PdfPage _, PdfDictionary annotation) in context.Annotations())
        {
            if (context.Resolve(annotation.Get(PdfNames.Subtype)).AsName?.Value != "FileAttachment")
                continue;

            if (context.Resolve(annotation.Get(PdfName.Get("FS"))).AsDictionary is { } specification)
                yield return specification;
        }

        // The catalogue's embedded file name tree holds the attachments that are not annotations.
        if (context.Resolve(context.Catalog.Get(PdfName.Get("Names"))).AsDictionary is not { } names)
            yield break;

        if (context.Resolve(names.Get(PdfName.Get("EmbeddedFiles"))).AsDictionary is not { } tree)
            yield break;

        foreach (PdfDictionary specification in WalkNameTree(context, tree, 0))
            yield return specification;
    }

    private static IEnumerable<PdfDictionary> WalkNameTree(PdfUaContext context, PdfDictionary node, int depth)
    {
        if (depth > 32)
            yield break;

        if (context.Resolve(node.Get(PdfName.Get("Names"))).AsArray is { } entries)
        {
            // The array alternates name and value, so only the odd positions are specifications.
            for (int i = 1; i < entries.Count; i += 2)
            {
                if (context.Resolve(entries[i]).AsDictionary is { } specification)
                    yield return specification;
            }
        }

        if (context.Resolve(node.Get(PdfName.Get("Kids"))).AsArray is not { } kids)
            yield break;

        for (int i = 0; i < kids.Count; i++)
        {
            if (context.Resolve(kids[i]).AsDictionary is not { } child)
                continue;

            foreach (PdfDictionary specification in WalkNameTree(context, child, depth + 1))
                yield return specification;
        }
    }

    /// <summary>
    /// Checks the objects a page draws through (part 1 clause 7.20). A reference object pulls its
    /// content out of another file, which takes the content out of reach of the structure tree.
    /// </summary>
    internal static void CheckXObjects(PdfUaContext context)
    {
        string clause = context.Clause("7.20", "8.2.2");

        foreach (PdfPage page in context.Document.Pages)
        {
            if (context.Resolve(page.Dictionary.Get(PdfNames.Resources)).AsDictionary is not { } resources)
                continue;

            if (context.Resolve(resources.Get(PdfName.Get("XObject"))).AsDictionary is not { } objects)
                continue;

            foreach (PdfName key in objects.Keys.ToArray())
            {
                if (context.Resolve(objects.Get(key)).AsStream is not { } stream)
                    continue;

                if (!stream.Dictionary.Get(PdfName.Get("Ref")).IsNull)
                    context.Violation(clause, "A page draws through a reference object, which the standard forbids.");
            }
        }
    }

    /// <summary>
    /// Checks navigation (part 1 clause 7.17, part 2 clause 8.12). An outline is advice rather than
    /// requirement, but a long document without one is hard to move around in.
    /// </summary>
    internal static void CheckNavigation(PdfUaContext context)
    {
        string clause = context.Clause("7.17", "8.12.2");

        if (context.Document.Pages.Count < 10)
            return;

        if (context.Resolve(context.Catalog.Get(PdfName.Get("Outlines"))).AsDictionary is { } outlines &&
            !outlines.Get(PdfName.Get("First")).IsNull)
        {
            return;
        }

        context.Advice(
            clause,
            $"A document of {context.Document.Pages.Count} pages has no outline to navigate by.");
    }

    private static string Describe(string subtype) => subtype.Length == 0 ? "an untyped" : subtype;
}
