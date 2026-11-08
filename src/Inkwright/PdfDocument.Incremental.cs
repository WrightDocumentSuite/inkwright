using Inkwright.Cos;
using Inkwright.IO;

namespace Inkwright;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Appends the changes to the original file instead of rewriting it. The bytes that were there
    /// stay byte for byte where they were, which is the only way to change a signed document without
    /// invalidating the signatures that came before.
    /// </summary>
    /// <param name="stream">Where to write the whole file: original bytes first, then the update.</param>
    /// <param name="options">How to lay the appended section out; pruning and encryption do not apply.</param>
    /// <exception cref="InvalidOperationException">
    /// The document was created from scratch, or its cross-reference data had to be rebuilt, in
    /// which case an appended section would chain back to something that was never trustworthy.
    /// </exception>
    public void SaveIncremental(Stream stream, PdfSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_reader is null)
            throw new InvalidOperationException("An incremental save needs an original file to append to.");

        if (_reader.WasRebuilt || _reader.StartXref < 0)
        {
            throw new InvalidOperationException(
                "The source file's cross-reference data was rebuilt, so it cannot be extended; save a full copy instead.");
        }

        options ??= PdfSaveOptions.Default;
        PrepareForSave(options);

        ReadOnlyMemory<byte> original = _reader.FileBytes;
        stream.Write(original.Span);

        long baseLength = original.Length;
        if (original.Length > 0 && original.Span[^1] != (byte)'\n')
        {
            stream.WriteByte((byte)'\n');
            baseLength++;
        }

        PdfVersion version = PdfVersion.Max(Version, _reader.Version);

        // An appended section is still part of the same file, and a file that declares 1.4 has no
        // business acquiring object streams or a cross-reference stream in its update: that is how
        // an archival PDF/A-1 document quietly stops being one.
        var writeOptions = new PdfWriteOptions
        {
            UseObjectStreams = options.UseObjectStreams && _reader.Security is null && version >= PdfVersion.Pdf15,
            Compress = options.Compress,
        };

        using PdfWriter writer = PdfWriter.Append(stream, version, baseLength, _reader.StartXref, writeOptions);

        writer.ReserveThrough(Math.Max(_reader.MaxObjectNumber, _nextNumber - 1));
        writer.UseEncryption(_reader.Security);

        foreach (int number in ChangedObjectNumbers())
        {
            PdfValue value = GetObject(number);
            if (value.IsNull)
                continue;

            writer.WriteObject(new PdfObjectId(number, GenerationOf(number)), value);
        }

        writer.Finish(BuildIncrementalTrailer());
    }

    /// <summary>Appends the changes to a copy of the original file on disk.</summary>
    /// <param name="path">Where to write the updated file.</param>
    /// <param name="options">How to lay the appended section out.</param>
    public void SaveIncremental(string path, PdfSaveOptions? options = null)
    {
        using FileStream stream = File.Create(path);
        SaveIncremental(stream, options);
    }

    /// <summary>Appends the changes and returns the whole updated file.</summary>
    /// <param name="options">How to lay the appended section out.</param>
    public byte[] ToIncrementalArray(PdfSaveOptions? options = null)
    {
        using var buffer = new MemoryStream((_reader?.FileBytes.Length ?? 0) + 8192);
        SaveIncremental(buffer, options);
        return buffer.ToArray();
    }

    /// <summary>
    /// Captures the caller's current document state in a private document suitable for a
    /// transactional incremental operation. Existing signatures and encryption stay intact.
    /// </summary>
    internal PdfDocument CreateIncrementalSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_reader is { } sourceReader &&
            (sourceReader.WasRebuilt || sourceReader.StartXref < 0))
        {
            throw UntrustedSnapshotSource();
        }

        var options = new PdfSaveOptions
        {
            UseObjectStreams = false,
            UpdateModificationDate = false,
            SetProducer = false,
        };

        PdfDocument target = _reader is null
            ? Create()
            : new PdfDocument(_reader.ReopenIncremental(_reader.FileBytes.ToArray()));

        bool keepTargetAlive = false;
        try
        {
            if (target._reader is { } targetReader &&
                (targetReader.WasRebuilt || targetReader.StartXref < 0))
            {
                throw UntrustedSnapshotSource();
            }

            CopySnapshotStateTo(target);

            if (target._reader is not null)
            {
                keepTargetAlive = true;
                return target;
            }

            // A new document has no xref section to append to. Normalize the private copy first;
            // PrepareForSave is then free to finish fonts and page-tree bookkeeping without
            // touching the caller-owned document.
            PdfDocument normalized = Load(target.ToArray(options));
            normalized.ArchivalClaim = target.ArchivalClaim;
            normalized.SuppressInformationDictionary = target.SuppressInformationDictionary;
            return normalized;
        }
        finally
        {
            // A returned reader-backed snapshot stays alive. A normalized snapshot owns a newly
            // parsed graph, and every failure path releases the temporary graph here.
            if (!keepTargetAlive)
                target.Dispose();
        }
    }

    private static InvalidOperationException UntrustedSnapshotSource() =>
        new(
            "The source file's cross-reference data was rebuilt or has no trustworthy startxref, " +
            "so it cannot be captured for a transactional incremental operation.");

    /// <summary>Copies the current in-memory state into an independently parsed document.</summary>
    private void CopySnapshotStateTo(PdfDocument target)
    {
        var copier = new SnapshotValueCopier();

        target._added.Clear();
        target._addedGenerations.Clear();
        target._changed.Clear();

        if (_reader is null)
        {
            foreach (int number in EnumerateObjectNumbers())
            {
                target.SetObject(
                    new PdfObjectId(number, GenerationOf(number)),
                    copier.Copy(GetObject(number)));
            }
        }
        else
        {
            foreach (KeyValuePair<int, PdfValue> entry in _added)
            {
                target.SetObject(
                    new PdfObjectId(entry.Key, GenerationOf(entry.Key)),
                    copier.Copy(entry.Value));
            }

            foreach (int number in _changed)
            {
                if (_added.ContainsKey(number))
                    continue;

                CopyChangedObjectTo(target, number, copier);
            }
        }

        target._nextNumber = _nextNumber;
        target._changed.UnionWith(_changed);
        target.Version = Version;
        target.ArchivalClaim = ArchivalClaim;
        target.SuppressInformationDictionary = SuppressInformationDictionary;
        target.HadInformationDictionary = HadInformationDictionary;
        target.CatalogId = CatalogId;
        target.InfoId = InfoId;
        target.PageTreeRootId = PageTreeRootId;

        target.Trailer = copier.Copy(PdfValue.Dictionary(Trailer)).AsDictionary!;
        target.Attach(PdfValue.Dictionary(target.Trailer));

        target.Catalog = SnapshotDictionary(target, CatalogId, Catalog, copier);
        PdfDictionary info = SnapshotDictionary(target, InfoId, Info.Dictionary, copier);
        target.Info = new PdfDocumentInfo(info, () => target.MarkChanged(target.InfoId));
        target.PageTreeRoot = SnapshotDictionary(target, PageTreeRootId, PageTreeRoot, copier);

        var pages = new List<PdfPage>(Pages.Count);
        foreach (PdfPage page in Pages.Items)
        {
            PdfDictionary dictionary = SnapshotDictionary(target, page.Id, page.Dictionary, copier);
            pages.Add(new PdfPage(target, page.Id, dictionary));
        }

        target.Pages.ReplaceSnapshot(pages);

        foreach (Fonts.EmbeddedTrueTypeFont font in _embeddedFonts)
            font.CloneForSnapshot(target);
    }

    private void CopyChangedObjectTo(PdfDocument target, int number, SnapshotValueCopier copier)
    {
        PdfValue source = copier.Copy(GetObject(number));
        PdfValue destination = target.GetObject(number);

        if (!ReplaceComposite(destination, source))
        {
            throw new InvalidOperationException(
                $"Changed object {number} cannot be copied transactionally because its kind changed " +
                "without going through PdfDocument.SetObject.");
        }

        target.MarkChanged(new PdfObjectId(number, target.GenerationOf(number)));
    }

    private PdfDictionary SnapshotDictionary(
        PdfDocument target,
        PdfObjectId id,
        PdfDictionary source,
        SnapshotValueCopier copier)
    {
        PdfDictionary copy = copier.Copy(PdfValue.Dictionary(source)).AsDictionary!;
        if (!id.IsValid)
        {
            target.Attach(PdfValue.Dictionary(copy));
            return copy;
        }

        if (_added.ContainsKey(id.Number))
            return target.GetObject(id).AsDictionary ?? throw SnapshotKindChanged(id.Number);

        PdfValue destination = target.GetObject(id);
        if (!ReplaceComposite(destination, PdfValue.Dictionary(copy)))
            throw SnapshotKindChanged(id.Number);

        return destination.AsDictionary!;
    }

    private static InvalidOperationException SnapshotKindChanged(int number) =>
        new($"Object {number} changed kind without going through PdfDocument.SetObject.");

    private static bool ReplaceComposite(PdfValue destination, PdfValue source)
    {
        if (destination.Kind != source.Kind)
            return false;

        switch (source.Kind)
        {
            case PdfValueKind.Array:
                {
                    PdfArray target = destination.AsArray!;
                    target.Clear();
                    target.AddRange(source.AsArray!.AsSpan());
                    return true;
                }

            case PdfValueKind.Dictionary:
                ReplaceDictionary(destination.AsDictionary!, source.AsDictionary!);
                return true;

            case PdfValueKind.Stream:
                {
                    PdfStream target = destination.AsStream!;
                    PdfStream replacement = source.AsStream!;
                    ReplaceDictionary(target.Dictionary, replacement.Dictionary);
                    target.SetRawData(replacement.RawData);
                    return true;
                }

            default:
                // Scalar indirect values cannot be mutated in place; MarkChanged on one therefore
                // carries no state that differs from the independently parsed source object.
                return destination == source;
        }
    }

    private static void ReplaceDictionary(PdfDictionary target, PdfDictionary source)
    {
        PdfName[] keys = target.Keys.ToArray();
        foreach (PdfName key in keys)
            target.Remove(key);

        foreach (KeyValuePair<PdfName, PdfValue> entry in source)
            target.Set(entry.Key, entry.Value);
    }

    /// <summary>Deep-copies direct containers while preserving indirect object identities.</summary>
    private sealed class SnapshotValueCopier
    {
        private const int MaxDepth = 128;
        private readonly Dictionary<object, PdfValue> _copies =
            new(ReferenceEqualityComparer.Instance);

        internal PdfValue Copy(PdfValue value) => Copy(value, 0);

        private PdfValue Copy(PdfValue value, int depth)
        {
            if (depth > MaxDepth)
                throw new InvalidOperationException("The document contains a direct object graph deeper than 128 values.");

            switch (value.Kind)
            {
                case PdfValueKind.String:
                    return PdfValue.String(value.AsStringBytes.ToArray(), value.IsHexString);

                case PdfValueKind.Array:
                    return CopyArray(value.AsArray!, depth);

                case PdfValueKind.Dictionary:
                    return CopyDictionary(value.AsDictionary!, depth);

                case PdfValueKind.Stream:
                    return CopyStream(value.AsStream!, depth);

                default:
                    return value;
            }
        }

        private PdfValue CopyArray(PdfArray source, int depth)
        {
            if (_copies.TryGetValue(source, out PdfValue existing))
                return existing;

            var target = new PdfArray(source.Count);
            PdfValue value = PdfValue.Array(target);
            _copies[source] = value;
            for (int i = 0; i < source.Count; i++)
                target.Add(Copy(source[i], depth + 1));

            return value;
        }

        private PdfValue CopyDictionary(PdfDictionary source, int depth)
        {
            if (_copies.TryGetValue(source, out PdfValue existing))
                return existing;

            var target = new PdfDictionary(source.Count);
            PdfValue value = PdfValue.Dictionary(target);
            _copies[source] = value;
            CopyDictionaryEntries(source, target, depth);
            return value;
        }

        private PdfValue CopyStream(PdfStream source, int depth)
        {
            if (_copies.TryGetValue(source, out PdfValue existing))
                return existing;

            PdfDictionary dictionary;
            if (_copies.TryGetValue(source.Dictionary, out PdfValue copiedDictionary))
            {
                dictionary = copiedDictionary.AsDictionary!;
            }
            else
            {
                dictionary = new PdfDictionary(source.Dictionary.Count);
                _copies[source.Dictionary] = PdfValue.Dictionary(dictionary);
                CopyDictionaryEntries(source.Dictionary, dictionary, depth);
            }

            var target = new PdfStream(dictionary, source.RawData.ToArray());
            PdfValue value = PdfValue.Stream(target);
            _copies[source] = value;
            return value;
        }

        private void CopyDictionaryEntries(PdfDictionary source, PdfDictionary target, int depth)
        {
            foreach (KeyValuePair<PdfName, PdfValue> entry in source)
                target.Set(entry.Key, Copy(entry.Value, depth + 1));
        }
    }

    /// <summary>The objects an incremental save would write.</summary>
    internal SortedSet<int> ChangedObjectNumbers()
    {
        SortedSet<int> numbers = [.. _changed];
        foreach (int number in _added.Keys)
            numbers.Add(number);

        return numbers;
    }

    private PdfDictionary BuildIncrementalTrailer()
    {
        var trailer = new PdfDictionary(5);
        trailer.Set(PdfNames.Root, PdfValue.Reference(CatalogId));

        if (InfoId.IsValid && !SuppressInformationDictionary)
            trailer.Set(PdfNames.Info, PdfValue.Reference(InfoId));

        if (Trailer.TryGetRaw(PdfNames.Encrypt, out PdfValue encrypt) && !encrypt.IsNull)
            trailer.Set(PdfNames.Encrypt, encrypt);

        // The first half of the identifier is permanent; the second half changes with every update.
        byte[] permanent = Trailer.GetArray(PdfNames.ID) is { Count: > 0 } existing
            ? existing.Get(0).AsStringBytes.ToArray()
            : System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);

        trailer.Set(PdfNames.ID, PdfValue.Array(new PdfArray(
            PdfValue.String(permanent, hex: true),
            PdfValue.String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), hex: true))));

        // Integrity protection is declared in the trailer, and an appended revision has to carry
        // the declaration forward or the code it adds would have nothing pointing at it.
        if (Trailer.TryGetRaw(PdfName.Get("AuthCode"), out PdfValue authentication) && !authentication.IsNull)
            trailer.Set(PdfName.Get("AuthCode"), authentication);

        return trailer;
    }
}
