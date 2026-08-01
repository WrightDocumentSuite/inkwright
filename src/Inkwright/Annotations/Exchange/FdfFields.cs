using System.Globalization;
using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>
/// The form half of FDF (ISO 32000-1 §12.7.8.3.2). An FDF field is a node of the same tree a PDF
/// form is, spelled by nesting <c>Kids</c> and named by <c>T</c>, so the fully qualified name is
/// the partial names joined with full stops — exactly as in the document itself. Reading turns that
/// tree into the same <see cref="IncomingField"/> records XFDF produces, so the two formats share
/// the code that writes values back into a form.
/// </summary>
internal static class FdfFields
{
    /// <summary>How deep a field tree may nest before it is taken to be looping.</summary>
    private const int MaxDepth = 32;

    private static readonly PdfName Kids = PdfName.Get("Kids");
    private static readonly PdfName T = PdfName.Get("T");
    private static readonly PdfName V = PdfName.Get("V");
    private static readonly PdfName RV = PdfName.Get("RV");

    /// <summary>Reads the field values an FDF dictionary carries.</summary>
    /// <param name="source">The document the FDF was parsed as.</param>
    /// <param name="fdf">The FDF dictionary, holding the <c>Fields</c> array.</param>
    internal static List<IncomingField> Read(PdfDocument source, PdfDictionary fdf)
    {
        List<IncomingField> fields = [];

        if (source.Resolve(fdf.Get(PdfName.Get("Fields"))).AsArray is { } roots)
        {
            for (int i = 0; i < roots.Count; i++)
                ReadField(source, roots.Get(i), prefix: string.Empty, fields, depth: 0);
        }

        return fields;
    }

    /// <summary>Builds the <c>Fields</c> array of an FDF from a document's form.</summary>
    /// <param name="document">The document to read the form off.</param>
    /// <param name="importer">Copies referenced field values into the FDF object graph.</param>
    /// <returns>The array, or <see langword="null"/> when the document has no form.</returns>
    internal static PdfArray? Write(PdfDocument document, PdfObjectImporter importer)
    {
        if (document.Catalog.GetDictionary(PdfNames.AcroForm)?.GetArray(PdfNames.Fields) is not { Count: > 0 } roots)
            return null;

        var array = new PdfArray();

        for (int i = 0; i < roots.Count; i++)
        {
            if (roots.GetDictionary(i) is { } field &&
                WriteField(document, importer, field, type: null, inherited: PdfValue.Null, depth: 0) is { } node)
            {
                array.Add(PdfValue.Dictionary(node));
            }
        }

        return array.Count > 0 ? array : null;
    }

    private static void ReadField(
        PdfDocument source, PdfValue reference, string prefix, List<IncomingField> fields, int depth)
    {
        if (depth > MaxDepth || source.Resolve(reference).AsDictionary is not { } field)
            return;

        if (field.GetTextString(T) is not { Length: > 0 } partial)
            return;

        string name = prefix.Length == 0 ? partial : $"{prefix}.{partial}";

        List<PdfValue> children = ChildFields(source, field);

        if (children.Count > 0)
        {
            foreach (PdfValue child in children)
                ReadField(source, child, name, fields, depth + 1);

            return;
        }

        List<string> values = [.. ValueStrings(source, field.Get(V))];
        string? rich = field.GetTextString(RV);

        if (values.Count > 0 || rich is not null)
            fields.Add(new IncomingField(name, values, rich));
    }

    private static PdfDictionary? WriteField(
        PdfDocument document, PdfObjectImporter importer, PdfDictionary field, string? type, PdfValue inherited, int depth)
    {
        if (depth > MaxDepth || field.GetTextString(PdfNames.T) is not { } partial)
            return null;

        var node = new PdfDictionary(4);
        node.Set(T, PdfValue.TextString(partial));

        string? effective = field.GetName(PdfNames.FT)?.Value ?? type;
        PdfValue value = field[PdfNames.V].IsNull ? inherited : field.Get(PdfNames.V);

        List<PdfDictionary> children = ChildFields(field);

        if (children.Count > 0)
        {
            var kids = new PdfArray();
            foreach (PdfDictionary child in children)
            {
                if (WriteField(document, importer, child, effective, value, depth + 1) is { } childNode)
                    kids.Add(PdfValue.Dictionary(childNode));
            }

            if (kids.Count > 0)
                node.Set(Kids, PdfValue.Array(kids));
        }
        else if (effective != "Sig")
        {
            // A signature value means nothing away from the file it signed (§12.7.8.3.2), so it is
            // the one field type that exports no value.
            if (!value.IsNull)
                node.Set(V, importer.Import(value));

            if (field.TryGetRaw(RV, out PdfValue rich) && !rich.IsNull)
                node.Set(RV, importer.Import(rich));
        }

        return node;
    }

    private static IEnumerable<string> ValueStrings(PdfDocument source, PdfValue value)
    {
        value = source.Resolve(value);

        switch (value.Kind)
        {
            case PdfValueKind.String:
                yield return value.AsTextString ?? string.Empty;
                break;

            case PdfValueKind.Name:
                yield return value.AsName!.Value;
                break;

            case PdfValueKind.Integer or PdfValueKind.Real:
                yield return value.AsNumber.ToString("0.####", CultureInfo.InvariantCulture);
                break;

            case PdfValueKind.Array:
            {
                PdfArray array = value.AsArray!;
                for (int i = 0; i < array.Count; i++)
                {
                    foreach (string text in ValueStrings(source, array.Get(i)))
                        yield return text;
                }

                break;
            }

            default:
                break;
        }
    }

    private static List<PdfValue> ChildFields(PdfDocument source, PdfDictionary field)
    {
        List<PdfValue> children = [];

        if (source.Resolve(field.Get(Kids)).AsArray is not { } kids)
            return children;

        for (int i = 0; i < kids.Count; i++)
        {
            if (source.Resolve(kids[i]).AsDictionary?.ContainsKey(T) == true)
                children.Add(kids[i]);
        }

        return children;
    }

    private static List<PdfDictionary> ChildFields(PdfDictionary field)
    {
        List<PdfDictionary> children = [];

        if (field.GetArray(PdfNames.Kids) is not { } kids)
            return children;

        for (int i = 0; i < kids.Count; i++)
        {
            if (kids.GetDictionary(i) is { } kid && !kid[PdfNames.T].IsNull)
                children.Add(kid);
        }

        return children;
    }
}
