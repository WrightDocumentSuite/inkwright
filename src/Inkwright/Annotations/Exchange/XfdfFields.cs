using System.Globalization;
using System.Xml.Linq;
using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>One field as an XFDF payload carries it: a fully qualified name and what it holds.</summary>
/// <param name="Name">The fully qualified field name, parents joined by full stops.</param>
/// <param name="Values">The values; more than one only for a list box that allows several.</param>
/// <param name="RichText">The rich text spelling of the value, when the payload carries one.</param>
internal readonly record struct IncomingField(string Name, IReadOnlyList<string> Values, string? RichText);

/// <summary>
/// The form half of XFDF (ISO 19444-1 §6.3). A form field in PDF is a node of a tree whose full
/// name is spelled with full stops; in XFDF the same tree is spelled by nesting, so
/// <c>Address.Name</c> becomes a <c>field</c> inside a <c>field</c>.
/// </summary>
/// <remarks>
/// Importing may only change the value of a field that already exists (§5.6.1): XFDF cannot create
/// a field, and nothing about a field but its value can be changed this way.
/// </remarks>
internal static class XfdfFields
{
    /// <summary>How deep a field tree may nest before it is taken to be looping.</summary>
    private const int MaxDepth = 32;

    private static readonly PdfName RV = PdfName.Get("RV");

    /// <summary>Writes a document's field values.</summary>
    /// <param name="document">The document to read the form off.</param>
    /// <returns>The <c>fields</c> element, or <see langword="null"/> when there is no form.</returns>
    internal static XElement? Write(PdfDocument document)
    {
        if (document.Catalog.GetDictionary(PdfNames.AcroForm)?.GetArray(PdfNames.Fields) is not { Count: > 0 } roots)
            return null;

        var container = new XElement(Xfdf.Namespace + "fields");

        for (int i = 0; i < roots.Count; i++)
        {
            if (roots.GetDictionary(i) is { } field)
                WriteField(field, container, type: null, inherited: PdfValue.Null, depth: 0);
        }

        return container.HasElements ? container : null;
    }

    /// <summary>Reads the field values a payload carries.</summary>
    /// <param name="root">The <c>xfdf</c> element.</param>
    internal static List<IncomingField> Read(XElement root)
    {
        List<IncomingField> fields = [];

        if (root.Elements().FirstOrDefault(child => child.Name.LocalName == "fields") is { } container)
            ReadFields(container, prefix: string.Empty, fields, depth: 0);

        return fields;
    }

    /// <summary>
    /// Writes the values the payload carries into the document's own fields. A name the document
    /// does not carry is reported rather than created, because XFDF has no way to say what kind of
    /// field it would be or where it would sit.
    /// </summary>
    /// <param name="document">The document to fill in.</param>
    /// <param name="fields">The values to write.</param>
    /// <param name="warnings">Where to report names the document does not carry.</param>
    /// <returns>How many fields were given a new value.</returns>
    internal static int Apply(PdfDocument document, IReadOnlyList<IncomingField> fields, List<string> warnings)
    {
        if (fields.Count == 0)
            return 0;

        PdfValue formValue = document.Catalog[PdfNames.AcroForm];
        if (document.Resolve(formValue).AsDictionary is not { } form)
        {
            warnings.Add($"The payload carries {fields.Count} field values, and the document has no form.");
            return 0;
        }

        PdfObjectId formOwner = formValue.IsReference ? formValue.AsReference : document.CatalogId;
        Dictionary<string, IndexedField> known = Index(document, form, formOwner);
        int written = 0;

        foreach (IncomingField field in fields)
        {
            if (!known.TryGetValue(field.Name, out IndexedField target))
            {
                warnings.Add($"The payload sets the field '{field.Name}', which the document does not carry.");
                continue;
            }

            SetValue(document, target, field);
            written++;
        }

        if (written > 0)
        {
            // The appearance streams no longer match the values. Saying so is what makes every
            // viewer redraw them; a caller wanting them baked in calls the form layer afterwards.
            form.Set(PdfName.Get("NeedAppearances"), PdfValue.Boolean(true));
            document.MarkChanged(formOwner);
        }

        return written;
    }

