using Inkwright.Cos;

namespace Inkwright.Filters;

/// <summary>
/// Runs the <c>Filter</c> chain of a stream dictionary. Image codecs are deliberately not decoded:
/// the pipeline stops at the first one and hands back the bytes in that codec's own format, which
/// is exactly what an image consumer wants and what a rewrite needs to copy through untouched.
/// </summary>
public static class PdfFilterPipeline
{
    /// <summary>Decodes a stream payload according to its dictionary.</summary>
    /// <param name="raw">The payload as stored in the file, already decrypted.</param>
    /// <param name="dictionary">The stream dictionary holding <c>Filter</c> and <c>DecodeParms</c>.</param>
    /// <param name="undecodedFilter">Receives the image codec that stopped the chain, if any.</param>
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

            if (IsImageCodec(filter))
            {
                undecodedFilter = filter;
                return data;
            }

            data = ApplyOne(data, filter, parms);
        }

        return data;
    }

    /// <summary>Whether a filter name denotes an image codec the library passes through.</summary>
    /// <param name="filter">The filter name.</param>
    public static bool IsImageCodec(PdfName filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Value is "DCTDecode" or "DCT" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode" or "CCF";
    }

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
}
