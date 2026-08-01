namespace Inkwright.Cos;

/// <summary>
/// Spreads a resolver down into containers as they are attached to an object graph. Without this,
/// a dictionary built by hand and then stored in one that came from a file would silently stop
/// resolving references, and every lookup through it would answer null.
/// </summary>
internal static class PdfResolverBinder
{
    private const int MaxDepth = 64;

    internal static void Bind(PdfValue value, IPdfResolver? resolver)
    {
        if (resolver is not null)
            Bind(value, resolver, 0);
    }

    private static void Bind(PdfValue value, IPdfResolver resolver, int depth)
    {
        if (depth > MaxDepth)
            return;

        switch (value.Kind)
        {
            case PdfValueKind.Array:
            {
                PdfArray array = value.AsArray!;
                if (array.Resolver is not null)
                    return;

                array.Resolver = resolver;
                for (int i = 0; i < array.Count; i++)
                    Bind(array[i], resolver, depth + 1);

                break;
            }

            case PdfValueKind.Dictionary:
            case PdfValueKind.Stream:
            {
                PdfDictionary dictionary = value.AsDictionary!;
                if (dictionary.Resolver is not null)
                    return;

                dictionary.Resolver = resolver;
                foreach (KeyValuePair<PdfName, PdfValue> entry in dictionary)
                    Bind(entry.Value, resolver, depth + 1);

                break;
            }

            default:
                break;
        }
    }
}
