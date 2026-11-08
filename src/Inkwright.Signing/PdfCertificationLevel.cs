namespace Inkwright.Signing;

/// <summary>
/// What a certification signature permits to happen to the document afterwards (ISO 32000-2
/// §12.8.2.2, the <c>P</c> entry of the DocMDP transform parameters). A conforming validator does not
/// count incremental updates that only add validation data or document timestamps as changes.
/// </summary>
public enum PdfCertificationLevel
{
    /// <summary>The signature is an ordinary approval, not a certification.</summary>
    None = 0,

    /// <summary>Any change to the document invalidates the signature.</summary>
    NoChanges = 1,

    /// <summary>Filling in forms and signing are permitted; anything else invalidates it.</summary>
    FormFilling = 2,

    /// <summary>Form filling, signing and comment work are permitted; anything else invalidates it.</summary>
    FormFillingAndAnnotations = 3,
}

/// <summary>
/// Which form fields a signature freezes (ISO 32000-2 §12.7.5.5 and §12.8.2.4). The set is written
/// twice, as the standard requires: into the field's <c>Lock</c> dictionary, and copied into the
/// FieldMDP transform parameters of the signature itself.
/// </summary>
public sealed record PdfFieldLock
{
    private PdfFieldLock(string action, IReadOnlyList<string> fields)
    {
        Action = action;
        Fields = fields;
    }

    /// <summary>The <c>Action</c> name: <c>All</c>, <c>Include</c> or <c>Exclude</c>.</summary>
    public string Action { get; }

    /// <summary>The field names the action applies to; empty when the action is <c>All</c>.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>Whether the lock freezes the field with the given fully qualified name.</summary>
    /// <param name="fieldName">The fully qualified field name to test.</param>
    public bool IsFieldLocked(string fieldName) => Action switch
    {
        "All" => true,
        "Include" => Fields.Contains(fieldName, StringComparer.Ordinal),
        "Exclude" => !Fields.Contains(fieldName, StringComparer.Ordinal),
        _ => false,
    };

    /// <summary>
    /// The change permission this lock also imposes on the whole document (PDF 2.0, the <c>P</c>
    /// entry of the lock dictionary). Permissions can only be narrowed, never widened, so a value
    /// looser than one already in force is ignored by readers.
    /// </summary>
    public PdfCertificationLevel? Permissions { get; init; }

    /// <summary>Locks every field in the document.</summary>
    public static PdfFieldLock All() => new("All", []);

    /// <summary>Locks the named fields and leaves the rest editable.</summary>
    /// <param name="fields">Fully qualified field names.</param>
    public static PdfFieldLock Include(params IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return Named("Include", fields);
    }

    /// <summary>Locks every field except the named ones.</summary>
    /// <param name="fields">Fully qualified field names.</param>
    public static PdfFieldLock Exclude(params IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return Named("Exclude", fields);
    }

    private static PdfFieldLock Named(string action, IEnumerable<string> fields)
    {
        string[] names = [.. fields];
        if (names.Length == 0 || names.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException(
                $"A {action} field lock requires at least one non-empty fully qualified field name.",
                nameof(fields));
        }

        return new PdfFieldLock(action, names);
    }
}
