using Inkwright.Annotations;
using Inkwright.Cos;
using Inkwright.Tagging;

namespace Inkwright.Layout;

/// <summary>
/// The rules ISO 14289 states about the parts of a document a person interacts with: annotations,
/// links, forms, attachments, navigation and the objects a page draws through.
/// </summary>
internal static partial class PdfUaRules
{
    /// <summary>
    /// Checks annotations (part 1 clause 7.18, part 2 clause 8.9). The two editions differ in what
    /// counts as an artifact: in particular, <c>Hidden</c> is not enough in PDF/UA-2, while
    /// <c>Invisible</c> and an unconditional <c>NoView</c> are.
    /// </summary>
    internal static void CheckAnnotations(PdfUaContext context)
    {
        string clause = context.Clause("7.18.1", "8.9.2");
        var pagesWithAnnotations = new HashSet<PdfPage>();
        Dictionary<PdfObjectId, List<StructureObjectOwner>> structureOwners = StructureOwnersByObject(context);
        var liveAnnotations = new HashSet<PdfObjectId>();
        var parentKeys = new Dictionary<int, PdfObjectId>();

        foreach ((PdfPage page, PdfDictionary annotation, PdfObjectId objectId) in context.Annotations())
        {
            string subtype = context.Resolve(annotation.Get(PdfNames.Subtype)).AsName?.Value ?? string.Empty;
            if (objectId.IsValid)
                liveAnnotations.Add(objectId);

            if (context.Part == 2)
                pagesWithAnnotations.Add(page);

            if (context.Part == 1 && IsUaOneArtifact(page, annotation, subtype))
                continue;

            if (subtype == "Popup")
            {
                if (context.Part == 2 && objectId.IsValid && structureOwners.ContainsKey(objectId))
                {
                    context.Violation(
                        "8.9.2.4.9",
                        "A Popup annotation is present in the structure tree even though it has no independent semantics.");
                }

                continue;
            }

            bool requiredArtifact = context.Part == 2 && IsRequiredUaTwoArtifact(annotation, subtype);
            if (requiredArtifact)
            {
                CheckArtifactStructure(
                    context, page, annotation, objectId, subtype, structureOwners, parentKeys, clause);
                CheckForbiddenAnnotationType(context, subtype);
                continue;
            }

            pagesWithAnnotations.Add(page);

            CheckAnnotationStructure(
                context, page, annotation, objectId, subtype, structureOwners, parentKeys, clause);
            CheckForbiddenAnnotationType(context, subtype);

            if (subtype == "Link")
                CheckLink(context, annotation);

            CheckAnnotationDescription(context, annotation, subtype, clause);
            CheckUaTwoAnnotationDetails(context, annotation, subtype);
        }

        CheckDanglingAnnotationReferences(context, structureOwners, liveAnnotations, clause);
        CheckTabOrder(context, pagesWithAnnotations);
    }

    private static bool IsUaOneArtifact(PdfPage page, PdfDictionary annotation, string subtype) =>
        subtype is "Popup" or "PrinterMark" ||
        ((PdfAnnotationFlags)annotation.GetInteger(PdfName.Get("F"))).HasFlag(PdfAnnotationFlags.Hidden) ||
        IsOutsideCropBox(page, annotation);

    private static bool IsRequiredUaTwoArtifact(PdfDictionary annotation, string subtype)
    {
        var flags = (PdfAnnotationFlags)annotation.GetInteger(PdfName.Get("F"));
        if (flags.HasFlag(PdfAnnotationFlags.Invisible) ||
            flags.HasFlag(PdfAnnotationFlags.NoView) && !flags.HasFlag(PdfAnnotationFlags.ToggleNoView))
        {
            return true;
        }

        if (subtype == "PrinterMark")
            return true;

        return subtype == "Widget" && IsZeroArea(annotation);
    }

    private static bool IsZeroArea(PdfDictionary annotation)
    {
        PdfArray? rectangleArray = annotation.GetArray(PdfName.Get("Rect"));
        if (rectangleArray is null || rectangleArray.Count < 4)
            return false;

        PdfRectangle rectangle = PdfRectangle.FromArray(rectangleArray);
        return rectangle.Width == 0 && rectangle.Height == 0;
    }

    private static bool IsOutsideCropBox(PdfPage page, PdfDictionary annotation)
    {
        PdfArray? rectangleArray = annotation.GetArray(PdfName.Get("Rect"));
        if (rectangleArray is null || rectangleArray.Count < 4)
            return false;

        PdfRectangle rectangle = PdfRectangle.FromArray(rectangleArray);
        return !rectangle.IntersectsWith(page.CropBox);
    }

