using Inkwright.Annotations;
using Inkwright.Cos;

namespace Inkwright.Tagging;

/// <summary>
/// Removes the logical-structure half of an annotation relationship when the annotation leaves its
/// page.  An annotation is reachable from both <c>Annots</c> and an <c>OBJR</c>; dropping only the
/// former keeps the annotation (and, for file attachments, its payload) alive during object pruning.
/// </summary>
internal static class AnnotationStructureLinks
{
    private const int MaxDepth = 64;

    internal static void Unlink(PdfAnnotation annotation)
    {
        PdfDocument document = annotation.Document;
        bool structureChanged = false;

        long statedKey = annotation.Dictionary.GetInteger(PdfNames.StructParent, -1);
        int? parentKey = statedKey is >= 0 and <= int.MaxValue ? (int)statedKey : null;

        if (document.Catalog.TryGetRaw(PdfNames.StructTreeRoot, out PdfValue rootValue) &&
            document.Resolve(rootValue).AsDictionary is { } root)
        {
            PdfObjectId rootStorage = rootValue.IsReference ? rootValue.AsReference : document.CatalogId;
            var editor = new Editor(document, annotation.Id, annotation.Dictionary);

            bool removedObjectReference = editor.RemoveObjectReferences(root, rootStorage);
            bool removedParentEntry = parentKey is { } key &&
                editor.RemoveMatchingParentEntry(root, rootStorage, key);

            structureChanged = removedObjectReference || removedParentEntry;
        }

        if (annotation.Dictionary.Remove(PdfNames.StructParent))
        {
            document.MarkChanged(annotation.Id);
            structureChanged = true;
        }

        if (structureChanged)
            document.InvalidateStructure();
    }

    private sealed class Editor
    {
        private readonly PdfDocument _document;
        private readonly PdfObjectId _annotation;
        private readonly PdfDictionary _annotationDictionary;
        private readonly List<Owner> _owners = [];
        private readonly HashSet<PdfDictionary> _activeDictionaries =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PdfArray> _activeArrays =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PdfDictionary> _prunedElements =
            new(ReferenceEqualityComparer.Instance);

        internal Editor(PdfDocument document, PdfObjectId annotation, PdfDictionary annotationDictionary)
        {
            _document = document;
            _annotation = annotation;
            _annotationDictionary = annotationDictionary;
        }

        internal bool RemoveObjectReferences(PdfDictionary root, PdfObjectId storage)
        {
            if (!root.TryGetRaw(PdfNames.K, out PdfValue kids))
                return false;

            SlotResult result = ProcessSlot(kids, storage, owner: null, depth: 0);
            if (result.RemoveSlot)
            {
                root.Remove(PdfNames.K);
                MarkChanged(storage);
            }

            return result.RemovedTarget;
        }

        internal bool RemoveMatchingParentEntry(PdfDictionary root, PdfObjectId storage, int key)
        {
            if (root.GetDictionary(PdfNames.ParentTree) is not { } parentTree)
                return false;

            List<(int Key, PdfValue Value)> entries = [.. PdfNumberTree.Enumerate(parentTree)];
            List<PdfValue> claimedOwners = [.. entries
                .Where(entry => entry.Key == key)
                .Select(entry => entry.Value)];

            if (claimedOwners.Count == 0 || KeyIsUsedByAnotherObject(key))
                return false;

            // A conforming file reaches this branch with a matching OBJR owner. Recovery also has
            // to handle producers that left StructParent/ParentTree behind after already dropping
            // OBJR. The key remains safe to remove when no other live page, annotation, or XObject
            // claims it. If its sole claimed owner is an empty Annot element, prune that shell too.
            if (!_owners.Any(owner => claimedOwners.Any(value => IsOwner(value, owner))))
                PruneUnreferencedEmptyOwner(root, storage, entries, key, claimedOwners);

            entries.RemoveAll(entry => entry.Key == key);

            if (entries.Count == 0)
            {
                root.Remove(PdfNames.ParentTree);
                root.Remove(PdfNames.ParentTreeNextKey);
            }
            else
            {
                PdfDictionary replacement = PdfNumberTree.Build(entries);
                PdfObjectId replacementId = _document.AddObject(PdfValue.Dictionary(replacement));
                root.Set(PdfNames.ParentTree, PdfValue.Reference(replacementId));
            }

            MarkChanged(storage);
            return true;
        }

