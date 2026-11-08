using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Inkwright.Cos;
using Inkwright.IO;
using Inkwright.Security;

namespace Inkwright;

public sealed partial class PdfDocument
{
    /// <summary>Writes the document to a stream.</summary>
    /// <param name="stream">Where to write; the stream is left open.</param>
    /// <param name="options">How to lay the file out.</param>
    public void Save(Stream stream, PdfSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        options ??= PdfSaveOptions.Default;

        // The claim set in this session is remembered directly; a loaded file carries its claim in
        // the XMP packet instead, and encrypting it would break the promise just as surely.
        if (options.Encryption is not null && (ArchivalClaim ?? DetectArchivalClaim()) is { } claim)
        {
            throw new InvalidOperationException(
                $"The document claims {claim}, and every part of PDF/A forbids encryption. " +
                "Save the archival copy in the clear, or drop the claim.");
        }

        // A code has to be computed over the finished bytes and written back into them, so a file
        // that carries one is assembled in memory first.
        if (options.AddIntegrityCode && options.Encryption is not null)
        {
            using var buffer = new MemoryStream(64 * 1024);
            WriteTo(buffer, options, out StandardSecurityHandler? sealing);

            byte[] file = buffer.ToArray();

            if (sealing is not null)
                PdfMacSealer.Seal(file, sealing);

            stream.Write(file);
            return;
        }

        WriteTo(stream, options, out _);
    }

    private void WriteTo(Stream stream, PdfSaveOptions options, out StandardSecurityHandler? security)
    {
        PrepareForSave(options);

        // A document that declares an older version means it: object streams arrived in PDF 1.5, so
        // packing them into a file that says 1.4 would make the claim a lie.
        bool objectStreams = options.UseObjectStreams && Version >= PdfVersion.Pdf15;

        PdfVersion version = options.UseObjectStreams && Version >= PdfVersion.Pdf15
            ? PdfVersion.Max(Version, PdfVersion.Pdf15)
            : Version;

        if (options.Encryption is not null)
            version = PdfVersion.Max(version, PdfVersion.Pdf20);

        var writeOptions = new PdfWriteOptions
        {
            UseObjectStreams = objectStreams && options.Encryption is null,
            Compress = options.Compress,
        };

        using PdfWriter writer = PdfWriter.Create(stream, version, writeOptions);

        // Claim every number the document already uses before anything is written, so the numbers
        // the writer allocates for its object and cross-reference streams sit above them.
        int[] numbers = [.. EnumerateObjectNumbers()];
        if (numbers.Length > 0)
            writer.ReserveThrough(numbers[^1]);

        StandardSecurityHandler? handler = ApplyEncryption(options, writer);
        security = handler;

        HashSet<int>? reachable = options.RemoveUnusedObjects ? ComputeReachable() : null;
        bool canCopyBytes = _reader is not null && handler is null && !_reader.IsEncrypted;

        foreach (int number in numbers)
        {
            if (reachable is not null && !reachable.Contains(number))
                continue;

            var id = new PdfObjectId(number, GenerationOf(number));

            if (_added.TryGetValue(number, out PdfValue added))
            {
                if (!added.IsNull)
                    writer.WriteObject(id, added);

                continue;
            }

            if (_reader is null)
                continue;

            if (canCopyBytes && _reader.CanCopyBytes(number) &&
                _reader.TryGetObjectBytes(number, out ReadOnlyMemory<byte> bytes))
            {
                writer.WriteRawObject(id, bytes.Span);
                continue;
            }

            PdfValue value = _reader.GetObject(number);
            if (!value.IsNull)
                writer.WriteObject(id, value);
        }

        writer.Finish(BuildTrailer(options.AddIntegrityCode && options.Encryption is not null));
    }

    /// <summary>Writes the document to a file.</summary>
    /// <param name="path">Where to write.</param>
    /// <param name="options">How to lay the file out.</param>
    public void Save(string path, PdfSaveOptions? options = null)
    {
        using FileStream stream = File.Create(path);
        Save(stream, options);
    }

