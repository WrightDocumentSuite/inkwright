using Inkwright.Filters;
using Inkwright.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Inkwright.Cos;

/// <summary>
/// Overrides stream decoding for documents opened under an internal validation policy. The public
/// reader leaves this unset and retains its compatibility-oriented decoder.
/// </summary>
internal interface IPdfStreamDecodePolicy
{
    /// <summary>Maximum xref/object-stream entries the protected reader may materialize.</summary>
    int MaxStructuralEntries { get; }

    /// <summary>Charges xref and object-stream entry allocations across the validation tree.</summary>
    void ConsumeStructuralEntries(int count);

    /// <summary>Charges values before the protected reader allocates parser containers.</summary>
    void ConsumeParsedValues(int count);

    ReadOnlyMemory<byte> Decode(PdfStream stream, out PdfName? undecodedFilter);

    /// <summary>Strictly decodes a stream under an additional caller-supplied output bound.</summary>
    PdfBoundedDecodeResult DecodeBounded(PdfStream stream, int maxDecodedBytes);
}

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
    private readonly IPdfStreamDecodePolicy? _decodePolicy;
    private bool _decodedByPolicy;
    private ExceptionDispatchInfo? _decodePolicyFailure;

    /// <summary>Creates a stream with an empty payload.</summary>
    public PdfStream() : this(new PdfDictionary(), ReadOnlyMemory<byte>.Empty)
    {
    }

    /// <summary>Creates a stream around an existing dictionary and its encoded payload.</summary>
    /// <param name="dictionary">The stream dictionary; the instance is used as-is.</param>
    /// <param name="rawData">The payload exactly as it appears between <c>stream</c> and <c>endstream</c>.</param>
    public PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> rawData)
        : this(dictionary, rawData, decodePolicy: null)
    {
    }

    internal PdfStream(
        PdfDictionary dictionary,
        ReadOnlyMemory<byte> rawData,
        IPdfStreamDecodePolicy? decodePolicy)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        Dictionary = dictionary;
        _raw = rawData;
        _decodePolicy = decodePolicy;
    }

    /// <summary>The stream dictionary, holding at least <c>Length</c> and any <c>Filter</c>.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The number of bytes in the encoded payload.</summary>
    public int RawLength => _raw.Length;

    /// <summary>The encoded payload, as written in the file.</summary>
    public ReadOnlyMemory<byte> RawData => _raw;

    /// <summary>
    /// The filter that stopped the decode pipeline, set after <see cref="Decode"/> when the payload
    /// ends in an image codec such as <c>DCTDecode</c> or an unsupported filter.
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

        _decodePolicyFailure?.Throw();

        ReadOnlyMemory<byte> result;
        if (_decodePolicy is null)
        {
            result = PdfFilterPipeline.Decode(_raw, Dictionary, out _undecodedFilter);
            _decodedByPolicy = false;
        }
        else
        {
            try
            {
                result = _decodePolicy.Decode(this, out _undecodedFilter);
                _decodedByPolicy = true;
            }
            catch (PdfFormatException exception)
            {
                // A lazy object may be reached through several graph paths. Re-throw the same
                // policy failure without charging its raw bytes or decode work again.
                _decodePolicyFailure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }

        _decoded = result;
        return result;
    }

    /// <summary>
    /// Decodes under a per-call output bound without caching either the result or a truncated
    /// prefix. Recursive validators use this for untrusted embedded streams.
    /// </summary>
    internal PdfBoundedDecodeResult DecodeBounded(int maxDecodedBytes)
        => PdfFilterPipeline.DecodeBounded(_raw, Dictionary, maxDecodedBytes);

    /// <summary>
    /// Decodes under a caller bound while preserving the protected-reader policy. Only a complete
    /// result is cached; a truncated prefix must never become the stream's normal decoded value.
    /// </summary>
    internal PdfBoundedDecodeResult DecodeBoundedWithPolicy(int maxDecodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);

        // SetData caches the plaintext that matched the dictionary at that instant, but callers
        // can subsequently mutate Filter or DecodeParms through the public Dictionary property.
        // A protected read may reuse only bytes produced by that same policy; every other cache
        // is re-decoded from the raw payload and the dictionary as they exist now.
        if (_decoded is { } cached && _decodedByPolicy)
        {
            if (cached.Length > maxDecodedBytes)
            {
                return new PdfBoundedDecodeResult(
                    cached[..maxDecodedBytes],
                    PdfBoundedDecodeStatus.LimitExceeded);
            }

            return new PdfBoundedDecodeResult(
                cached,
                _undecodedFilter is null
                    ? PdfBoundedDecodeStatus.Complete
                    : PdfBoundedDecodeStatus.UndecodedFilter,
                _undecodedFilter);
        }

        _decodePolicyFailure?.Throw();

        PdfBoundedDecodeResult result;
        if (_decodePolicy is null)
        {
            result = DecodeBounded(maxDecodedBytes);
        }
        else
        {
            try
            {
                result = _decodePolicy.DecodeBounded(this, maxDecodedBytes);
            }
            catch (PdfFormatException exception)
            {
                _decodePolicyFailure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }

        if (result.Status == PdfBoundedDecodeStatus.Complete)
        {
            _decoded = result.Data;
            _undecodedFilter = null;
            _decodedByPolicy = _decodePolicy is not null;
        }

        return result;
    }

    /// <summary>
    /// Returns a prior policy-validated decode so a stream aliased into an EF dictionary is not
    /// charged and decoded a second time by recursive attachment validation.
    /// </summary>
    internal bool TryGetPolicyDecoded(out PdfBoundedDecodeResult result)
    {
        if (!_decodedByPolicy || _decoded is not { } decoded)
        {
            result = default;
            return false;
        }

        result = new PdfBoundedDecodeResult(
            decoded,
            _undecodedFilter is null
                ? PdfBoundedDecodeStatus.Complete
                : PdfBoundedDecodeStatus.UndecodedFilter,
            _undecodedFilter);
        return true;
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
        _decodedByPolicy = false;
        _decodePolicyFailure = null;
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
        _decodedByPolicy = false;
        _decodePolicyFailure = null;
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
