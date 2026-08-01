using System.Globalization;
using Inkwright.Cos;

namespace Inkwright.Forms;

/// <summary>The kinds of interactive field an AcroForm can hold (ISO 32000-1 §12.7.4).</summary>
public enum PdfFieldType
{
    /// <summary>A field type the library does not model.</summary>
    Unknown,

    /// <summary>A single-line or multi-line text box.</summary>
    Text,

    /// <summary>A tick box.</summary>
    CheckBox,

    /// <summary>One option of a mutually exclusive group.</summary>
    RadioButton,

    /// <summary>A push button, which holds no value.</summary>
    PushButton,

    /// <summary>A drop-down list.</summary>
    ComboBox,

    /// <summary>A scrolling list.</summary>
    ListBox,

    /// <summary>A place for a digital signature.</summary>
    Signature,
}

/// <summary>
/// One field of an interactive form. The class reads and writes the field dictionary directly, so a
/// value set here is the value in the file; appearances are regenerated on demand rather than left
/// to the viewer, because too many viewers show nothing at all without them.
/// </summary>
public sealed class PdfFormField
{
    private readonly PdfForm _form;

    internal PdfFormField(PdfForm form, PdfObjectId id, PdfDictionary dictionary, string fullName)
    {
        _form = form;
        Id = id;
        Dictionary = dictionary;
        FullName = fullName;
    }

    /// <summary>The identity of the field object.</summary>
    public PdfObjectId Id { get; }

    /// <summary>The field dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The fully qualified name, with parents joined by full stops.</summary>
    public string FullName { get; }

    /// <summary>The name of this field on its own, without its parents.</summary>
    public string PartialName => Dictionary.GetTextString(PdfName.Get("T")) ?? string.Empty;

    /// <summary>What kind of field this is.</summary>
    public PdfFieldType Type
    {
        get
        {
            PdfName? type = InheritedName(PdfName.Get("FT"));
            long flags = InheritedInteger(PdfName.Get("Ff"));

            return type?.Value switch
            {
                "Tx" => PdfFieldType.Text,
                "Sig" => PdfFieldType.Signature,
                "Ch" => (flags & (1 << 17)) != 0 ? PdfFieldType.ComboBox : PdfFieldType.ListBox,
                "Btn" when (flags & (1 << 16)) != 0 => PdfFieldType.PushButton,
                "Btn" when (flags & (1 << 15)) != 0 => PdfFieldType.RadioButton,
                "Btn" => PdfFieldType.CheckBox,
                _ => PdfFieldType.Unknown,
            };
        }
    }

    /// <summary>The field flags of table 227.</summary>
    public long Flags => InheritedInteger(PdfName.Get("Ff"));

    /// <summary>Whether the field must be filled in before the form is submitted.</summary>
    public bool IsRequired => (Flags & 2) != 0;

    /// <summary>Whether the field refuses to be edited.</summary>
    public bool IsReadOnly => (Flags & 1) != 0;

    /// <summary>Whether a text field accepts more than one line.</summary>
    public bool IsMultiline => Type == PdfFieldType.Text && (Flags & (1 << 12)) != 0;

    /// <summary>Whether a text field hides what is typed into it.</summary>
    public bool IsPassword => Type == PdfFieldType.Text && (Flags & (1 << 13)) != 0;

    /// <summary>The most characters a text field accepts, or zero when it is unlimited.</summary>
    public int MaxLength => (int)InheritedInteger(PdfName.Get("MaxLen"));

    /// <summary>The tooltip shown over the field.</summary>
    public string? ToolTip => Dictionary.GetTextString(PdfName.Get("TU"));

    /// <summary>The options a list or combo box offers.</summary>
    public IReadOnlyList<string> Options
    {
        get
        {
            List<string> options = [];
            if (Inherited(PdfName.Get("Opt")).AsArray is not { } array)
                return options;

            for (int i = 0; i < array.Count; i++)
            {
                PdfValue entry = array.Get(i);

                // An option is either a string or a pair of export value and display text.
                if (entry.AsArray is { Count: >= 2 } pair)
                    options.Add(pair.Get(1).AsTextString ?? string.Empty);
                else if (entry.AsTextString is { } text)
                    options.Add(text);
            }

            return options;
        }
    }

