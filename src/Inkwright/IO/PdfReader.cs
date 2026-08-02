using System.Runtime.InteropServices;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Security;
using Inkwright.Syntax;

namespace Inkwright.IO;

/// <summary>
/// Reads the object graph of a PDF file. Objects are located through the cross-reference data and
/// parsed the first time they are asked for, so opening a large file costs only the cross-reference
/// pass. Damage is worked around and reported through <see cref="Diagnostics"/> rather than thrown.
/// </summary>
public sealed partial class PdfReader : IPdfResolver
{
    private const int MaxResolveDepth = 64;

    private readonly byte[] _data;
    private readonly ReadOnlyMemory<byte> _memory;
    private readonly Dictionary<int, XrefEntry> _entries = [];
    private readonly Dictionary<int, PdfValue> _cache = [];
    private readonly Dictionary<int, ObjectStreamIndex?> _objectStreams = [];
    private readonly Dictionary<int, (int Start, int End)> _protectedObjectRanges = [];
    private readonly Dictionary<int, (int Start, int End)> _protectedStreamPayloadRanges = [];
    private readonly HashSet<int> _loading = [];
    private readonly PdfReadOptions _options;

    private Dictionary<int, long>? _scannedOffsets;
    private StandardSecurityHandler? _security;
    private PdfObjectId _currentObject;
    private long _headerOffset;

    private PdfReader(byte[] data, PdfReadOptions options)
    {
        _data = data;
        _memory = data;
        _options = options;
        Diagnostics = new LoadDiagnostics(options.MaxWarnings);
        Diagnostics.SetSink(options.OnWarning);
        Trailer = new PdfDictionary { Resolver = this };
    }

    /// <summary>Everything the reader had to work around while opening the file.</summary>
    public LoadDiagnostics Diagnostics { get; }

    /// <summary>The trailer dictionary, merged across the whole cross-reference chain.</summary>
    public PdfDictionary Trailer { get; private set; }

    /// <summary>The version declared by the file header, raised by the catalog when it says so.</summary>
    public PdfVersion Version { get; private set; } = PdfVersion.Pdf14;

    /// <summary>Whether the file carried an <c>Encrypt</c> dictionary.</summary>
    public bool IsEncrypted { get; private set; }

    /// <summary>Whether the password supplied was the owner password.</summary>
    public bool HasOwnerAccess => _security?.HasOwnerAccess ?? true;

    /// <summary>What the security handler permits when opened with a user password.</summary>
    public PdfPermissions Permissions => _security?.Permissions ?? PdfPermissions.All;

    /// <summary>Whether the cross-reference data was unusable and objects were located by scanning.</summary>
    public bool WasRebuilt { get; private set; }

    /// <summary>The whole file, so callers can copy byte ranges out of it verbatim.</summary>
    public ReadOnlyMemory<byte> FileBytes => _memory;

    /// <summary>The object numbers the cross-reference data knows about.</summary>
    public IReadOnlyCollection<int> ObjectNumbers => _entries.Keys;

    /// <summary>The document catalog, located through the trailer or by scanning when that fails.</summary>
    public PdfDictionary? Catalog { get; private set; }

    /// <summary>The offset the file's <c>startxref</c> pointed at, or -1 when there was none.</summary>
    internal long StartXref { get; private set; } = -1;

    /// <summary>The security handler that opened the file, so an appended section can reuse its key.</summary>
    internal Security.StandardSecurityHandler? Security => _security;

    /// <summary>Whether damaged structure may be rebuilt by scanning the source bytes.</summary>
    internal bool Recover => _options.Recover;

    /// <summary>Whether streams and structural objects are being parsed under a fail-closed policy.</summary>
    internal bool UsesProtectedDecoding => _options.StreamDecodePolicy is not null;

    /// <summary>Opens a file from a byte array, which the reader keeps a reference to.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="options">How much damage to tolerate and what password to use.</param>
    public static PdfReader Open(byte[] data, PdfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var reader = new PdfReader(data, options ?? PdfReadOptions.Default);
        reader.Initialize();
        return reader;
    }

