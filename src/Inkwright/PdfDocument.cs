using System.Reflection;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.IO;
using Inkwright.Security;

namespace Inkwright;

/// <summary>
/// A PDF document in memory. Loading reads only the cross-reference data; objects are parsed as the
/// model reaches them, and objects nobody looked at are copied to the output verbatim, so opening a
/// file and changing one page does not disturb the rest of it.
/// </summary>
public sealed partial class PdfDocument : IPdfResolver, IDisposable
{
    /// <summary>What the library calls itself in the information dictionary and in the metadata.</summary>
    internal static readonly string ProducerName =
        $"Inkwright {typeof(PdfDocument).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0"}";

    private readonly PdfReader? _reader;
    private readonly Dictionary<int, PdfValue> _added = [];
    private readonly HashSet<int> _changed = [];
    private readonly List<Fonts.EmbeddedTrueTypeFont> _embeddedFonts = [];
    private IReadOnlyList<Tagging.PdfStructureNode>? _structure;
    private int _nextNumber = 1;
    private bool _disposed;

    private PdfDocument(PdfReader? reader)
    {
        _reader = reader;
        Diagnostics = reader?.Diagnostics ?? new LoadDiagnostics();

        if (reader is null)
        {
            Version = PdfVersion.Pdf17;
            Trailer = new PdfDictionary { Resolver = this };
            Catalog = new PdfDictionary { Resolver = this };
            Catalog.Set(PdfNames.Type, PdfValue.Name(PdfNames.Catalog));
            CatalogId = AddObject(PdfValue.Dictionary(Catalog));

            var pages = new PdfDictionary { Resolver = this };
            pages.Set(PdfNames.Type, PdfValue.Name(PdfNames.Pages));
            pages.Set(PdfNames.Kids, PdfValue.Array(new PdfArray { Resolver = this }));
            pages.Set(PdfNames.Count, PdfValue.Integer(0));
            PageTreeRoot = pages;
            PageTreeRootId = AddObject(PdfValue.Dictionary(pages));
            Catalog.Set(PdfNames.Pages, PdfValue.Reference(PageTreeRootId));

            var info = new PdfDictionary { Resolver = this };
            InfoId = AddObject(PdfValue.Dictionary(info));
            Info = new PdfDocumentInfo(info, () => MarkChanged(InfoId));
            Info.CreationDate = DateTimeOffset.Now;

            Trailer.Set(PdfNames.Root, PdfValue.Reference(CatalogId));
            Trailer.Set(PdfNames.Info, PdfValue.Reference(InfoId));
            Pages = new PdfPageCollection(this, []);
            return;
        }

        // Everything parsed out of the file carries the reader as its resolver; sending that through
        // the document makes objects added later visible from containers that came out of the file.
        reader.Outer = this;

        Version = reader.Version;
        Trailer = reader.Trailer;
        Catalog = reader.Catalog ?? new PdfDictionary { Resolver = this };
        CatalogId = ReferenceOf(Trailer, PdfNames.Root);

        // New objects start above everything the file reserved, not just above what it actually
        // holds: a dangling reference to a number inside Size must stay dangling, or the object we
        // put there would silently become whatever pointed at it.
        _nextNumber = Math.Max(reader.MaxObjectNumber + 1, (int)Math.Min(Trailer.GetInteger(PdfNames.Size), int.MaxValue - 1));

        // A few damaged files write the catalog straight into the trailer instead of naming an
        // object. Nothing can point at such a dictionary, so it is given a number of its own before
        // anything tries: a rewrite that referred to object zero would lose the catalog outright.
        if (!CatalogId.IsValid)
        {
            CatalogId = AddObject(PdfValue.Dictionary(Catalog));
            Trailer.Set(PdfNames.Root, PdfValue.Reference(CatalogId));
        }

        InfoId = ReferenceOf(Trailer, PdfNames.Info);
        PdfDictionary infoDictionary = Trailer.GetDictionary(PdfNames.Info) ?? new PdfDictionary { Resolver = this };

        // A file written against PDF 2.0 may legitimately have no information dictionary at all,
        // and ISO 19005-4 §6.1.3 all but requires that. One is still made here so that callers can
        // set a title without checking, but the file is remembered as having arrived without it.
        HadInformationDictionary = InfoId.IsValid || Trailer.ContainsKey(PdfNames.Info);

        if (!InfoId.IsValid)
        {
            InfoId = AddObject(PdfValue.Dictionary(infoDictionary));
            Trailer.Set(PdfNames.Info, PdfValue.Reference(InfoId));
        }

        Info = new PdfDocumentInfo(infoDictionary, () => MarkChanged(InfoId));

        PageTreeRootId = ReferenceOf(Catalog, PdfNames.Pages);
        PageTreeRoot = Catalog.GetDictionary(PdfNames.Pages) ?? BuildEmptyPageTree();
        Pages = new PdfPageCollection(this, PageTreeWalker.Collect(this, Catalog, Diagnostics));
    }

    /// <summary>The document catalog (§7.7.2).</summary>
    public PdfDictionary Catalog { get; }

    /// <summary>The trailer dictionary, holding at least <c>Root</c> and <c>Info</c>.</summary>
    public PdfDictionary Trailer { get; }

    /// <summary>The pages, in display order.</summary>
    public PdfPageCollection Pages { get; }

    /// <summary>The document information dictionary.</summary>
    public PdfDocumentInfo Info { get; }

    /// <summary>Everything the reader had to work around while loading this document.</summary>
    public LoadDiagnostics Diagnostics { get; }

    /// <summary>The fonts this document draws with.</summary>
    public Fonts.PdfFontCollection Fonts => field ??= new Fonts.PdfFontCollection(this);

    /// <summary>The files carried inside this document.</summary>
    public PdfAttachments Attachments => field ??= new PdfAttachments(this);

    /// <summary>What each page is called, as opposed to where it sits (§12.4.2).</summary>
    public PdfPageLabels PageLabels => field ??= new PdfPageLabels(this);

    /// <summary>
    /// The bookmarks a reader shows in its navigation pane (§12.3.3). Reading returns nothing when
    /// the document has none; <see cref="PdfOutline.OpenOrCreate"/> makes an empty tree to add to.
    /// </summary>
    public PdfOutline? Outline => PdfOutline.Open(this);

    /// <summary>
    /// Adds an output intent to the document as a whole (ISO 32000-1 §14.11.5): the colour
    /// characterisation of the device it was prepared for, embedded as an ICC profile.
    /// </summary>
    /// <param name="intent">The intent to add.</param>
    public void AddOutputIntent(PdfOutputIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        PdfArray intents = Catalog.GetArray(PdfNames.OutputIntents) ?? AttachIntents();
        intents.Add(intent.Build(this));
        MarkChanged(CatalogId);

        PdfArray AttachIntents()
        {
            var array = new PdfArray();
            Attach(PdfValue.Array(array));
            Catalog.Set(PdfNames.OutputIntents, PdfValue.Array(array));
            return array;
        }
    }

    /// <summary>The layers this document declares and which of them are shown (§8.11).</summary>
    public Content.PdfOptionalContent OptionalContent => field ??= new Content.PdfOptionalContent(this);

    /// <summary>
    /// The logical structure tree (§14.7): what the content means and what order it reads in. The
    /// list is empty when the document is not tagged.
    /// </summary>
    public IReadOnlyList<Tagging.PdfStructureNode> Structure =>
        _structure ??= Tagging.StructureReader.Read(this);

    /// <summary>Forgets the parsed logical tree after an in-memory writer replaces it.</summary>
    internal void InvalidateStructure() => _structure = null;

    /// <summary>The version to declare when saving.</summary>
    public PdfVersion Version { get; set; }

    /// <summary>Whether the file this document came from was encrypted.</summary>
    public bool WasEncrypted => _reader?.IsEncrypted ?? false;

    /// <summary>What the source file's security handler permitted.</summary>
    public PdfPermissions SourcePermissions => _reader?.Permissions ?? PdfPermissions.All;

    /// <summary>
    /// The archival level the document claims, once a profile has been applied. Saving has to know:
    /// PDF/A forbids encryption outright, and a claim of part 1 forbids the object streams the
    /// writer would otherwise reach for.
    /// </summary>
    internal string? ArchivalClaim { get; set; }

    /// <summary>The identity of the catalog object.</summary>
    public PdfObjectId CatalogId { get; private set; }

    internal PdfObjectId InfoId { get; private set; }

    /// <summary>
    /// Whether the document information dictionary is left out of the trailer when the file is
    /// written. PDF 2.0 deprecated it in favour of the XMP packet, and ISO 19005-4 §6.1.3 makes
    /// that deprecation normative: a PDF/A-4 file may only carry one beside a <c>PieceInfo</c>
    /// entry. Suppressing it also stops the save from stamping a producer name into it.
    /// </summary>
    internal bool SuppressInformationDictionary { get; set; }

    /// <summary>
    /// Whether the file this document was read from carried an information dictionary of its own.
    /// A document created from scratch counts as having one, because it will be written with one
    /// unless <see cref="SuppressInformationDictionary"/> says otherwise.
    /// </summary>
    internal bool HadInformationDictionary { get; private set; } = true;

    internal PdfDictionary PageTreeRoot { get; private set; }

    internal PdfObjectId PageTreeRootId { get; private set; }

    internal PdfReader? Reader => _reader;

    /// <summary>Creates an empty document with no pages.</summary>
    public static PdfDocument Create() => new(null);

    /// <summary>
    /// Joins several documents into a new one, in the order given. Everything that belongs to a
    /// document rather than to a page comes too: bookmarks, named destinations, form fields,
    /// logical structure, page labels, attachments and layers.
    /// </summary>
    /// <param name="sources">The documents to join; they are left untouched.</param>
    public static PdfDocument Merge(params IEnumerable<PdfDocument> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var merged = Create();
        foreach (PdfDocument source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            merged.Pages.ImportAll(source);
        }

        return merged;
    }

    /// <summary>
    /// Copies some of this document's pages into a new one, keeping the bookmarks, destinations,
    /// fields and structure that still lead somewhere and dropping the ones that do not.
    /// </summary>
    /// <param name="indices">The zero-based indices of the pages to take, in the order wanted.</param>
    public PdfDocument ExtractPages(params IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);

        List<PdfPage> wanted = [];
        foreach (int index in indices)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Pages.Count);
            wanted.Add(Pages[index]);
        }

        var extracted = Create();
        extracted.Pages.ImportMany(this, wanted);
        return extracted;
    }

    /// <summary>Opens a document from a byte array.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="options">How much damage to tolerate and what password to use.</param>
    public static PdfDocument Load(byte[] data, PdfReadOptions? options = null) =>
        new(PdfReader.Open(data, options));

    /// <summary>Opens a document from a stream, reading it fully into memory.</summary>
    /// <param name="stream">The stream to read; it is left open.</param>
    /// <param name="options">How much damage to tolerate and what password to use.</param>
    public static PdfDocument Load(Stream stream, PdfReadOptions? options = null) =>
        new(PdfReader.Open(stream, options));

    /// <summary>Opens a document from disk.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="options">How much damage to tolerate and what password to use.</param>
    public static PdfDocument Load(string path, PdfReadOptions? options = null) =>
        new(PdfReader.Open(File.ReadAllBytes(path), options));

    /// <summary>Opens a document from disk.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="options">How much damage to tolerate and what password to use.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<PdfDocument> LoadAsync(
        string path, PdfReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Load(data, options);
    }

    /// <summary>Opens a document from a stream, reading it fully into memory.</summary>
    /// <param name="stream">The stream to read; it is left open.</param>
    /// <param name="options">How much damage to tolerate and what password to use.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<PdfDocument> LoadAsync(
        Stream stream, PdfReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        PdfReader reader = await PdfReader.OpenAsync(stream, options, cancellationToken).ConfigureAwait(false);
        return new PdfDocument(reader);
    }

    /// <inheritdoc />
    public PdfValue Resolve(PdfValue value)
    {
        for (int depth = 0; value.IsReference && depth < 64; depth++)
            value = GetObject(value.AsReference.Number);

        return value.IsReference ? PdfValue.Null : value;
    }

    /// <summary>Fetches an indirect object by number.</summary>
    /// <param name="number">The object number.</param>
    public PdfValue GetObject(int number) =>
        _added.TryGetValue(number, out PdfValue added) ? added : _reader?.GetObject(number) ?? PdfValue.Null;

    /// <summary>Fetches an indirect object.</summary>
    /// <param name="id">The object identity.</param>
    public PdfValue GetObject(PdfObjectId id) => GetObject(id.Number);

    /// <summary>
    /// Stores a value as a new indirect object and returns its identity. Containers inside the value
    /// are wired to this document so that navigating them resolves references.
    /// </summary>
    /// <param name="value">The object body.</param>
    public PdfObjectId AddObject(PdfValue value)
    {
        var id = new PdfObjectId(_nextNumber++, 0);
        Attach(value, 0);
        _added[id.Number] = value;
        return id;
    }

    /// <summary>Replaces the body of an existing object.</summary>
    /// <param name="id">The object identity.</param>
    /// <param name="value">The new body.</param>
    public void SetObject(PdfObjectId id, PdfValue value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id.Number);
        Attach(value, 0);
        _added[id.Number] = value;
        _nextNumber = Math.Max(_nextNumber, id.Number + 1);
    }

    /// <summary>Wires a container to this document so that reading its entries resolves references.</summary>
    /// <param name="value">The value to attach; nested containers are attached too.</param>
    public void Attach(PdfValue value) => Attach(value, 0);

    /// <summary>
    /// Records that an object loaded from the file has been changed, so that an incremental save
    /// includes it. Objects created through <see cref="AddObject"/> are tracked automatically; this
    /// is only needed after mutating a dictionary the model handed out.
    /// </summary>
    /// <param name="id">The object that changed.</param>
    public void MarkChanged(PdfObjectId id)
    {
        if (id.IsValid)
            _changed.Add(id.Number);
    }

    internal void RegisterFont(Fonts.EmbeddedTrueTypeFont font) => _embeddedFonts.Add(font);

    internal void FinishFonts()
    {
        foreach (Fonts.EmbeddedTrueTypeFont font in _embeddedFonts)
            font.FinishForSave();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _added.Clear();
    }

    private void Attach(PdfValue value, int depth)
    {
        if (depth > 64)
            return;

        switch (value.Kind)
        {
            case PdfValueKind.Array:
            {
                PdfArray array = value.AsArray!;
                if (array.Resolver is not null)
                    return;

                array.Resolver = this;
                for (int i = 0; i < array.Count; i++)
                    Attach(array[i], depth + 1);

                break;
            }

            case PdfValueKind.Dictionary:
            case PdfValueKind.Stream:
            {
                PdfDictionary dictionary = value.AsDictionary!;
                if (dictionary.Resolver is not null)
                    return;

                dictionary.Resolver = this;
                foreach (KeyValuePair<PdfName, PdfValue> entry in dictionary)
                    Attach(entry.Value, depth + 1);

                break;
            }

            default:
                break;
        }
    }

    private PdfDictionary BuildEmptyPageTree()
    {
        var pages = new PdfDictionary { Resolver = this };
        pages.Set(PdfNames.Type, PdfValue.Name(PdfNames.Pages));
        pages.Set(PdfNames.Kids, PdfValue.Array(new PdfArray { Resolver = this }));
        pages.Set(PdfNames.Count, PdfValue.Integer(0));

        PageTreeRootId = AddObject(PdfValue.Dictionary(pages));
        Catalog.Set(PdfNames.Pages, PdfValue.Reference(PageTreeRootId));
        return pages;
    }

    private static PdfObjectId ReferenceOf(PdfDictionary dictionary, PdfName key) =>
        dictionary.TryGetRaw(key, out PdfValue value) && value.IsReference ? value.AsReference : PdfObjectId.None;
}
