using Inkwright.Cos;
using Inkwright.Filters;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    private const int MaxEmbeddedFileTreeDepth = 64;
    private const int MaxEmbeddedFileTreeNodes = 4_096;

    private readonly record struct NameTreeRange(PdfValue Minimum, PdfValue Maximum);
    internal readonly record struct EmbeddedFilePayload(string Location, PdfStream Stream);

    private static readonly PdfName[] EmbeddedFileKeys =
    [
        PdfNames.F,
        PdfNames.UF,
        PdfName.Get("DOS"),
        PdfName.Get("Mac"),
        PdfName.Get("Unix"),
    ];

    /// <summary>Attachment and presentation clauses of all four PDF/A parts.</summary>
    internal static void CheckAttachments(PdfAContext context)
    {
        CheckEmbeddedFiles(context);
        CheckOptionalContent(context);
        CheckPresentations(context);
        CheckRequirements(context);
    }

    private static void CheckEmbeddedFiles(PdfAContext context)
    {
        string clause = context.Clause("6.1.11", "6.8", "6.9");
        bool traversalProblemReported = false;
        bool traversalStopped = false;
        PdfDictionary? nameDictionary = null;

        if (context.Document.Catalog.ContainsKey(PdfNames.Names))
        {
            PdfValue namesValue = context.Document.Catalog.Get(PdfNames.Names);
            if (namesValue.Kind == PdfValueKind.Dictionary)
            {
                nameDictionary = namesValue.AsDictionary;
            }
            else
            {
                ReportMalformedEmbeddedFiles(
                    context,
                    clause,
                    ref traversalProblemReported,
                    "the catalog Names entry is not a dictionary");
            }
        }

        bool hasEmbeddedFilesKey = nameDictionary?.ContainsKey(PdfNames.EmbeddedFiles) == true;
        PdfValue nameTreeValue = hasEmbeddedFilesKey
            ? nameDictionary!.Get(PdfNames.EmbeddedFiles)
            : PdfValue.Null;
        PdfDictionary? nameTree = nameTreeValue.Kind == PdfValueKind.Dictionary
            ? nameTreeValue.AsDictionary
            : null;
        var nameTreeSpecifications = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        if (hasEmbeddedFilesKey && nameTree is null)
        {
            ReportMalformedEmbeddedFiles(
                context,
                clause,
                ref traversalProblemReported,
                "the catalog EmbeddedFiles entry is not a name-tree dictionary");
        }

        if (nameTree is not null)
        {
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            int visitedNodes = 0;
            _ = CollectFileSpecifications(
                context,
                nameTree,
                nameTreeSpecifications,
                visited,
                depth: 0,
                ref visitedNodes,
                ref traversalProblemReported,
                ref traversalStopped,
                clause);
        }

        var allSpecifications = new HashSet<PdfDictionary>(
            nameTreeSpecifications,
            ReferenceEqualityComparer.Instance);

        // Parts 1 to 3 govern embedded file specifications rooted at every live xref object, even
        // when they are not reachable from the catalog. Part 4 deliberately does not: its §6.9
        // requirements are conditional on entries in the catalog's EmbeddedFiles name tree, while
        // an AF-only schema or data file remains legal in base A4.
        if (context.Part != 4)
        {
            foreach (PdfDictionary dictionary in context.FileDictionaries)
            {
                if ((!dictionary.ContainsKey(PdfNames.EF) &&
                     !dictionary.ContainsKey(PdfName.Get("RF"))) ||
                    allSpecifications.Contains(dictionary))
                    continue;

                if (allSpecifications.Count >= context.Validation.AttachmentCandidateLimit)
                {
                    ReportAttachmentTraversalLimit(
                        context,
                        clause,
                        ref traversalProblemReported,
                        $"more than {context.Validation.AttachmentCandidateLimit} file specifications");
                    break;
                }

                allSpecifications.Add(dictionary);
            }
        }

        // Annex A.2 requires the key itself. An AF array elsewhere does not make a file PDF/A-4f,
        // while a branched name tree remains valid even though its root has no Names array.
        if (!hasEmbeddedFilesKey && context.Conformance == PdfAConformance.A4F)
        {
            context.Report(
                "A.2",
                "The document claims PDF/A-4f but its catalog name dictionary has no EmbeddedFiles key.");
        }

        if (context.Part == 1)
        {
            if (hasEmbeddedFilesKey || allSpecifications.Count > 0)
            {
                context.Report(
                    "6.1.11",
                    "The document carries embedded files. Part 1 forbids them outright; a document that " +
                    "needs an attachment needs PDF/A-3.");
            }

            return;
        }

        IEnumerable<PdfDictionary> specifications = context.Part == 4
            ? nameTreeSpecifications
            : allSpecifications;

        foreach (PdfDictionary specification in specifications)
            CheckFileSpecification(context, specification);
    }

    private static NameTreeRange? CollectFileSpecifications(
        PdfAContext context,
        PdfDictionary node,
        HashSet<PdfDictionary> specifications,
        HashSet<PdfDictionary> visited,
        int depth,
        ref int visitedNodes,
        ref bool traversalProblemReported,
        ref bool traversalStopped,
        string clause)
    {
        if (traversalStopped)
            return null;

        if (depth > MaxEmbeddedFileTreeDepth)
        {
            ReportAttachmentTraversalLimit(
                context,
                clause,
                ref traversalProblemReported,
                $"a name-tree depth greater than {MaxEmbeddedFileTreeDepth}");
            return null;
        }

        if (!visited.Add(node))
        {
            ReportMalformedEmbeddedFiles(
                context,
                clause,
                ref traversalProblemReported,
                "the name tree contains a cycle or reuses the same node in more than one branch");
            return null;
        }

        bool hasNamesEntry = node.ContainsKey(PdfNames.Names);
        bool hasKidsEntry = node.ContainsKey(PdfName.Get("Kids"));
        if (hasNamesEntry == hasKidsEntry)
        {
            ReportMalformedEmbeddedFiles(
                context,
                clause,
                ref traversalProblemReported,
                "a name-tree node does not contain exactly one of Names or Kids");
        }

        visitedNodes++;
        if (visitedNodes > MaxEmbeddedFileTreeNodes)
        {
            traversalStopped = true;
            ReportAttachmentTraversalLimit(
                context,
                clause,
                ref traversalProblemReported,
                $"more than {MaxEmbeddedFileTreeNodes} name-tree nodes or entries");
            return null;
        }

        PdfArray? names = null;
        if (node.ContainsKey(PdfNames.Names))
        {
            PdfValue namesValue = node.Get(PdfNames.Names);
            if (namesValue.Kind != PdfValueKind.Array)
            {
                ReportMalformedEmbeddedFiles(
                    context,
                    clause,
                    ref traversalProblemReported,
                    "a name-tree Names entry is not an array");
            }
            else
            {
                names = namesValue.AsArray;
            }
        }

        NameTreeRange? namesRange = null;
        if (names is not null)
        {
            if ((names.Count & 1) != 0)
            {
                ReportMalformedEmbeddedFiles(
                    context,
                    clause,
                    ref traversalProblemReported,
                    "a name-tree Names array has an unmatched key without a value");
            }

            PdfValue firstKey = default;
            PdfValue previousKey = default;
            bool hasKey = false;
            for (int i = 1; i < names.Count; i += 2)
            {
                if (++visitedNodes > MaxEmbeddedFileTreeNodes)
                {
                    traversalStopped = true;
                    ReportAttachmentTraversalLimit(
                        context,
                        clause,
                        ref traversalProblemReported,
                        $"more than {MaxEmbeddedFileTreeNodes} name-tree nodes or entries");
                    return null;
                }

                PdfValue key = names.Get(i - 1);
                PdfValue value = names.Get(i);
                if (key.Kind != PdfValueKind.String)
                {
                    ReportMalformedEmbeddedFiles(
                        context,
                        clause,
                        ref traversalProblemReported,
                        "a name-tree key is not a string");
                    continue;
                }

                if (hasKey && CompareNameTreeKeys(previousKey, key) >= 0)
                {
                    ReportMalformedEmbeddedFiles(
                        context,
                        clause,
                        ref traversalProblemReported,
                        "the name-tree Names keys are not strictly sorted and unique");
                }

                if (!hasKey)
                    firstKey = key;
                previousKey = key;
                hasKey = true;

                if (value.Kind != PdfValueKind.Dictionary)
                {
                    ReportMalformedEmbeddedFiles(
                        context,
                        clause,
                        ref traversalProblemReported,
                        "a name-tree value is not a file-specification dictionary");
                    continue;
                }

                PdfDictionary specification = value.AsDictionary!;
                if (specifications.Contains(specification))
                    continue;

                if (specifications.Count >= context.Validation.AttachmentCandidateLimit)
                {
                    traversalStopped = true;
                    ReportAttachmentTraversalLimit(
                        context,
                        clause,
                        ref traversalProblemReported,
                        $"more than {context.Validation.AttachmentCandidateLimit} file specifications");
                    return null;
                }

                specifications.Add(specification);
            }

            if (hasKey)
                namesRange = new NameTreeRange(firstKey, previousKey);
        }

        PdfName kidsName = PdfName.Get("Kids");
        if (!node.ContainsKey(kidsName))
        {
            ValidateNameTreeLimits(
                context,
                node,
                depth,
                namesRange,
                clause,
                ref traversalProblemReported);
            return namesRange;
        }

        PdfValue kidsValue = node.Get(kidsName);
        if (kidsValue.Kind != PdfValueKind.Array)
        {
            ReportMalformedEmbeddedFiles(
                context,
                clause,
                ref traversalProblemReported,
                "a name-tree Kids entry is not an array");
            return null;
        }

        PdfArray kids = kidsValue.AsArray!;
        NameTreeRange? childrenRange = null;
        NameTreeRange? previousChildRange = null;

        for (int i = 0; i < kids.Count; i++)
        {
            if (traversalStopped)
                break;

            if (++visitedNodes > MaxEmbeddedFileTreeNodes)
            {
                traversalStopped = true;
                ReportAttachmentTraversalLimit(
                    context,
                    clause,
                    ref traversalProblemReported,
                    $"more than {MaxEmbeddedFileTreeNodes} name-tree nodes or entries");
                break;
            }

            PdfValue rawChildValue = kids[i];
            if (!rawChildValue.IsReference)
            {
                ReportMalformedEmbeddedFiles(
                    context,
                    clause,
                    ref traversalProblemReported,
                    "a name-tree Kids entry is not an indirect reference");
            }

            PdfValue childValue = kids.Get(i);
            if (childValue.Kind != PdfValueKind.Dictionary)
            {
                ReportMalformedEmbeddedFiles(
                    context,
                    clause,
                    ref traversalProblemReported,
                    "a name-tree Kids entry is not a dictionary");
                continue;
            }

            if (childValue.AsDictionary is { } child)
            {
                NameTreeRange? childRange = CollectFileSpecifications(
                    context,
                    child,
                    specifications,
                    visited,
                    depth + 1,
                    ref visitedNodes,
                    ref traversalProblemReported,
                    ref traversalStopped,
                    clause);

                if (childRange is not { } currentRange)
                    continue;

                if (previousChildRange is { } previousRange &&
                    CompareNameTreeKeys(previousRange.Maximum, currentRange.Minimum) >= 0)
                {
                    ReportMalformedEmbeddedFiles(
                        context,
                        clause,
                        ref traversalProblemReported,
                        "name-tree child ranges are not strictly ordered and non-overlapping");
                }

                childrenRange = childrenRange is { } aggregate
                    ? new NameTreeRange(aggregate.Minimum, currentRange.Maximum)
                    : currentRange;
                previousChildRange = currentRange;
            }
        }

        NameTreeRange? actualRange = hasNamesEntry ? namesRange : childrenRange;
        ValidateNameTreeLimits(
            context,
            node,
            depth,
            actualRange,
            clause,
            ref traversalProblemReported);
        return actualRange;
    }

    private static void ValidateNameTreeLimits(
        PdfAContext context,
        PdfDictionary node,
        int depth,
        NameTreeRange? actualRange,
        string clause,
        ref bool traversalProblemReported)
    {
        if (!node.TryGetRaw(PdfNames.Limits, out PdfValue limitsValue))
        {
            if (depth > 0)
            {
                ReportMalformedEmbeddedFiles(
                    context,
                    clause,
                    ref traversalProblemReported,
                    "a non-root name-tree node has no direct Limits array");
            }

            return;
        }

        if (limitsValue.Kind != PdfValueKind.Array || limitsValue.AsArray!.Count != 2)
        {
            ReportMalformedEmbeddedFiles(
                context,
                clause,
                ref traversalProblemReported,
                "a name-tree Limits entry is not a direct two-string array");
            return;
        }

        PdfArray limits = limitsValue.AsArray;
        PdfValue minimum = limits[0];
        PdfValue maximum = limits[1];
        if (minimum.Kind != PdfValueKind.String || maximum.Kind != PdfValueKind.String)
        {
            ReportMalformedEmbeddedFiles(
                context,
                clause,
                ref traversalProblemReported,
                "a name-tree Limits entry does not contain two direct strings");
            return;
        }

        if (actualRange is not { } actual ||
            CompareNameTreeKeys(minimum, actual.Minimum) != 0 ||
            CompareNameTreeKeys(maximum, actual.Maximum) != 0)
        {
            ReportMalformedEmbeddedFiles(
                context,
                clause,
                ref traversalProblemReported,
                "a name-tree Limits range does not match its actual descendant key range");
        }
    }

    private static int CompareNameTreeKeys(PdfValue left, PdfValue right) =>
        left.AsStringBytes.SequenceCompareTo(right.AsStringBytes);

    private static void ReportAttachmentTraversalLimit(
        PdfAContext context,
        string clause,
        ref bool alreadyReported,
        string limit)
    {
        if (alreadyReported)
            return;

        alreadyReported = true;
        context.Report(
            clause,
            $"EmbeddedFiles discovery stopped at {limit}; attachment conformance cannot be established.");
    }

    private static void ReportMalformedEmbeddedFiles(
        PdfAContext context,
        string clause,
        ref bool alreadyReported,
        string detail)
    {
        if (alreadyReported)
            return;

        alreadyReported = true;
        context.Report(
            clause,
            $"EmbeddedFiles discovery found malformed data: {detail}; attachment conformance cannot be established.");
    }

    /// <summary>Checks both the file specification and, where required, the embedded PDF itself.</summary>
    private static void CheckFileSpecification(PdfAContext context, PdfDictionary specification)
    {
        string name = specification.GetTextString(PdfName.Get("UF")) ??
                      specification.GetTextString(PdfName.Get("F")) ?? "an attachment";
        string clause = context.Clause("6.8", "6.8", "6.9");

        if (!context.IsIndirectOnly(specification))
        {
            context.Report(
                clause,
                $"The file specification for '{name}' is not an indirect object, as required for a " +
                "dictionary that carries an EF entry.");
        }

        if (specification.GetName(PdfNames.Type) != PdfNames.Filespec)
        {
            context.Report(
                clause,
                $"The file specification for '{name}' has an EF entry but does not have the exact " +
                "Type Filespec.");
        }

        if (!specification.ContainsKey(PdfNames.F) || !specification.ContainsKey(PdfNames.UF))
        {
            context.Report(clause, $"The file specification for '{name}' needs both the F and UF entries.");
        }
        else
        {
            if (specification.Get(PdfNames.F).Kind != PdfValueKind.String)
                context.Report(clause, $"The F entry of the file specification for '{name}' is not a string.");
            if (specification.Get(PdfNames.UF).Kind != PdfValueKind.String)
                context.Report(clause, $"The UF entry of the file specification for '{name}' is not a string.");
        }

        IReadOnlyList<EmbeddedFilePayload> payloads = EnumerateEmbeddedPayloads(
            specification,
            detail => context.Report(
                clause,
                $"The attachment '{name}' has {detail}; attachment conformance cannot be established."));

        // Parts 3 and 4 ask every attachment for a relationship that says why it is there,
        // alongside both names. Which files are admitted then depends on the level.
        if (context.Part is 3 or 4)
            CheckAssociation(context, specification, name, payloads);

        if (payloads.Count == 0)
        {
            context.Report(
                clause,
                $"The attachment '{name}' has no recognized embedded-file stream under EF or RF, so its " +
                "PDF/A conformance cannot be established.");
            return;
        }

        if (context.Part == 3 || context.Conformance.AllowsAnyEmbeddedFile)
        {
            foreach (EmbeddedFilePayload payload in payloads)
            {
                string payloadName = payloads.Count == 1 ? name : $"{name} [{payload.Location}]";
                PdfAValidator.ValidateArbitraryEmbeddedFile(context, payload.Stream, payloadName);
            }

            return;
        }

        foreach (EmbeddedFilePayload payload in payloads)
        {
            PdfStream embeddedFile = payload.Stream;
            string payloadName = payloads.Count == 1 ? name : $"{name} [{payload.Location}]";
            long? declaredSize = DeclaredEmbeddedFileSize(embeddedFile);
            using PdfAValidationReservation? reservation = PdfAValidator.BeginEmbeddedPdf(
                context,
                payloadName,
                clause,
                declaredSize);
            if (reservation is null)
                continue;

            int decodeLimit = reservation.DecodedByteLimit;
            PdfBoundedDecodeResult decoded;
            if (embeddedFile.TryGetPolicyDecoded(out decoded))
            {
                // A pathological graph may alias the same stream as Metadata (or another internal
                // role) and EF. Its reader policy has already charged and strictly decoded it.
            }
            else
            {
                if (!reservation.TryChargeEncodedInput(embeddedFile.RawLength, out string? encodedProblem))
                {
                    context.Report(clause, encodedProblem!);
                    continue;
                }

                try
                {
                    decoded = embeddedFile.DecodeBounded(decodeLimit);
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or
                    OperationCanceledException or AccessViolationException))
                {
                    context.Report(
                        clause,
                        $"Embedded PDF '{reservation.Path}' cannot be decoded from its embedded-file stream: " +
                        exception.Message);
                    continue;
                }

                if (!reservation.TryChargeDecodeWork(decoded.ProducedBytes, out string? workProblem))
                {
                    context.Report(clause, workProblem!);
                    continue;
                }
            }

            if (decoded.Status == PdfBoundedDecodeStatus.LimitExceeded)
            {
                context.Report(clause, reservation.ExhaustByteBudget(decodeLimit));
                continue;
            }

            if (decoded.Status == PdfBoundedDecodeStatus.FilterChainLimit)
            {
                context.Report(
                    clause,
                    $"Embedded PDF '{reservation.Path}' has more than 64 stream filters, so bounded " +
                    "recursive validation cannot establish its conformance.");
                continue;
            }

            if (decoded.Status == PdfBoundedDecodeStatus.InvalidParameters)
            {
                string filter = decoded.TerminalFilter?.Value ?? "the stream";
                context.Report(
                    clause,
                    $"Embedded PDF '{reservation.Path}' has malformed DecodeParms for {filter}, so its bytes " +
                    "cannot be decoded and recursively validated.");
                continue;
            }

            if (decoded.Status == PdfBoundedDecodeStatus.InvalidData)
            {
                string filter = decoded.TerminalFilter?.Value ?? "the stream filter";
                context.Report(
                    clause,
                    $"Embedded PDF '{reservation.Path}' contains invalid data for {filter}, so its bytes " +
                    "cannot be decoded and recursively validated.");
                continue;
            }

            if (decoded.Status == PdfBoundedDecodeStatus.UndecodedFilter)
            {
                string filter = decoded.TerminalFilter?.Value ?? "a non-name or unsupported filter entry";
                context.Report(
                    clause,
                    $"Embedded PDF '{reservation.Path}' uses {filter} as a stream filter, so its bytes " +
                    "cannot be decoded and recursively validated.");
                continue;
            }

            PdfAValidator.ValidateEmbeddedPdf(context, decoded.Data, clause, reservation);
        }
    }

    internal static IReadOnlyList<EmbeddedFilePayload> EnumerateEmbeddedPayloads(
        PdfDictionary specification,
        Action<string>? reportMalformed)
    {
        var payloads = new List<EmbeddedFilePayload>();
        var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        PdfDictionary? embeddedFiles = null;

        if (specification.ContainsKey(PdfNames.EF))
        {
            PdfValue embeddedFilesValue = specification.Get(PdfNames.EF);
            if (embeddedFilesValue.Kind == PdfValueKind.Dictionary)
            {
                embeddedFiles = embeddedFilesValue.AsDictionary!;
                ReportUnknownKeys(embeddedFiles, "EF");
                foreach (PdfName key in EmbeddedFileKeys)
                {
                    if (!embeddedFiles.ContainsKey(key))
                        continue;

                    if (!specification.ContainsKey(key) ||
                        specification.Get(key).Kind != PdfValueKind.String)
                    {
                        reportMalformed?.Invoke(
                            $"EF/{key.Value} without a corresponding string entry in the file specification");
                    }

                    PdfValue value = embeddedFiles.Get(key);
                    if (value.Kind != PdfValueKind.Stream)
                    {
                        reportMalformed?.Invoke(
                            $"EF/{key.Value} that is not an embedded-file stream");
                        continue;
                    }

                    AddPayload($"EF/{key.Value}", value.AsStream!);
                }
            }
            else
            {
                reportMalformed?.Invoke("an EF entry that is not a dictionary");
            }
        }

        PdfName relatedFilesName = PdfName.Get("RF");
        if (!specification.ContainsKey(relatedFilesName))
            return payloads;

        PdfValue relatedFilesValue = specification.Get(relatedFilesName);
        if (relatedFilesValue.Kind != PdfValueKind.Dictionary)
        {
            reportMalformed?.Invoke("an RF entry that is not a dictionary");
            return payloads;
        }

        if (embeddedFiles is null)
            reportMalformed?.Invoke("an RF entry without the required EF dictionary");

        PdfDictionary relatedFiles = relatedFilesValue.AsDictionary!;
        ReportUnknownKeys(relatedFiles, "RF");
        foreach (PdfName key in EmbeddedFileKeys)
        {
            if (!relatedFiles.ContainsKey(key))
                continue;

            if (embeddedFiles?.ContainsKey(key) != true)
            {
                reportMalformed?.Invoke(
                    $"RF/{key.Value} without the same key in the EF dictionary");
            }

            PdfValue value = relatedFiles.Get(key);
            if (value.Kind != PdfValueKind.Array)
            {
                reportMalformed?.Invoke($"RF/{key.Value} that is not a related-files array");
                continue;
            }

            PdfArray pairs = value.AsArray!;
            if ((pairs.Count & 1) != 0)
            {
                reportMalformed?.Invoke(
                    $"RF/{key.Value} with an unmatched related-file name without a payload");
            }

            for (int i = 0; i + 1 < pairs.Count; i += 2)
            {
                PdfValue relatedName = pairs.Get(i);
                PdfValue payload = pairs.Get(i + 1);
                string pairLabel = relatedName.AsTextString ?? $"pair {i / 2 + 1}";
                if (relatedName.Kind != PdfValueKind.String)
                {
                    reportMalformed?.Invoke(
                        $"RF/{key.Value} pair {i / 2 + 1} whose name is not a string");
                }

                if (payload.Kind != PdfValueKind.Stream)
                {
                    reportMalformed?.Invoke(
                        $"RF/{key.Value} pair {i / 2 + 1} whose payload is not an embedded-file stream");
                    continue;
                }

                AddPayload($"RF/{key.Value}: {pairLabel}", payload.AsStream!);
            }
        }

        return payloads;

        void AddPayload(string location, PdfStream stream)
        {
            if (stream.Dictionary.ContainsKey(PdfNames.Type) &&
                stream.Dictionary.GetName(PdfNames.Type) != PdfNames.EmbeddedFile)
            {
                reportMalformed?.Invoke($"{location} with a Type other than EmbeddedFile");
            }

            if (seen.Add(stream))
                payloads.Add(new EmbeddedFilePayload(location, stream));
        }

        void ReportUnknownKeys(PdfDictionary dictionary, string dictionaryName)
        {
            foreach (PdfName key in dictionary.Keys)
            {
                if (!EmbeddedFileKeys.Contains(key))
                    reportMalformed?.Invoke($"an unrecognized {dictionaryName}/{key.Value} entry");
            }
        }
    }

    private static long? DeclaredEmbeddedFileSize(PdfStream stream)
    {
        PdfValue size = stream.Dictionary.GetDictionary(PdfNames.Params)?.Get(PdfNames.Size) ?? PdfValue.Null;
        return size.TryGetInteger(out long declaredSize) && declaredSize > 0 ? declaredSize : null;
    }

    /// <summary>
    /// Part 3 exists so that a document can carry a file of any kind, and it pays for that by
    /// requiring each one to say what it is and how it relates to the document. PDF 2.0 Application
    /// Note 002 spells the same requirements out for associated files generally.
    /// </summary>
    private static void CheckAssociation(
        PdfAContext context,
        PdfDictionary specification,
        string name,
        IReadOnlyList<EmbeddedFilePayload> payloads)
    {
        // The MIME/Subtype requirement comes from Part 3 Annex E.1. Part 4 clause 6.9 does not
        // repeat it, so applying the Part 3-only mandate there would create a false error.
        if (context.Part == 3)
        {
            if (payloads.Count == 0)
            {
                context.Report(
                    "E.1",
                    $"The embedded file '{name}' names no MIME type because it has no EF stream.");
            }
            else
            {
                foreach (EmbeddedFilePayload payload in payloads)
                {
                    PdfStream stream = payload.Stream;
                    if (stream.Dictionary.GetName(PdfNames.Subtype) is not null)
                        continue;

                    string payloadName = payloads.Count == 1 ? name : $"{name} [{payload.Location}]";
                    context.Report(
                        "E.1",
                        $"The embedded file '{payloadName}' names no MIME type in the Subtype entry of its stream.");
                }
            }
        }

        string? relationship = specification.GetName(PdfNames.AFRelationship)?.Value;

        if (relationship is null)
        {
            // Part 3 defines the required key in Annex E.2, Table E.1. Part 4 moved the
            // corresponding normative requirement into its main attachment clause.
            string relationshipClause = context.Part == 3 ? "E.2" : "6.9";
            context.Report(relationshipClause, $"The file specification for '{name}' states no AFRelationship.");
        }
    }

    /// <summary>
    /// Clause 6.1.13 of part 1 forbids optional content outright. Later parts allow it as a way of
    /// keeping several variants in one file. Parts 2 and 3 forbid AS; part 4 permits but ignores it.
    /// </summary>
    private static void CheckOptionalContent(PdfAContext context)
    {
        if (context.Document.Catalog.GetDictionary(PdfName.Get("OCProperties")) is not { } properties)
            return;

        if (context.Part == 1)
        {
            context.Report("6.1.13", "The catalogue carries OCProperties; part 1 does not permit optional content.");
            return;
        }

        List<PdfDictionary> configurations = [];
        if (properties.GetDictionary(PdfName.Get("D")) is { } standard)
            configurations.Add(standard);

        if (properties.GetArray(PdfName.Get("Configs")) is { } configs)
        {
            for (int i = 0; i < configs.Count; i++)
            {
                if (configs.GetDictionary(i) is { } configuration)
                    configurations.Add(configuration);
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        PdfArray? groups = properties.GetArray(PdfName.Get("OCGs"));

        foreach (PdfDictionary configuration in configurations)
            CheckConfiguration(context, configuration, names, groups);
    }

    private static void CheckConfiguration(
        PdfAContext context, PdfDictionary configuration, HashSet<string> names, PdfArray? groups)
    {
        // Parts 2 and 3 state optional content in 6.9; part 4 moved the same rules to 6.10.
        string clause = context.Clause("6.9", "6.9", "6.10");

        if (configuration.GetTextString(PdfName.Get("Name")) is not { Length: > 0 } name)
        {
            context.Report(clause, "An optional content configuration has no Name, so its variant cannot be identified.");
        }
        else if (!names.Add(name))
        {
            context.Report(clause, $"Two optional content configurations are both named '{name}'.");
        }

        // Part 4 permits AS but requires processors to ignore it. Parts 2 and 3 forbid it.
        if (context.Part != 4 && configuration.ContainsKey(PdfName.Get("AS")))
            context.Report(clause, "An optional content configuration carries AS, which lets content switch itself on and off.");

        if (configuration.GetArray(PdfName.Get("Order")) is { } order &&
            groups is { Count: > 0 } &&
            !ListsEveryGroup(order, groups))
        {
            context.Report(clause, "The Order array does not list every optional content group in the file.");
        }
    }

    private static bool ListsEveryGroup(PdfArray order, PdfArray groups)
    {
        var identities = new List<(PdfValue Raw, PdfValue Resolved)>(groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            PdfValue raw = groups[i];
            PdfValue resolved = groups.Get(i);
            if (!identities.Any(group => group.Raw == raw || group.Resolved == resolved))
                identities.Add((raw, resolved));
        }

        var found = new HashSet<int>();
        var visited = new HashSet<PdfArray>(ReferenceEqualityComparer.Instance);
        CollectGroups(order, identities, found, visited, 0);
        return found.Count == identities.Count;
    }

    private static void CollectGroups(
        PdfArray order,
        IReadOnlyList<(PdfValue Raw, PdfValue Resolved)> groups,
        HashSet<int> found,
        HashSet<PdfArray> visited,
        int depth)
    {
        // Malformed PDFs can make indirect arrays recursive. Validation must return a problem, not
        // overflow the process stack; 64 levels are already far beyond a usable layer hierarchy.
        if (depth > 64 || !visited.Add(order))
            return;

        for (int i = 0; i < order.Count; i++)
        {
            PdfValue raw = order[i];
            PdfValue resolved = order.Get(i);
            int group = IndexOfGroup(groups, raw, resolved);
            if (group >= 0)
            {
                found.Add(group);
                continue;
            }

            // Strings are hierarchy labels, not groups. Arrays may nest further labels and OCGs.
            if (resolved.AsArray is { } nested)
                CollectGroups(nested, groups, found, visited, depth + 1);
        }
    }

    private static int IndexOfGroup(
        IReadOnlyList<(PdfValue Raw, PdfValue Resolved)> groups,
        PdfValue raw,
        PdfValue resolved)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Raw == raw || groups[i].Resolved == resolved)
                return i;
        }

        return -1;
    }

    /// <summary>Clause 6.10 of parts 2 and 3, clause 6.11 of part 4: alternate presentations.</summary>
    private static void CheckPresentations(PdfAContext context)
    {
        if (context.Part == 1)
            return;

        string clause = context.Clause("6.10", "6.10", "6.11");

        if (context.Document.Catalog.GetDictionary(PdfName.Get("Names"))?
                .ContainsKey(PdfName.Get("AlternatePresentations")) == true)
        {
            context.Report(clause, "The name dictionary carries AlternatePresentations, which PDF/A forbids.");
        }

        for (int i = 0; i < context.Document.Pages.Count; i++)
        {
            if (context.Document.Pages[i].Dictionary.ContainsKey(PdfName.Get("PresSteps")))
                context.Report(clause, $"Page {i + 1} carries PresSteps, which turns the page into a slide show.");
        }
    }

    /// <summary>Clause 6.11 of parts 2 and 3, clause 6.12 of part 4: document requirements.</summary>
    private static void CheckRequirements(PdfAContext context)
    {
        if (context.Part > 1 && context.Document.Catalog.ContainsKey(PdfName.Get("Requirements")))
        {
            context.Report(
                context.Clause("6.11", "6.11", "6.12"),
                "The catalogue carries Requirements, which asks the reader for features PDF/A does not know.");
        }
    }
}