        private void PruneUnreferencedEmptyOwner(
            PdfDictionary root,
            PdfObjectId storage,
            IReadOnlyList<(int Key, PdfValue Value)> entries,
            int key,
            IReadOnlyList<PdfValue> claimedOwners)
        {
            foreach (PdfValue claimed in claimedOwners)
            {
                if (entries.Any(entry => entry.Key != key && SameObject(entry.Value, claimed)))
                    continue;

                if (_document.Resolve(claimed).AsDictionary is not { } owner ||
                    !IsPrunableEmptyOwner(owner))
                {
                    continue;
                }

                _prunedElements.Add(owner);
            }

            if (_prunedElements.Count > 0)
                RemoveObjectReferences(root, storage);
        }

        private SlotResult ProcessSlot(PdfValue raw, PdfObjectId storage, Owner? owner, int depth)
        {
            if (depth > MaxDepth)
                return default;

            PdfValue resolved = _document.Resolve(raw);
            if (resolved.AsArray is { } array)
                return ProcessArray(raw, array, storage, owner, depth);

            if (resolved.AsDictionary is not { } dictionary)
                return default;

            if (IsObjectReference(dictionary))
            {
                if (!TargetsAnnotation(dictionary))
                    return default;

                if (owner is { } actualOwner)
                    Remember(actualOwner);

                return new SlotResult(RemoveSlot: true, RemovedTarget: true);
            }

            if (dictionary.GetName(PdfNames.S) is null)
                return default;

            if (_prunedElements.Contains(dictionary))
                return new SlotResult(RemoveSlot: true, RemovedTarget: false);

            PdfObjectId elementId = raw.IsReference ? raw.AsReference : PdfObjectId.None;
            PdfObjectId elementStorage = raw.IsReference ? raw.AsReference : storage;
            bool removed = ProcessElement(dictionary, elementId, elementStorage, depth + 1);

            if (removed && IsPrunableEmptyOwner(dictionary))
            {
                _prunedElements.Add(dictionary);
                return new SlotResult(RemoveSlot: true, RemovedTarget: true);
            }

            return new SlotResult(RemoveSlot: false, RemovedTarget: removed);
        }

        private SlotResult ProcessArray(
            PdfValue raw,
            PdfArray array,
            PdfObjectId storage,
            Owner? owner,
            int depth)
        {
            if (!_activeArrays.Add(array))
                return default;

            PdfObjectId arrayStorage = raw.IsReference ? raw.AsReference : storage;
            bool changed = false;
            bool removedTarget = false;

            for (int i = array.Count - 1; i >= 0; i--)
            {
                SlotResult result = ProcessSlot(array[i], arrayStorage, owner, depth + 1);
                removedTarget |= result.RemovedTarget;

                if (!result.RemoveSlot)
                    continue;

                array.RemoveAt(i);
                changed = true;
            }

            _activeArrays.Remove(array);

            if (changed)
                MarkChanged(arrayStorage);

            return new SlotResult(
                RemoveSlot: changed && array.Count == 0,
                RemovedTarget: removedTarget);
        }

        private bool ProcessElement(
            PdfDictionary element,
            PdfObjectId elementId,
            PdfObjectId storage,
            int depth)
        {
            if (depth > MaxDepth || !_activeDictionaries.Add(element))
                return false;

            bool removed = false;
            if (element.TryGetRaw(PdfNames.K, out PdfValue kids))
            {
                var owner = new Owner(element, elementId);
                SlotResult result = ProcessSlot(kids, storage, owner, depth + 1);
                removed = result.RemovedTarget;

                if (result.RemoveSlot)
                {
                    element.Remove(PdfNames.K);
                    MarkChanged(storage);
                }
            }

            _activeDictionaries.Remove(element);
            return removed;
        }

        private bool TargetsAnnotation(PdfDictionary objectReference)
        {
            if (!objectReference.TryGetRaw(PdfName.Get("Obj"), out PdfValue target))
                return false;

            if (target.IsReference)
                return _annotation.IsValid && target.AsReference.Number == _annotation.Number;

            return ReferenceEquals(_document.Resolve(target).AsDictionary, _annotationDictionary);
        }

