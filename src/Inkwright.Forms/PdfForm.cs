using System.Collections;
using System.Globalization;
using Inkwright.Cos;

namespace Inkwright.Forms;

/// <summary>
/// The interactive form of a document. The collection is built by walking the field tree once, so
/// fields are addressable by their fully qualified names, which is how every form-filling caller
/// actually thinks about them.
/// </summary>
public sealed class PdfForm : IReadOnlyCollection<PdfFormField>
{
    private readonly Dictionary<string, PdfFormField> _byName = new(StringComparer.Ordinal);
    private readonly List<PdfFormField> _fields = [];

    private PdfForm(PdfDocument document, PdfDictionary root, PdfObjectId rootId)
    {
        Document = document;
        Root = root;
        RootId = rootId;
        AppearanceGenerator = new AppearanceGenerator(this);
    }

    /// <summary>The document the form belongs to.</summary>
    public PdfDocument Document { get; }

    /// <summary>The <c>AcroForm</c> dictionary.</summary>
    public PdfDictionary Root { get; }

    /// <summary>The identity of the <c>AcroForm</c> object, when it is an indirect one.</summary>
    public PdfObjectId RootId { get; }

    /// <inheritdoc />
    public int Count => _fields.Count;

    /// <summary>The fields, in the order the field tree lists them.</summary>
    public IReadOnlyList<PdfFormField> Fields => _fields;

    internal AppearanceGenerator AppearanceGenerator { get; }

    /// <summary>Whether the document asks viewers to build the appearances themselves.</summary>
    public bool NeedAppearances
    {
        get => Root.GetBoolean(PdfName.Get("NeedAppearances"));
        set
        {
            Root.Set(PdfName.Get("NeedAppearances"), PdfValue.Boolean(value));
            MarkChanged();
        }
    }

    /// <summary>Opens the form of a document.</summary>
    /// <param name="document">The document to read.</param>
    /// <returns>The form, or <see langword="null"/> when the document has no interactive form.</returns>
    public static PdfForm? Open(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Catalog.GetDictionary(PdfName.Get("AcroForm")) is not { } root)
            return null;

        PdfObjectId rootId = document.Catalog.TryGetRaw(PdfName.Get("AcroForm"), out PdfValue reference) && reference.IsReference
            ? reference.AsReference
            : PdfObjectId.None;

