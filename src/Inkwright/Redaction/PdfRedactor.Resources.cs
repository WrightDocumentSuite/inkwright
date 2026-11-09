using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Diagnostics;

namespace Inkwright.Redaction;

internal static partial class PdfRedactor
{
    /// <summary>A resource entry the rewritten content stopped using.</summary>
    private readonly record struct ResourceEntry(PdfDictionary Category, PdfName Name, PdfObjectId Owner);

    /// <summary>A redacted copy of a Form XObject and the entry that now draws it.</summary>
    private sealed record FormReplacement(
        PdfDictionary XObjects,
        PdfName NewName,
        PdfObjectId Owner,
        PdfObjectId OriginalId,
        PdfObjectId CloneId,
        PdfStream RedactedForm);

    /// <summary>
    /// Which resource entries the document's content still uses. Resource dictionaries are routinely
    /// shared between pages and forms, so whether an entry can go is a question about every stream
    /// that resolves names through it. Only those streams are parsed; the rest of the document is
    /// walked through its dictionaries alone.
    /// </summary>
    private sealed class ResourceUsage
    {
        private readonly HashSet<(PdfDictionary Category, PdfName Name)> _used = [];
        private readonly HashSet<PdfDictionary> _unreadable = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every object some resource entry or appearance still points at.</summary>
        internal HashSet<PdfObjectId> ReferencedObjects { get; } = [];

        internal bool IsUsed(ResourceEntry entry) =>
            _unreadable.Contains(entry.Category) || _used.Contains((entry.Category, entry.Name));

        internal static ResourceUsage Collect(PdfDocument document, IReadOnlyCollection<ResourceEntry> entries)
        {
            var usage = new ResourceUsage();
            var categories = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (ResourceEntry entry in entries)
                categories.Add(entry.Category);

            var visited = new HashSet<(PdfStream, PdfDictionary)>();
            var forms = new Stack<(PdfStream Form, PdfDictionary Resources)>();

            foreach (PdfPage page in document.Pages.Items)
            {
                PdfDictionary? resources = page.Dictionary.GetDictionary(PdfNames.Resources);
                if (resources is not null)
                {
                    usage.Inspect(document, page.GetContent, resources, categories);
                    usage.QueueForms(document, resources, visited, forms);
                }

                usage.QueueAppearances(document, page, resources, visited, forms);
            }

            while (forms.TryPop(out (PdfStream Form, PdfDictionary Resources) item))
            {
                usage.Inspect(document, () => item.Form.DecodedSpan().ToArray(), item.Resources, categories);
                usage.QueueForms(document, item.Resources, visited, forms);
            }

            return usage;
        }

        private void Inspect(
            PdfDocument document,
            Func<byte[]> content,
            PdfDictionary resources,
            HashSet<PdfDictionary> categories)
        {
            PdfDictionary? xObjects = resources.GetDictionary(PdfNames.XObject);
            PdfDictionary? properties = resources.GetDictionary(PdfNames.Properties);
            bool relevantXObjects = xObjects is not null && categories.Contains(xObjects);
            bool relevantProperties = properties is not null && categories.Contains(properties);
            if (!relevantXObjects && !relevantProperties)
                return;

            ContentOperations operations;
            try
            {
                operations = ContentOperations.Parse(content(), document);
            }
            catch (Exception exception) when (exception is NotSupportedException or PdfFormatException or PdfResourceLimitException or InvalidDataException)
            {
                // A stream that cannot be read may use anything these dictionaries hold.
                if (relevantXObjects)
                    _unreadable.Add(xObjects!);
                if (relevantProperties)
                    _unreadable.Add(properties!);
                return;
            }

            for (int i = 0; i < operations.Count; i++)
            {
                ReadOnlySpan<PdfValue> operands = operations.GetOperands(i);
                switch (operations.GetOperator(i))
                {
                    case PdfOperator.DrawXObject when xObjects is not null && operands.Length >= 1 && operands[0].AsName is { } name:
                        _used.Add((xObjects, name));
                        break;

                    case PdfOperator.BeginMarkedContentProperties when properties is not null && operands.Length >= 2 && operands[1].AsName is { } name:
                        _used.Add((properties, name));
                        break;
                }
            }
        }

        private void QueueForms(
            PdfDocument document,
            PdfDictionary resources,
            HashSet<(PdfStream, PdfDictionary)> visited,
            Stack<(PdfStream, PdfDictionary)> forms)
        {
            if (resources.GetDictionary(PdfNames.XObject) is not { } xObjects)
                return;

            foreach (KeyValuePair<PdfName, PdfValue> entry in xObjects)
            {
                if (entry.Value.IsReference)
                    ReferencedObjects.Add(entry.Value.AsReference);

                Queue(document.Resolve(entry.Value).AsStream, resources, visited, forms);
            }
        }

