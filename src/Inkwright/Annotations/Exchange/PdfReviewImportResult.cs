namespace Inkwright.Annotations;

/// <summary>
/// What became of a payload the document was handed: how many comments arrived, how many replaced
/// ones already there, how many form fields were filled in, and everything the payload asked for
/// that could not be done. A payload from a stranger is never quite what one expects, so nothing
/// here throws — what could not be honoured is counted and explained instead.
/// </summary>
/// <param name="Added">Comments the document had never seen before.</param>
/// <param name="Updated">Comments already present under the same name, rewritten from the payload.</param>
/// <param name="Skipped">Entries that could not be placed at all; the reason is among the warnings.</param>
/// <param name="Warnings">What was ignored and why, in the order it was met.</param>
/// <param name="FieldsFilled">Form fields the payload gave a new value to.</param>
public sealed record PdfReviewImportResult(
    int Added,
    int Updated,
    int Skipped,
    IReadOnlyList<string> Warnings,
    int FieldsFilled = 0)
{
    /// <summary>How many comments the document carries from this round, added and updated together.</summary>
    public int Imported => Added + Updated;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Added} added, {Updated} updated, {Skipped} skipped, " +
        $"{FieldsFilled} fields filled, {Warnings.Count} warnings";
}
