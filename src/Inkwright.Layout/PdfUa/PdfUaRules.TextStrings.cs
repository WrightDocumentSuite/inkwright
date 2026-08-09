using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>
/// The rule ISO 14289-2 §8.6 states about text strings: one meant for a person to read shall not
/// use the Unicode private use area. A private code point means whatever the font that defined it
/// says it means, and a reader that has no such font — which is every assistive technology — has
/// nothing to announce.
/// </summary>
internal static partial class PdfUaRules
{
    /// <summary>
    /// Checks the text strings a document shows to a person. Not every string in a file is one:
    /// field names, JavaScript and internal identifiers are machinery, and the standard's own
    /// example of what it means is a bookmark.
    /// </summary>
    internal static void CheckTextStrings(PdfUaContext context)
    {
        foreach (Tagging.PdfStructureNode node in context.AllNodes())
        {
            Check(context, node.AlternateText, $"the alternative text of a {node.Tag} element");
            Check(context, node.ActualText, $"the actual text of a {node.Tag} element");
            Check(context, node.Expansion, $"the expansion of a {node.Tag} element");
            Check(context, node.Title, $"the title of a {node.Tag} element");
        }

        foreach ((PdfPage _, PdfDictionary annotation, PdfObjectId _) in context.Annotations())
        {
            Check(context, annotation.GetTextString(PdfNames.Contents), "the description of an annotation");
            Check(context, annotation.GetTextString(PdfName.Get("TU")), "the tooltip of a form field");
        }

        foreach (PdfDictionary item in Outline(context))
            Check(context, item.GetTextString(PdfNames.Title), "the title of an outline entry");

        foreach (PdfDictionary configuration in OptionalContentConfigurations(context))
            Check(context, configuration.GetTextString(PdfNames.Name), "the name of an optional content configuration");
    }

    private static void Check(PdfUaContext context, string? text, string what)
    {
        if (FindPrivateUse(text) is not { } offending)
            return;

        context.Violation(
            "8.6",
            $"A private use character U+{offending:X4} appears in {what}, which no reader can announce.");
    }

    /// <summary>
    /// The first private use code point in the text, if any. All three areas count: the one in the
    /// basic plane and the two supplementary planes, which arrive as surrogate pairs.
    /// </summary>
    private static int? FindPrivateUse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        for (int i = 0; i < text.Length; i++)
        {
            int code = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? char.ConvertToUtf32(text[i], text[++i])
                : text[i];

            if (code is (>= 0xE000 and <= 0xF8FF) or (>= 0xF0000 and <= 0xFFFFD) or (>= 0x100000 and <= 0x10FFFD))
                return code;
        }

        return null;
    }

    /// <summary>Every optional content configuration, the default one included (§8.11.4.3).</summary>
    private static IEnumerable<PdfDictionary> OptionalContentConfigurations(PdfUaContext context)
    {
        if (context.Resolve(context.Catalog.Get(PdfName.Get("OCProperties"))).AsDictionary is not { } properties)
            yield break;

        if (context.Resolve(properties.Get(PdfName.Get("D"))).AsDictionary is { } fallback)
            yield return fallback;

        if (context.Resolve(properties.Get(PdfName.Get("Configs"))).AsArray is not { } configurations)
            yield break;

        for (int i = 0; i < configurations.Count; i++)
        {
            if (context.Resolve(configurations[i]).AsDictionary is { } configuration)
                yield return configuration;
        }
    }
}