    /// <summary>The states a check box or radio button can take, besides <c>Off</c>.</summary>
    public IReadOnlyList<string> OnStates
    {
        get
        {
            List<string> states = [];

            foreach (PdfDictionary widget in Widgets)
            {
                if (widget.GetDictionary(PdfName.Get("AP"))?.GetDictionary(PdfName.Get("N")) is not { } normal)
                    continue;

                foreach (PdfName state in normal.Keys)
                {
                    if (state.Value != "Off" && !states.Contains(state.Value))
                        states.Add(state.Value);
                }
            }

            return states;
        }
    }

    /// <summary>
    /// The field value as text. Check boxes answer with their state name, lists with the selected
    /// option, and text fields with what is in them.
    /// </summary>
    public string? Value
    {
        get
        {
            PdfValue value = Inherited(PdfName.Get("V"));
            return value.Kind switch
            {
                PdfValueKind.String => value.AsTextString,
                PdfValueKind.Name => value.AsName!.Value,
                PdfValueKind.Array when value.AsArray is { Count: > 0 } array => array.Get(0).AsTextString,
                PdfValueKind.Integer or PdfValueKind.Real => value.AsNumber.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
        }

        set => SetValue(value);
    }

    /// <summary>Whether a check box or radio button is on.</summary>
    public bool IsChecked
    {
        get => Value is { } value && value != "Off";
        set => SetValue(value ? OnStates.FirstOrDefault() ?? "Yes" : "Off");
    }

    /// <summary>The widget annotations that show this field on a page.</summary>
    public IReadOnlyList<PdfDictionary> Widgets
    {
        get
        {
            List<PdfDictionary> widgets = [];

            // A field with one widget merges the two dictionaries; otherwise the widgets are kids.
            if (Dictionary.ContainsKey(PdfName.Get("Rect")))
                widgets.Add(Dictionary);

            if (Dictionary.GetArray(PdfName.Get("Kids")) is { } kids)
            {
                for (int i = 0; i < kids.Count; i++)
                {
                    if (kids.Get(i).AsDictionary is { } kid && kid.ContainsKey(PdfName.Get("Rect")))
                        widgets.Add(kid);
                }
            }

            return widgets;
        }
    }

    /// <summary>Where the field sits on the page, taken from its first widget.</summary>
    public PdfRectangle Bounds =>
        Widgets.Count > 0 ? PdfRectangle.FromArray(Widgets[0].GetArray(PdfName.Get("Rect"))) : default;

    /// <summary>The display format the field asks for, read from its format action.</summary>
    public FieldFormat Format => FieldFormat.Read(this);

    /// <summary>How the field's value is worked out from others, if it is.</summary>
    public FieldCalculation Calculation => FieldCalculation.Read(this);

    /// <summary>
    /// The value as a viewer would show it: the stored value put through the field's format. A
    /// total stored as <c>1234.5</c> reads back as <c>$1,234.50</c> when the field says so.
    /// </summary>
    public string DisplayValue => Format.Apply(Value);

    /// <summary>Sets the value and regenerates the appearance so every viewer shows it.</summary>
    /// <param name="value">The new value, or <see langword="null"/> to clear the field.</param>
    public void SetValue(string? value)
    {
        PdfName key = PdfName.Get("V");

        if (value is null)
        {
            Dictionary.Remove(key);
        }
        else if (Type is PdfFieldType.CheckBox or PdfFieldType.RadioButton)
        {
            PdfName state = PdfName.Get(value);
            Dictionary.Set(key, PdfValue.Name(state));

            foreach (PdfDictionary widget in Widgets)
            {
                bool on = widget.GetDictionary(PdfName.Get("AP"))?.GetDictionary(PdfName.Get("N"))?.ContainsKey(state) == true;
                widget.Set(PdfName.Get("AS"), PdfValue.Name(on ? state : PdfName.Get("Off")));
            }
        }
        else
        {
            Dictionary.Set(key, PdfValue.TextString(value));
        }

        _form.Document.MarkChanged(Id);
        _form.AppearanceGenerator.Update(this);
    }

    internal PdfValue Inherited(PdfName key)
    {
        PdfDictionary? current = Dictionary;
        for (int depth = 0; current is not null && depth < 32; depth++)
        {
            if (current.TryGetRaw(key, out PdfValue value) && !value.IsNull)
                return current.Get(key);

            current = current.GetDictionary(PdfName.Get("Parent"));
        }

        return _form.Root.Get(key);
    }

    private PdfName? InheritedName(PdfName key) => Inherited(key).AsName;

    private long InheritedInteger(PdfName key)
    {
        PdfValue value = Inherited(key);
        return value.IsNumber ? value.AsInteger : 0;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Type} '{FullName}' = {Value ?? "(empty)"}";
}
