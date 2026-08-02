using System.Text;
using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    private const int _maxStructureNameDepth = 64;

    private static readonly UTF8Encoding _strictNameEncoding = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Parts 2 and 3 clause 6.1.8, and part 4 clause 6.1.7: font names, colourant
    /// names, and structure type names have to be valid UTF-8 after #xx expansion.
    /// </summary>
    internal static void CheckNames(PdfAContext context)
    {
        if (context.Part == 1)
            return;

        string clause = context.Part == 4 ? "6.1.7" : "6.1.8";
        var reported = new HashSet<string>(StringComparer.Ordinal);

        CheckFontNames(context, clause, reported);
        CheckColourantNames(context, clause, reported);
        CheckStructureTypeNames(context, clause, reported);
    }

    private static void CheckFontNames(PdfAContext context, string clause, HashSet<string> reported)
    {
        foreach (PdfDictionary dictionary in context.FileDictionaries)
        {
            CheckSpecialName(context, dictionary.GetName(PdfNames.BaseFont), "font name", clause, reported);
            CheckSpecialName(context, dictionary.GetName(PdfNames.FontName), "font name", clause, reported);

            // Name is the legacy font-dictionary name used by Type 3 fonts. In other dictionaries
            // it has unrelated meanings, so only consider it when the dictionary declares Font.
            if (dictionary.GetName(PdfNames.Type) == PdfNames.Font)
                CheckSpecialName(context, dictionary.GetName(PdfNames.Name), "font name", clause, reported);
        }
    }

    private static void CheckColourantNames(PdfAContext context, string clause, HashSet<string> reported)
    {
        foreach (PdfValue value in context.FileValues)
        {
            if (value.AsArray is not { Count: >= 2 } colourSpace)
                continue;

            string? family = context.Resolve(colourSpace[0]).AsName?.Value;
            if (family == "Separation")
            {
                CheckSpecialName(
                    context,
                    context.Resolve(colourSpace[1]).AsName,
                    "Separation colourant name",
                    clause,
                    reported);
                continue;
            }

            if (family != "DeviceN" || context.Resolve(colourSpace[1]).AsArray is not { } names)
                continue;

            for (int i = 0; i < names.Count; i++)
            {
                CheckSpecialName(
                    context,
                    context.Resolve(names[i]).AsName,
                    "DeviceN colourant name",
                    clause,
                    reported);
            }

            // The optional DeviceN attributes dictionary can repeat its colourant names as keys
            // of Colorants. They are names in exactly the same colour-space role.
            if (colourSpace.Count >= 5 &&
                context.Resolve(colourSpace[4]).AsDictionary?.GetDictionary(PdfName.Get("Colorants")) is { } colorants)
            {
                foreach (PdfName name in colorants.Keys)
                    CheckSpecialName(context, name, "DeviceN colourant name", clause, reported);
            }
        }
    }

    private static void CheckStructureTypeNames(PdfAContext context, string clause, HashSet<string> reported)
    {
        // Most structure elements declare /Type /StructElem. Checking these also covers elements
        // reachable through the parent tree but absent from StructTreeRoot/K.
        foreach (PdfDictionary dictionary in context.FileDictionaries)
        {
            if (dictionary.GetName(PdfNames.Type) == PdfNames.StructElem)
                CheckSpecialName(context, dictionary.GetName(PdfNames.S), "structure type name", clause, reported);
        }

        if (context.Document.Catalog.GetDictionary(PdfNames.StructTreeRoot) is not { } root)
            return;

        // /Type is optional on structure elements. Walk K as well so those elements are not lost.
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        CheckStructureKids(context, root.Get(PdfNames.K), clause, reported, seen, 0);

        // Both sides of a RoleMap entry are structure type names. The key retains the original
        // decoded bytes, which matters when an invalid byte became U+FFFD in PdfName.Value.
        if (root.GetDictionary(PdfNames.RoleMap) is { } roles)
        {
            foreach (PdfName source in roles.Keys)
            {
                CheckSpecialName(context, source, "structure type name", clause, reported);
                CheckSpecialName(
                    context,
                    roles.GetName(source),
                    "structure type name",
                    clause,
                    reported);
            }
        }
    }

    private static void CheckStructureKids(
        PdfAContext context,
        PdfValue value,
        string clause,
        HashSet<string> reported,
        HashSet<PdfDictionary> seen,
        int depth)
    {
        if (depth > _maxStructureNameDepth)
            return;

        PdfValue resolved = context.Resolve(value);
        if (resolved.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
                CheckStructureKids(context, array[i], clause, reported, seen, depth + 1);

            return;
        }

        if (resolved.AsDictionary is not { } dictionary || !seen.Add(dictionary))
            return;

        string? type = dictionary.GetName(PdfNames.Type)?.Value;
        if (type is "MCR" or "OBJR")
            return;

        CheckSpecialName(context, dictionary.GetName(PdfNames.S), "structure type name", clause, reported);
        CheckStructureKids(context, dictionary.Get(PdfNames.K), clause, reported, seen, depth + 1);
    }

    private static void CheckSpecialName(
        PdfAContext context,
        PdfName? name,
        string role,
        string clause,
        HashSet<string> reported)
    {
        if (name is null)
            return;

        try
        {
            _ = _strictNameEncoding.GetCharCount(name.Utf8);
        }
        catch (DecoderFallbackException)
        {
            string bytes = Convert.ToHexString(name.Utf8);
            if (reported.Add(role + "\0" + bytes))
            {
                context.Report(
                    clause,
                    $"The {role} is not a valid UTF-8 sequence after # escape expansion (bytes {bytes}).");
            }
        }
    }
}
