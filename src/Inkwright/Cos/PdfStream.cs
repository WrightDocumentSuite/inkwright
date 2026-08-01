using Inkwright.Filters;

namespace Inkwright.Cos;

/// <summary>
/// A PDF stream object (ISO 32000-1 §7.3.8): a dictionary plus a byte payload that is stored
/// encoded and decoded on demand. Decryption happens while reading, so the payload held here is
/// always plaintext with respect to document security.
/// </summary>
public sealed class PdfStream
{
    private ReadOnlyMemory<byte> _raw;
    private ReadOnlyMemory<byte>? _decoded;
    private PdfName? _undecodedFilter;

    /// <summary>Creates a stream with an empty payload.</summary>
    public PdfStream() : this(new PdfDictionary(), ReadOnlyMemory<byte>.Empty)
    {
    }

    /// <summary>Creates a stream around an existing dictionary and its encoded payload.</summary>
    /// <param name="dictionary">The stream dictionary; the instance is used as-is.</param>
    /// <param name="rawData">The payload exactly as it appears between <c>stream</c> and <c>endstream</c>.</param>
    public PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> rawData)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        Dictionary = dictionary;
        _raw = rawData;
    }

    /// <summary>The stream dictionary, holding at least <c>Length</c> and any <c>Filter</c>.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The number of bytes in the encoded payload.</summary>
    public int RawLength => _raw.Length;

    /// <summary>The encoded payload, as written in the file.</summary>
    public ReadOnlyMemory<byte> RawData => _raw;

    /// <summary>
    /// The filter that stopped the decode pipeline, set after <see cref="Decode"/> when the payload
    /// ends in an image codec such as <c>DCTDecode</c> that the library deliberately passes through.
    /// </summary>
    public PdfName? UndecodedFilter
    {
        get
        {
            _ = Decode();
            return _undecodedFilter;
        }
    }

    /// <summary>
    /// Decodes the payload, applying every filter the library understands and stopping at an image
    /// codec. The result is cached, so repeated calls are free.
    /// </summary>
    /// <returns>The decoded bytes, or the bytes as far as the pipeline got.</returns>
    public ReadOnlyMemory<byte> Decode()
    {
        if (_decoded is { } cached)
            return cached;

        ReadOnlyMemory<byte> result = PdfFilterPipeline.Decode(_raw, Dictionary, out _undecodedFilter);
        _decoded = result;
        return result;
    }

    /// <summary>Decodes the payload and returns it as a span.</summary>
    public ReadOnlySpan<byte> DecodedSpan() => Decode().Span;

    /// <summary>
    /// Replaces the payload with bytes that are already encoded the way the dictionary declares.
    /// </summary>
    /// <param name="rawData">The encoded payload.</param>
    public void SetRawData(ReadOnlyMemory<byte> rawData)
    {
        _raw = rawData;
        _decoded = null;
        _undecodedFilter = null;
        Dictionary.Set(PdfNames.Length, PdfValue.Integer(rawData.Length));
    }

    /// <summary>
    /// Replaces the payload with plaintext bytes, compressing them and declaring
    /// <c>/Filter /FlateDecode</c>, or storing them verbatim when compression is not wanted.
    /// </summary>
    /// <param name="data">The plaintext payload.</param>
    /// <param name="compress">Whether to deflate the payload.</param>
    public void SetData(ReadOnlyMemory<byte> data, bool compress = true)
    {
        Dictionary.Remove(PdfNames.DecodeParms);

        if (compress)
        {
            byte[] deflated = FlateFilter.Encode(data.Span);
            Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfNames.FlateDecode));
            _raw = deflated;
        }
        else
        {
            Dictionary.Remove(PdfNames.Filter);
            _raw = data;
        }

        Dictionary.Set(PdfNames.Length, PdfValue.Integer(_raw.Length));
        _decoded = data;
        _undecodedFilter = null;
    }

    /// <summary>Creates a stream holding the given plaintext payload.</summary>
    /// <param name="data">The plaintext payload.</param>
    /// <param name="compress">Whether to deflate the payload.</param>
    public static PdfStream FromData(ReadOnlyMemory<byte> data, bool compress = true)
    {
        var stream = new PdfStream();
        stream.SetData(data, compress);
        return stream;
    }

    /// <inheritdoc />
    public override string ToString() => $"stream({_raw.Length} bytes) {Dictionary}";
}
