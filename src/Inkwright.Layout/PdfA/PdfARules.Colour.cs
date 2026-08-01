using Inkwright.Content;
using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>Which device colour spaces a file actually paints with.</summary>
[Flags]
internal enum DeviceColours
{
    None = 0,
    Gray = 1,
    Rgb = 2,
    Cmyk = 4,
}

internal static partial class PdfARules
{
    /// <summary>
    /// Clause 6.2.3 of part 1 and 6.2.4 of parts 2 and 3: every colour has to mean something
    /// definite. A device colour is only a triple of numbers until an output intent or a default
    /// colour space says which device it was meant for.
    /// </summary>
    private static void CheckColourSpaces(PdfAContext context)
    {
        DeviceColours used = UsedDeviceColours(context);
        if (used == DeviceColours.None)
            return;

        string clause = context.Clause("6.2.3.3", "6.2.4.3");
        string? profile = OutputIntentColourSpace(context);

        if (context.Part == 1 && used.HasFlag(DeviceColours.Rgb) && used.HasFlag(DeviceColours.Cmyk))
        {
            context.Report(
                "6.2.3.3",
                "The file paints in both DeviceRGB and DeviceCMYK. Part 1 allows one or the other, " +
                "because a single output intent cannot stand behind both.");
        }

        // Either route makes a device colour definite: a destination profile of the right kind, or
        // a device-independent default colour space standing in for it.
        Check(DeviceColours.Rgb, "DeviceRGB", "DefaultRGB", "RGB");
        Check(DeviceColours.Cmyk, "DeviceCMYK", "DefaultCMYK", "CMYK");

        // Grey needs an intent of some sort, because the standard says how to convert it to each of
        // the three; part 1 asks the same through its blanket requirement for uncalibrated colour.
        if (used.HasFlag(DeviceColours.Gray) && profile is null && !HasDefault(context, "DefaultGray"))
            context.Report(clause, "The file paints in DeviceGray with neither an output intent nor a DefaultGray space.");

        void Check(DeviceColours colour, string name, string fallback, string wanted)
        {
            if (!used.HasFlag(colour) || HasDefault(context, fallback))
                return;

            if (profile == wanted)
                return;

            context.Report(
                clause,
                profile is null
                    ? $"The file paints in {name} with neither an output intent nor a {fallback} space, " +
                      "so nothing says what those numbers mean."
                    : $"The file paints in {name}, but its output intent carries a {profile} profile and " +
                      $"no {fallback} colour space is set.");
        }
    }

    private static bool HasDefault(PdfAContext context, string name)
    {
        foreach (PdfDictionary dictionary in context.Dictionaries)
        {
            if (dictionary.GetDictionary(PdfName.Get("ColorSpace"))?.ContainsKey(PdfName.Get(name)) == true)
                return true;
        }

        return false;
    }

    /// <summary>The colour space of the profile in the PDF/A output intent, read from its ICC header.</summary>
    private static string? OutputIntentColourSpace(PdfAContext context)
    {
        if (context.Document.Catalog.Get(PdfName.Get("OutputIntents")).AsArray is not { } intents)
            return null;

        for (int i = 0; i < intents.Count; i++)
        {
            if (intents.GetDictionary(i)?.GetStream(PdfName.Get("DestOutputProfile")) is not { } profile)
                continue;

            if (IccHeader.Read(profile) is { } header)
                return header.ColourSpace;
        }

        return null;
    }

    private static DeviceColours UsedDeviceColours(PdfAContext context)
    {
        DeviceColours used = DeviceColours.None;

        foreach (PdfAContentStream stream in PdfAContent.Enumerate(context))
        {
            for (int i = 0; i < stream.Operations.Count; i++)
                used |= FromOperator(stream, i);
        }

        foreach (PdfStream image in context.Streams)
        {
            if (image.Dictionary.GetName(PdfNames.Subtype)?.Value == "Image")
                used |= FromColourSpace(image.Dictionary.Get(PdfName.Get("ColorSpace")));
        }

        return used;
    }

    private static DeviceColours FromOperator(PdfAContentStream stream, int index) =>
        stream.Operations.GetOperator(index) switch
        {
            PdfOperator.SetFillGray or PdfOperator.SetStrokeGray => DeviceColours.Gray,
            PdfOperator.SetFillRgb or PdfOperator.SetStrokeRgb => DeviceColours.Rgb,
            PdfOperator.SetFillCmyk or PdfOperator.SetStrokeCmyk => DeviceColours.Cmyk,
            PdfOperator.SetFillColorSpace or PdfOperator.SetStrokeColorSpace => FromName(stream, index),
            _ => DeviceColours.None,
        };

    private static DeviceColours FromName(PdfAContentStream stream, int index)
    {
        ReadOnlySpan<PdfValue> operands = stream.Operations.GetOperands(index);
        if (operands.Length == 0 || operands[0].AsName is not { } name)
            return DeviceColours.None;

        DeviceColours direct = Named(name.Value);
        if (direct != DeviceColours.None)
            return direct;

        PdfValue resource = stream.Resources.GetDictionary(PdfName.Get("ColorSpace"))?.Get(name) ?? PdfValue.Null;
        return FromColourSpace(resource);
    }

    private static DeviceColours FromColourSpace(PdfValue value)
    {
        if (value.AsName is { } name)
            return Named(name.Value);

        if (value.AsArray is not { Count: > 0 } array)
            return DeviceColours.None;

        string? family = array.Get(0).AsName?.Value;

        // Indexed and Pattern hide their real space one level down, and the rules follow it there.
        return family switch
        {
            "Indexed" when array.Count > 1 => FromColourSpace(array.Get(1)),
            "Pattern" when array.Count > 1 => FromColourSpace(array.Get(1)),
            "Separation" when array.Count > 2 => FromColourSpace(array.Get(2)),
            "DeviceN" when array.Count > 2 => FromColourSpace(array.Get(2)),
            _ => Named(family),
        };
    }

    private static DeviceColours Named(string? name) => name switch
    {
        "DeviceGray" or "G" or "CalGray" => name == "CalGray" ? DeviceColours.None : DeviceColours.Gray,
        "DeviceRGB" or "RGB" => DeviceColours.Rgb,
        "DeviceCMYK" or "CMYK" => DeviceColours.Cmyk,
        _ => DeviceColours.None,
    };

    private static string Describe(DeviceColours colours)
    {
        List<string> names = [];
        if (colours.HasFlag(DeviceColours.Gray))
            names.Add("DeviceGray");

        if (colours.HasFlag(DeviceColours.Rgb))
            names.Add("DeviceRGB");

        if (colours.HasFlag(DeviceColours.Cmyk))
            names.Add("DeviceCMYK");

        return string.Join(" and ", names);
    }
}