    /// <summary>
    /// ISO 32000 represents an annotation as a whole object: a structure element owns an OBJR
    /// whose Obj entry names the indirect annotation, while StructParent and ParentTree provide
    /// the reverse lookup. Checking both directions prevents a dangling OBJR from looking valid.
    /// </summary>
    private static void CheckAnnotationStructure(
        PdfUaContext context,
        PdfPage page,
        PdfDictionary annotation,
        PdfObjectId objectId,
        string subtype,
        Dictionary<PdfObjectId, List<StructureObjectOwner>> structureOwners,
        Dictionary<int, PdfObjectId> parentKeys,
        string clause,
        bool artifactOwner = false)
    {
        if (!objectId.IsValid || !structureOwners.TryGetValue(objectId, out List<StructureObjectOwner>? owners))
        {
            context.Violation(
                clause,
                $"A {Describe(subtype)} annotation is not represented in the structure tree by an OBJR targeting its object.");
            return;
        }

        if (owners.Count != 1)
        {
            context.Violation(
                clause,
                $"A {Describe(subtype)} annotation occurs {owners.Count} times in the structure tree; an annotation object shall have exactly one OBJR owner.");
        }

        StructureObjectOwner owner = owners[0];
        if (owner.Reference.Page.IsValid && owner.Reference.Page != page.Id)
        {
            context.Violation(
                clause,
                $"A {Describe(subtype)} annotation's OBJR names page {owner.Reference.Page} instead of the page that contains the annotation.");
        }

        bool correctOwner = artifactOwner
            ? owner.Node.ResolvedTag == "Artifact"
            : HasSemanticOwner(subtype, owner.Node.ResolvedTag);
        if (!correctOwner)
        {
            context.Violation(
                clause,
                $"A {Describe(subtype)} annotation is enclosed by {owner.Node.ResolvedTag} rather than {(artifactOwner ? "an Artifact structure element" : ExpectedOwner(subtype))}.");
        }

        PdfValue parentKeyValue = context.Resolve(annotation.Get(PdfNames.StructParent));
        if (parentKeyValue.Kind != PdfValueKind.Integer ||
            parentKeyValue.AsInteger is < 0 or > int.MaxValue)
        {
            context.Violation(
                clause,
                $"A {Describe(subtype)} annotation is referenced by an OBJR but has no valid StructParent key.");
            return;
        }

        int parentKey = (int)parentKeyValue.AsInteger;
        if (!ParentTreePointsTo(context, parentKey, owner.Node.Id))
        {
            context.Violation(
                clause,
                $"A {Describe(subtype)} annotation's StructParent entry does not lead back to its structure element through ParentTree.");
        }

        if (parentKeys.TryGetValue(parentKey, out PdfObjectId other) && other != objectId)
        {
            context.Violation(
                clause,
                $"StructParent key {parentKey} is shared by more than one annotation object.");
        }
        else
        {
            parentKeys[parentKey] = objectId;
        }
    }

    private static void CheckArtifactStructure(
        PdfUaContext context,
        PdfPage page,
        PdfDictionary annotation,
        PdfObjectId objectId,
        string subtype,
        Dictionary<PdfObjectId, List<StructureObjectOwner>> structureOwners,
        Dictionary<int, PdfObjectId> parentKeys,
        string clause)
    {
        if (!objectId.IsValid || !structureOwners.TryGetValue(objectId, out List<StructureObjectOwner>? owners))
            return;

        if (owners.Any(owner => owner.Node.ResolvedTag != "Artifact"))
        {
            context.Violation(
                "8.9.2.2",
                $"A {Describe(subtype)} annotation whose flags make it an artifact is enclosed by a real-content structure element.");
            return;
        }

        CheckAnnotationStructure(
            context, page, annotation, objectId, subtype, structureOwners, parentKeys, clause, artifactOwner: true);
    }

    private static bool HasSemanticOwner(string subtype, string tag) => subtype switch
    {
        "Link" => tag is "Link" or "Reference",
        "Widget" => tag == "Form",
        _ when IsMarkupAnnotation(subtype) => tag == "Annot",
        _ => true,
    };

    private static string ExpectedOwner(string subtype) => subtype switch
    {
        "Link" => "a Link or Reference structure element",
        "Widget" => "a Form structure element",
        _ => "an Annot structure element",
    };

    private static bool IsMarkupAnnotation(string subtype) => subtype is
        "Text" or "FreeText" or "Line" or "Square" or "Circle" or "Polygon" or "PolyLine" or
        "Highlight" or "Underline" or "Squiggly" or "StrikeOut" or "Caret" or "Stamp" or "Ink" or
        "FileAttachment" or "Redact" or "Watermark" or "Projection";

    private sealed record StructureObjectOwner(PdfStructureNode Node, PdfObjectReference Reference);

