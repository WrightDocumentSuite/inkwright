namespace Inkwright.Layout;

/// <summary>
/// A level of the accessibility standard. Part 1 sits on ISO 32000-1 and states its rules in
/// clause 7; part 2 sits on ISO 32000-2 and restates them in clause 8, adding the structure
/// namespaces of ISO/TS 32005.
/// </summary>
/// <param name="Part">Which part of ISO 14289 is claimed, 1 or 2.</param>
public readonly record struct PdfUaConformance(int Part)
{
    /// <summary>PDF/UA-1, ISO 14289-1:2014.</summary>
    public static PdfUaConformance Ua1 => new(1);

    /// <summary>PDF/UA-2, ISO 14289-2:2024.</summary>
    public static PdfUaConformance Ua2 => new(2);

    /// <inheritdoc />
    public override string ToString() => $"PDF/UA-{Part}";
}

/// <summary>
/// How serious a finding is. The accessibility standard states a great deal as advice rather than
/// as requirement, and a report that cannot tell the two apart is one nobody can act on.
/// </summary>
public enum PdfUaSeverity
{
    /// <summary>A requirement of the standard is broken; the file does not conform.</summary>
    Violation,

    /// <summary>Something the standard recommends, or that the guidance documents advise.</summary>
    Advice,
}

/// <summary>
/// One finding about a document's accessibility, named by the clause that states the rule.
/// </summary>
/// <param name="Clause">The clause of ISO 14289 concerned, such as <c>7.4.2</c>.</param>
/// <param name="Severity">Whether the standard requires this or merely advises it.</param>
/// <param name="Message">What is wrong, in terms of the document rather than of the standard.</param>
public sealed record PdfUaProblem(string Clause, PdfUaSeverity Severity, string Message)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"{Clause} ({(Severity == PdfUaSeverity.Violation ? "violation" : "advice")}): {Message}";
}