    /// <summary>Writes the document to a file.</summary>
    /// <param name="path">Where to write.</param>
    /// <param name="options">How to lay the file out.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task SaveAsync(string path, PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        byte[] bytes = ToArray(options);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the document to a stream.</summary>
    /// <param name="stream">Where to write; the stream is left open.</param>
    /// <param name="options">How to lay the file out.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task SaveAsync(Stream stream, PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] bytes = ToArray(options);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the document into a byte array.</summary>
    /// <param name="options">How to lay the file out.</param>
    public byte[] ToArray(PdfSaveOptions? options = null)
    {
        using var buffer = new MemoryStream(64 * 1024);
        Save(buffer, options);
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads the PDF/A identification out of the XMP packet. A file loaded from disk states its
    /// claim there rather than in this object's memory, and the claim binds all the same.
    /// </summary>
    private string? DetectArchivalClaim()
    {
        if (Resolve(Catalog.Get(PdfName.Get("Metadata"))).AsStream is not { } stream)
            return null;

        string packet;
        try
        {
            packet = System.Text.Encoding.UTF8.GetString(stream.DecodedSpan());
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
        }

        Match part = PdfAPart().Match(packet);
        if (!part.Success)
            return null;

        Match level = PdfALevel().Match(packet);
        string suffix = level.Success ? level.Groups[1].Value.ToLowerInvariant() : string.Empty;
        return $"PDF/A-{part.Groups[1].Value}{suffix}";
    }

    [GeneratedRegex("""pdfaid:part(?:>\s*|=")([1-4])""")]
    private static partial Regex PdfAPart();

    [GeneratedRegex("""pdfaid:conformance(?:>\s*|=")([ABUabu])""")]
    private static partial Regex PdfALevel();

    private void PrepareForSave(PdfSaveOptions options)
    {
        FinishFonts();
        RebuildPageTree();

        if (options.SetProducer && !SuppressInformationDictionary)
            Info.Producer = ProducerName;

        if (options.UpdateModificationDate && !SuppressInformationDictionary)
            Info.ModificationDate = DateTimeOffset.Now;

        // Whatever was stamped into the information dictionary must also hold in the XMP packet,
        // or an archival file stops agreeing with itself on the very save that touched it.
        Metadata.XmpSynchronizer.Sync(this, options);

        PdfValue version = PdfValue.Name(PdfName.Get(Version.ToString()));
        if (Catalog[PdfNames.Version] != version)
        {
            Catalog.Set(PdfNames.Version, version);
            MarkChanged(CatalogId);
        }
    }

    private void RebuildPageTree()
    {
        SeparatePageTreeRootFromPages();

        var kids = new PdfArray(Pages.Count) { Resolver = this };
        PdfValue parent = PdfValue.Reference(PageTreeRootId);

        foreach (PdfPage page in Pages.Items)
        {
            if (!page.Id.IsValid)
                page.Id = AddObject(PdfValue.Dictionary(page.Dictionary));

            if (page.Dictionary[PdfNames.Parent] != parent)
            {
                page.Dictionary.Set(PdfNames.Parent, parent);
                MarkChanged(page.Id);
            }

            kids.Add(PdfValue.Reference(page.Id));
        }

        // The tree the model writes is one node deep, so nothing above the pages inherits any more.
        if (!SameKids(PageTreeRoot.GetArray(PdfNames.Kids), kids))
        {
            PageTreeRoot.Set(PdfNames.Type, PdfValue.Name(PdfNames.Pages));
            PageTreeRoot.Set(PdfNames.Kids, PdfValue.Array(kids));
            PageTreeRoot.Set(PdfNames.Count, PdfValue.Integer(Pages.Count));
            PageTreeRoot.Remove(PdfNames.Parent);
            MarkChanged(PageTreeRootId);
        }

        if (Catalog[PdfNames.Pages] != PdfValue.Reference(PageTreeRootId))
        {
            Catalog.Set(PdfNames.Pages, PdfValue.Reference(PageTreeRootId));
            MarkChanged(CatalogId);
        }
    }

    /// <summary>
    /// Makes sure the node that lists the pages is not one of them. Damaged files sometimes point
    /// the catalog straight at a page, and writing a list of kids into that same dictionary would
    /// leave a tree whose only child is itself — a document that reads back with no pages at all.
    /// </summary>
    private void SeparatePageTreeRootFromPages()
    {
        if (!SharesObjectWithAPage())
            return;

        var root = new PdfDictionary(3) { Resolver = this };
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.Pages));

