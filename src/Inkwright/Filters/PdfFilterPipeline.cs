using System.Runtime.InteropServices;
using Inkwright.Cos;

namespace Inkwright.Filters;

/// <summary>The outcome of decoding an untrusted stream under a per-call resource bound.</summary>
internal enum PdfBoundedDecodeStatus
{
    Complete,
    LimitExceeded,
    UndecodedFilter,
    FilterChainLimit,
    InvalidParameters,
    InvalidData,
}

/// <summary>The independent bound that stopped a bounded decode.</summary>
internal enum PdfBoundedDecodeLimit
{
    None,
    StreamOutput,
    RetainedOutput,
    Work,
}

/// <summary>A non-cached bounded decode result.</summary>
internal readonly record struct PdfBoundedDecodeResult(
    ReadOnlyMemory<byte> Data,
    PdfBoundedDecodeStatus Status,
    PdfName? TerminalFilter = null,
    long ProducedBytes = 0,
    PdfBoundedDecodeLimit ExceededLimit = PdfBoundedDecodeLimit.None);

/// <summary>
/// Runs the <c>Filter</c> chain of a stream dictionary. Image codecs are deliberately not decoded:
/// the pipeline stops at the first one and hands back the bytes in that codec's own format, which
/// is exactly what an image consumer wants and what a rewrite needs to copy through untouched.
/// </summary>
public static class PdfFilterPipeline
{
    private const int MaxBoundedFilterStages = 64;

    /// <summary>Decodes a stream payload according to its dictionary.</summary>
    /// <param name="raw">The payload as stored in the file, already decrypted.</param>
    /// <param name="dictionary">The stream dictionary holding <c>Filter</c> and <c>DecodeParms</c>.</param>
    /// <param name="undecodedFilter">Receives the image codec or unsupported filter that stopped the chain, if any.</param>
    /// <returns>The decoded payload.</returns>
    public static ReadOnlyMemory<byte> Decode(
        ReadOnlyMemory<byte> raw,
        PdfDictionary dictionary,
        out PdfName? undecodedFilter)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        undecodedFilter = null;

        PdfValue filterValue = dictionary.GetFirst(PdfNames.Filter, PdfName.Get("F"));

        // A file specification under /F is a external-file reference, not a filter chain.
        if (filterValue.Kind is not (PdfValueKind.Name or PdfValueKind.Array))
            return raw;

        ReadOnlyMemory<byte> data = raw;
        int index = 0;

        foreach (PdfValue entry in Enumerate(filterValue, dictionary))
        {
            PdfName? filter = entry.AsName;
            if (filter is null)
                continue;

            PdfDictionary? parms = GetDecodeParms(dictionary, index++);

            if (!IsCompatibilitySupportedFilter(filter))
            {
                undecodedFilter = filter;
                return data;
            }

            if (IsImageCodec(filter))
            {
                undecodedFilter = filter;
                return data;
            }

            data = ApplyOne(data, filter, parms);
        }

