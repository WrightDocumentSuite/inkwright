namespace Inkwright.Forms;

/// <summary>
/// Marks a type as the shape of a form. The source generator implements
/// <see cref="IPdfFormSchema{TSelf}"/> for it, so filling and reading a form costs no reflection and
/// works under Native AOT.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class PdfFormAttribute : Attribute
{
}

/// <summary>Names the form field a member maps to, when it differs from the member's own name.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class PdfFieldAttribute : Attribute
{
    /// <summary>Maps the member to a field of the same name.</summary>
    public PdfFieldAttribute()
    {
    }

    /// <summary>Maps the member to a named field.</summary>
    /// <param name="name">The fully qualified field name.</param>
    public PdfFieldAttribute(string name) => Name = name;

    /// <summary>The fully qualified field name, or <see langword="null"/> to use the member's name.</summary>
    public string? Name { get; }

    /// <summary>A composite format string applied when writing the value.</summary>
    public string? Format { get; init; }
}

/// <summary>Leaves a member out of the mapping.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class PdfFieldIgnoreAttribute : Attribute
{
}

/// <summary>
/// The contract the generator implements: a type that knows how to write itself into a form and how
/// to read itself back out, with the field names resolved at compile time.
/// </summary>
/// <typeparam name="TSelf">The type being mapped.</typeparam>
public interface IPdfFormSchema<TSelf>
    where TSelf : IPdfFormSchema<TSelf>
{
    /// <summary>The field names this type maps to.</summary>
    static abstract IReadOnlyList<string> FieldNames { get; }

    /// <summary>Writes the values of an instance into a form.</summary>
    /// <param name="form">The form to fill in.</param>
    /// <param name="value">The instance to read from.</param>
    static abstract void Fill(PdfForm form, TSelf value);

    /// <summary>Reads an instance out of a filled form.</summary>
    /// <param name="form">The form to read.</param>
    static abstract TSelf Read(PdfForm form);
}

/// <summary>Typed access to a form, backed by the generated schema.</summary>
public static class PdfFormMapping
{
    /// <summary>Writes a typed value into a form.</summary>
    /// <typeparam name="T">A type marked with <see cref="PdfFormAttribute"/>.</typeparam>
    /// <param name="form">The form to fill in.</param>
    /// <param name="value">The value to write.</param>
    public static void Fill<T>(this PdfForm form, T value)
        where T : IPdfFormSchema<T>
    {
        ArgumentNullException.ThrowIfNull(form);
        T.Fill(form, value);
    }

    /// <summary>Reads a typed value out of a form.</summary>
    /// <typeparam name="T">A type marked with <see cref="PdfFormAttribute"/>.</typeparam>
    /// <param name="form">The form to read.</param>
    public static T Read<T>(this PdfForm form)
        where T : IPdfFormSchema<T>
    {
        ArgumentNullException.ThrowIfNull(form);
        return T.Read(form);
    }

    /// <summary>
    /// Reports which of a schema's fields the document does not have, so a mismatch between a form
    /// and the type describing it is found before anything is filled in.
    /// </summary>
    /// <typeparam name="T">A type marked with <see cref="PdfFormAttribute"/>.</typeparam>
    /// <param name="form">The form to check.</param>
    public static IReadOnlyList<string> MissingFields<T>(this PdfForm form)
        where T : IPdfFormSchema<T>
    {
        ArgumentNullException.ThrowIfNull(form);

        List<string> missing = [];
        foreach (string name in T.FieldNames)
        {
            if (form[name] is null)
                missing.Add(name);
        }

        return missing;
    }
}
