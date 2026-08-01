using Inkwright.Content;
using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>One content stream and the resources its operators name.</summary>
/// <param name="Description">Where the stream came from, for a message a person can act on.</param>
/// <param name="Operations">The parsed operators.</param>
/// <param name="Resources">The resource dictionary in force, empty when the stream declares none.</param>
internal readonly record struct PdfAContentStream(
    string Description, ContentOperations Operations, PdfDictionary Resources);

/// <summary>
/// Finds every stream of operators a reader would execute. Several clauses — inline images, nesting
/// depth, colour operators, text without Unicode — are about what the page draws rather than about
/// what the objects say, and none of them can be judged without running down this list.
/// </summary>
internal static class PdfAContent
{
    internal static IEnumerable<PdfAContentStream> Enumerate(PdfAContext context)
    {
        for (int i = 0; i < context.Document.Pages.Count; i++)
        {
            PdfPage page = context.Document.Pages[i];
            byte[] content;

            try
            {
                content = page.GetContent();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                continue;
            }

            if (content.Length > 0 && TryParse(content, context, $"page {i + 1}", page.Resources) is { } parsed)
                yield return parsed;
        }

        foreach (PdfAContentStream stream in Forms(context))
            yield return stream;

        foreach (PdfAContentStream stream in TypeThreeGlyphs(context))
            yield return stream;
    }

    /// <summary>
    /// The glyph procedures of Type 3 fonts. They carry no subtype of their own, so the only way
    /// to them is through the font's CharProcs dictionary.
    /// </summary>
    private static IEnumerable<PdfAContentStream> TypeThreeGlyphs(PdfAContext context)
    {
        foreach (PdfDictionary font in context.Dictionaries)
        {
            if (font.GetName(PdfNames.Subtype)?.Value != "Type3" ||
                font.GetDictionary(PdfName.Get("CharProcs")) is not { } procedures)
            {
                continue;
            }

            PdfDictionary resources = font.GetDictionary(PdfNames.Resources) ?? new PdfDictionary(0);

            foreach ((PdfName name, PdfValue value) in procedures)
            {
                if (context.Resolve(value).AsStream is not { } glyph)
                    continue;

                byte[] content;
                try
                {
                    content = glyph.DecodedSpan().ToArray();
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    continue;
                }

                if (content.Length > 0 &&
                    TryParse(content, context, $"the Type 3 glyph {name.Value}", resources) is { } parsed)
                {
                    yield return parsed;
                }
            }
        }
    }

    /// <summary>
    /// The form XObjects, patterns, Type 3 glyphs and annotation appearances: everything drawn
    /// through a stream that is not the page's own content.
    /// </summary>
    private static IEnumerable<PdfAContentStream> Forms(PdfAContext context)
    {
        foreach (PdfStream stream in context.Streams)
        {
            PdfDictionary dictionary = stream.Dictionary;
            string? subtype = dictionary.GetName(PdfNames.Subtype)?.Value;
            bool pattern = dictionary.GetName(PdfNames.Type)?.Value == "Pattern" ||
                           dictionary.ContainsKey(PdfName.Get("PatternType"));

            if (subtype != "Form" && !pattern)
                continue;

            byte[] content;
            try
            {
                content = dictionary.ContainsKey(PdfName.Get("BBox")) || pattern
                    ? stream.DecodedSpan().ToArray()
                    : [];
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                continue;
            }

            if (content.Length == 0)
                continue;

            PdfDictionary resources = dictionary.GetDictionary(PdfNames.Resources) ?? new PdfDictionary(0);
            if (TryParse(content, context, pattern ? "a pattern" : "a form XObject", resources) is { } parsed)
                yield return parsed;
        }
    }

    private static PdfAContentStream? TryParse(
        byte[] content, PdfAContext context, string description, PdfDictionary resources)
    {
        try
        {
            return new PdfAContentStream(description, ContentOperations.Parse(content, context.Document), resources);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
