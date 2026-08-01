using Inkwright.Cos;
using Inkwright.Tagging;

namespace Inkwright.Markdown;

/// <summary>Reads standard structure attributes from direct and class-based representations.</summary>
internal static class PdfStructureAttributes
{
    public static string? InheritedName(PdfStructureNode node, string owner, string name)
    {
        for (PdfStructureNode? current = node; current is not null; current = current.Parent)
        {
            if (Find(current, owner, name).AsName is { } value)
                return value.Value;
        }

        return null;
    }

    public static int Integer(PdfStructureNode node, string owner, string name, int fallback = 1)
    {
        PdfValue value = Find(node, owner, name);
        return value.IsNumber && value.AsInteger is >= 1 and <= int.MaxValue
            ? (int)value.AsInteger
            : fallback;
    }

    public static string? Name(PdfStructureNode node, string owner, string name) =>
        Find(node, owner, name).AsName?.Value;

    public static string? Text(PdfStructureNode node, string owner, string name) =>
        Find(node, owner, name).AsTextString;

    public static bool Boolean(PdfStructureNode node, string owner, string name) =>
        Find(node, owner, name).AsBoolean;

    public static bool Has(PdfStructureNode node, string owner, string name) =>
        !Find(node, owner, name).IsNull;

    private static PdfValue Find(PdfStructureNode node, string owner, string name)
    {
        PdfValue direct = FindInAttributes(node.Dictionary.Get(PdfName.Get("A")), owner, name);
        if (!direct.IsNull)
            return direct;

        PdfDictionary? classMap = node.TreeRoot.GetDictionary(PdfName.Get("ClassMap"));
        if (classMap is null)
            return PdfValue.Null;

        PdfValue classes = node.Dictionary.Get(PdfName.Get("C"));
        if (classes.AsName is { } singleClass)
            return FindInAttributes(classMap.Get(singleClass), owner, name);

        if (classes.AsArray is not { } classArray)
            return PdfValue.Null;

        // Later classes take precedence. Revision numbers may be interspersed in the array.
        for (int i = classArray.Count - 1; i >= 0; i--)
        {
            if (classArray.Get(i).AsName is { } className &&
                FindInAttributes(classMap.Get(className), owner, name) is { IsNull: false } value)
            {
                return value;
            }
        }

        return PdfValue.Null;
    }

    private static PdfValue FindInAttributes(PdfValue attributes, string owner, string name)
    {
        if (attributes.AsDictionary is { } single)
            return Value(single, owner, name);

        if (attributes.AsArray is not { } array)
            return PdfValue.Null;

        // Later attribute objects and later revisions override earlier ones.
        for (int i = array.Count - 1; i >= 0; i--)
        {
            if (array.Get(i).AsDictionary is { } dictionary &&
                Value(dictionary, owner, name) is { IsNull: false } value)
            {
                return value;
            }
        }

        return PdfValue.Null;
    }

    private static PdfValue Value(PdfDictionary dictionary, string owner, string name) =>
        dictionary.GetName(PdfName.Get("O"))?.Value == owner
            ? dictionary.Get(PdfName.Get(name))
            : PdfValue.Null;
}