        PageTreeRoot = root;
        PageTreeRootId = AddObject(PdfValue.Dictionary(root));
        Catalog.Set(PdfNames.Pages, PdfValue.Reference(PageTreeRootId));
        MarkChanged(CatalogId);
    }

    private bool SharesObjectWithAPage()
    {
        foreach (PdfPage page in Pages.Items)
        {
            if (ReferenceEquals(page.Dictionary, PageTreeRoot) ||
                (page.Id.IsValid && PageTreeRootId.IsValid && page.Id.Number == PageTreeRootId.Number))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameKids(PdfArray? existing, PdfArray candidate)
    {
        if (existing is null || existing.Count != candidate.Count)
            return false;

        for (int i = 0; i < existing.Count; i++)
        {
            if (existing[i] != candidate[i])
                return false;
        }

        return true;
    }

    private StandardSecurityHandler? ApplyEncryption(PdfSaveOptions options, PdfWriter writer)
    {
        if (options.Encryption is not { } settings)
        {
            Trailer.Remove(PdfNames.Encrypt);
            return null;
        }

        PdfDictionary encrypt;
        StandardSecurityHandler handler;

        if (settings.Recipients.Count > 0)
        {
            handler = PublicKeySecurityHandler.Create(
                settings.Recipients,
                settings.Permissions,
                settings.EncryptMetadata,
                out encrypt,
                requireMac: options.AddIntegrityCode);
        }
        else
        {
            handler = StandardSecurityHandler.CreateAes256(
                settings.UserPassword,
                settings.OwnerPassword,
                settings.Permissions,
                settings.EncryptMetadata,
                out encrypt,
                settings.UseGaloisCounterMode,
                requireMac: options.AddIntegrityCode);

            // Galois/Counter mode is not in ISO 32000-2 itself, so the file has to name the
            // technical specification that adds it (ISO/TS 32003 clause 4).
            if (settings.UseGaloisCounterMode)
            {
                PdfDeveloperExtensions.DeclareIso(
                    this, 32003, ":2023", "https://www.iso.org/standard/45876.html");
            }
        }

        if (options.AddIntegrityCode)
            PrepareIntegrity(encrypt);

        PdfObjectId id = AddObject(PdfValue.Dictionary(encrypt));
        Trailer.Set(PdfNames.Encrypt, PdfValue.Reference(id));

        // The encryption dictionary itself is written in the clear, so it goes out before the key
        // takes effect for everything that follows.
        writer.WriteObject(id, PdfValue.Dictionary(encrypt));
        _added.Remove(id.Number);
        _addedGenerations.Remove(id.Number);
        writer.UseEncryption(handler);
        return handler;
    }

    /// <summary>
    /// Sets up integrity protection before the encryption dictionary is written: the salt the key
    /// derivation needs, and the permission bit that tells a reader to expect a code
    /// (ISO/TS 32004 §5.1).
    /// </summary>
    private void PrepareIntegrity(PdfDictionary encrypt)
    {
        byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(PdfMacWriter.SaltLength);
        encrypt.Set(PdfName.Get("KDFSalt"), PdfValue.String(salt, hex: true));

        PdfDeveloperExtensions.DeclareIso(this, 32004, ":2024", "https://www.iso.org/standard/45877.html");
    }

    private PdfDictionary BuildTrailer(bool integrity = false)
    {
        var trailer = new PdfDictionary(4);
        trailer.Set(PdfNames.Root, PdfValue.Reference(CatalogId));

        if (InfoId.IsValid && !SuppressInformationDictionary)
            trailer.Set(PdfNames.Info, PdfValue.Reference(InfoId));

        if (Trailer.TryGetRaw(PdfNames.Encrypt, out PdfValue encrypt) && !encrypt.IsNull)
            trailer.Set(PdfNames.Encrypt, encrypt);

        trailer.Set(PdfNames.ID, PdfValue.Array(BuildFileIdentifier()));

        if (integrity)
        {
            trailer.Set(PdfName.Get("AuthCode"), PdfValue.Dictionary(PdfMacSealer.CreatePlaceholder()));
        }
        else if (Trailer.TryGetRaw(PdfName.Get("AuthCode"), out PdfValue authentication) && !authentication.IsNull)
        {
            // A signer writes its own authentication dictionary before the file is laid out, and
            // it has to survive into the trailer that is actually written.
            trailer.Set(PdfName.Get("AuthCode"), authentication);
        }

        return trailer;
    }

    private PdfArray BuildFileIdentifier()
    {
        byte[] permanent = Trailer.GetArray(PdfNames.ID) is { Count: > 0 } existing && !existing.Get(0).IsNull
            ? existing.Get(0).AsStringBytes.ToArray()
            : RandomNumberGenerator.GetBytes(16);

        return new PdfArray(PdfValue.String(permanent, hex: true),
                            PdfValue.String(RandomNumberGenerator.GetBytes(16), hex: true));
    }

    private SortedSet<int> EnumerateObjectNumbers()
    {
        SortedSet<int> numbers = [];

        if (_reader is not null)
        {
            foreach (int number in _reader.LiveObjectNumbers)
                numbers.Add(number);
        }

        foreach (int number in _added.Keys)
            numbers.Add(number);

        return numbers;
    }

    private int GenerationOf(int number) =>
        _addedGenerations.TryGetValue(number, out int generation)
            ? generation
            : _reader?.GetGeneration(number) ?? 0;

    private HashSet<int> ComputeReachable()
    {
        HashSet<int> seen = [];
        Queue<int> queue = [];

        void Enqueue(PdfValue value)
        {
            if (value.IsReference && seen.Add(value.AsReference.Number))
                queue.Enqueue(value.AsReference.Number);
        }

        Enqueue(PdfValue.Reference(CatalogId));
        if (InfoId.IsValid)
            Enqueue(PdfValue.Reference(InfoId));

        if (Trailer.TryGetRaw(PdfNames.Encrypt, out PdfValue encrypt))
            Enqueue(encrypt);

        while (queue.Count > 0)
        {
            int number = queue.Dequeue();
            Scan(GetObject(number), 0, Enqueue);
        }

        return seen;
    }

    private static void Scan(PdfValue value, int depth, Action<PdfValue> enqueue)
    {
        if (depth > 96)
            return;

        switch (value.Kind)
        {
            case PdfValueKind.Reference:
                enqueue(value);
                break;

            case PdfValueKind.Array:
                {
                    PdfArray array = value.AsArray!;
                    for (int i = 0; i < array.Count; i++)
                        Scan(array[i], depth + 1, enqueue);

                    break;
                }

            case PdfValueKind.Dictionary:
            case PdfValueKind.Stream:
                {
                    foreach (KeyValuePair<PdfName, PdfValue> entry in value.AsDictionary!)
                        Scan(entry.Value, depth + 1, enqueue);

                    break;
                }

            default:
                break;
        }
    }
}