        private bool KeyIsUsedByAnotherObject(int key)
        {
            var visitedXObjects = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

            foreach (PdfPage page in _document.Pages)
            {
                if (HasKey(page.Dictionary, PdfNames.StructParent, key) ||
                    HasKey(page.Dictionary, PdfNames.StructParents, key))
                {
                    return true;
                }

                if (page.Dictionary.GetArray(PdfNames.Annots) is { } annotations)
                {
                    for (int i = 0; i < annotations.Count; i++)
                    {
                        PdfValue raw = annotations[i];
                        if (raw.IsReference && raw.AsReference.Number == _annotation.Number)
                            continue;

                        if (_document.Resolve(raw).AsDictionary is { } candidate &&
                            !ReferenceEquals(candidate, _annotationDictionary) &&
                            HasKey(candidate, PdfNames.StructParent, key))
                        {
                            return true;
                        }
                    }
                }

                if (UsesKeyInXObjects(page.Dictionary.GetDictionary(PdfNames.Resources), key, visitedXObjects))
                    return true;
            }

            return false;
        }

        private bool UsesKeyInXObjects(
            PdfDictionary? resources,
            int key,
            HashSet<PdfDictionary> visited)
        {
            if (resources?.GetDictionary(PdfNames.XObject) is not { } objects)
                return false;

            foreach (KeyValuePair<PdfName, PdfValue> entry in objects)
            {
                if (_document.Resolve(entry.Value).AsStream is not { } xObject ||
                    !visited.Add(xObject.Dictionary))
                {
                    continue;
                }

                if (HasKey(xObject.Dictionary, PdfNames.StructParent, key) ||
                    HasKey(xObject.Dictionary, PdfNames.StructParents, key) ||
                    UsesKeyInXObjects(xObject.Dictionary.GetDictionary(PdfNames.Resources), key, visited))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsOwner(PdfValue value, Owner owner)
        {
            if (value.IsReference && owner.ElementId.IsValid)
                return owner.ElementId.Number == value.AsReference.Number;

            return ReferenceEquals(_document.Resolve(value).AsDictionary, owner.Dictionary);
        }

        private void Remember(Owner owner)
        {
            if (!_owners.Any(existing =>
                    (owner.ElementId.IsValid && existing.ElementId.Number == owner.ElementId.Number) ||
                    ReferenceEquals(existing.Dictionary, owner.Dictionary)))
            {
                _owners.Add(owner);
            }
        }

        private void MarkChanged(PdfObjectId storage) =>
            _document.MarkChanged(storage.IsValid ? storage : _document.CatalogId);

        private static bool IsObjectReference(PdfDictionary dictionary) =>
            dictionary.GetName(PdfNames.Type)?.Value == "OBJR";

        private static bool HasKey(PdfDictionary dictionary, PdfName name, int key)
        {
            PdfValue value = dictionary.Get(name);
            return value.Kind == PdfValueKind.Integer && value.AsInteger == key;
        }

        private bool SameObject(PdfValue left, PdfValue right)
        {
            if (left.IsReference || right.IsReference)
                return left.IsReference && right.IsReference && left.AsReference.Number == right.AsReference.Number;

            return ReferenceEquals(_document.Resolve(left).AsDictionary, _document.Resolve(right).AsDictionary);
        }

        private static bool IsPrunableEmptyOwner(PdfDictionary dictionary)
        {
            if (dictionary.TryGetRaw(PdfNames.K, out PdfValue kids) && !IsEmpty(kids, dictionary.Resolver))
                return false;

            string? tag = dictionary.GetName(PdfNames.S)?.Value;
            if (tag == "Annot")
                return true;

            // Existing-tree insertion creates Link/Form shells with only structural plumbing.
            // Prune that exact minimal shape, but retain authored semantic elements carrying Alt,
            // ActualText, attributes, IDs, titles, or any other caller-owned information.
            if (tag is not ("Link" or "Form") ||
                dictionary.GetName(PdfNames.Type)?.Value != "StructElem" ||
                !dictionary[PdfNames.P].IsReference ||
                !dictionary[PdfNames.Pg].IsReference)
            {
                return false;
            }

            foreach (PdfName key in dictionary.Keys)
            {
                if (key != PdfNames.Type && key != PdfNames.S && key != PdfNames.P &&
                    key != PdfNames.Pg && key != PdfNames.K)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsEmpty(PdfValue value, IPdfResolver? resolver)
        {
            PdfValue resolved = value.IsReference && resolver is not null ? resolver.Resolve(value) : value;
            return resolved.AsArray is { Count: 0 };
        }

        private readonly record struct Owner(PdfDictionary Dictionary, PdfObjectId ElementId);

        private readonly record struct SlotResult(bool RemoveSlot, bool RemovedTarget);
    }
}
