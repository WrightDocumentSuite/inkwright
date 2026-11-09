using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.IO;

namespace Inkwright.Redaction;

internal static partial class PdfRedactor
{
    /// <summary>
    /// The entries that restate content as text (ISO 32000-2 §14.9): replacement text, alternate
    /// descriptions and abbreviation expansions. Removing a glyph leaves these behind unless they
    /// are removed with it, and they can no longer be trimmed to what survives because nothing maps
    /// their characters to individual glyphs.
    /// </summary>
    private static readonly PdfName[] SemanticTextKeys = [PdfNames.ActualText, PdfNames.Alt, PdfName.Get("E")];

    private static int CountSemanticEntries(PdfDictionary dictionary)
    {
        int count = 0;
        foreach (PdfName key in SemanticTextKeys)
        {
            if (dictionary.ContainsKey(key))
                count++;
        }

        return count;
    }

    /// <summary>One open <c>BMC</c>/<c>BDC</c> sequence and where its operator landed in the output.</summary>
    private sealed class MarkedSequence(
        long operatorStart,
        long operatorEnd,
        PdfName? tag,
        PdfName? resourceName,
        PdfDictionary? properties)
    {
        internal long OperatorStart => operatorStart;

        internal long OperatorEnd => operatorEnd;

        internal PdfName? Tag => tag;

        /// <summary>The <c>/Properties</c> name, when the list is not written inline.</summary>
        internal PdfName? ResourceName => resourceName;

        internal PdfDictionary? Properties => properties;

        internal bool Redacted { get; set; }
    }

    /// <summary>A replacement for an already written operator, applied once the stream is complete.</summary>
    private readonly record struct Splice(long Start, long End, byte[] Replacement);

    /// <summary>The state of one content stream while it is being rewritten.</summary>
    private sealed class StreamContext(
        PdfDictionary resources,
        PdfObjectId resourcesOwner,
        int depth,
        long structParents,
        Utf8Writer writer,
        PdfObjectSerializer serializer)
    {
        internal PdfDictionary Resources => resources;

        /// <summary>The indirect object an edit to <see cref="Resources"/> is saved under.</summary>
        internal PdfObjectId ResourcesOwner => resourcesOwner;

        internal int Depth => depth;

        /// <summary>The key of this stream's marked content in the parent tree, or -1.</summary>
        internal long StructParents => structParents;

        internal Utf8Writer Writer => writer;

        internal PdfObjectSerializer Serializer => serializer;

        internal List<MarkedSequence> OpenSequences { get; } = [];

        internal List<Splice> Splices { get; } = [];

        internal byte[] ApplySplices(byte[] output)
        {
            if (Splices.Count == 0)
                return output;

            Splices.Sort((left, right) => left.Start.CompareTo(right.Start));

            using var result = new MemoryStream(output.Length);
            int position = 0;
            foreach (Splice splice in Splices)
            {
                result.Write(output, position, (int)splice.Start - position);
                result.Write(splice.Replacement);
                position = (int)splice.End;
            }

            result.Write(output, position, output.Length - position);
            return result.ToArray();
        }
    }

    private sealed partial class StreamRewriter
    {
        private readonly List<(long StructParents, int MarkedContentId)> _redactedContent = [];
        private readonly List<long> _redactedObjects = [];

        internal int SemanticEntriesRemoved { get; private set; }

        private static void BeginMarkedSequence(
            PdfOperator op,
            ReadOnlySpan<PdfValue> operands,
            StreamContext context,
            string unknownName)
        {
            PdfValue operand = op == PdfOperator.BeginMarkedContentProperties && operands.Length >= 2
                ? operands[1]
                : PdfValue.Null;
            PdfName? resourceName = operand.AsName;
            PdfDictionary? properties = resourceName is not null
                ? context.Resources.GetDictionary(PdfNames.Properties)?.GetDictionary(resourceName)
                : operand.AsDictionary;

            long start = context.Writer.Length;
            WriteOperation(context.Writer, context.Serializer, op, operands, unknownName);

            context.OpenSequences.Add(new MarkedSequence(
                start,
                context.Writer.Length,
                operands.Length >= 1 ? operands[0].AsName : null,
                resourceName,
                properties));
        }

        private void EndMarkedSequence(StreamContext context)
        {
            if (context.OpenSequences.Count == 0)
                return;

            MarkedSequence sequence = context.OpenSequences[^1];
            context.OpenSequences.RemoveAt(context.OpenSequences.Count - 1);
            FinishSequence(sequence, context);
        }

        /// <summary>A stream may end with sequences still open; they are held to the same rule.</summary>
        private void FinishOpenSequences(StreamContext context)
        {
            for (int i = context.OpenSequences.Count - 1; i >= 0; i--)
                FinishSequence(context.OpenSequences[i], context);

            context.OpenSequences.Clear();
        }

        private void FinishSequence(MarkedSequence sequence, StreamContext context)
        {
            if (!sequence.Redacted || sequence.Properties is not { } properties)
                return;

            PdfValue id = properties.Get(PdfNames.MCID);
            if (context.StructParents >= 0 && id.Kind == PdfValueKind.Integer && id.AsInteger is >= 0 and <= int.MaxValue)
                _redactedContent.Add((context.StructParents, (int)id.AsInteger));

            int semanticEntries = CountSemanticEntries(properties);
            if (semanticEntries == 0)
                return;

            SemanticEntriesRemoved += semanticEntries;
            context.Splices.Add(new Splice(
                sequence.OperatorStart,
                sequence.OperatorEnd,
                BuildScrubbedOperator(sequence, properties, context)));
        }

