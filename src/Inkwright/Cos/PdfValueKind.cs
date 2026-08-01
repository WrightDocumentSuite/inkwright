namespace Inkwright.Cos;

/// <summary>
/// Discriminates the eight basic object types of ISO 32000-1 §7.3 plus the indirect reference
/// (§7.3.10) that stands in for an object stored elsewhere in the file.
/// </summary>
public enum PdfValueKind : byte
{
    /// <summary>The null object, and the value of <c>default(PdfValue)</c>.</summary>
    Null = 0,

    /// <summary>A boolean object, <c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>An integer numeric object.</summary>
    Integer,

    /// <summary>A real numeric object.</summary>
    Real,

    /// <summary>A string object; the payload is the raw bytes after escape resolution.</summary>
    String,

    /// <summary>A name object.</summary>
    Name,

    /// <summary>An array object.</summary>
    Array,

    /// <summary>A dictionary object.</summary>
    Dictionary,

    /// <summary>A stream object: a dictionary plus a byte payload.</summary>
    Stream,

    /// <summary>An indirect reference such as <c>12 0 R</c>.</summary>
    Reference,
}