        return data;
    }

    /// <summary>
    /// Decodes an untrusted payload without producing more than <paramref name="maxDecodedBytes"/>
    /// bytes, caching a truncated value, or silently treating an unsupported filter as decoded.
    /// </summary>
    internal static PdfBoundedDecodeResult DecodeBounded(
        ReadOnlyMemory<byte> raw,
        PdfDictionary dictionary,
        int maxDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        int effectiveLimit = Math.Min(maxDecodedBytes, PdfFilterLimits.Cap);

        PdfValue filterValue = dictionary.Get(PdfNames.Filter);
        if (filterValue.Kind is not (PdfValueKind.Name or PdfValueKind.Array))
        {
            bool malformedFilter =
                dictionary.ContainsKey(PdfNames.Filter) || dictionary.ContainsKey(PdfName.Get("F"));
            if (malformedFilter)
            {
                return new PdfBoundedDecodeResult(
                    raw.Length > effectiveLimit ? raw[..effectiveLimit] : raw,
                    PdfBoundedDecodeStatus.UndecodedFilter);
            }

            return raw.Length > effectiveLimit
                ? new PdfBoundedDecodeResult(
                    raw[..effectiveLimit],
                    PdfBoundedDecodeStatus.LimitExceeded,
                    ExceededLimit: PdfBoundedDecodeLimit.StreamOutput)
                : new PdfBoundedDecodeResult(raw, PdfBoundedDecodeStatus.Complete);
        }

        ReadOnlyMemory<byte> data = raw;
        int index = 0;
        int stages = 0;
        int filterCount = filterValue.AsArray?.Count ?? 1;
        long producedBytes = 0;

        // Inspect the whole bounded chain before materializing its first stage. This prevents a
        // terminal image codec, a late Crypt, an unsupported suffix, or an oversized array from
        // hiding beyond an otherwise valid prefix.
        if (filterCount > MaxBoundedFilterStages)
        {
            return new PdfBoundedDecodeResult(
                data,
                PdfBoundedDecodeStatus.FilterChainLimit);
        }

        for (int i = 0; i < filterCount; i++)
        {
            PdfValue entry = filterValue.AsArray is { } entries ? entries.Get(i) : filterValue;
            PdfName? filter = entry.AsName;
            if (filter is null || !IsSupportedFilter(filter))
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.UndecodedFilter,
                    filter);
            }

            if (filter.Value == "Crypt" && i != 0)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.InvalidParameters,
                    filter);
            }

            if (IsImageCodec(filter) && i != filterCount - 1)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.InvalidParameters,
                    filter);
            }
        }

        if (!HasValidBoundedDecodeParmsShape(dictionary, filterCount))
        {
            return new PdfBoundedDecodeResult(
                data,
                PdfBoundedDecodeStatus.InvalidParameters);
        }

        foreach (PdfValue entry in Enumerate(filterValue, dictionary))
        {
            if (++stages > MaxBoundedFilterStages)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.FilterChainLimit,
                    ProducedBytes: producedBytes);
            }

            PdfName? filter = entry.AsName;
            if (filter is null || !IsSupportedFilter(filter))
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.UndecodedFilter,
                    filter,
                    producedBytes);
            }

            // ISO 32000-2 §7.4.10 requires Crypt to be the first filter. A later (or second)
            // identity Crypt cannot be treated as a harmless no-op in a fail-closed decode.
            if (filter.Value == "Crypt" && stages != 1)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.InvalidParameters,
                    filter,
                    producedBytes);
            }

            if (IsImageCodec(filter))
            {
                if (stages != filterCount)
                {
                    return new PdfBoundedDecodeResult(
                        data,
                        PdfBoundedDecodeStatus.InvalidParameters,
                        filter,
                        producedBytes);
                }

                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.UndecodedFilter,
                    filter,
                    producedBytes);
            }

            if (!TryGetBoundedDecodeParms(dictionary, index++, out PdfDictionary? parms) ||
                !HasValidPredictorParameters(parms))
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.InvalidParameters,
                    filter,
                    producedBytes);
            }

            // Crypt is an identity operation here: the reader has already decrypted the stream.
            // It consumes a stage but materializes no new bytes.
            if (filter.Value == "Crypt")
                continue;

            long remainingWork = effectiveLimit - producedBytes;
            if (remainingWork <= 0)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.LimitExceeded,
                    ProducedBytes: producedBytes,
                    ExceededLimit: PdfBoundedDecodeLimit.Work);
            }

            data = ApplyOneBounded(
                data,
                filter,
                parms,
                effectiveLimit,
                remainingWork,
                strict: true,
                out PdfBoundedDecodeLimit exceededLimit,
                out bool invalidData,
                out long stageProducedBytes);
            producedBytes += stageProducedBytes;
            if (invalidData)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.InvalidData,
                    filter,
                    producedBytes);
            }

            if (exceededLimit != PdfBoundedDecodeLimit.None)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.LimitExceeded,
                    ProducedBytes: producedBytes,
                    ExceededLimit: exceededLimit);
            }
        }

        return data.Length > effectiveLimit
            ? new PdfBoundedDecodeResult(
                data[..effectiveLimit],
                PdfBoundedDecodeStatus.LimitExceeded,
                ProducedBytes: producedBytes,
                ExceededLimit: PdfBoundedDecodeLimit.StreamOutput)
            : new PdfBoundedDecodeResult(
                data,
                PdfBoundedDecodeStatus.Complete,
                ProducedBytes: producedBytes);
    }

    /// <summary>
    /// Applies the compatibility decoder under explicit output/work and filter-stage bounds. It
    /// preserves the public decoder's tolerance for aliases and damaged encodings, but reports a
    /// resource boundary instead of returning and caching a truncated prefix.
    /// </summary>
    internal static PdfBoundedDecodeResult DecodeCompatibilityBounded(
        ReadOnlyMemory<byte> raw,
        PdfDictionary dictionary,
        int maxDecodedBytes,
        int maxRetainedBytes,
        long maxDecodeWorkBytes,
        int maxFilterStages)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetainedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodeWorkBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFilterStages);

        // PdfResourceBudget already selected and named the exact request/global/runtime ceiling for
        // this call. Re-reading the mutable process-wide setting here would race that classification.
        int effectiveLimit = maxDecodedBytes;
        PdfValue filterValue = dictionary.GetFirst(PdfNames.Filter, PdfName.Get("F"));
        if (filterValue.Kind is not (PdfValueKind.Name or PdfValueKind.Array))
        {
            return raw.Length > maxRetainedBytes
                ? new PdfBoundedDecodeResult(
                    raw[..maxRetainedBytes],
                    PdfBoundedDecodeStatus.LimitExceeded,
                    ExceededLimit: PdfBoundedDecodeLimit.RetainedOutput)
                : new PdfBoundedDecodeResult(raw, PdfBoundedDecodeStatus.Complete);
        }

        int declaredStages = filterValue.AsArray?.Count ?? 1;
        if (declaredStages > maxFilterStages)
        {
            return new PdfBoundedDecodeResult(
                raw,
                PdfBoundedDecodeStatus.FilterChainLimit);
        }

        ReadOnlyMemory<byte> data = raw;
        int index = 0;
        int stages = 0;
        long producedBytes = 0;

        foreach (PdfValue entry in Enumerate(filterValue, dictionary))
        {
            PdfName? filter = entry.AsName;
            if (filter is null)
                continue;

            if (++stages > maxFilterStages)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.FilterChainLimit,
                    ProducedBytes: producedBytes);
            }

            PdfDictionary? parms = GetDecodeParms(dictionary, index++);
            if (!IsCompatibilitySupportedFilter(filter) || IsImageCodec(filter))
            {
                if (data.Length > maxRetainedBytes)
                {
                    return new PdfBoundedDecodeResult(
                        data[..maxRetainedBytes],
                        PdfBoundedDecodeStatus.LimitExceeded,
                        filter,
                        producedBytes,
                        PdfBoundedDecodeLimit.RetainedOutput);
                }

                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.UndecodedFilter,
                    filter,
                    producedBytes);
            }

            long remainingWork = maxDecodeWorkBytes - producedBytes;
            if (remainingWork <= 0)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.LimitExceeded,
                    filter,
                    producedBytes,
                    PdfBoundedDecodeLimit.Work);
            }

            data = ApplyOneBounded(
                data,
                filter,
                parms,
                effectiveLimit,
                remainingWork,
                strict: false,
                out PdfBoundedDecodeLimit exceededLimit,
                out bool invalidData,
                out long stageProducedBytes);
            producedBytes += stageProducedBytes;

            if (invalidData)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.InvalidData,
                    filter,
                    producedBytes);
            }

            if (exceededLimit != PdfBoundedDecodeLimit.None)
            {
                return new PdfBoundedDecodeResult(
                    data,
                    PdfBoundedDecodeStatus.LimitExceeded,
                    filter,
                    producedBytes,
                    exceededLimit);
            }
        }

        return data.Length > maxRetainedBytes
            ? new PdfBoundedDecodeResult(
                data[..maxRetainedBytes],
                PdfBoundedDecodeStatus.LimitExceeded,
                ProducedBytes: producedBytes,
                ExceededLimit: PdfBoundedDecodeLimit.RetainedOutput)
            : new PdfBoundedDecodeResult(
                data,
                PdfBoundedDecodeStatus.Complete,
                ProducedBytes: producedBytes);
    }

    /// <summary>Whether a filter name denotes an image codec the library passes through.</summary>
    /// <param name="filter">The filter name.</param>
    public static bool IsImageCodec(PdfName filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Value is "DCTDecode" or "DCT" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode" or "CCF";
    }

    /// <summary>
    /// Whether a filter is a terminal image codec under ordinary-stream syntax. Abbreviations from
    /// the inline-image table are deliberately excluded.
    /// </summary>
    internal static bool IsStrictImageCodec(PdfName filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Value is "DCTDecode" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode";
    }

    private static bool IsCompatibilitySupportedFilter(PdfName filter) => filter.Value is
        "FlateDecode" or "Fl" or
        "LZWDecode" or "LZW" or
        "ASCII85Decode" or "A85" or
        "ASCIIHexDecode" or "AHx" or
        "RunLengthDecode" or "RL" or
        "BrotliDecode" or "Crypt" or
        "DCTDecode" or "DCT" or "JPXDecode" or "JBIG2Decode" or
        "CCITTFaxDecode" or "CCF";

    private static bool IsSupportedFilter(PdfName filter) => filter.Value is
        "FlateDecode" or
        "LZWDecode" or
        "ASCII85Decode" or
        "ASCIIHexDecode" or
        "RunLengthDecode" or
        "BrotliDecode" or "Crypt" or
        "DCTDecode" or "JPXDecode" or "JBIG2Decode" or
        "CCITTFaxDecode";

    private static IEnumerable<PdfValue> Enumerate(PdfValue filterValue, PdfDictionary dictionary)
    {
        if (filterValue.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
                yield return array.Get(i);
        }
        else
        {
            _ = dictionary;
            yield return filterValue;
        }
    }

    private static PdfDictionary? GetDecodeParms(PdfDictionary dictionary, int index)
    {
        PdfValue parms = dictionary.GetFirst(PdfNames.DecodeParms, PdfName.Get("DP"), PdfName.Get("FDecodeParms"));

        if (parms.AsArray is { } array)
            return index < array.Count ? array.Get(index).AsDictionary : null;

        return index == 0 ? parms.AsDictionary : null;
    }

    private static bool TryGetBoundedDecodeParms(
        PdfDictionary dictionary,
        int index,
        out PdfDictionary? parms)
    {
        PdfValue value = dictionary.Get(PdfNames.DecodeParms);

        if (value.IsNull)
        {
            parms = null;
            return true;
        }

        if (value.Kind == PdfValueKind.Array)
        {
            PdfArray array = value.AsArray!;
            if (index >= array.Count || array.Get(index).IsNull)
            {
                parms = null;
                return true;
            }

            PdfValue entry = array.Get(index);
            parms = entry.Kind == PdfValueKind.Dictionary ? entry.AsDictionary : null;
            return parms is not null;
        }

        parms = index == 0 && value.Kind == PdfValueKind.Dictionary ? value.AsDictionary : null;
        return parms is not null;
    }

    private static bool HasValidBoundedDecodeParmsShape(
        PdfDictionary dictionary,
        int filterCount)
    {
        PdfValue value = dictionary.Get(PdfNames.DecodeParms);

        if (value.IsNull)
            return true;

        if (filterCount == 1)
            return value.Kind == PdfValueKind.Dictionary;

        if (value.Kind != PdfValueKind.Array || value.AsArray!.Count != filterCount)
            return false;

        PdfArray array = value.AsArray!;
        for (int i = 0; i < array.Count; i++)
        {
            PdfValue entry = array.Get(i);
            if (!entry.IsNull && entry.Kind != PdfValueKind.Dictionary)
                return false;
        }

        return true;
    }

    private static bool HasValidPredictorParameters(PdfDictionary? parms)
    {
        if (parms?.ContainsKey(PdfNames.Predictor) != true)
            return true;

        PdfValue predictorValue = parms.Get(PdfNames.Predictor);
        if (predictorValue.Kind != PdfValueKind.Integer)
            return false;

        long predictor = predictorValue.AsInteger;
        if (predictor is not (1 or 2) && predictor is < 10 or > 15)
            return false;

        return IsPositiveInt(PdfNames.Colors) &&
               IsPositiveInt(PdfNames.Columns) &&
               IsBitsPerComponent();

        bool IsPositiveInt(PdfName key)
        {
            if (!parms.ContainsKey(key))
                return true;

            PdfValue value = parms.Get(key);
            return value.Kind == PdfValueKind.Integer && value.AsInteger is > 0 and <= int.MaxValue;
        }

        bool IsBitsPerComponent()
        {
            if (!parms.ContainsKey(PdfNames.BitsPerComponent))
                return true;

            PdfValue value = parms.Get(PdfNames.BitsPerComponent);
            return value.Kind == PdfValueKind.Integer && value.AsInteger is 1 or 2 or 4 or 8 or 16;
        }
    }

    private static ReadOnlyMemory<byte> ApplyOne(ReadOnlyMemory<byte> data, PdfName filter, PdfDictionary? parms)
    {
        byte[] decoded = filter.Value switch
        {
            "FlateDecode" or "Fl" => FlateFilter.Decode(data.Span),
            "LZWDecode" or "LZW" => LzwFilter.Decode(data.Span, parms?.GetInteger(PdfNames.EarlyChange, 1) != 0),
            "ASCII85Decode" or "A85" => Ascii85Filter.Decode(data.Span),
            "ASCIIHexDecode" or "AHx" => AsciiHexFilter.Decode(data.Span),
            "RunLengthDecode" or "RL" => RunLengthFilter.Decode(data.Span),
            "BrotliDecode" => BrotliFilter.Decode(data.Span),
            "Crypt" => data.ToArray(),
            _ => data.ToArray(),
        };

        if (parms is null)
            return decoded;

        int predictor = (int)parms.GetInteger(PdfNames.Predictor, 1);
        if (predictor <= 1)
            return decoded;

        return Predictor.Undo(
            decoded,
            predictor,
            (int)parms.GetInteger(PdfNames.Colors, 1),
            (int)parms.GetInteger(PdfNames.BitsPerComponent, 8),
            (int)parms.GetInteger(PdfNames.Columns, 1));
    }

    private static ReadOnlyMemory<byte> ApplyOneBounded(
        ReadOnlyMemory<byte> data,
        PdfName filter,
        PdfDictionary? parms,
        int maxOutputBytes,
        long maxWorkBytes,
        bool strict,
        out PdfBoundedDecodeLimit exceededLimit,
        out bool invalidData,
        out long producedBytes)
    {
        exceededLimit = PdfBoundedDecodeLimit.None;
        invalidData = false;
        producedBytes = 0;
        int inputCopyBytes = 0;
        if (filter.Value is "FlateDecode" or "Fl" or "BrotliDecode" &&
            !MemoryMarshal.TryGetArray(data, out _))
        {
            inputCopyBytes = data.Length;
            bool outputExceeded = inputCopyBytes > maxOutputBytes;
            bool workExhausted = inputCopyBytes >= maxWorkBytes;
            if (outputExceeded || workExhausted)
            {
                exceededLimit = workExhausted && (!outputExceeded || maxWorkBytes < maxOutputBytes)
                    ? PdfBoundedDecodeLimit.Work
                    : PdfBoundedDecodeLimit.StreamOutput;
                return data[..Math.Min(data.Length, maxOutputBytes)];
            }

            data = data.ToArray();
        }

        long decoderWorkBytes = maxWorkBytes - inputCopyBytes;
        if (decoderWorkBytes <= 0)
        {
            exceededLimit = PdfBoundedDecodeLimit.Work;
            return data;
        }

        int outputBudget = (int)Math.Min(maxOutputBytes, decoderWorkBytes);
        bool limitExceeded;
        byte[] decoded;
        switch (filter.Value)
        {
            case "FlateDecode":
            case "Fl":
                decoded = strict
                    ? FlateFilter.Decode(data, outputBudget, out limitExceeded, out invalidData)
                    : FlateFilter.DecodeCompatible(data, outputBudget, out limitExceeded, out invalidData);
                break;

            case "LZWDecode":
            case "LZW":
                decoded = strict
                    ? LzwFilter.Decode(
                        data.Span,
                        parms?.GetInteger(PdfNames.EarlyChange, 1) != 0,
                        outputBudget,
                        out limitExceeded,
                        out invalidData)
                    : LzwFilter.DecodeCompatible(
                        data.Span,
                        parms?.GetInteger(PdfNames.EarlyChange, 1) != 0,
                        outputBudget,
                        out limitExceeded,
                        out invalidData);
                break;

            case "ASCII85Decode":
            case "A85":
                decoded = strict
                    ? Ascii85Filter.Decode(data.Span, outputBudget, out limitExceeded, out invalidData)
                    : Ascii85Filter.DecodeCompatible(data.Span, outputBudget, out limitExceeded, out invalidData);
                break;

            case "ASCIIHexDecode":
            case "AHx":
                decoded = strict
                    ? AsciiHexFilter.Decode(data.Span, outputBudget, out limitExceeded, out invalidData)
                    : AsciiHexFilter.DecodeCompatible(data.Span, outputBudget, out limitExceeded, out invalidData);
                break;

            case "RunLengthDecode":
            case "RL":
                decoded = strict
                    ? RunLengthFilter.Decode(data.Span, outputBudget, out limitExceeded, out invalidData)
                    : RunLengthFilter.DecodeCompatible(data.Span, outputBudget, out limitExceeded, out invalidData);
                break;

            case "BrotliDecode":
                decoded = BrotliFilter.Decode(data, outputBudget, out limitExceeded);
                break;

            default: // Crypt is an identity transform after the reader has decrypted the bytes.
                if (data.Length > maxOutputBytes)
                {
                    exceededLimit = PdfBoundedDecodeLimit.StreamOutput;
                    return data[..maxOutputBytes];
                }

                return data;
        }

        producedBytes = (long)inputCopyBytes + decoded.Length;

        if (limitExceeded)
        {
            exceededLimit = decoderWorkBytes < maxOutputBytes
                ? PdfBoundedDecodeLimit.Work
                : PdfBoundedDecodeLimit.StreamOutput;
            return decoded;
        }

        if (invalidData || parms is null)
            return decoded;

        int predictor = (int)parms.GetInteger(PdfNames.Predictor, 1);
        if (predictor <= 1)
            return decoded;

        long predictorWorkBytes = maxWorkBytes - producedBytes;
        if (predictorWorkBytes <= 0)
        {
            exceededLimit = PdfBoundedDecodeLimit.Work;
            return decoded;
        }

        int predictorBudget = (int)Math.Min(maxOutputBytes, predictorWorkBytes);

        byte[] predicted = strict
            ? Predictor.Undo(
                decoded,
                predictor,
                (int)parms.GetInteger(PdfNames.Colors, 1),
                (int)parms.GetInteger(PdfNames.BitsPerComponent, 8),
                (int)parms.GetInteger(PdfNames.Columns, 1),
                predictorBudget,
                out limitExceeded,
                out invalidData)
            : Predictor.UndoCompatible(
                decoded,
                predictor,
                (int)parms.GetInteger(PdfNames.Colors, 1),
                (int)parms.GetInteger(PdfNames.BitsPerComponent, 8),
                (int)parms.GetInteger(PdfNames.Columns, 1),
                predictorBudget,
                out limitExceeded,
                out invalidData);
        producedBytes += predicted.Length;
        if (limitExceeded)
        {
            exceededLimit = predictorWorkBytes < maxOutputBytes
                ? PdfBoundedDecodeLimit.Work
                : PdfBoundedDecodeLimit.StreamOutput;
        }

        return predicted;
    }
}