        /// <summary>
        /// Writes the sequence's opening operator without its text alternatives. A list held in
        /// <c>/Properties</c> may be shared with content that was not selected, so it is replaced by
        /// a new entry rather than edited; the old entry goes only if nothing else still uses it.
        /// </summary>
        private byte[] BuildScrubbedOperator(MarkedSequence sequence, PdfDictionary properties, StreamContext context)
        {
            PdfDictionary scrubbed = properties.Clone();
            foreach (PdfName key in SemanticTextKeys)
                scrubbed.Remove(key);

            using var buffer = new MemoryStream();
            using var writer = new Utf8Writer(buffer);
            var serializer = new PdfObjectSerializer(writer);
            PdfValue tag = PdfValue.Name(sequence.Tag ?? PdfName.Get("Span"));
            PdfDictionary? propertyLists = sequence.ResourceName is not null
                ? context.Resources.GetDictionary(PdfNames.Properties)
                : null;

            if (scrubbed.Count == 0)
            {
                WriteOperation(writer, serializer, PdfOperator.BeginMarkedContent, [tag], "BMC");
            }
            else if (propertyLists is not null)
            {
                PdfName newName = UniqueResourceName(propertyLists, "RdP");
                propertyLists.Set(newName, PdfValue.Dictionary(scrubbed));
                document.MarkChanged(CategoryOwner(context, PdfNames.Properties));
                WriteOperation(writer, serializer, PdfOperator.BeginMarkedContentProperties, [tag, PdfValue.Name(newName)], "BDC");
            }
            else
            {
                WriteOperation(writer, serializer, PdfOperator.BeginMarkedContentProperties, [tag, PdfValue.Dictionary(scrubbed)], "BDC");
            }

            if (propertyLists is not null)
                _staleEntries.Add(new ResourceEntry(propertyLists, sequence.ResourceName!, CategoryOwner(context, PdfNames.Properties)));

            writer.Flush();
            return buffer.ToArray();
        }

        /// <summary>
        /// Removes the text alternatives from every structure element that owned redacted content.
        /// Ancestors are included because an <c>ActualText</c> on a parent element restates the
        /// content of everything beneath it.
        /// </summary>
        private void ScrubStructure(PdfPage page)
        {
            if ((_redactedContent.Count == 0 && _redactedObjects.Count == 0) ||
                document.Catalog.GetDictionary(PdfNames.StructTreeRoot) is not { } root)
            {
                return;
            }

            Dictionary<long, PdfValue> parents = ReadParentTree(root);
            HashSet<int> unresolved = [];

            foreach ((long key, int markedContentId) in _redactedContent)
            {
                if (parents.TryGetValue(key, out PdfValue entry) &&
                    document.Resolve(entry).AsArray is { } owners &&
                    markedContentId < owners.Count &&
                    !document.Resolve(owners[markedContentId]).IsNull)
                {
                    ScrubElementChain(owners[markedContentId]);
                }
                else
                {
                    unresolved.Add(markedContentId);
                }
            }

            foreach (long key in _redactedObjects)
            {
                if (parents.TryGetValue(key, out PdfValue element))
                    ScrubElementChain(element);
            }

            if (unresolved.Count > 0)
                ScrubUnindexedContent(page, unresolved);
        }

        /// <summary>
        /// The parent tree is how an MCID leads to its element, but a damaged file can omit it, so
        /// anything it could not place is looked for through the elements' own claims.
        /// </summary>
        private void ScrubUnindexedContent(PdfPage page, HashSet<int> markedContentIds)
        {
            var pending = new Stack<Tagging.PdfStructureNode>(document.Structure);
            while (pending.TryPop(out Tagging.PdfStructureNode? node))
            {
                foreach (Tagging.PdfContentReference reference in node.Content)
                {
                    if (reference.Page == page.Id && markedContentIds.Contains(reference.MarkedContentId))
                    {
                        ScrubElementChain(node.Id.IsValid
                            ? PdfValue.Reference(node.Id)
                            : PdfValue.Dictionary(node.Dictionary));
                        break;
                    }
                }

                foreach (Tagging.PdfStructureNode child in node.Children)
                    pending.Push(child);
            }
        }

        private void ScrubElementChain(PdfValue start)
        {
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            PdfValue current = start;

            while (document.Resolve(current).AsDictionary is { } element && seen.Add(element))
            {
                if (element.GetName(PdfNames.Type) == PdfNames.StructTreeRoot)
                    return;

                int removed = 0;
                foreach (PdfName key in SemanticTextKeys)
                {
                    if (element.Remove(key))
                        removed++;
                }

                if (removed > 0)
                {
                    SemanticEntriesRemoved += removed;
                    if (current.IsReference)
                        document.MarkChanged(current.AsReference);
                }

                if (!element.TryGetRaw(PdfNames.P, out current))
                    return;
            }
        }

        private Dictionary<long, PdfValue> ReadParentTree(PdfDictionary root)
        {
            var entries = new Dictionary<long, PdfValue>();
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<PdfValue>();

            if (root.TryGetRaw(PdfNames.ParentTree, out PdfValue tree))
                pending.Push(tree);

            while (pending.TryPop(out PdfValue current))
            {
                if (document.Resolve(current).AsDictionary is not { } node || !seen.Add(node))
                    continue;

                if (node.GetArray(PdfNames.Nums) is { } numbers)
                {
                    for (int i = 0; i + 1 < numbers.Count; i += 2)
                    {
                        PdfValue key = numbers.Get(i);
                        if (key.Kind == PdfValueKind.Integer)
                            entries.TryAdd(key.AsInteger, numbers[i + 1]);
                    }
                }

                if (node.GetArray(PdfNames.Kids) is { } kids)
                {
                    for (int i = 0; i < kids.Count; i++)
                        pending.Push(kids[i]);
                }
            }

            return entries;
        }
    }
}