    private static Dictionary<PdfObjectId, List<StructureObjectOwner>> StructureOwnersByObject(PdfUaContext context)
    {
        var result = new Dictionary<PdfObjectId, List<StructureObjectOwner>>();

        foreach (PdfStructureNode node in context.AllNodes())
        {
            foreach (PdfObjectReference reference in node.Objects)
            {
                if (!reference.Object.IsValid)
                    continue;

                if (!result.TryGetValue(reference.Object, out List<StructureObjectOwner>? parents))
                {
                    parents = [];
                    result.Add(reference.Object, parents);
                }

                parents.Add(new StructureObjectOwner(node, reference));
            }
        }

        return result;
    }

    private static void CheckDanglingAnnotationReferences(
        PdfUaContext context,
        Dictionary<PdfObjectId, List<StructureObjectOwner>> structureOwners,
        HashSet<PdfObjectId> liveAnnotations,
        string clause)
    {
        foreach (PdfObjectId objectId in structureOwners.Keys)
        {
            if (liveAnnotations.Contains(objectId) ||
                context.Resolve(PdfValue.Reference(objectId)).AsDictionary is not { } dictionary)
            {
                continue;
            }

            string? type = context.Resolve(dictionary.Get(PdfNames.Type)).AsName?.Value;
            string? subtype = context.Resolve(dictionary.Get(PdfNames.Subtype)).AsName?.Value;
            // OBJR is also the standard representation for complete non-annotation objects such
            // as image and form XObjects. An explicit non-Annot Type wins over a coincidental
            // Subtype name; otherwise every structured /XObject /Subtype /Image would be reported
            // as a deleted annotation merely because it is absent from page Annots arrays.
            if (type is not null && type != "Annot")
                continue;

            if (type is null && string.IsNullOrEmpty(subtype))
                continue;

            context.Violation(
                clause,
                $"The structure tree contains an OBJR for a {Describe(subtype ?? string.Empty)} annotation that is absent from every page Annots array.");
        }
    }

    private static bool ParentTreePointsTo(PdfUaContext context, int key, PdfObjectId parent)
    {
        if (!parent.IsValid ||
            context.Resolve(context.Catalog.Get(PdfNames.StructTreeRoot)).AsDictionary is not { } root ||
            context.Resolve(root.Get(PdfNames.ParentTree)).AsDictionary is not { } parentTree)
        {
            return false;
        }

        return ParentTreePointsTo(context, parentTree, key, parent, 0);
    }