    private static void WriteField(PdfDictionary field, XElement parent, string? type, PdfValue inherited, int depth)
    {
        if (depth > MaxDepth || field.GetTextString(PdfNames.T) is not { } partial)
            return;

        var element = new XElement(Xfdf.Namespace + "field", new XAttribute("name", Xfdf.Escape(partial)));

        string? effective = field.GetName(PdfNames.FT)?.Value ?? type;
        PdfValue value = field[PdfNames.V].IsNull ? inherited : field.Get(PdfNames.V);

        List<PdfDictionary> children = ChildFields(field);

        if (children.Count > 0)
        {
            foreach (PdfDictionary child in children)
                WriteField(child, element, effective, value, depth + 1);
        }
        else
            WriteValues(field, element, effective, value);

        parent.Add(element);
    }

    private static void WriteValues(PdfDictionary field, XElement element, string? type, PdfValue value)
    {
        // A signature field exports no value (§6.3.3.1): what it holds is a dictionary of
        // certificates and byte ranges, which means nothing away from the file it signed.
        if (type == "Sig")
            return;

        foreach (string text in ValueStrings(value))
            element.Add(new XElement(Xfdf.Namespace + "value", Xfdf.Escape(Newlines(text))));

        if (field.GetTextString(RV) is { Length: > 0 } rich)
            element.Add(new XElement(Xfdf.Namespace + "value-richtext", rich));
    }

    /// <summary>
    /// The values an entry holds. Almost always one, but a list box that allows several selections
    /// holds an array, and the content model of a field allows a value element for each.
    /// </summary>
    private static IEnumerable<string> ValueStrings(PdfValue value)
    {
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
                    foreach (string text in ValueStrings(array.Get(i)))
                        yield return text;
                }