        private void QueueAppearances(
            PdfDocument document,
            PdfPage page,
            PdfDictionary? pageResources,
            HashSet<(PdfStream, PdfDictionary)> visited,
            Stack<(PdfStream, PdfDictionary)> forms)
        {
            if (page.Dictionary.GetArray(PdfNames.Annots) is not { } annotations)
                return;

            for (int i = 0; i < annotations.Count; i++)
            {
                if (annotations.Get(i).AsDictionary?.GetDictionary(PdfNames.AP) is not { } appearances)
                    continue;

                foreach (KeyValuePair<PdfName, PdfValue> appearance in appearances)
                {
                    if (appearance.Value.IsReference)
                        ReferencedObjects.Add(appearance.Value.AsReference);

                    PdfValue resolved = document.Resolve(appearance.Value);
                    if (resolved.AsDictionary is { } states)
                    {
                        foreach (KeyValuePair<PdfName, PdfValue> state in states)
                        {
                            if (state.Value.IsReference)
                                ReferencedObjects.Add(state.Value.AsReference);

                            Queue(document.Resolve(state.Value).AsStream, pageResources, visited, forms);
                        }
                    }
                    else
                    {
                        Queue(resolved.AsStream, pageResources, visited, forms);
                    }
                }
            }
        }

        private static void Queue(
            PdfStream? stream,
            PdfDictionary? inheritedResources,
            HashSet<(PdfStream, PdfDictionary)> visited,
            Stack<(PdfStream, PdfDictionary)> forms)
        {
            if (stream is null || stream.Dictionary.GetName(PdfNames.Subtype) != PdfNames.Form)
                return;

            PdfDictionary? resources = stream.Dictionary.GetDictionary(PdfNames.Resources) ?? inheritedResources;
            if (resources is not null && visited.Add((stream, resources)))
                forms.Push((stream, resources));
        }
    }

    private sealed partial class StreamRewriter
    {
        private readonly List<ResourceEntry> _staleEntries = [];
        private readonly List<FormReplacement> _replacements = [];

        /// <summary>Finishes the edits that depend on the page already holding its new content.</summary>
        internal void Commit(PdfPage page)
        {
            ScrubStructure(page);
            PruneStaleEntries();
        }

        private static PdfName UniqueResourceName(PdfDictionary category, string prefix)
        {
            for (int number = 1; ; number++)
            {
                PdfName candidate = PdfName.Get($"{prefix}{number}");
                if (!category.ContainsKey(candidate))
                    return candidate;
            }
        }

        private static PdfObjectId CategoryOwner(StreamContext context, PdfName category) =>
            context.Resources.TryGetRaw(category, out PdfValue raw) && raw.IsReference
                ? raw.AsReference
                : context.ResourcesOwner;

        /// <summary>
        /// Drops the entries nothing draws any more. Removing a replaced form can orphan what only
        /// that form used, so this repeats until an entire pass removes nothing. A form nothing else
        /// draws then has its redacted copy moved into its object number, which keeps structure
        /// references to that object valid.
        /// </summary>
        private void PruneStaleEntries()
        {
            if (_staleEntries.Count == 0)
                return;

            List<ResourceEntry> pending = [.. _staleEntries.Distinct()];
            ResourceUsage usage;
            bool removedAny;

            do
            {
                usage = ResourceUsage.Collect(document, pending);
                ResourceUsage current = usage;
                removedAny = pending.RemoveAll(entry =>
                {
                    if (current.IsUsed(entry))
                        return false;

                    if (entry.Category.Remove(entry.Name))
                        document.MarkChanged(entry.Owner);

                    return true;
                }) > 0;
            }
            while (removedAny);

            HashSet<PdfObjectId> reused = [];
            foreach (FormReplacement replacement in _replacements)
            {
                if (usage.ReferencedObjects.Contains(replacement.OriginalId) || !reused.Add(replacement.OriginalId))
                    continue;

                document.SetObject(replacement.OriginalId, PdfValue.Stream(replacement.RedactedForm));
                document.MarkChanged(replacement.OriginalId);
                replacement.XObjects.Set(replacement.NewName, PdfValue.Reference(replacement.OriginalId));
                document.MarkChanged(replacement.Owner);
                document.SetObject(replacement.CloneId, PdfValue.Null);
            }
        }
    }
}
