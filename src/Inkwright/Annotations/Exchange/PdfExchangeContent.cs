namespace Inkwright.Annotations;

/// <summary>
/// Which halves of an exchange payload to carry. ISO 19444-1 lets one file hold form values,
/// annotations or both, and which of them is wanted depends on what the round trip is for: sending
/// a form back holds values, sending a review back holds comments.
/// </summary>
[Flags]
public enum PdfExchangeContent
{
    /// <summary>Neither half, which writes an empty payload.</summary>
    None = 0,

    /// <summary>The values of the document's form fields (§6.3).</summary>
    Fields = 1,

    /// <summary>The document's comments (§6.4).</summary>
    Annotations = 2,

    /// <summary>Both halves.</summary>
    All = Fields | Annotations,
}
