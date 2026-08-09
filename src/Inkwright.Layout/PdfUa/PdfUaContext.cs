using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>
/// What every accessibility rule needs: the document, the part being claimed, somewhere to report,
/// and the structure tree, which almost every rule in the standard is stated in terms of.
/// </summary>
internal sealed class PdfUaContext
{
    private readonly List<PdfUaProblem> _problems = [];
    private IReadOnlyList<Tagging.PdfStructureNode>? _structure;

    internal PdfUaContext(PdfDocument document, PdfUaConformance conformance)
    {
        Document = document;
        Conformance = conformance;
    }

    internal PdfDocument Document { get; }

    internal PdfUaConformance Conformance { get; }

    internal int Part => Conformance.Part;

    internal IReadOnlyList<PdfUaProblem> Problems => _problems;

    /// <summary>The document catalogue.</summary>
    internal PdfDictionary Catalog => Document.Catalog;

    /// <summary>The roots of the logical structure, or an empty list when there is no tree.</summary>
    internal IReadOnlyList<Tagging.PdfStructureNode> Structure =>
        _structure ??= Tagging.StructureReader.Read(Document);

    /// <summary>Records a broken requirement.</summary>
    /// <param name="clause">The clause of ISO 14289 that states it.</param>
    /// <param name="message">What is wrong with this document.</param>
    internal void Violation(string clause, string message) =>
        _problems.Add(new PdfUaProblem(clause, PdfUaSeverity.Violation, message));

    /// <summary>Records something the standard recommends rather than requires.</summary>
    /// <param name="clause">The clause concerned.</param>
    /// <param name="message">What would be better.</param>
    internal void Advice(string clause, string message) =>
        _problems.Add(new PdfUaProblem(clause, PdfUaSeverity.Advice, message));

    /// <summary>Resolves a value, following an indirect reference when it is one.</summary>
    /// <param name="value">The value to resolve.</param>
    internal PdfValue Resolve(PdfValue value) => Document.Resolve(value);

    /// <summary>
    /// The clause number for a rule the two parts state in different places. Part 1 puts its file
    /// format rules in clause 7 and part 2 puts them in clause 8, so nearly every rule has two.
    /// </summary>
    /// <param name="partOne">Where part 1 states it.</param>
    /// <param name="partTwo">Where part 2 states it.</param>
    internal string Clause(string partOne, string partTwo) => Part == 1 ? partOne : partTwo;

    /// <summary>Walks every node of the structure tree, deepest last.</summary>
    internal IEnumerable<Tagging.PdfStructureNode> AllNodes()
    {
        var stack = new Stack<Tagging.PdfStructureNode>(Structure.Reverse());

        while (stack.Count > 0)
        {
            Tagging.PdfStructureNode node = stack.Pop();
            yield return node;

            for (int i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
    }

    /// <summary>Every annotation on every page, with the page it sits on.</summary>
    internal IEnumerable<(PdfPage Page, PdfDictionary Annotation, PdfObjectId ObjectId)> Annotations()
    {
        foreach (PdfPage page in Document.Pages)
        {
            if (Resolve(page.Dictionary.Get(PdfName.Get("Annots"))).AsArray is not { } annotations)
                continue;

            for (int i = 0; i < annotations.Count; i++)
            {
                PdfValue value = annotations[i];
                if (Resolve(value).AsDictionary is { } annotation)
                {
                    yield return (
                        page,
                        annotation,
                        value.IsReference ? value.AsReference : PdfObjectId.None);
                }
            }
        }
    }
}
