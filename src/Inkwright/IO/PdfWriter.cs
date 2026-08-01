using Inkwright.Cos;
using Inkwright.Security;

namespace Inkwright.IO;

/// <summary>How to lay out a file that is being written.</summary>
public sealed class PdfWriteOptions
{
    /// <summary>The defaults: object streams and a cross-reference stream, both compressed.</summary>
    public static PdfWriteOptions Default { get; } = new();

    /// <summary>
    /// Whether to pack small objects into object streams, which typically halves the size of a
    /// text-heavy file. Requires PDF 1.5, and is switched off automatically below that.
    /// </summary>
    public bool UseObjectStreams { get; init; } = true;

    /// <summary>How many objects to gather before emitting an object stream.</summary>
    public int ObjectStreamBatchSize { get; init; } = 200;

    /// <summary>Whether to compress the object and cross-reference streams the writer produces.</summary>
    public bool Compress { get; init; } = true;
}

/// <summary>
/// A forward-only writer for PDF files. Objects are serialised the moment they are handed over and
/// only their offsets are retained, so writing a document of any size costs a constant amount of
/// memory beyond the objects the caller is holding anyway.
/// </summary>
public sealed class PdfWriter : IDisposable
{
    private readonly Utf8Writer _writer;
    private readonly PdfObjectSerializer _serializer;
    private readonly PdfWriteOptions _options;
    private readonly SortedDictionary<int, XrefEntry> _entries = [];
    private readonly List<(PdfObjectId Id, byte[] Body)> _pending = [];
    private readonly bool _useObjectStreams;

    private int _nextFreeNumber = 1;
    private bool _finished;
    private long _previousXref = -1;

    private PdfWriter(Stream stream, PdfVersion version, PdfWriteOptions options, long startOffset)
    {
        _writer = new Utf8Writer(stream, leaveOpen: true, startOffset: startOffset);
        _serializer = new PdfObjectSerializer(_writer);
        _options = options;
        Version = version;
        _useObjectStreams = options.UseObjectStreams && version >= PdfVersion.Pdf15;

        if (startOffset == 0)
            WriteHeader();
    }

    /// <summary>The version written into the file header.</summary>
    public PdfVersion Version { get; }

    /// <summary>How many bytes have been written so far.</summary>
    public long Position => _writer.Length;

    /// <summary>Starts writing a file.</summary>
    /// <param name="stream">Where to write; the stream is left open.</param>
    /// <param name="version">The version to declare in the header.</param>
    /// <param name="options">How to lay out the file.</param>
    public static PdfWriter Create(Stream stream, PdfVersion version, PdfWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new PdfWriter(stream, version, options ?? PdfWriteOptions.Default, startOffset: 0);
    }

    /// <summary>
    /// Continues an existing file: no header is written, offsets start after the bytes already in
    /// place, and the cross-reference section chains back to the one the file already had.
    /// </summary>
    /// <param name="stream">Where to write; the original bytes must already be in it.</param>
    /// <param name="version">The version of the document being appended to.</param>
    /// <param name="baseLength">How many bytes the original file occupies.</param>
    /// <param name="previousXref">The offset of the cross-reference section this one supersedes.</param>
    /// <param name="options">How to lay the appended section out.</param>
    public static PdfWriter Append(
        Stream stream, PdfVersion version, long baseLength, long previousXref, PdfWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(baseLength);

        return new PdfWriter(stream, version, options ?? PdfWriteOptions.Default, baseLength)
        {
            _previousXref = previousXref,
        };
    }

    /// <summary>Encrypts everything written from now on with the given handler.</summary>
    /// <param name="handler">The security handler, or <see langword="null"/> to write in the clear.</param>
    internal void UseEncryption(StandardSecurityHandler? handler) => _serializer.Encryption = handler;

    /// <summary>
    /// Reserves an object number without writing anything. Callers that already know which numbers
    /// their objects will use must claim them with <see cref="ReserveThrough"/> first, so that the
    /// numbers the writer allocates for its own object and cross-reference streams cannot collide.
    /// </summary>
    public PdfObjectId Reserve() => new(_nextFreeNumber++, 0);

    /// <summary>Ensures that numbers handed out later do not collide with ones already in use.</summary>
    /// <param name="number">The highest object number already spoken for.</param>
    public void ReserveThrough(int number) => _nextFreeNumber = Math.Max(_nextFreeNumber, number + 1);

    /// <summary>
    /// Writes an object, packing it into an object stream when that is allowed and worthwhile.
    /// </summary>
    /// <param name="id">The identity to write it under.</param>
    /// <param name="value">The object body.</param>
    public void WriteObject(PdfObjectId id, PdfValue value)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        ReserveThrough(id.Number);

