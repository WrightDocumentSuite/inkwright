namespace Inkwright.Layout;

/// <summary>
/// One archival rule a document breaks, named by the clause that states it. Carrying the clause
/// rather than prose alone is what lets a caller look the rule up, and what keeps the report
/// comparable with what an external validator says about the same file.
/// </summary>
/// <param name="Clause">The clause of ISO 19005 that is broken, such as <c>6.1.3</c>.</param>
/// <param name="Message">What is wrong, in terms of the document rather than of the standard.</param>
public sealed record PdfAProblem(string Clause, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Clause}: {Message}";
}
