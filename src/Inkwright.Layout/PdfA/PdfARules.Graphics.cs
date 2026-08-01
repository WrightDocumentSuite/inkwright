using Inkwright.Content;
using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    private static readonly string[] RenderingIntents =
        ["RelativeColorimetric", "AbsoluteColorimetric", "Perceptual", "Saturation"];

    /// <summary>Clause 6.2: output intent, colour, graphics state, images, transparency.</summary>
    internal static void CheckGraphics(PdfAContext context)
    {
        CheckOutputIntent(context);
        CheckColourSpaces(context);
        CheckExtGState(context);
        CheckContentOperators(context);
        CheckXObjects(context);
        CheckImages(context);
        CheckTransparency(context);
    }

    /// <summary>
    /// An output intent says which device the colours were meant for. Without it a DeviceRGB grey is
    /// only a promise; with it the file carries the profile that makes the promise checkable.
    /// </summary>
    private static void CheckOutputIntent(PdfAContext context)
    {
        string clause = context.Clause("6.2.2", "6.2.3");

        // An output intent is not required in itself: the standard demands one only when device
        // colours are used, and clause 6.2.4 reports that. What is required is that an intent which
        // is present be a usable one.
        if (context.Document.Catalog.Get(PdfName.Get("OutputIntents")).AsArray is not { Count: > 0 } intents)
            return;

        PdfStream? shared = null;
        bool archival = false;

        for (int i = 0; i < intents.Count; i++)
        {
            if (intents.GetDictionary(i) is not { } intent)
                continue;

            archival |= intent.GetName(PdfName.Get("S"))?.Value == "GTS_PDFA1";

            if (context.Part > 1 && intent.ContainsKey(PdfName.Get("DestOutputProfileRef")))
                context.Report(clause, "An output intent carries DestOutputProfileRef, which points outside the file.");

            if (intent.GetStream(PdfName.Get("DestOutputProfile")) is not { } profile)
                continue;

            // Several intents may coexist, but they have to agree on the one profile they name.
            if (shared is not null && !ReferenceEquals(shared, profile))
                context.Report(clause, "The output intents name different destination profiles; they must share one.");

            shared ??= profile;
            CheckProfile(context, profile, clause);
        }

        if (!archival)
            context.Report(clause, "No output intent has GTS_PDFA1 as its S value, so none of them is the PDF/A one.");

        if (shared is null)
            context.Report(clause, "The output intent has no DestOutputProfile stream.");
    }

    private static void CheckProfile(PdfAContext context, PdfStream profile, string clause)
    {
        if (IccHeader.Read(profile) is not { } header)
        {
            context.Report(clause, "The destination profile is not a readable ICC profile.");
            return;
        }

        if (context.Part == 1)
            return;

        if (header.DeviceClass is not ("prtr" or "mntr"))
            context.Report(clause, $"The destination profile is of class {header.DeviceClass}; PDF/A wants prtr or mntr.");

        if (header.ColourSpace is not ("GRAY" or "RGB" or "CMYK"))
            context.Report(clause, $"The destination profile speaks {header.ColourSpace}; PDF/A wants GRAY, RGB or CMYK.");
    }

    /// <summary>Clause 6.2.8 of part 1, 6.2.5 of parts 2 and 3: what the graphics state may carry.</summary>
    private static void CheckExtGState(PdfAContext context)
    {
        string clause = context.Clause("6.2.8", "6.2.5");

        foreach (PdfDictionary state in context.Dictionaries)
        {
            if (state.GetName(PdfNames.Type)?.Value != "ExtGState" && !LooksLikeState(state))
                continue;

            if (state.ContainsKey(PdfName.Get("TR")))
                context.Report(clause, "An ExtGState dictionary carries a TR transfer function.");

            if (context.Part > 1 && state.ContainsKey(PdfName.Get("HTP")))
                context.Report(clause, "An ExtGState dictionary carries the HTP key, withdrawn back in PDF 1.3.");

            if (state.TryGetRaw(PdfName.Get("TR2"), out PdfValue transfer) &&
                context.Resolve(transfer).AsName?.Value != "Default")
            {
                context.Report(clause, "An ExtGState dictionary carries TR2 with a value other than Default.");
            }

            CheckRenderingIntent(context, state.GetName(PdfName.Get("RI"))?.Value);
            CheckHalftone(context, state, clause);
        }
    }

    private static bool LooksLikeState(PdfDictionary dictionary) =>
        dictionary.ContainsKey(PdfName.Get("TR")) || dictionary.ContainsKey(PdfName.Get("TR2")) ||
        dictionary.ContainsKey(PdfName.Get("HTP"));

    private static void CheckHalftone(PdfAContext context, PdfDictionary state, string clause)
    {
        if (context.Part == 1 || state.GetDictionary(PdfName.Get("HT")) is not { } halftone)
            return;

        long type = halftone.GetInteger(PdfName.Get("HalftoneType"), 1);
        if (type is not (1 or 5))
            context.Report(clause, $"A halftone is of type {type}; PDF/A allows only types 1 and 5.");

        if (halftone.ContainsKey(PdfName.Get("HalftoneName")))
            context.Report(clause, "A halftone carries a HalftoneName key, which PDF/A forbids.");
    }

    private static void CheckRenderingIntent(PdfAContext context, string? intent)
    {
        if (intent is null || RenderingIntents.Contains(intent, StringComparer.Ordinal))
            return;

        context.Report(
            context.Clause("6.2.9", "6.2.6"),
            $"The rendering intent {intent} is not one of the four ISO 32000-1 defines.");
    }

    /// <summary>
    /// Clause 6.2.10 of part 1, 6.2.2 of parts 2 and 3: only operators the specification defines,
    /// even between BX and EX, where a reader is otherwise told to forgive what it does not know.
    /// </summary>
    private static void CheckContentOperators(PdfAContext context)
    {
        string clause = context.Clause("6.2.10", "6.2.2");
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (PdfAContentStream stream in PdfAContent.Enumerate(context))
        {
            for (int i = 0; i < stream.Operations.Count; i++)
            {
                PdfOperator op = stream.Operations.GetOperator(i);

                if (op == PdfOperator.SetRenderingIntent)
                {
                    ReadOnlySpan<PdfValue> operands = stream.Operations.GetOperands(i);
                    if (operands.Length > 0)
                        CheckRenderingIntent(context, operands[0].AsName?.Value);

                    continue;
                }

                if (op != PdfOperator.Unknown)
                    continue;

                string name = stream.Operations.GetUnknownName(i);
                if (name.Length > 0 && reported.Add(name))
                    context.Report(clause, $"The content of {stream.Description} uses the operator '{name}', which ISO 32000-1 does not define.");
            }
        }
    }
}
