namespace Inkwright.Security;

/// <summary>
/// The user access permissions of ISO 32000-1 table 22. The values are the bit positions of the
/// <c>P</c> entry, where a set bit grants the operation.
/// </summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>Nothing beyond opening the document is allowed.</summary>
    None = 0,

    /// <summary>Printing, at low resolution when <see cref="PrintHighQuality"/> is absent.</summary>
    Print = 1 << 2,

    /// <summary>Modifying the contents of the document.</summary>
    ModifyContents = 1 << 3,

    /// <summary>Copying text and graphics out of the document.</summary>
    CopyContents = 1 << 4,

    /// <summary>Adding or modifying annotations and filling in form fields.</summary>
    ModifyAnnotations = 1 << 5,

    /// <summary>Filling in form fields even when <see cref="ModifyAnnotations"/> is absent.</summary>
    FillForms = 1 << 8,

    /// <summary>Extracting content for accessibility tools.</summary>
    ExtractForAccessibility = 1 << 9,

    /// <summary>Assembling the document: inserting, rotating and deleting pages.</summary>
    Assemble = 1 << 10,

    /// <summary>Printing at full resolution.</summary>
    PrintHighQuality = 1 << 11,

    /// <summary>Everything the standard security handler can grant.</summary>
    All = Print | ModifyContents | CopyContents | ModifyAnnotations | FillForms |
          ExtractForAccessibility | Assemble | PrintHighQuality,
}
