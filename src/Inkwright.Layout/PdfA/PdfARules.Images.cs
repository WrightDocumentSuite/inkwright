using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    /// <summary>Clause 6.2.4 of part 1, 6.2.8 of parts 2 and 3: images.</summary>
    private static void CheckImages(PdfAContext context)
    {
        string clause = context.Clause("6.2.4", "6.2.8.1");

        foreach (PdfStream stream in context.Streams)
        {
            PdfDictionary image = stream.Dictionary;
            if (image.GetName(PdfNames.Subtype)?.Value != "Image")
                continue;

            if (image.ContainsKey(PdfName.Get("Alternates")))
                context.Report(clause, "An image dictionary carries Alternates, which PDF/A forbids.");

            if (image.ContainsKey(PdfName.Get("OPI")))
                context.Report(clause, "An image dictionary carries OPI, which points at content outside the file.");

            if (image.GetBoolean(PdfName.Get("Interpolate")))
                context.Report(clause, "An image asks to be interpolated; PDF/A wants Interpolate false or absent.");

            CheckRenderingIntent(context, image.GetName(PdfName.Get("Intent"))?.Value);

            if (context.Part == 1 && FilterNames(image, PdfNames.Filter).Contains("JPXDecode"))
                context.Report("6.1.10", "An image uses JPEG 2000, which arrived after PDF 1.4 and part 1 cannot carry.");
        }
    }

    /// <summary>Clause 6.2.5 to 6.2.7 of part 1, 6.2.9 of parts 2 and 3: external objects.</summary>
    private static void CheckXObjects(PdfAContext context)
    {
        foreach (PdfStream stream in context.Streams)
        {
            PdfDictionary xobject = stream.Dictionary;
            string? subtype = xobject.GetName(PdfNames.Subtype)?.Value;

            if (subtype == "PS")
            {
                context.Report(
                    context.Clause("6.2.7", "6.2.9.3"),
                    "The file carries a PostScript XObject: a program the page hands to the renderer, " +
                    "which is the opposite of a predictable appearance.");
            }

            if (subtype != "Form")
                continue;

            if (xobject.ContainsKey(PdfName.Get("Ref")))
                context.Report(context.Clause("6.2.6", "6.2.9.2"), "A form XObject is a reference XObject, which draws content from another file.");

            if (xobject.ContainsKey(PdfName.Get("OPI")))
                context.Report(context.Clause("6.2.5", "6.2.9.1"), "A form XObject carries the OPI key.");

            if (xobject.ContainsKey(PdfName.Get("PS")) ||
                xobject.GetName(PdfName.Get("Subtype2"))?.Value == "PS")
            {
                context.Report(context.Clause("6.2.5", "6.2.9.1"), "A form XObject carries embedded PostScript.");
            }
        }
    }

    /// <summary>
    /// Clause 6.4 of part 1, 6.2.10 of parts 2 and 3. Part 1 forbids transparency outright; parts 2
    /// and 3 allow it but insist that something says which colour space the blending happens in.
    /// </summary>
    private static void CheckTransparency(PdfAContext context)
    {
        if (context.Part == 1)
        {
            CheckPartOneTransparency(context);
            return;
        }

        bool intent = context.Document.Catalog.Get(PdfName.Get("OutputIntents")).AsArray is { Count: > 0 };

        foreach (PdfDictionary dictionary in context.Dictionaries)
        {
            if (dictionary.GetName(PdfName.Get("BM")) is { } blend && !IsKnownBlendMode(blend.Value))
                context.Report("6.2.10", $"The blend mode {blend.Value} is not one ISO 32000-1 defines.");
        }

        if (intent)
            return;

        for (int i = 0; i < context.Document.Pages.Count; i++)
        {
            if (!PageHasTransparency(context, context.Document.Pages[i]))
                continue;

            PdfDictionary? group = context.Document.Pages[i].Dictionary.GetDictionary(PdfName.Get("Group"));
            if (group?.ContainsKey(PdfName.Get("CS")) != true)
            {
                context.Report(
                    "6.2.10",
                    $"Page {i + 1} uses transparency, and with no output intent the page group must " +
                    "name the blending colour space itself.");
            }
        }
    }

    private static void CheckPartOneTransparency(PdfAContext context)
    {
        foreach (PdfDictionary dictionary in context.Dictionaries)
        {
            bool state = dictionary.GetName(PdfNames.Type)?.Value == "ExtGState";

            if (dictionary.TryGetRaw(PdfName.Get("SMask"), out PdfValue mask) &&
                context.Resolve(mask).AsName?.Value != "None")
            {
                context.Report("6.4", "A soft mask is in play; part 1 allows SMask only with the value None.");
            }

            if (dictionary.GetName(PdfName.Get("S"))?.Value == "Transparency")
                context.Report("6.4", "A transparency group is present, which part 1 does not permit.");

            if (!state)
                continue;

            if (dictionary.GetName(PdfName.Get("BM")) is { } blend && blend.Value is not ("Normal" or "Compatible"))
                context.Report("6.4", $"The blend mode {blend.Value} implies transparency, which part 1 forbids.");

            CheckOpacity(context, dictionary, "CA");
            CheckOpacity(context, dictionary, "ca");
        }
    }

    private static void CheckOpacity(PdfAContext context, PdfDictionary state, string key)
    {
        if (!state.ContainsKey(PdfName.Get(key)))
            return;

        double alpha = state.GetNumber(PdfName.Get(key), 1);
        if (alpha != 1)
            context.Report("6.4", $"The graphics state sets {key} to {alpha:0.###}; part 1 allows only 1.0.");
    }

    /// <summary>
    /// Annex A, which is normative and precise about this: a soft mask counts only when it is a
    /// dictionary, an alpha only when it is below one, a blend mode only when it is not Normal, and
    /// an image only when its mask is a stream. A page that merely declares a transparency group is
    /// not, by that reckoning, a page with transparency on it. The hunt runs through the resources
    /// of every form XObject the page can reach, and through annotation appearances, which A.2 says
    /// to treat exactly as form XObjects.
    /// </summary>
    private static bool PageHasTransparency(PdfAContext context, PdfPage page)
    {
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        if (ResourcesHaveTransparency(context, page.Resources, visited))
            return true;

        if (page.Dictionary.GetArray(PdfName.Get("Annots")) is not { } annotations)
            return false;

        for (int i = 0; i < annotations.Count; i++)
        {
            PdfDictionary? appearance = context.Resolve(annotations.Get(i)).AsDictionary
                ?.GetDictionary(PdfName.Get("AP"));

            if (appearance is null)
                continue;

            foreach ((PdfName _, PdfValue entry) in appearance)
            {
                foreach (PdfStream form in AppearanceStreams(context, entry))
                {
                    if (XObjectHasTransparency(context, form.Dictionary, visited))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>An appearance entry is a stream, or a dictionary of states each holding one.</summary>
    private static IEnumerable<PdfStream> AppearanceStreams(PdfAContext context, PdfValue entry)
    {
        PdfValue resolved = context.Resolve(entry);
        if (resolved.AsStream is { } direct)
        {
            yield return direct;
            yield break;
        }

        if (resolved.AsDictionary is not { } states)
            yield break;

        foreach ((PdfName _, PdfValue state) in states)
        {
            if (context.Resolve(state).AsStream is { } stream)
                yield return stream;
        }
    }

    private static bool ResourcesHaveTransparency(
        PdfAContext context, PdfDictionary resources, HashSet<PdfDictionary> visited)
    {
        if (!visited.Add(resources))
            return false;

        PdfDictionary? states = resources.GetDictionary(PdfName.Get("ExtGState"));
        foreach (PdfName name in states?.Keys.ToArray() ?? [])
        {
            if (states!.GetDictionary(name) is { } state && StateHasTransparency(context, state))
                return true;
        }

        PdfDictionary? xobjects = resources.GetDictionary(PdfName.Get("XObject"));
        foreach (PdfName name in xobjects?.Keys.ToArray() ?? [])
        {
            if (xobjects!.GetStream(name)?.Dictionary is { } xobject &&
                XObjectHasTransparency(context, xobject, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StateHasTransparency(PdfAContext context, PdfDictionary state)
    {
        if (state.TryGetRaw(PdfName.Get("SMask"), out PdfValue mask) &&
            context.Resolve(mask).AsDictionary is not null)
        {
            return true;
        }

        if (state.GetNumber(PdfName.Get("CA"), 1) < 1 || state.GetNumber(PdfName.Get("ca"), 1) < 1)
            return true;

        // ISO 32000-1 defines Compatible as another name for Normal, so it is not transparency.
        return state.GetName(PdfName.Get("BM"))?.Value is { } blend && blend is not ("Normal" or "Compatible");
    }

    private static bool XObjectHasTransparency(
        PdfAContext context, PdfDictionary xobject, HashSet<PdfDictionary> visited)
    {
        if (xobject.GetName(PdfNames.Subtype)?.Value == "Form" || xobject.ContainsKey(PdfName.Get("BBox")))
        {
            if (xobject.GetDictionary(PdfName.Get("Group"))?.GetName(PdfName.Get("S"))?.Value == "Transparency")
                return true;

            // Annex A.3: the form's own content is processed the same way as a page's, so its
            // resources get the same examination.
            return xobject.GetDictionary(PdfNames.Resources) is { } resources &&
                   ResourcesHaveTransparency(context, resources, visited);
        }

        if (xobject.TryGetRaw(PdfName.Get("SMask"), out PdfValue mask) && context.Resolve(mask).AsStream is not null)
            return true;

        return xobject.GetInteger(PdfName.Get("SMaskInData"), 0) > 0;
    }

    private static bool IsKnownBlendMode(string mode) => mode is
        "Normal" or "Compatible" or "Multiply" or "Screen" or "Overlay" or "Darken" or "Lighten" or
        "ColorDodge" or "ColorBurn" or "HardLight" or "SoftLight" or "Difference" or "Exclusion" or
        "Hue" or "Saturation" or "Color" or "Luminosity";
}
