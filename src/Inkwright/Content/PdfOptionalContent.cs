using Inkwright.Cos;

namespace Inkwright.Content;

/// <summary>A layer of a document: content that can be shown or hidden as a whole (§8.11.2).</summary>
/// <param name="Name">The name a viewer offers the reader, such as <c>Watermark</c>.</param>
/// <param name="Reference">The reference to the group's dictionary.</param>
/// <param name="IsVisible">Whether the document's default configuration shows it.</param>
public sealed record PdfLayer(string Name, PdfValue Reference, bool IsVisible);

/// <summary>
/// The optional content of a document: which layers it declares and which of them the default
/// configuration turns on. Layers hold things a page paints but does not always mean to show, such
/// as the alternative languages of a drawing or a watermark that only prints, and text extraction
/// that ignores them reports words no reader ever sees.
/// </summary>
public sealed class PdfOptionalContent
{
    /// <summary>How deeply a visibility expression may nest before it is taken to be looping.</summary>
    private const int MaxDepth = 16;

    private readonly PdfDocument _document;
    private readonly PdfDictionary? _properties;
    private readonly HashSet<int> _off = [];

    internal PdfOptionalContent(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _document = document;
        _properties = document.Catalog.GetDictionary(PdfNames.OCProperties);
        if (_properties?.GetDictionary(PdfNames.D) is not { } configuration)
            return;

        // A configuration lists what it turns off, and optionally states that everything starts off
        // and only the ON list is shown.
        if (configuration.GetName(PdfNames.BaseState)?.Value == "OFF")
        {
            foreach (PdfValue group in Each(_properties.GetArray(PdfNames.OCGs)))
                Mark(group, on: false);

            foreach (PdfValue group in Each(configuration.GetArray(PdfNames.ON)))
                Mark(group, on: true);
        }

        foreach (PdfValue group in Each(configuration.GetArray(PdfNames.OFF)))
            Mark(group, on: false);
    }

    /// <summary>Whether the document declares any layers at all.</summary>
    public bool IsDefined => _properties is not null;

    /// <summary>The layers the document declares, with the visibility its default configuration gives them.</summary>
    public IReadOnlyList<PdfLayer> Layers
    {
        get
        {
            List<PdfLayer> layers = [];

            PdfArray? groups = _properties?.GetArray(PdfNames.OCGs);

            for (int i = 0; groups is not null && i < groups.Count; i++)
            {
                if (groups.Get(i).AsDictionary is not { } dictionary)
                    continue;

                // The raw element is kept rather than the dictionary it resolves to, because a
                // group is identified by which object it is.
                PdfValue reference = groups[i];
                layers.Add(new PdfLayer(
                    dictionary.GetTextString(PdfNames.Name) ?? string.Empty,
                    reference,
                    IsGroupVisible(reference)));
            }

            return layers;
        }
    }

    /// <summary>
    /// Whether content governed by an entry from a <c>Properties</c> resource is visible. The entry
    /// is either a group, which is simply on or off, or a membership dictionary, which combines
    /// several groups through a policy or an arbitrary boolean expression.
    /// </summary>
    /// <param name="value">The group or membership dictionary, as named by a <c>BDC</c> operator.</param>
    public bool IsVisible(PdfValue value)
    {
        if (Resolve(value) is not { } dictionary)
            return true;

        // A plain group is identified by which object it is, so the unresolved value is what counts.
        if (dictionary.GetName(PdfNames.Type) != PdfNames.OCMD)
            return IsGroupVisible(value);

        // A visibility expression outranks the policy whenever a document carries both (§8.11.2.3).
        if (dictionary.GetArray(PdfNames.VE) is { Count: > 0 } expression)
            return Evaluate(expression, 0);

        PdfValue groups = dictionary[PdfNames.OCGs];
        List<bool> states = [];

        if (dictionary.GetArray(PdfNames.OCGs) is { } array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (!array[i].IsNull)
                    states.Add(IsGroupVisible(array[i]));
            }
        }
        else if (!groups.IsNull)
            states.Add(IsGroupVisible(groups));

        if (states.Count == 0)
            return true;

        return dictionary.GetName(PdfNames.P)?.Value switch
        {
            "AllOn" => states.TrueForAll(state => state),
            "AnyOff" => states.Exists(state => !state),
            "AllOff" => states.TrueForAll(state => !state),
            _ => states.Exists(state => state),
        };
    }

    /// <summary>
    /// Evaluates a visibility expression: an array whose first element is <c>/And</c>, <c>/Or</c> or
    /// <c>/Not</c> and whose remaining elements are groups or further expressions (§8.11.2.3).
    /// </summary>
    private bool Evaluate(PdfArray expression, int depth)
    {
        if (depth > MaxDepth || expression.Count == 0)
            return true;

        string @operator = expression.GetName(0)?.Value ?? string.Empty;
        bool all = @operator == "And";

        if (@operator == "Not")
            return expression.Count > 1 && !Operand(expression, 1, depth);

        if (@operator is not ("And" or "Or"))
            return true;

        for (int i = 1; i < expression.Count; i++)
        {
            bool operand = Operand(expression, i, depth);
            if (operand != all)
                return operand;
        }

        return all;
    }

    private bool Operand(PdfArray expression, int index, int depth) =>
        expression.Get(index).AsArray is { } nested
            ? Evaluate(nested, depth + 1)
            : IsGroupVisible(expression[index]);

    private bool IsGroupVisible(PdfValue group) =>
        !group.IsReference || !_off.Contains(group.AsReference.Number);

    private PdfDictionary? Resolve(PdfValue value) =>
        value.IsReference ? _document.GetObject(value.AsReference).AsDictionary : value.AsDictionary;

    private void Mark(PdfValue group, bool on)
    {
        if (!group.IsReference)
            return;

        if (on)
            _off.Remove(group.AsReference.Number);
        else
            _off.Add(group.AsReference.Number);
    }

    /// <summary>Walks an array without resolving its elements, so that references stay identifiable.</summary>
    private static IEnumerable<PdfValue> Each(PdfArray? array)
    {
        if (array is null)
            yield break;

        for (int i = 0; i < array.Count; i++)
            yield return array[i];
    }
}
