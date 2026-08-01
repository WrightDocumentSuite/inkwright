using System.Text;
using System.Text.RegularExpressions;
using Inkwright.Cos;

namespace Inkwright.Forms;

/// <summary>The arithmetic <c>AFSimple_Calculate</c> can do over a list of fields.</summary>
public enum FieldCalculationKind
{
    /// <summary>The field is not calculated.</summary>
    None,

    /// <summary>The total of the named fields.</summary>
    Sum,

    /// <summary>The mean of the named fields.</summary>
    Average,

    /// <summary>The product of the named fields.</summary>
    Product,

    /// <summary>The smallest of the named fields.</summary>
    Minimum,

    /// <summary>The largest of the named fields.</summary>
    Maximum,
}

/// <summary>
/// A field whose value is worked out from other fields. Like formatting, Acrobat stores this as a
/// call to one of its own scripts — <c>AFSimple_Calculate("SUM", ["price", "tax"])</c> — in the
/// field's <c>/AA /C</c> action, and the order the fields are recalculated in comes from the form's
/// <c>/CO</c> array.
/// </summary>
/// <param name="Kind">Which arithmetic to do.</param>
/// <param name="Sources">The fully qualified names of the fields to read.</param>
public sealed partial record FieldCalculation(FieldCalculationKind Kind, IReadOnlyList<string> Sources)
{
    /// <summary>A field that is not calculated.</summary>
    public static FieldCalculation None { get; } = new(FieldCalculationKind.None, []);

    /// <summary>Whether this field is worked out from others.</summary>
    public bool IsDefined => Kind != FieldCalculationKind.None && Sources.Count > 0;

    /// <summary>Reads the calculation a field declares in its additional-actions dictionary.</summary>
    /// <param name="field">The field to inspect.</param>
    public static FieldCalculation Read(PdfFormField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        PdfDictionary? actions = field.Inherited(PdfName.Get("AA")).AsDictionary;
        if (actions?.GetDictionary(PdfName.Get("C")) is not { } calculate)
            return None;

        return Parse(Script(calculate));
    }

    /// <summary>Reads a calculation out of a line of Acrobat's own form script.</summary>
    /// <param name="script">The script body of a calculate action.</param>
    public static FieldCalculation Parse(string? script)
    {
        if (string.IsNullOrWhiteSpace(script) || SimpleCall().Match(script) is not { Success: true } call)
            return None;

        FieldCalculationKind kind = Unquote(call.Groups[1].Value).ToUpperInvariant() switch
        {
            "SUM" => FieldCalculationKind.Sum,
            "AVG" => FieldCalculationKind.Average,
            "PRD" => FieldCalculationKind.Product,
            "MIN" => FieldCalculationKind.Minimum,
            "MAX" => FieldCalculationKind.Maximum,
            _ => FieldCalculationKind.None,
        };

        if (kind == FieldCalculationKind.None)
            return None;

        List<string> sources = [];
        foreach (string part in call.Groups[2].Value.Split(','))
        {
            if (Unquote(part) is { Length: > 0 } name)
                sources.Add(name);
        }

        return new FieldCalculation(kind, sources);
    }

    /// <summary>Works out the value from the numbers the named fields hold.</summary>
    /// <param name="values">The numbers, in the order the calculation names them.</param>
    /// <returns>The result, or <see langword="null"/> when there is nothing to work out.</returns>
    public double? Compute(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
            return null;

        return Kind switch
        {
            FieldCalculationKind.Sum => values.Sum(),
            FieldCalculationKind.Average => values.Average(),
            FieldCalculationKind.Product => values.Aggregate(1d, (product, value) => product * value),
            FieldCalculationKind.Minimum => values.Min(),
            FieldCalculationKind.Maximum => values.Max(),
            _ => null,
        };
    }

    private static string? Script(PdfDictionary action)
    {
        PdfValue js = action.Get(PdfName.Get("JS"));

        if (js.AsStream is { } stream)
            return Encoding.UTF8.GetString(stream.Decode().Span);

        return js.AsTextString;
    }

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();

        if (trimmed.Length >= 2 && trimmed[0] is '"' or '\'' && trimmed[^1] == trimmed[0])
            return trimmed[1..^1];

        return trimmed;
    }

    [GeneratedRegex(@"AFSimple_Calculate\s*\(\s*([^,]*)\s*,\s*(?:new\s+Array\s*\(|\[)?([^)\]]*)")]
    private static partial Regex SimpleCall();
}