        if (CanCompress(id, value))
        {
            _pending.Add((id, SerializeToArray(value)));
            if (_pending.Count >= _options.ObjectStreamBatchSize)
                FlushObjectStream();

            return;
        }

        WriteDirect(id, value);
    }

    /// <summary>
    /// Writes an object that is already serialised, copying the bytes through untouched. This is how
    /// a rewrite preserves objects the model never looked at.
    /// </summary>
    /// <param name="id">The identity the bytes declare.</param>
    /// <param name="bytes">The complete object, from its header through <c>endobj</c>.</param>
    public void WriteRawObject(PdfObjectId id, ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        ReserveThrough(id.Number);

        _entries[id.Number] = XrefEntry.Normal(_writer.Length, id.Generation);
        _writer.Write(bytes);

        if (bytes.Length > 0 && bytes[^1] != (byte)'\n')
            _writer.WriteNewLine();
    }

    /// <summary>Writes the cross-reference data and the trailer, closing the file.</summary>
    /// <param name="trailer">The trailer entries; <c>Size</c> is filled in by the writer.</param>
    public void Finish(PdfDictionary trailer)
    {
        ArgumentNullException.ThrowIfNull(trailer);
        ObjectDisposedException.ThrowIf(_finished, this);

        FlushObjectStream();

        if (_previousXref >= 0)
            trailer.Set(PdfNames.Prev, PdfValue.Integer(_previousXref));

        if (_useObjectStreams || NeedsWideOffsets())
            WriteXrefStream(trailer);
        else
            WriteXrefTable(trailer);

        _finished = true;
        _writer.Flush();
    }

    /// <inheritdoc />
    public void Dispose() => _writer.Dispose();

    private void WriteHeader()
    {
        _writer.Write("%PDF-"u8);
        _writer.Write(Version.Major);
        _writer.WriteByte((byte)'.');
        _writer.Write(Version.Minor);
        _writer.WriteNewLine();

        // A comment of high bytes tells transfer tools the file is binary (§7.5.2).
        _writer.Write("%\u00E2\u00E3\u00CF\u00D3\n"u8);
    }

    private bool CanCompress(PdfObjectId id, PdfValue value) =>
        _useObjectStreams &&
        id.Generation == 0 &&
        value.Kind is not PdfValueKind.Stream &&
        _serializer.Encryption is null;

    private void WriteDirect(PdfObjectId id, PdfValue value)
    {
        _entries[id.Number] = XrefEntry.Normal(_writer.Length, id.Generation);
        _serializer.WriteIndirectObject(id, value);
    }

    private byte[] SerializeToArray(PdfValue value)
    {
        using var buffer = new MemoryStream(256);
        using (var scratch = new Utf8Writer(buffer))
        {
            var serializer = new PdfObjectSerializer(scratch) { CurrentObject = default };
            serializer.WriteValue(value);
        }

        return buffer.ToArray();
    }

    private void FlushObjectStream()
    {
        if (_pending.Count == 0)
            return;

        var header = new MemoryStream(_pending.Count * 12);
        var body = new MemoryStream(_pending.Count * 64);

        using (var headerWriter = new Utf8Writer(header))
        {
            foreach ((PdfObjectId id, byte[] bytes) in _pending)
            {
                headerWriter.Write(id.Number);
                headerWriter.WriteSpace();
                headerWriter.Write(body.Length);
                headerWriter.WriteSpace();
                body.Write(bytes);
                body.WriteByte((byte)'\n');
            }
        }

        byte[] headerBytes = header.ToArray();
        byte[] bodyBytes = body.ToArray();
        byte[] payload = new byte[headerBytes.Length + bodyBytes.Length];
        headerBytes.CopyTo(payload, 0);
        bodyBytes.CopyTo(payload, headerBytes.Length);

        PdfObjectId containerId = Reserve();
        var container = PdfStream.FromData(payload, _options.Compress);
        container.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.ObjStm));
        container.Dictionary.Set(PdfNames.N, PdfValue.Integer(_pending.Count));
        container.Dictionary.Set(PdfNames.First, PdfValue.Integer(headerBytes.Length));

        for (int i = 0; i < _pending.Count; i++)
            _entries[_pending[i].Id.Number] = XrefEntry.Compressed(containerId.Number, i);

        _pending.Clear();
        WriteDirect(containerId, PdfValue.Stream(container));
    }

    private bool NeedsWideOffsets() => _writer.Length > 0xFFFFFFFFL;

    private void WriteXrefTable(PdfDictionary trailer)
    {
        long start = _writer.Length;

        _writer.Write("xref\n"u8);

        foreach ((int first, int count) in Subsections())
        {
            _writer.Write(first);
            _writer.WriteSpace();
            _writer.Write(count);
            _writer.WriteNewLine();

            for (int number = first; number < first + count; number++)
            {
                if (number == 0 || !_entries.TryGetValue(number, out XrefEntry entry))
                {
                    _writer.Write("0000000000 65535 f \n"u8);
                    continue;
                }

                _writer.WritePadded(entry.Offset, 10);
                _writer.WriteSpace();
                _writer.WritePadded(entry.Generation, 5);
                _writer.Write(" n \n"u8);
            }
        }

        trailer.Set(PdfNames.Size, PdfValue.Integer(_nextFreeNumber));
        trailer.Remove(PdfNames.W);
        trailer.Remove(PdfNames.Index);

        _writer.Write("trailer\n"u8);
        _serializer.CurrentObject = default;

        StandardSecurityHandler? encryption = _serializer.Encryption;
        _serializer.Encryption = null;
        _serializer.WriteValue(PdfValue.Dictionary(trailer));
        _serializer.Encryption = encryption;

        WriteTail(start);
    }

    private void WriteXrefStream(PdfDictionary trailer)
    {
        PdfObjectId id = Reserve();
        long start = _writer.Length;
        _entries[id.Number] = XrefEntry.Normal(start, 0);

        int offsetWidth = _writer.Length > 0xFFFFFF ? (_writer.Length > 0xFFFFFFFFL ? 8 : 4) : 3;
        int size = _nextFreeNumber;

        List<(int First, int Count)> subsections = [.. Subsections()];
        int rows = 0;
        foreach ((int _, int count) in subsections)
            rows += count;

        int rowLength = 1 + offsetWidth + 2;
        byte[] data = new byte[rows * rowLength];
        int position = 0;

        foreach ((int first, int count) in subsections)
        {
            for (int number = first; number < first + count; number++)
            {
                Span<byte> row = data.AsSpan(position, rowLength);
                position += rowLength;

                if (number == 0 || !_entries.TryGetValue(number, out XrefEntry entry))
                {
                    row[0] = 0;
                    WriteField(row.Slice(1, offsetWidth), 0);
                    WriteField(row[(1 + offsetWidth)..], 0xFFFF);
                    continue;
                }

                if (entry.Type == XrefEntryType.Compressed)
                {
                    row[0] = 2;
                    WriteField(row.Slice(1, offsetWidth), entry.Offset);
                    WriteField(row[(1 + offsetWidth)..], entry.Generation);
                }
                else
                {
                    row[0] = 1;
                    WriteField(row.Slice(1, offsetWidth), entry.Offset);
                    WriteField(row[(1 + offsetWidth)..], entry.Generation);
                }
            }
        }

        var index = new PdfArray(subsections.Count * 2);
        foreach ((int first, int count) in subsections)
        {
            index.Add(PdfValue.Integer(first));
            index.Add(PdfValue.Integer(count));
        }

        var stream = PdfStream.FromData(data, _options.Compress);
        PdfDictionary dictionary = stream.Dictionary;

        foreach (KeyValuePair<PdfName, PdfValue> entry in trailer)
        {
            if (entry.Key != PdfNames.Length && entry.Key != PdfNames.Filter)
                dictionary.Set(entry.Key, entry.Value);
        }

        dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XRef));
        dictionary.Set(PdfNames.Size, PdfValue.Integer(size));
        dictionary.Set(PdfNames.Index, PdfValue.Array(index));
        dictionary.Set(PdfNames.W, PdfValue.Array(PdfArray.OfNumbers(1, offsetWidth, 2)));

        StandardSecurityHandler? encryption = _serializer.Encryption;
        _serializer.Encryption = null;
        _serializer.WriteIndirectObject(id, PdfValue.Stream(stream));
        _serializer.Encryption = encryption;

        WriteTail(start);
    }

    private void WriteTail(long xrefStart)
    {
        _writer.Write("\nstartxref\n"u8);
        _writer.Write(xrefStart);
        _writer.Write("\n%%EOF\n"u8);
    }

    private IEnumerable<(int First, int Count)> Subsections()
    {
        // Object zero is always the head of the free list, so every file starts with it.
        int first = 0;
        int previous = 0;
        int count = 1;

        foreach (int number in _entries.Keys)
        {
            if (number == previous + 1)
            {
                previous = number;
                count++;
                continue;
            }

            yield return (first, count);
            first = number;
            previous = number;
            count = 1;
        }

        yield return (first, count);
    }

    private static void WriteField(Span<byte> destination, long value)
    {
        for (int i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= 8;
        }
    }
}