    /// <summary>Opens a file from a stream, reading it fully into memory.</summary>
    /// <param name="stream">The stream to read; it is left open.</param>
    /// <param name="options">How much damage to tolerate and what password to use.</param>
    public static PdfReader Open(Stream stream, PdfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> segment) &&
            segment.Offset == 0 && segment.Count == segment.Array!.Length)
        {
            return Open(segment.Array, options);
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Open(buffer.ToArray(), options);
    }

    /// <summary>Opens a file from disk.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="options">How much damage to tolerate and what password to use.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<PdfReader> OpenAsync(
        string path, PdfReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Open(data, options);
    }

    /// <summary>Opens a file from a stream, reading it fully into memory.</summary>
    /// <param name="stream">The stream to read; it is left open.</param>
    /// <param name="options">How much damage to tolerate and what password to use.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<PdfReader> OpenAsync(
        Stream stream, PdfReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Open(buffer.ToArray(), options);
    }

    /// <summary>Fetches an indirect object.</summary>
    /// <param name="number">The object number.</param>
    /// <returns>The object, or the null object when it is missing or unparsable.</returns>
    public PdfValue GetObject(int number)
    {
        if (_cache.TryGetValue(number, out PdfValue cached))
            return cached;

        if (!_loading.Add(number))
        {
            Diagnostics.Add(PdfWarningCode.ReferenceCycle, "An object referred to itself while loading.",
                new PdfObjectId(number, 0));
            return PdfValue.Null;
        }

        try
        {
            PdfValue value = LoadObject(number);
            _cache[number] = value;
            return value;
        }
        finally
        {
            _loading.Remove(number);
        }
    }

    /// <summary>Fetches an indirect object.</summary>
    /// <param name="id">The object identity.</param>
    public PdfValue GetObject(PdfObjectId id)
    {
        if (_options.StreamDecodePolicy is not null &&
            _entries.TryGetValue(id.Number, out XrefEntry entry) &&
            entry.Type is XrefEntryType.Normal or XrefEntryType.Compressed)
        {
            int expectedGeneration = entry.Type == XrefEntryType.Normal ? entry.Generation : 0;
            if (id.Generation != expectedGeneration)
                return PdfValue.Null;
        }

        return GetObject(id.Number);
    }

    /// <summary>
    /// Where reference resolution is sent once a document wraps this reader. Objects the document
    /// creates are not in the file, so containers parsed out of it still have to reach them.
    /// </summary>
    internal IPdfResolver? Outer { get; set; }

    /// <inheritdoc />
    public PdfValue Resolve(PdfValue value)
    {
        if (Outer is { } outer)
            return outer.Resolve(value);

        for (int depth = 0; value.IsReference && depth < MaxResolveDepth; depth++)
            value = GetObject(value.AsReference);

        return value.IsReference ? PdfValue.Null : value;
    }

    /// <summary>The generation recorded for an object, or zero when it is unknown.</summary>
    /// <param name="number">The object number.</param>
    internal int GetGeneration(int number) =>
        _entries.TryGetValue(number, out XrefEntry entry) && entry.Type == XrefEntryType.Normal
            ? entry.Generation
            : 0;

    /// <summary>Whether an object is stored inside an object stream rather than at a file offset.</summary>
    /// <param name="number">The object number.</param>
    internal bool IsInObjectStream(int number) =>
        _entries.TryGetValue(number, out XrefEntry entry) && entry.Type == XrefEntryType.Compressed;

    /// <summary>Whether an object has been parsed, and therefore may have been changed by the caller.</summary>
    /// <param name="number">The object number.</param>
    internal bool IsParsed(int number) => _cache.ContainsKey(number);

    /// <summary>The numbers of every object the cross-reference data lists as present.</summary>
    internal IEnumerable<int> LiveObjectNumbers
    {
        get
        {
            foreach (KeyValuePair<int, XrefEntry> entry in _entries)
            {
                if (entry.Value.Type is XrefEntryType.Normal or XrefEntryType.Compressed && entry.Key > 0)
                    yield return entry.Key;
            }
        }
    }

    /// <summary>The highest object number the file uses.</summary>
    internal int MaxObjectNumber
    {
        get
        {
            int max = 0;
            foreach (int number in _entries.Keys)
                max = Math.Max(max, number);

            return max;
        }
    }

    /// <summary>
    /// Whether an object can be copied to a new file byte for byte: it must live at a file offset,
    /// never have been parsed, and not be wrapped in encryption that the copy would not reproduce.
    /// </summary>
    /// <param name="number">The object number.</param>
    internal bool CanCopyBytes(int number) =>
        !IsEncrypted &&
        !IsParsed(number) &&
        _entries.TryGetValue(number, out XrefEntry entry) &&
        entry.Type == XrefEntryType.Normal;

    /// <summary>
    /// The exact bytes of an indirect object as they appear in the file, header and <c>endobj</c>
    /// included. Rewriting uses this to copy untouched objects through without reserialising them.
    /// </summary>
    /// <param name="number">The object number.</param>
    /// <param name="bytes">Receives the byte range.</param>
    internal bool TryGetObjectBytes(int number, out ReadOnlyMemory<byte> bytes)
    {
        bytes = default;

        if (!TryGetObjectRange(number, out int start, out int end))
            return false;

        bytes = _memory[start..end];
        return true;
    }

    /// <summary>
    /// Locates an indirect object's exact half-open byte range. Callers that validate delimiters
    /// use the offsets as well as the payload so they can inspect the bytes immediately outside it.
    /// </summary>
    internal bool TryGetObjectRange(int number, out int start, out int end)
    {
        start = 0;
        end = 0;

        if (!_entries.TryGetValue(number, out XrefEntry entry) || entry.Type != XrefEntryType.Normal)
            return false;

        if (_options.StreamDecodePolicy is not null)
        {
            if (!_protectedObjectRanges.TryGetValue(number, out var range))
                return false;

            start = range.Start;
            end = range.End;
            return true;
        }

        if (!TryLocate(number, entry, out start))
            return false;

        end = FindObjectEnd(start);
        if (end <= start)
            return false;

        return true;
    }

    /// <summary>
    /// Returns the exact decoded bytes occupied by an object-stream slot. Compressed objects have
    /// no physical <c>obj</c>/<c>endobj</c> wrapper, but byte-oriented conformance checks still need
    /// the slot boundary declared by the object stream's offset table.
    /// </summary>
    internal bool TryGetCompressedObjectBytes(int number, out ReadOnlyMemory<byte> bytes)
    {
        bytes = default;
        if (!_entries.TryGetValue(number, out XrefEntry entry) ||
            entry.Type != XrefEntryType.Compressed)
        {
            return false;
        }

        ObjectStreamIndex? index = ReadObjectStream((int)entry.Offset);
        if (index is null)
            return false;

        int slot = entry.Generation;
        if (slot < 0 || slot >= index.Numbers.Length || index.Numbers[slot] != number)
            return false;

        int start = index.First + index.Offsets[slot];
        int end = slot + 1 < index.Offsets.Length
            ? index.First + index.Offsets[slot + 1]
            : index.Data.Length;
        if (start < 0 || start >= index.Data.Length || end <= start || end > index.Data.Length)
            return false;

        bytes = index.Data[start..end];
        return true;
    }

    /// <summary>
    /// Locates the exact half-open range occupied by a stream's encoded payload. A loaded,
    /// unencrypted stream keeps that range as a slice of the reader's file buffer, which avoids
    /// mistaking marker-like bytes inside the payload for PDF syntax.
    /// </summary>
    internal bool TryGetStreamPayloadRange(int number, out int start, out int end)
    {
        start = 0;
        end = 0;

        if (_options.StreamDecodePolicy is not null)
        {
            if (!_protectedStreamPayloadRanges.TryGetValue(number, out var range))
                return false;

            start = range.Start;
            end = range.End;
            return true;
        }

        PdfStream? stream = GetObject(number).AsStream;
        if (stream is null ||
            !MemoryMarshal.TryGetArray(stream.RawData, out ArraySegment<byte> payload) ||
            !ReferenceEquals(payload.Array, _data))
        {
            return false;
        }

        start = payload.Offset;
        end = start + payload.Count;
        return start >= 0 && end >= start && end <= _data.Length;
    }

    private void Initialize()
    {
        ParseHeader();
        LoadCrossReferences();
        SetUpSecurity();
        ValidateProtectedLiveObjects();
        ResolveCatalog();
    }

    private void ValidateProtectedLiveObjects()
    {
        if (_options.StreamDecodePolicy is null)
            return;

        foreach (KeyValuePair<int, XrefEntry> item in _entries)
        {
            if (item.Value.Type is XrefEntryType.Free or XrefEntryType.Unknown ||
                (item.Value.Type == XrefEntryType.Compressed && IsEncrypted && _security is null))
            {
                continue;
            }

            _ = GetObject(item.Key);
        }

        ValidateProtectedProvenanceRanges();
        ValidateProtectedLinearizationStructure();
    }

    private void ParseHeader()
    {
        ReadOnlySpan<byte> span = _data;
        int limit = Math.Min(span.Length, 4096);
        int headerIndex = span[..limit].IndexOf("%PDF-"u8);

        if (headerIndex < 0)
        {
            // Some files lose the header entirely but still hold a usable body.
            if (span.IndexOf(" obj"u8) < 0)
                throw new PdfFormatException("The data does not contain a PDF header or any indirect objects.");

            Diagnostics.Add(PdfWarningCode.HeaderNotAtStart, "No %PDF- header was found.");
            return;
        }

        if (headerIndex > 0)
        {
            _headerOffset = headerIndex;
            Diagnostics.Add(PdfWarningCode.HeaderNotAtStart,
                $"The header starts {headerIndex} bytes into the file.", offset: headerIndex);
        }

        int versionEnd = headerIndex + 5;
        while (versionEnd < span.Length && versionEnd < headerIndex + 13 &&
               (char.IsAsciiDigit((char)span[versionEnd]) || span[versionEnd] == (byte)'.'))
        {
            versionEnd++;
        }

        Span<char> text = stackalloc char[versionEnd - headerIndex - 5];
        for (int i = 0; i < text.Length; i++)
            text[i] = (char)span[headerIndex + 5 + i];

        if (PdfVersion.TryParse(text, out PdfVersion version))
            Version = version;
        else
            Diagnostics.Add(PdfWarningCode.UnknownVersion, "The header version could not be parsed.");
    }

    private void SetUpSecurity()
    {
        PdfValue encryptValue = Trailer[PdfNames.Encrypt];
        if (encryptValue.IsNull)
            return;

        IsEncrypted = true;
        int encryptNumber = encryptValue.IsReference ? encryptValue.AsReference.Number : 0;
        PdfDictionary? encrypt = Resolve(encryptValue).AsDictionary;

        if (encrypt is null)
        {
            Diagnostics.Add(PdfWarningCode.UnsupportedSecurityHandler, "The Encrypt entry is not a dictionary.");
            return;
        }

        ReadOnlySpan<byte> firstId = [];
        if (Trailer.GetArray(PdfNames.ID) is { Count: > 0 } id)
            firstId = id.Get(0).AsStringBytes;

        bool opened = PublicKeySecurityHandler.Handles(encrypt)
            ? PublicKeySecurityHandler.TryOpen(encrypt, _options.DecryptionCertificate, out StandardSecurityHandler? handler, out string? failure)
            : StandardSecurityHandler.TryOpen(encrypt, firstId, _options.Password, out handler, out failure);

        if (opened)
        {
            _security = handler;

            // Anything cached before the key existed was read as ciphertext; the Encrypt dictionary
            // itself is exempt from encryption and must survive the purge.
            _cache.Clear();
            _objectStreams.Clear();
            if (encryptNumber > 0)
                _cache[encryptNumber] = PdfValue.Dictionary(encrypt);

            return;
        }

        string message = failure ?? "The document could not be decrypted.";
        Diagnostics.Add(PdfWarningCode.UnsupportedSecurityHandler, message);

        if (!_options.OpenLockedDocuments)
            throw new PdfPasswordException(message, _options.Password.Length > 0);
    }

    private void ResolveCatalog()
    {
        PdfValue root = Trailer.Get(PdfNames.Root);
        if (_options.StreamDecodePolicy is not null && root.Kind != PdfValueKind.Dictionary)
        {
            throw new PdfFormatException(
                "A protected document catalog shall be a dictionary, not a stream or other value.");
        }

        PdfDictionary? catalog = root.AsDictionary;

        if (_options.Recover && (catalog is null || !catalog.ContainsKey(PdfNames.Pages)))
        {
            PdfDictionary? scanned = ScanForCatalog();
            if (scanned is not null)
            {
                if (catalog is null)
                {
                    Diagnostics.Add(PdfWarningCode.CatalogRecovered,
                        "The trailer had no usable Root; the catalog was found by scanning.");
                }

                catalog = scanned;
            }
        }

        Catalog = catalog;

        if (catalog is null)
            throw new PdfFormatException("The document has no catalog.");

        if (_options.StreamDecodePolicy is not null &&
            catalog.GetName(PdfNames.Type) != PdfNames.Catalog)
        {
            throw new PdfFormatException(
                "A protected document catalog requires a Type entry whose value is Catalog.");
        }

        if (catalog.GetName(PdfNames.Version) is { } declared &&
            PdfVersion.TryParse(declared.Value, out PdfVersion version) && version > Version)
        {
            Version = version;
        }
    }

    private PdfDictionary? ScanForCatalog()
    {
        EnsureScanned();

        // Objects hidden in object streams never show up in a byte scan, so look at everything the
        // cross-reference data knows about as well.
        SortedSet<int> candidates = [.. _scannedOffsets!.Keys];
        candidates.UnionWith(LiveObjectNumbers);

        foreach (int number in candidates)
        {
            PdfValue value = GetObject(number);
            if (value.AsDictionary is { } dictionary &&
                dictionary.GetName(PdfNames.Type) == PdfNames.Catalog &&
                dictionary.ContainsKey(PdfNames.Pages))
            {
                Trailer.Set(PdfNames.Root, PdfValue.Reference(number, GetGeneration(number)));
                return dictionary;
            }
        }

        return null;
    }
}
