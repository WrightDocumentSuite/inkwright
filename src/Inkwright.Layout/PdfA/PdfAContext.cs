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
    private List<PdfDictionary>? _fileDictionaries;
    private List<PdfStream>? _streams;
    private List<PdfStream>? _fileStreams;
    private List<PdfValue>? _values;
    private List<PdfValue>? _fileValues;
    private HashSet<int>? _objects;
    private HashSet<PdfObjectId>? _objectIds;
    private HashSet<int>? _fileObjects;
    private HashSet<PdfDictionary>? _indirectDictionaries;
    private HashSet<PdfDictionary>? _directDictionaries;
    private bool _graphTraversalStopped;
    private bool _graphProblemReported;

    internal PdfAContext(
        PdfDocument document,
        PdfAConformance conformance,
        PdfAValidationSession validation,
        string embeddedPath)
    {
        Document = document;
        Conformance = conformance;
        Validation = validation;
        EmbeddedPath = embeddedPath;
    }

    internal PdfDocument Document { get; }

    internal PdfAConformance Conformance { get; }

    /// <summary>The shared recursion and resource budget for this validation tree.</summary>
    internal PdfAValidationSession Validation { get; }

    /// <summary>The attachment path from the root document, empty for the root itself.</summary>
    internal string EmbeddedPath { get; }

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

    /// <summary>The clause number for a rule that moved again in part 4.</summary>
    /// <param name="partOne">Where part 1 states it.</param>
    /// <param name="partsTwoAndThree">Where parts 2 and 3 state it.</param>
    /// <param name="partFour">Where part 4 states it.</param>
    internal string Clause(string partOne, string partsTwoAndThree, string partFour) => Part switch
    {
        1 => partOne,
        4 => partFour,
        _ => partsTwoAndThree,
    };

    private void Flatten()
    {
        if (_dictionaries is not null)
            return;

        _dictionaries = [];
        _streams = [];
        _values = [];
        _objects = [];
        _objectIds = [];
        _indirectDictionaries = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        _directDictionaries = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        HashSet<PdfDictionary> seenDictionaries = new(ReferenceEqualityComparer.Instance);
        HashSet<PdfArray> seenArrays = new(ReferenceEqualityComparer.Instance);

        Visit(PdfValue.Dictionary(Document.Catalog), 0, indirectObject: false);
        Visit(PdfValue.Dictionary(Document.Trailer), 0, indirectObject: false);

        // The page tree is only rebuilt on the way out, so before a save the catalogue does not yet
        // reach the pages the model holds. Judging a document by what it will be written as means
        // starting from those too.
        foreach (PdfPage page in Document.Pages)
        {
            if (_graphTraversalStopped)
                break;

            Visit(PdfValue.Dictionary(page.Dictionary), 0, indirectObject: false);
        }

        void Visit(PdfValue value, int depth, bool indirectObject = false)
        {
            if (_graphTraversalStopped)
                return;

            if (depth > MaxDepth)
            {
                ReportGraphProblem(
                    $"Object-graph validation exceeded {MaxDepth} levels, so conformance cannot be established.");
                return;
            }

            if (!Validation.TryConsumeGraphValue(EmbeddedPath, out string? budgetProblem))
            {
                _graphTraversalStopped = true;
                if (budgetProblem is not null)
                    ReportGraphProblem(budgetProblem);
                return;
            }

            _values!.Add(value);

            switch (value.Kind)
            {
                case PdfValueKind.Reference:
                    PdfValue resolved = Document.GetObject(value.AsReference);
                    _objects!.Add(value.AsReference.Number);
                    if (_objectIds!.Add(value.AsReference))
                        Visit(resolved, depth + 1, indirectObject: true);

                    break;

                case PdfValueKind.Array:
                    PdfArray array = value.AsArray!;
                    if (!seenArrays.Add(array))
                        break;

                    for (int i = 0; i < array.Count; i++)
                    {
                        if (_graphTraversalStopped)
                            break;

                        Visit(array[i], depth + 1);
                    }

                    break;

                case PdfValueKind.Stream:
                    PdfStream stream = value.AsStream!;
                    (indirectObject ? _indirectDictionaries! : _directDictionaries!).Add(stream.Dictionary);
                    if (seenDictionaries.Add(stream.Dictionary))
                    {
                        _streams!.Add(stream);
                        _dictionaries!.Add(stream.Dictionary);
                        VisitEntries(stream.Dictionary, depth);
                    }

                    break;

                case PdfValueKind.Dictionary:
                    PdfDictionary dictionary = value.AsDictionary!;
                    (indirectObject ? _indirectDictionaries! : _directDictionaries!).Add(dictionary);
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
            for (int i = 0; i < dictionary.Count; i++)
            {
                if (_graphTraversalStopped)
                    break;

                PdfName key = dictionary.Keys[i];

                if (!Validation.TryConsumeGraphValue(EmbeddedPath, out string? budgetProblem))
                {
                    _graphTraversalStopped = true;
                    if (budgetProblem is not null)
                        ReportGraphProblem(budgetProblem);
                    break;
                }

                _values!.Add(PdfValue.Name(key));

                if (dictionary.TryGetRaw(key, out PdfValue entry))
                    Visit(entry, depth + 1);
            }
        }

        void ReportGraphProblem(string message)
        {
            if (_graphProblemReported)
                return;

            _graphProblemReported = true;
            Report("6.1", message);
        }
    }

    /// <summary>
    /// Every dictionary contained in a live indirect object, plus the reachable dictionaries.
    /// File-scope rules use this without treating an unused resource as page semantics.
    /// </summary>
    internal IReadOnlyList<PdfDictionary> FileDictionaries
    {
        get
        {
            FlattenFileScope();
            return _fileDictionaries!;
        }
    }

    /// <summary>Every stream contained in a live indirect object, plus reachable streams.</summary>
    internal IReadOnlyList<PdfStream> FileStreams
    {
        get
        {
            FlattenFileScope();
            return _fileStreams!;
        }
    }

    /// <summary>Every scalar and container value contained in the file's live objects.</summary>
    internal IReadOnlyList<PdfValue> FileValues
    {
        get
        {
            FlattenFileScope();
            return _fileValues!;
        }
    }

    /// <summary>The numbers of every indirect object that a save preserves.</summary>
    internal IReadOnlyCollection<int> FileObjectNumbers
    {
        get
        {
            FlattenFileScope();
            return _fileObjects!;
        }
    }

    private void FlattenFileScope()
    {
        if (_fileDictionaries is not null)
            return;

        Flatten();
        _fileDictionaries = [.. _dictionaries!];
        _fileStreams = [.. _streams!];
        _fileValues = [.. _values!];

        // Snapshot before resolving. A tolerant reader may discover object-stream members while
        // loading an object, and mutating its xref map during enumeration must not invalidate this
        // file-scope pass.
        PdfObjectId[] ids = Document.IndirectObjectIds.Distinct().ToArray();
        _fileObjects = [.. ids.Select(static id => id.Number)];

        var seenDictionaries = new HashSet<PdfDictionary>(
            _dictionaries!,
            ReferenceEqualityComparer.Instance);
        var seenStreams = new HashSet<PdfStream>(
            _streams!,
            ReferenceEqualityComparer.Instance);
        var seenArrays = new HashSet<PdfArray>(ReferenceEqualityComparer.Instance);
        foreach (PdfValue value in _values!)
        {
            if (value.AsArray is { } array)
                seenArrays.Add(array);
        }

        bool stopped = false;
        for (int i = 0; i < ids.Length && !stopped; i++)
        {
            if (!_objectIds!.Contains(ids[i]))
                Visit(Document.GetObject(ids[i]), 0, indirectObject: true);
        }

        void Visit(PdfValue value, int depth, bool indirectObject = false)
        {
            if (stopped)
                return;

            if (depth > MaxDepth)
            {
                ReportGraphProblem(
                    $"File-scope object validation exceeded {MaxDepth} levels, so conformance cannot be established.");
                return;
            }

            if (!Validation.TryConsumeGraphValue(EmbeddedPath, out string? budgetProblem))
            {
                stopped = true;
                if (budgetProblem is not null)
                    ReportGraphProblem(budgetProblem);
                return;
            }

            _fileValues!.Add(value);
            switch (value.Kind)
            {
                case PdfValueKind.Reference:
                    // Every live indirect object is visited as its own root. Following references
                    // here would let an undefined or wrong-generation reference mark the real
                    // object number as seen before that root is checked.
                    break;

                case PdfValueKind.Array:
                    PdfArray array = value.AsArray!;
                    if (!seenArrays.Add(array))
                        break;

                    for (int i = 0; i < array.Count && !stopped; i++)
                        Visit(array[i], depth + 1);
                    break;

                case PdfValueKind.Stream:
                    PdfStream stream = value.AsStream!;
                    (indirectObject ? _indirectDictionaries! : _directDictionaries!).Add(stream.Dictionary);
                    if (seenStreams.Add(stream))
                        _fileStreams!.Add(stream);
                    if (seenDictionaries.Add(stream.Dictionary))
                    {
                        _fileDictionaries!.Add(stream.Dictionary);
                        VisitEntries(stream.Dictionary, depth);
                    }
                    break;

                case PdfValueKind.Dictionary:
                    PdfDictionary dictionary = value.AsDictionary!;
                    (indirectObject ? _indirectDictionaries! : _directDictionaries!).Add(dictionary);
                    if (seenDictionaries.Add(dictionary))
                    {
                        _fileDictionaries!.Add(dictionary);
                        VisitEntries(dictionary, depth);
                    }
                    break;
            }
        }

        void VisitEntries(PdfDictionary dictionary, int depth)
        {
            for (int i = 0; i < dictionary.Count && !stopped; i++)
            {
                PdfName key = dictionary.Keys[i];
                if (!Validation.TryConsumeGraphValue(EmbeddedPath, out string? budgetProblem))
                {
                    stopped = true;
                    if (budgetProblem is not null)
                        ReportGraphProblem(budgetProblem);
                    break;
                }

                _fileValues!.Add(PdfValue.Name(key));
                if (dictionary.TryGetRaw(key, out PdfValue entry))
                    Visit(entry, depth + 1);
            }
        }

        void ReportGraphProblem(string message)
        {
            if (_graphProblemReported)
                return;

            _graphProblemReported = true;
            Report("6.1", message);
        }
    }

    /// <summary>
    /// Whether a reachable dictionary is represented only as an indirect object. A dictionary
    /// embedded directly at any reachable location does not satisfy an indirect-object rule.
    /// </summary>
    internal bool IsIndirectOnly(PdfDictionary dictionary)
    {
        FlattenFileScope();
        return _indirectDictionaries!.Contains(dictionary) && !_directDictionaries!.Contains(dictionary);
    }
}