    private static bool ParentTreePointsTo(
        PdfUaContext context,
        PdfDictionary node,
        int key,
        PdfObjectId parent,
        int depth)
    {
        if (depth > 32)
            return false;

        if (context.Resolve(node.Get(PdfNames.Nums)).AsArray is { } numbers)
        {
            for (int i = 0; i + 1 < numbers.Count; i += 2)
            {
                PdfValue candidate = context.Resolve(numbers[i]);
                if (candidate.Kind == PdfValueKind.Integer && candidate.AsInteger == key)
                    return numbers[i + 1].IsReference && numbers[i + 1].AsReference == parent;
            }
        }

        if (context.Resolve(node.Get(PdfNames.Kids)).AsArray is not { } kids)
            return false;

        for (int i = 0; i < kids.Count; i++)
        {
            if (context.Resolve(kids[i]).AsDictionary is { } child &&
                ParentTreePointsTo(context, child, key, parent, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckForbiddenAnnotationType(PdfUaContext context, string subtype)
    {
        if (subtype == "TrapNet")
        {
            context.Violation(
                context.Clause("7.18.2", "8.9.2.4.15"),
                "The document carries a trapping network annotation, which the standard forbids.");
        }

        if (context.Part == 2 && subtype is "Sound" or "Movie")
        {
            context.Violation(
                "8.9.2.4.11",
                $"The document carries a deprecated {subtype} annotation, which PDF/UA-2 forbids.");
        }
    }

    private static void CheckAnnotationDescription(
        PdfUaContext context,
        PdfDictionary annotation,
        string subtype,
        string clause)
    {
        string? contents = annotation.GetTextString(PdfNames.Contents);
        if (context.Part == 1)
        {
            if (subtype is not ("Link" or "Widget") && string.IsNullOrWhiteSpace(contents))
                context.Violation(clause, $"A {Describe(subtype)} annotation carries no description.");

            return;
        }

        bool requiresContents = subtype is "Ink" or "Screen" or "3D" or "RichMedia";
        if (requiresContents && string.IsNullOrWhiteSpace(contents))
        {
            context.Violation(
                subtype == "Ink" ? "8.9.2.4.8" : subtype == "Screen" ? "8.9.2.4.12" : "8.9.2.4.19",
                $"A {Describe(subtype)} annotation carries no required Contents description.");
        }

        if (subtype == "FreeText" && string.IsNullOrWhiteSpace(contents) &&
            string.IsNullOrWhiteSpace(annotation.GetTextString(PdfName.Get("RC"))))
        {
            context.Violation(
                "8.9.2.3",
                "A FreeText annotation has neither Contents nor RC text to provide its displayed content.");
        }
    }

    private static void CheckUaTwoAnnotationDetails(
        PdfUaContext context,
        PdfDictionary annotation,
        string subtype)
    {
        if (context.Part != 2 || subtype != "FileAttachment")
            return;

        PdfValue fileSpecificationValue = annotation.Get(PdfName.Get("FS"));
        if (context.Resolve(fileSpecificationValue).AsDictionary is not { } fileSpecification)
            return; // The string form is equivalent to AFRelationship /Unspecified.

        if (context.Resolve(fileSpecification.Get(PdfName.Get("AFRelationship"))).AsName is null)
        {
            context.Violation(
                "8.9.2.4.10",
                "A file attachment annotation's file specification has no AFRelationship entry.");
        }
    }

    private static void CheckLink(PdfUaContext context, PdfDictionary annotation)
    {
        string clause = context.Clause("7.18.5", "8.9.2.4");

        if (string.IsNullOrWhiteSpace(annotation.GetTextString(PdfNames.Contents)))
        {
            if (context.Part == 1)
                context.Violation(clause, "A link annotation carries no description of where it leads.");
            else
                context.Advice(clause, "A link annotation would be clearer out of context with a Contents description.");
        }

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
        string clause = context.Clause("7.18.3", "8.9.3.3");

        foreach (PdfPage page in pages)
        {
            string? tabs = context.Resolve(page.Dictionary.Get(PdfName.Get("Tabs"))).AsName?.Value;

            bool accepted = context.Part == 1 ? tabs == "S" : tabs is "A" or "W" or "S";
            if (!accepted)
            {
                context.Violation(
                    clause,
                    context.Part == 1
                        ? $"A page with annotations orders them by {(tabs is null ? "nothing" : tabs)} rather than by structure."
                        : $"A page with annotations has {(tabs is null ? "no Tabs entry" : $"unsupported Tabs /{tabs}")} instead of /A, /W or /S.");
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

        Dictionary<PdfObjectId, List<StructureObjectOwner>> owners = StructureOwnersByObject(context);
        foreach ((PdfPage _, PdfDictionary annotation, PdfObjectId objectId) in context.Annotations())
        {
            if (context.Resolve(annotation.Get(PdfNames.Subtype)).AsName?.Value != "Widget")
                continue;

            if (!objectId.IsValid || !owners.TryGetValue(objectId, out List<StructureObjectOwner>? widgetOwners) ||
                widgetOwners.Count != 1 || widgetOwners[0].Node.ResolvedTag != "Form")
            {
                context.Violation(
                    clause,
                    "A Widget annotation is not owned by exactly one Form element in the structure tree.");
            }
        }

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

        var seen = new HashSet<PdfDictionary>();
        foreach ((PdfDictionary specification, bool inNameTree) in FileSpecifications(context))
        {
            if (!seen.Add(specification))
                continue;

            if (specification.Get(PdfName.Get("F")).IsNull)
                context.Violation(clause, "An embedded file specification has no file name.");

            if (specification.Get(PdfName.Get("UF")).IsNull)
                context.Violation(clause, "An embedded file specification has no Unicode file name.");

            if (specification.Get(PdfName.Get("Desc")).IsNull)
            {
                if (context.Part == 2 && inNameTree)
                    context.Violation(clause, "An embedded file specification carries no required description.");
                else
                    context.Advice(clause, "An embedded file specification carries no description.");
            }
        }
    }

    private static IEnumerable<(PdfDictionary Specification, bool InNameTree)> FileSpecifications(PdfUaContext context)
    {
        foreach ((PdfPage _, PdfDictionary annotation, PdfObjectId _) in context.Annotations())
        {
            if (context.Resolve(annotation.Get(PdfNames.Subtype)).AsName?.Value != "FileAttachment")
                continue;

            if (context.Resolve(annotation.Get(PdfName.Get("FS"))).AsDictionary is { } specification)
                yield return (specification, false);
        }

        // The catalogue's embedded file name tree holds the attachments that are not annotations.
        if (context.Resolve(context.Catalog.Get(PdfName.Get("Names"))).AsDictionary is not { } names)
            yield break;

        if (context.Resolve(names.Get(PdfName.Get("EmbeddedFiles"))).AsDictionary is not { } tree)
            yield break;

        foreach (PdfDictionary specification in WalkNameTree(context, tree, 0))
            yield return (specification, true);
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
