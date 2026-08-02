using Inkwright.Cos;
using Inkwright.Syntax;

namespace Inkwright.Content;

/// <summary>
/// Reads the <c>BI … ID … EI</c> construct of ISO 32000-1 §8.9.7. Its length is not declared, so
/// uncompressed images are measured from their own geometry and compressed ones are found by
/// looking for an <c>EI</c> that stands alone.
/// </summary>
internal static class InlineImageReader
{
    internal static bool TryRead(
        ReadOnlySpan<byte> content, ref int position, IPdfResolver? resolver, out PdfValue image)
    {
        image = PdfValue.Null;

        var dictionary = new PdfDictionary(8) { Resolver = resolver };
        var parser = new PdfObjectParser(content, position, resolver);

        while (true)
        {
            PdfTokenType token = parser.ReadToken();

            if (token == PdfTokenType.EndOfInput)
                return false;

            if (token == PdfTokenType.Keyword)
            {
                if (parser.TokenSpan.SequenceEqual("ID"u8))
                    break;

                return false;
            }

            if (token != PdfTokenType.Name)
                continue;

            PdfName key = ExpandKey(PdfName.Get(parser.TokenName()));
            dictionary.Set(key, ExpandValue(parser.ParseValue()));
        }

        int dataStart = parser.Position;
        if (dataStart < content.Length && PdfCharacters.IsWhitespace(content[dataStart]))
            dataStart++;

        // With a text-encoding filter last in the chain the single white-space character after ID
        // is not the only one allowed, so the data proper starts at the first non-blank byte.
        if (EndsWithTextFilter(dictionary))
        {
            while (dataStart < content.Length && PdfCharacters.IsWhitespace(content[dataStart]))
                dataStart++;
        }

        int end;

        // The declared length settles the matter; scanning for a lone EI is a guess, and a
        // compressed payload is free to contain those two bytes. Both are still checked against
        // the end marker, because a length that misses it belongs to a file that is already wrong.
        if (!TryMeasure(DeclaredLength(dictionary), content, dataStart, out end) &&
            !TryMeasure(MeasureUnfiltered(dictionary), content, dataStart, out end))
        {
            end = FindEndMarker(content, dataStart);
            if (end < 0)
                return false;
        }

        var stream = new PdfStream(dictionary, content[dataStart..end].ToArray());
        stream.Dictionary.Set(PdfNames.Length, PdfValue.Integer(end - dataStart));

        int after = content[end..].IndexOf("EI"u8);
        position = after < 0 ? content.Length : end + after + 2;
        image = PdfValue.Stream(stream);
        return true;
    }

    /// <summary>Expands the abbreviated dictionary keys in ISO 32000-2 Table 91.</summary>
    private static PdfName ExpandKey(PdfName name) => name.Value switch
    {
        "BPC" => PdfNames.BitsPerComponent,
        "CS" => PdfNames.ColorSpace,
        "D" => PdfNames.Decode,
        "DP" => PdfNames.DecodeParms,
        "F" => PdfNames.Filter,
        "H" => PdfNames.Height,
        "IM" => PdfNames.ImageMask,
        "I" => PdfNames.Interpolate,
        "W" => PdfNames.Width,
        "L" => PdfNames.Length,
        _ => name,
    };

    /// <summary>
    /// Expands the abbreviated name values in ISO 32000-2 Table 92. This is deliberately separate
    /// from the key table: <c>/I</c> means Interpolate as a key but Indexed as a value.
    /// </summary>
    private static PdfName ExpandValue(PdfName name) => name.Value switch
    {
        "G" => PdfNames.DeviceGray,
        "RGB" => PdfNames.DeviceRGB,
        "CMYK" => PdfNames.DeviceCMYK,
        "I" => PdfNames.Indexed,
        "AHx" => PdfNames.ASCIIHexDecode,
        "A85" => PdfNames.ASCII85Decode,
        "LZW" => PdfNames.LZWDecode,
        "Fl" => PdfNames.FlateDecode,
        "RL" => PdfNames.RunLengthDecode,
        "CCF" => PdfNames.CCITTFaxDecode,
        "DCT" => PdfNames.DCTDecode,
        _ => name,
    };

    private static PdfValue ExpandValue(PdfValue value)
    {
        if (value.AsName is { } name)
            return PdfValue.Name(ExpandValue(name));

        if (value.AsArray is not { } array)
            return value;

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i].AsName is { } element)
                array[i] = PdfValue.Name(ExpandValue(element));
        }

        return value;
    }

    private static bool TryMeasure(int length, ReadOnlySpan<byte> content, int dataStart, out int end)
    {
        end = 0;
        if (length <= 0 || dataStart + length > content.Length || !LooksLikeEnd(content, dataStart + length))
            return false;

        end = dataStart + length;
        return true;
    }

    /// <summary>
    /// The <c>Length</c> (or <c>L</c>) key of §8.9.7, which PDF 2.0 requires on every inline image:
    /// how many bytes lie between <c>ID</c> and <c>EI</c>, the delimiting white space aside.
    /// </summary>
    private static int DeclaredLength(PdfDictionary dictionary)
    {
        long declared = dictionary.GetInteger(PdfNames.Length);
        return declared is > 0 and <= int.MaxValue ? (int)declared : 0;
    }

    /// <summary>
    /// Whether the last filter in the chain encodes the data as text, which is what lets more than
    /// one white-space character follow the <c>ID</c> operator.
    /// </summary>
    private static bool EndsWithTextFilter(PdfDictionary dictionary)
    {
        PdfValue filters = dictionary.Get(PdfNames.Filter);

        PdfName? last = filters.AsArray is { Count: > 0 } array
            ? array.Get(array.Count - 1).AsName
            : filters.AsName;

        return last == PdfNames.ASCIIHexDecode || last == PdfNames.ASCII85Decode;
    }

    private static int MeasureUnfiltered(PdfDictionary dictionary)
    {
        if (dictionary.ContainsKey(PdfNames.Filter))
            return 0;

        int width = (int)dictionary.GetInteger(PdfNames.Width);
        int height = (int)dictionary.GetInteger(PdfNames.Height);
        if (width <= 0 || height <= 0)
            return 0;

        int bits = (int)dictionary.GetInteger(PdfNames.BitsPerComponent, 8);
        int components = dictionary.GetBoolean(PdfNames.ImageMask)
            ? 1
            : dictionary.GetName(PdfNames.ColorSpace)?.Value switch
            {
                "DeviceRGB" => 3,
                "DeviceCMYK" => 4,
                _ => 1,
            };

        if (dictionary.GetBoolean(PdfNames.ImageMask))
            bits = 1;

        return ((width * components * bits) + 7) / 8 * height;
    }

    private static bool LooksLikeEnd(ReadOnlySpan<byte> content, int position)
    {
        while (position < content.Length && PdfCharacters.IsWhitespace(content[position]))
            position++;

        return content[position..].StartsWith("EI"u8);
    }

    private static int FindEndMarker(ReadOnlySpan<byte> content, int from)
    {
        for (int i = from; i + 1 < content.Length; i++)
        {
            if (content[i] != (byte)'E' || content[i + 1] != (byte)'I')
                continue;

            // "EI" only ends the image when it stands as its own token.
            if (i > from && !PdfCharacters.IsWhitespace(content[i - 1]))
                continue;

            if (i + 2 < content.Length && PdfCharacters.IsRegular(content[i + 2]))
                continue;

            int end = i;
            while (end > from && PdfCharacters.IsWhitespace(content[end - 1]))
                end--;

            return end;
        }

        return -1;
    }
}
