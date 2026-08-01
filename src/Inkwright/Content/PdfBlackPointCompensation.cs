namespace Inkwright.Content;

/// <summary>
/// Whether to compensate for the black point when converting colour, which PDF 2.0 expresses as the
/// <c>UseBlackPtComp</c> entry of a graphics state parameter dictionary. The three states are named
/// exactly as the standard spells them.
/// </summary>
public enum PdfBlackPointCompensation
{
    /// <summary>Leave the decision to the reader, which is what a file says by not saying anything.</summary>
    Default,

    /// <summary>Compensate, which preserves shadow detail in photographs.</summary>
    On,

    /// <summary>Do not compensate, which keeps exact colours exact.</summary>
    Off,
}