        var form = new PdfForm(document, root, rootId);
        form.Collect();
        return form;
    }

    /// <summary>Opens the form of a document, creating an empty one when there is none.</summary>
    /// <param name="document">The document to read.</param>
    public static PdfForm OpenOrCreate(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Open(document) is { } existing)
            return existing;

        var root = new PdfDictionary(3);
        root.Set(PdfName.Get("Fields"), PdfValue.Array(new PdfArray()));
        document.Attach(PdfValue.Dictionary(root));

        PdfObjectId id = document.AddObject(PdfValue.Dictionary(root));
        document.Catalog.Set(PdfName.Get("AcroForm"), PdfValue.Reference(id));
        document.MarkChanged(document.CatalogId);

        var form = new PdfForm(document, root, id);
        form.Collect();
        return form;
    }

    /// <summary>Records that the form dictionary changed, so an incremental save writes it out.</summary>
    public void MarkChanged()
    {
        Document.MarkChanged(RootId);
        Document.MarkChanged(Document.CatalogId);
    }

    /// <summary>Finds a field by its fully qualified name.</summary>
    /// <param name="name">The name, with parents joined by full stops.</param>
    public PdfFormField? this[string name] => _byName.GetValueOrDefault(name);

    /// <summary>Sets a field's value by name.</summary>
    /// <param name="name">The fully qualified field name.</param>
    /// <param name="value">The value to write.</param>
    /// <returns>Whether a field of that name existed.</returns>
    public bool Set(string name, string? value)
    {
        if (this[name] is not { } field)
            return false;

        field.SetValue(value);
        return true;
    }

    /// <summary>Sets a check box or radio button by name.</summary>
    /// <param name="name">The fully qualified field name.</param>
    /// <param name="value">Whether the field should be on.</param>
    /// <returns>Whether a field of that name existed.</returns>
    public bool Set(string name, bool value)
    {
        if (this[name] is not { } field)
            return false;

        field.IsChecked = value;
        return true;
    }

    /// <summary>
    /// Works out every calculated field, in the order the form's <c>/CO</c> array gives. That order
    /// matters: a grand total that adds up subtotals is only right if the subtotals were worked out
    /// first, and the array is where the form says which comes first.
    /// </summary>
    /// <returns>How many fields were given a new value.</returns>
    public int Recalculate()
    {
        int changed = 0;

        foreach (PdfFormField field in CalculationOrder())
        {
            FieldCalculation calculation = field.Calculation;
            if (!calculation.IsDefined)
                continue;

            List<double> values = [];
            foreach (string name in calculation.Sources)
            {
                if (this[name] is { } source && FieldFormat.TryReadNumber(source.Value, out double number))
                    values.Add(number);
            }

            if (calculation.Compute(values) is not { } result)
                continue;

            string text = result.ToString("0.####################", CultureInfo.InvariantCulture);
            if (string.Equals(field.Value, text, StringComparison.Ordinal))
                continue;

            field.SetValue(text);
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// The fields to recalculate, in order. A form that names an order is believed; one that does
    /// not gets the fields in the order they are declared, which is what viewers fall back to.
    /// </summary>
    private IEnumerable<PdfFormField> CalculationOrder()
    {
        if (Root.GetArray(PdfName.Get("CO")) is not { Count: > 0 } order)
            return _fields;

        List<PdfFormField> ordered = [];
        var seen = new HashSet<int>();

        for (int i = 0; i < order.Count; i++)
        {
            if (!order[i].IsReference)
                continue;

            int number = order[i].AsReference.Number;
            if (!seen.Add(number))
                continue;

            if (_fields.Find(candidate => candidate.Id.Number == number) is { } field)
                ordered.Add(field);
        }

        // Anything the array forgot still gets its turn, after everything the form did name.
        foreach (PdfFormField field in _fields)
        {
            if (!seen.Contains(field.Id.Number))
                ordered.Add(field);
        }

        return ordered;
    }

    /// <summary>
    /// Rebuilds the appearance of every field from its current value. This is what turns values
    /// written straight into the dictionaries — by importing XFDF, say — into something every
    /// viewer draws, rather than leaving them to whichever ones honour <c>NeedAppearances</c>.
    /// </summary>
    /// <returns>How many fields were redrawn.</returns>
    public int RegenerateAppearances()
    {
        int redrawn = 0;

        foreach (PdfFormField field in _fields)
        {
            if (field.Type == PdfFieldType.PushButton)
                continue;

            AppearanceGenerator.Update(field);
            redrawn++;
        }

        if (redrawn > 0)
        {
            NeedAppearances = false;
            MarkChanged();
        }

        return redrawn;
    }

    /// <summary>Reads every field value into a dictionary.</summary>
    public Dictionary<string, string?> ReadValues()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (PdfFormField field in _fields)
        {
            if (field.Type != PdfFieldType.PushButton)
                values[field.FullName] = field.Value;
        }

        return values;
    }

    /// <summary>
    /// Draws every field's appearance into the page content and removes the interactivity, which is
    /// what makes a filled form safe to archive and impossible to change back.
    /// </summary>
    public void Flatten()
    {
        foreach (PdfFormField field in _fields)
            AppearanceGenerator.Update(field);

        foreach (PdfPage page in Document.Pages)
            AppearanceGenerator.FlattenPage(page, _fields);

        Root.Set(PdfName.Get("Fields"), PdfValue.Array(new PdfArray()));
        Root.Remove(PdfName.Get("NeedAppearances"));
        Document.Catalog.Remove(PdfName.Get("AcroForm"));
        MarkChanged();

        _fields.Clear();
        _byName.Clear();
    }

    /// <inheritdoc />
    public IEnumerator<PdfFormField> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal PdfDictionary CreateWidget(PdfPage page, PdfRectangle bounds, string name, string fieldType)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // A field with a single widget is written as one dictionary that is both, which is what
        // every producer does and what keeps the object graph small. Only the last part of a
        // qualified name goes in T: the parts before it name ancestors, which Register builds.
        var field = new PdfDictionary(10);
        field.Set(PdfName.Get("Type"), PdfValue.Name("Annot"));
        field.Set(PdfName.Get("Subtype"), PdfValue.Name("Widget"));
        field.Set(PdfName.Get("FT"), PdfValue.Name(fieldType));
        field.Set(PdfName.Get("T"), PdfValue.TextString(PartialName(name)));
        field.Set(PdfName.Get("Rect"), PdfValue.Array(bounds.ToArray()));
        field.Set(PdfName.Get("F"), PdfValue.Integer(4));
        field.Set(PdfName.Get("P"), PdfValue.Reference(page.Id));
        Document.Attach(PdfValue.Dictionary(field));
        return field;
    }

    internal PdfFormField Register(PdfDictionary dictionary, string name)
    {
        PdfObjectId id = Document.AddObject(PdfValue.Dictionary(dictionary));

        // A qualified name is a path through the field tree, so the branches it names are made
        // first and the new field is hung off the last of them (§12.7.3.2).
        (PdfArray siblings, PdfObjectId parent) = Parent(name);

        if (parent.IsValid)
            dictionary.Set(PdfName.Get("Parent"), PdfValue.Reference(parent));

        siblings.Add(PdfValue.Reference(id));

        PdfPage? page = FindPage(dictionary);
        page?.Annotations.RawArray.Add(PdfValue.Reference(id));
        page?.MarkChanged();

        var field = new PdfFormField(this, id, dictionary, name);
        _fields.Add(field);
        _byName[name] = field;
        MarkChanged();
        return field;
    }

    /// <summary>
    /// The array a field of this name belongs in, making the branches above it if they are not
    /// there yet. A name with no full stops belongs at the root and needs no branch at all.
    /// </summary>
    private (PdfArray Siblings, PdfObjectId Parent) Parent(string name)
    {
        PdfArray siblings = Root.GetArray(PdfName.Get("Fields")) ?? CreateRootFields();
        PdfObjectId parent = default;

        int start = 0;
        while (name.IndexOf('.', start) is var dot and >= 0)
        {
            string prefix = name[..dot];
            (siblings, parent) = Branch(siblings, parent, name[start..dot], prefix);
            start = dot + 1;
        }

        return (siblings, parent);

        PdfArray CreateRootFields()
        {
            var array = new PdfArray();
            Document.Attach(PdfValue.Array(array));
            Root.Set(PdfName.Get("Fields"), PdfValue.Array(array));
            return array;
        }
    }

    /// <summary>Finds or makes the branch of a given partial name among a list of siblings.</summary>
    private (PdfArray Kids, PdfObjectId Id) Branch(
        PdfArray siblings, PdfObjectId parent, string partial, string qualified)
    {
        for (int i = 0; i < siblings.Count; i++)
        {
            if (siblings.GetDictionary(i) is not { } candidate ||
                candidate.GetTextString(PdfName.Get("T")) != partial)
            {
                continue;
            }

            PdfObjectId existing = siblings[i].IsReference ? siblings[i].AsReference : default;
            PdfArray kids = candidate.GetArray(PdfName.Get("Kids")) ?? AttachKids(candidate);
            return (kids, existing);
        }

        var branch = new PdfDictionary(3);
        branch.Set(PdfName.Get("T"), PdfValue.TextString(partial));

        if (parent.IsValid)
            branch.Set(PdfName.Get("Parent"), PdfValue.Reference(parent));

        Document.Attach(PdfValue.Dictionary(branch));
        PdfObjectId id = Document.AddObject(PdfValue.Dictionary(branch));
        siblings.Add(PdfValue.Reference(id));

        _ = qualified;
        return (AttachKids(branch), id);

        PdfArray AttachKids(PdfDictionary node)
        {
            var kids = new PdfArray();
            Document.Attach(PdfValue.Array(kids));
            node.Set(PdfName.Get("Kids"), PdfValue.Array(kids));
            return kids;
        }
    }

    /// <summary>
    /// The last part of a qualified name, which is the only part that may go in a field's own
    /// <c>T</c> entry: §12.7.3.2 forbids a partial name from containing a full stop.
    /// </summary>
    private static string PartialName(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    private PdfPage? FindPage(PdfDictionary widget)
    {
        if (!widget.TryGetRaw(PdfName.Get("P"), out PdfValue reference) || !reference.IsReference)
            return null;

        PdfObjectId id = reference.AsReference;
        foreach (PdfPage page in Document.Pages)
        {
            if (page.Id == id)
                return page;
        }

        return null;
    }

    private void Collect()
    {
        if (Root.GetArray(PdfName.Get("Fields")) is not { } fields)
            return;

        HashSet<int> visited = [];
        for (int i = 0; i < fields.Count; i++)
            Walk(fields[i], string.Empty, visited, 0);
    }

    private void Walk(PdfValue reference, string prefix, HashSet<int> visited, int depth)
    {
        if (depth > 32)
            return;

        PdfObjectId id = reference.IsReference ? reference.AsReference : PdfObjectId.None;
        if (id.IsValid && !visited.Add(id.Number))
            return;

        if (Document.Resolve(reference).AsDictionary is not { } dictionary)
            return;

        string partial = dictionary.GetTextString(PdfName.Get("T")) ?? string.Empty;
        string name = prefix.Length == 0 ? partial : partial.Length == 0 ? prefix : prefix + "." + partial;

        PdfArray? kids = dictionary.GetArray(PdfName.Get("Kids"));
        bool hasNamedKids = false;

        if (kids is not null)
        {
            for (int i = 0; i < kids.Count; i++)
            {
                if (Document.Resolve(kids[i]).AsDictionary?.ContainsKey(PdfName.Get("T")) == true)
                {
                    hasNamedKids = true;
                    break;
                }
            }
        }

        // A node with named children is a branch; anything else is a field, even when it also has
        // unnamed widget kids of its own.
        if (!hasNamedKids && (dictionary.ContainsKey(PdfName.Get("FT")) || dictionary.ContainsKey(PdfName.Get("T"))))
        {
            var field = new PdfFormField(this, id, dictionary, name);
            _fields.Add(field);
            _byName[name] = field;
        }

        if (kids is null || !hasNamedKids)
            return;

        for (int i = 0; i < kids.Count; i++)
            Walk(kids[i], name, visited, depth + 1);
    }
}
