using Inkwright.Annotations;
using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    /// <summary>Clauses 6.5, 6.6 and 6.9 of part 1; 6.3, 6.4 and 6.5 of parts 2 and 3.</summary>
    internal static void CheckInteractive(PdfAContext context)
    {
        for (int i = 0; i < context.Document.Pages.Count; i++)
        {
            PdfPage page = context.Document.Pages[i];

            if (page.Dictionary.ContainsKey(PdfName.Get("AA")))
            {
                context.ReportPages(
                    "6.5.2",
                    $"Page {i + 1} carries an additional-actions dictionary.");
            }

            foreach (PdfAnnotation annotation in page.Annotations)
                CheckAnnotation(context, annotation, i);
        }

        CheckForm(context);
        CheckActions(context);
    }

    /// <summary>Only parts 2 and 3 forbid page-level additional actions; part 1 says nothing of pages.</summary>
    private static void ReportPages(this PdfAContext context, string clause, string message)
    {
        if (context.Part > 1)
            context.Report(clause, message);
    }

    /// <summary>
    /// A comment added to an archival document is exactly the kind of thing that quietly breaks
    /// conformance: the wrong subtype, a hidden flag, or a drawing left for the reader to invent.
    /// </summary>
    private static void CheckAnnotation(PdfAContext context, PdfAnnotation annotation, int pageIndex)
    {
        string types = context.Clause("6.5.2", "6.3.1");
        string where = $"The {Describe(annotation)} on page {pageIndex + 1}";

        if (annotation.Subtype.Length == 0)
        {
            context.Report(types, $"An annotation on page {pageIndex + 1} has no subtype.");
            return;
        }

        if (Forbidden(context, annotation.Subtype))
        {
            context.Report(types, $"{where} is of a type {context.Conformance.Name} does not permit.");
            return;
        }

        if (annotation.Type != PdfAnnotationType.Popup)
            CheckAnnotationFlags(context, annotation, where);

        CheckAnnotationColour(context, annotation, where);
        CheckAnnotationAppearance(context, annotation, where);
    }

    private static bool Forbidden(PdfAContext context, string subtype)
    {
        if (subtype is "Sound" or "Screen" or "Movie")
            return true;

        if (context.Part == 4)
        {
            // Part 4 hands 3D to Annex B and attachments to Annex A, so which of them a file may
            // carry depends on the level it claims rather than on the part (6.3.1).
            if (subtype is "3D" or "RichMedia")
                return !context.Conformance.AllowsThreeDimensional;

            return subtype == "FileAttachment" && context.Conformance != PdfAConformance.A4F;
        }

        if (subtype == "3D")
            return true;

        // Part 1 predates attachments entirely, and Rich Media came long after all three parts.
        return context.Part == 1 && subtype is "FileAttachment" or "RichMedia";
    }

    private static void CheckAnnotationFlags(PdfAContext context, PdfAnnotation annotation, string where)
    {
        string clause = context.Clause("6.5.3", "6.3.2");

        if (!annotation.Dictionary.ContainsKey(PdfName.Get("F")))
        {
            context.Report(clause, $"{where} has no F entry; PDF/A requires one on every annotation but a pop-up.");
            return;
        }

        PdfAnnotationFlags flags = annotation.Flags;
        PdfAnnotationFlags banned = PdfAnnotationFlags.Hidden | PdfAnnotationFlags.Invisible | PdfAnnotationFlags.NoView;

        // ToggleNoView arrived with PDF 1.5, so only parts 2 and 3 name it.
        if (context.Part > 1)
            banned |= PdfAnnotationFlags.ToggleNoView;

        if (!flags.HasFlag(PdfAnnotationFlags.Print) || (flags & banned) != 0)
        {
            context.Report(
                clause,
                $"{where} has flags {(int)flags}; PDF/A wants Print set and Hidden, Invisible and " +
                "NoView clear.");
        }

        if (annotation.Type == PdfAnnotationType.Text &&
            (!flags.HasFlag(PdfAnnotationFlags.NoZoom) || !flags.HasFlag(PdfAnnotationFlags.NoRotate)))
        {
            context.Report(clause, $"{where} is a sticky note, so PDF/A wants its NoZoom and NoRotate flags set.");
        }
    }

    /// <summary>
    /// Part 1 alone constrains the colours an annotation names outside its appearance: they are
    /// device colours with nothing behind them unless the output intent happens to be RGB.
    /// </summary>
    private static void CheckAnnotationColour(PdfAContext context, PdfAnnotation annotation, string where)
    {
        if (context.Part != 1)
            return;

        bool coloured = annotation.Dictionary.ContainsKey(PdfName.Get("C")) ||
                        annotation.Dictionary.ContainsKey(PdfName.Get("IC"));

        if (!coloured)
            return;

        if (OutputIntentColourSpace(context) != "RGB")
            context.Report("6.5.3", $"{where} names a colour in C or IC, which part 1 allows only with an RGB output intent.");

        if (annotation.Dictionary.GetNumber(PdfName.Get("CA"), 1) != 1)
            context.Report("6.5.3", $"{where} sets CA below 1.0, which part 1 forbids.");
    }

    private static void CheckAnnotationAppearance(PdfAContext context, PdfAnnotation annotation, string where)
    {
        string clause = context.Clause("6.5.3", "6.3.3");

        if (annotation.Dictionary.GetDictionary(PdfName.Get("AP")) is not { } appearance)
        {
            PdfRectangle box = annotation.Rectangle;
            bool degenerate = box.Width == 0 && box.Height == 0;

            // Only parts 2 and 3 insist that every annotation bring a drawing. Part 1 says what an
            // appearance must look like when there is one and leaves the rest to the reader.
            if (context.Part > 1 && annotation.Type is not (PdfAnnotationType.Popup or PdfAnnotationType.Link) && !degenerate)
                context.Report(clause, $"{where} has no appearance stream, so every reader would draw it differently.");

            return;
        }

        foreach (PdfName key in appearance.Keys.ToArray())
        {
            if (key.Value != "N")
                context.Report(clause, $"{where} has a {key.Value} appearance; PDF/A allows only the normal one.");
        }

        bool button = annotation.Type == PdfAnnotationType.Widget && IsButton(annotation.Dictionary, 0);

        if (button && appearance.GetDictionary(PdfName.Get("N")) is null)
            context.Report(clause, $"{where} is a button, so its N entry has to be a dictionary of states.");

        if (!button && appearance.GetStream(PdfName.Get("N")) is null)
            context.Report(clause, $"{where} has an appearance dictionary whose N entry is not a stream.");
    }

    /// <summary>
    /// Whether a widget belongs to a button field. A field can be split across a parent that holds
    /// the type and kids that hold the appearances, so the type has to be looked for up the chain.
    /// </summary>
    private static bool IsButton(PdfDictionary widget, int depth)
    {
        if (depth > 16)
            return false;

        if (widget.GetName(PdfName.Get("FT"))?.Value is { } type)
            return type == "Btn";

        return widget.GetDictionary(PdfName.Get("Parent")) is { } parent && IsButton(parent, depth + 1);
    }

    /// <summary>Clause 6.9 of part 1, 6.4 of parts 2 and 3: interactive forms.</summary>
    private static void CheckForm(PdfAContext context)
    {
        if (context.Document.Catalog.GetDictionary(PdfName.Get("AcroForm")) is not { } form)
            return;

        string clause = context.Clause("6.9", "6.4.1");

        if (form.GetBoolean(PdfName.Get("NeedAppearances")))
        {
            context.Report(
                clause,
                "The form sets NeedAppearances, which asks the reader to draw the fields itself — " +
                "the one thing an archival file must never leave to chance.");
        }

        if (context.Part > 1 && form.ContainsKey(PdfName.Get("XFA")))
            context.Report("6.4.2", "The form carries an XFA packet, which PDF/A does not permit.");

        if (context.Part > 1 && context.Document.Catalog.ContainsKey(PdfName.Get("NeedsRendering")))
            context.Report("6.4.2", "The catalogue carries NeedsRendering, which belongs to XFA forms.");

        CheckFields(context, form.GetArray(PdfName.Get("Fields")), clause, 0);
    }

    private static void CheckFields(PdfAContext context, PdfArray? fields, string clause, int depth)
    {
        if (fields is null || depth > 32)
            return;

        for (int i = 0; i < fields.Count; i++)
        {
            if (fields.GetDictionary(i) is not { } field)
                continue;

            if (field.ContainsKey(PdfName.Get("A")) || field.ContainsKey(PdfName.Get("AA")))
                context.Report(clause, "A form field carries an action, and a field in an archival file may not act.");

            CheckFields(context, field.GetArray(PdfName.Get("Kids")), clause, depth + 1);
        }
    }

    /// <summary>Clause 6.6 of part 1, 6.5 of parts 2 and 3: what a file may ask a reader to do.</summary>
    private static void CheckActions(PdfAContext context)
    {
        string clause = context.Clause("6.6.1", "6.5.1");

        if (context.Document.Catalog.ContainsKey(PdfName.Get("AA")))
            context.Report(context.Clause("6.6.2", "6.5.2"), "The catalogue carries an additional-actions dictionary.");

        foreach (PdfDictionary dictionary in context.Dictionaries)
        {
            if (dictionary.GetName(PdfNames.Type)?.Value != "Action" && !dictionary.ContainsKey(PdfName.Get("S")))
                continue;

            if (dictionary.GetName(PdfName.Get("S"))?.Value is not { } action)
                continue;

            if (IsForbiddenAction(context, action))
                context.Report(clause, $"The file carries a {action} action, which PDF/A does not permit.");

            if (action == "Named" && dictionary.GetName(PdfName.Get("N"))?.Value is { } named &&
                named is not ("NextPage" or "PrevPage" or "FirstPage" or "LastPage"))
            {
                context.Report(clause, $"The named action {named} is not one of the four PDF/A allows.");
            }
        }
    }

    private static bool IsForbiddenAction(PdfAContext context, string action)
    {
        if (action is "Launch" or "Sound" or "Movie" or "ResetForm" or "ImportData" or
            "SetState" or "NoOp" or "Hide")
        {
            return true;
        }

        // Part 4 keeps JavaScript in the file and moves the restriction to the processor, which may
        // run such an action only when a person asks for it (6.6.2). The parts before it forbid the
        // action outright.
        if (action == "JavaScript")
            return context.Part < 4;

        // 3D belongs to PDF/A-4e alone, and the state-switching action that goes with it (6.6.1).
        if (action is "SetOCGState" or "GoTo3DView")
        {
            return context.Part == 4
                ? !context.Conformance.AllowsThreeDimensional
                : context.Part > 1;
        }

        return context.Part > 1 && action is "Rendition" or "Trans";
    }

    private static string Describe(PdfAnnotation annotation) =>
        annotation is PdfMarkupAnnotation { Author: { Length: > 0 } author }
            ? $"{annotation.Subtype} annotation by {author}"
            : $"{annotation.Subtype} annotation";
}
