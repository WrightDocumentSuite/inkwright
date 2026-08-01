using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>
/// What every archival rule needs: the document, the level being claimed, somewhere to report, and
/// a flattened view of the object graph. The graph is walked once and shared, because a validator
/// that re-descends from the catalogue for each of a hundred rules is a validator nobody runs.
/// </summary>
internal sealed class PdfAContext
{
    private const int MaxDepth = 64;

    private readonly List<PdfAProblem> _problems = [];
    private List<PdfDictionary>? _dictionaries;
    private List<PdfStream>? _streams;
    private List<PdfValue>? _values;
    private HashSet<int>? _objects;

    internal PdfAContext(PdfDocument document, PdfAConformance conformance)
    {
        Document = document;
        Conformance = conformance;
    }

    internal PdfDocument Document { get; }

    internal PdfAConformance Conformance { get; }

    internal int Part => Conformance.Part;

    internal char Level => Conformance.Level;

    internal IReadOnlyList<PdfAProblem> Problems => _problems;

    /// <summary>The bytes the document was read from, when it came from a file at all.</summary>
    internal ReadOnlyMemory<byte> FileBytes => Document.Reader?.FileBytes ?? default;

    /// <summary>Whether the document was read from a file, so byte-level rules can be judged.</summary>
    internal bool HasFile => Document.Reader is not null && !Document.Reader.FileBytes.IsEmpty;

    /// <summary>
    /// Every dictionary the catalogue can reach, stream dictionaries included, each visited once.
    /// Objects nothing reaches are left out on purpose: a file is judged by what a reader would
    /// follow, and part 1 says so outright in 6.1.4.
    /// </summary>
    internal IReadOnlyList<PdfDictionary> Dictionaries
    {
        get
        {
            Flatten();
            return _dictionaries!;
        }
    }

    /// <summary>Every stream the catalogue can reach.</summary>
    internal IReadOnlyList<PdfStream> Streams
    {
        get
        {
            Flatten();
            return _streams!;
        }
    }

    /// <summary>Every value the catalogue can reach, scalars included.</summary>
    internal IReadOnlyList<PdfValue> Values
    {
        get
        {
            Flatten();
            return _values!;
        }
    }

    /// <summary>The numbers of the indirect objects the catalogue can reach.</summary>
    internal IReadOnlyCollection<int> ObjectNumbers
    {
        get
        {
            Flatten();
            return _objects!;
        }
    }

    /// <summary>Records a broken rule.</summary>
    /// <param name="clause">The clause of ISO 19005 that states it.</param>
    /// <param name="message">What is wrong with this document.</param>
    internal void Report(string clause, string message) => _problems.Add(new PdfAProblem(clause, message));

    /// <summary>Resolves a value, following an indirect reference when it is one.</summary>
    /// <param name="value">The value to resolve.</param>
    internal PdfValue Resolve(PdfValue value) => Document.Resolve(value);

    /// <summary>The clause number for a rule the parts state in different places.</summary>
    /// <param name="partOne">Where part 1 states it.</param>
    /// <param name="rest">Where parts 2 and 3 state it.</param>
    internal string Clause(string partOne, string rest) => Part == 1 ? partOne : rest;

    private void Flatten()
    {
        if (_dictionaries is not null)
            return;

        _dictionaries = [];
        _streams = [];
        _values = [];
        _objects = [];

        HashSet<PdfDictionary> seenDictionaries = new(ReferenceEqualityComparer.Instance);
        HashSet<PdfArray> seenArrays = new(ReferenceEqualityComparer.Instance);

        Visit(PdfValue.Dictionary(Document.Catalog), 0);
        Visit(PdfValue.Dictionary(Document.Trailer), 0);

        // The page tree is only rebuilt on the way out, so before a save the catalogue does not yet
        // reach the pages the model holds. Judging a document by what it will be written as means
        // starting from those too.
        foreach (PdfPage page in Document.Pages)
            Visit(PdfValue.Dictionary(page.Dictionary), 0);

        void Visit(PdfValue value, int depth)
        {
            if (depth > MaxDepth)
                return;

            _values!.Add(value);

            switch (value.Kind)
            {
                case PdfValueKind.Reference:
                    if (_objects!.Add(value.AsReference.Number))
                        Visit(Document.GetObject(value.AsReference.Number), depth + 1);

                    break;

                case PdfValueKind.Array:
                    PdfArray array = value.AsArray!;
                    if (!seenArrays.Add(array))
                        break;

                    for (int i = 0; i < array.Count; i++)
                        Visit(array[i], depth + 1);

                    break;

                case PdfValueKind.Stream:
                    PdfStream stream = value.AsStream!;
                    if (seenDictionaries.Add(stream.Dictionary))
                    {
                        _streams!.Add(stream);
                        _dictionaries!.Add(stream.Dictionary);
                        VisitEntries(stream.Dictionary, depth);
                    }

                    break;

                case PdfValueKind.Dictionary:
                    PdfDictionary dictionary = value.AsDictionary!;
                    if (seenDictionaries.Add(dictionary))
                    {
                        _dictionaries!.Add(dictionary);
                        VisitEntries(dictionary, depth);
                    }

                    break;

                default:
                    break;
            }
        }

        void VisitEntries(PdfDictionary dictionary, int depth)
        {
            foreach (PdfName key in dictionary.Keys.ToArray())
            {
                _values!.Add(PdfValue.Name(key));

                if (dictionary.TryGetRaw(key, out PdfValue entry))
                    Visit(entry, depth + 1);
            }
        }
    }
}