                break;
            }

            default:
                break;
        }
    }

    /// <summary>
    /// A line break inside a multi-line field becomes a single line feed (§6.3.3.1). PDF writes
    /// them as carriage returns, which XML would otherwise normalise away on the far end.
    /// </summary>
    private static string Newlines(string text) =>
        text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : text;

    /// <summary>
    /// The kids of a field that are fields in their own right. A terminal field's kids are its
    /// widgets, which carry no name of their own and are not part of the tree XFDF describes.
    /// </summary>
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

    private static void ReadFields(XElement container, string prefix, List<IncomingField> fields, int depth)
    {
        if (depth > MaxDepth)
            return;

        foreach (XElement element in container.Elements())
        {
            if (element.Name.LocalName != "field")
                continue;

            if (element.Attribute("name")?.Value is not { Length: > 0 } partial)
                continue;

            string name = prefix.Length == 0 ? Xfdf.Unescape(partial) : $"{prefix}.{Xfdf.Unescape(partial)}";

            List<string> values = [];
            string? rich = null;

            foreach (XElement child in element.Elements())
            {
                if (child.Name.LocalName == "value")
                    values.Add(Xfdf.Unescape(child.Value));
                else if (child.Name.LocalName == "value-richtext")
                    rich = child.HasElements ? string.Concat(child.Nodes()) : child.Value;
            }

            if (values.Count > 0 || rich is not null)
                fields.Add(new IncomingField(name, values, rich));

            ReadFields(element, name, fields, depth + 1);
        }
    }

    /// <summary>The document's fields, keyed by the fully qualified name each is known by.</summary>
    private static Dictionary<string, IndexedField> Index(
        PdfDocument document, PdfDictionary form, PdfObjectId formOwner)
    {
        var known = new Dictionary<string, IndexedField>(StringComparer.Ordinal);

        if (!form.TryGetRaw(PdfNames.Fields, out PdfValue rootsValue) ||
            document.Resolve(rootsValue).AsArray is not { } roots)
            return known;

        PdfObjectId rootsOwner = rootsValue.IsReference ? rootsValue.AsReference : formOwner;
        for (int i = 0; i < roots.Count; i++)
            Collect(roots[i], rootsOwner, string.Empty, known, 0);

        return known;

        void Collect(
            PdfValue value,
            PdfObjectId owner,
            string prefix,
            Dictionary<string, IndexedField> index,
            int depth)
        {
            PdfObjectId currentOwner = value.IsReference ? value.AsReference : owner;
            if (document.Resolve(value).AsDictionary is not { } field)
                return;

            if (depth > MaxDepth || field.GetTextString(PdfNames.T) is not { } partial)
                return;

            string name = prefix.Length == 0 ? partial : $"{prefix}.{partial}";
            index.TryAdd(name, new IndexedField(field, currentOwner));

            if (!field.TryGetRaw(PdfNames.Kids, out PdfValue kidsValue) ||
                document.Resolve(kidsValue).AsArray is not { } kids)
            {
                return;
            }

            PdfObjectId kidsOwner = kidsValue.IsReference ? kidsValue.AsReference : currentOwner;
            for (int i = 0; i < kids.Count; i++)
            {
                PdfValue child = kids[i];
                if (document.Resolve(child).AsDictionary?.ContainsKey(PdfNames.T) == true)
                    Collect(child, kidsOwner, name, index, depth + 1);
            }
        }
    }

    /// <summary>
    /// Writes one value into a field. What shape the value takes depends on the kind of field: a
    /// button holds a name, a list box that allows several selections holds an array, and anything
    /// else holds a string.
    /// </summary>
    private static void SetValue(PdfDocument document, IndexedField target, IncomingField incoming)
    {
        PdfDictionary field = target.Dictionary;
        PdfName key = PdfNames.V;
        string? type = InheritedType(field);

        if (incoming.Values.Count == 0)
            field.Remove(key);
        else if (type == "Btn")
        {
            var state = PdfName.Get(incoming.Values[0]);
            field.Set(key, PdfValue.Name(state));

            // A button's widgets each show one state, and which one is showing is stored per widget.
            foreach (IndexedField widget in Widgets(document, target))
            {
                bool has = widget.Dictionary.GetDictionary(PdfNames.AP)?.GetDictionary(PdfNames.N)?.ContainsKey(state) == true;
                widget.Dictionary.Set(PdfName.Get("AS"), PdfValue.Name(has ? state : PdfName.Get("Off")));
                document.MarkChanged(widget.Owner);
            }
        }
        else if (incoming.Values.Count > 1)
        {
            var array = new PdfArray(incoming.Values.Count);
            foreach (string text in incoming.Values)
                array.Add(PdfValue.TextString(text));

            field.Set(key, PdfValue.Array(array));
        }
        else
            field.Set(key, PdfValue.TextString(incoming.Values[0]));

        if (incoming.RichText is { Length: > 0 } rich)
            field.Set(RV, PdfValue.TextString(rich));

        document.Attach(PdfValue.Dictionary(field));
        document.MarkChanged(target.Owner);
    }

    private static string? InheritedType(PdfDictionary field)
    {
        PdfDictionary? current = field;

        for (int depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            if (current.GetName(PdfNames.FT)?.Value is { } type)
                return type;

            current = current.GetDictionary(PdfNames.Parent);
        }

        return null;
    }

    /// <summary>
    /// The widget annotations of a field, which is the field itself when it carries only one and
    /// merged its widget into itself.
    /// </summary>
    private static IEnumerable<IndexedField> Widgets(PdfDocument document, IndexedField target)
    {
        PdfDictionary field = target.Dictionary;
        if (!field.TryGetRaw(PdfNames.Kids, out PdfValue kidsValue) ||
            document.Resolve(kidsValue).AsArray is not { Count: > 0 } kids)
        {
            yield return target;
            yield break;
        }

        PdfObjectId kidsOwner = kidsValue.IsReference ? kidsValue.AsReference : target.Owner;
        for (int i = 0; i < kids.Count; i++)
        {
            PdfValue value = kids[i];
            if (document.Resolve(value).AsDictionary is { } kid)
                yield return new IndexedField(kid, value.IsReference ? value.AsReference : kidsOwner);
        }
    }

    private readonly record struct IndexedField(PdfDictionary Dictionary, PdfObjectId Owner);
}
