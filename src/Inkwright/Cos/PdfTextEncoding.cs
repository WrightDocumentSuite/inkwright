using System.Buffers;
using System.Collections.Frozen;
using System.Text;

namespace Inkwright.Cos;

/// <summary>
/// Converts between .NET strings and the byte strings of ISO 32000-1 §7.9.2: PDFDocEncoding when
/// every character fits, UTF-16BE with a byte order mark otherwise, and UTF-8 with a mark on the
/// way in for files written against PDF 2.0.
/// </summary>
public static class PdfTextEncoding
{
    private const char Undefined = '\uFFFD';

    private static readonly char[] PdfDocToUnicode = BuildPdfDocTable();
    private static readonly FrozenDictionary<char, byte> UnicodeToPdfDoc = BuildReverseTable();

    /// <summary>Decodes a PDF text string.</summary>
    /// <param name="bytes">The raw string bytes, escapes already resolved.</param>
    /// <returns>The decoded text.</returns>
    public static string DecodeTextString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return DecodeUtf16Be(bytes[2..]);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);

        return DecodePdfDoc(bytes);
    }

    /// <summary>Decodes bytes as PDFDocEncoding.</summary>
    /// <param name="bytes">The raw bytes.</param>
    public static string DecodePdfDoc(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        char[]? rented = bytes.Length > 256 ? ArrayPool<char>.Shared.Rent(bytes.Length) : null;
        Span<char> chars = rented is not null ? rented.AsSpan(0, bytes.Length) : stackalloc char[bytes.Length];

        char[] table = PdfDocToUnicode;
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = table[bytes[i]];

        string result = new(chars);
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);

        return result;
    }

    /// <summary>
    /// Encodes text the way §7.9.2.2 prescribes: PDFDocEncoding when the text fits into it, and
    /// UTF-16BE prefixed with <c>FE FF</c> otherwise.
    /// </summary>
    /// <param name="text">The text to encode.</param>
    public static byte[] EncodeTextString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (TryEncodePdfDoc(text, out byte[] encoded))
            return encoded;

        byte[] utf16 = new byte[2 + (text.Length * 2)];
        utf16[0] = 0xFE;
        utf16[1] = 0xFF;
        Encoding.BigEndianUnicode.GetBytes(text, utf16.AsSpan(2));
        return utf16;
    }

    /// <summary>Encodes text as PDFDocEncoding when every character is representable.</summary>
    /// <param name="text">The text to encode.</param>
    /// <param name="encoded">Receives the encoded bytes on success, an empty array on failure.</param>
    public static bool TryEncodePdfDoc(string text, out byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            encoded = [];
            return true;
        }

        byte[] buffer = new byte[text.Length];
        FrozenDictionary<char, byte> reverse = UnicodeToPdfDoc;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= ' ' and <= '~')
            {
                buffer[i] = (byte)c;
                continue;
            }

            if (!reverse.TryGetValue(c, out byte code))
            {
                encoded = [];
                return false;
            }

            buffer[i] = code;
        }

        encoded = buffer;
        return true;
    }

    /// <summary>Decodes big-endian UTF-16 bytes, tolerating an odd trailing byte.</summary>
    /// <param name="bytes">The UTF-16BE bytes without a byte order mark.</param>
    public static string DecodeUtf16Be(ReadOnlySpan<byte> bytes)
    {
        if ((bytes.Length & 1) != 0)
            bytes = bytes[..^1];

        return Encoding.BigEndianUnicode.GetString(bytes);
    }

    private static char[] BuildPdfDocTable()
    {
        char[] table = new char[256];
        for (int i = 0; i < 256; i++)
            table[i] = (char)i;

        // Annex D.2: the control range 0x18-0x1F carries accents in PDFDocEncoding.
        ReadOnlySpan<char> accents = ['\u02D8', '\u02C7', '\u02C6', '\u02D9', '\u02DD', '\u02DB', '\u02DA', '\u02DC'];
        accents.CopyTo(table.AsSpan(0x18));

        ReadOnlySpan<char> high =
        [
            '\u2022', '\u2020', '\u2021', '\u2026', '\u2014', '\u2013', '\u0192', '\u2044',
            '\u2039', '\u203A', '\u2212', '\u2030', '\u201E', '\u201C', '\u201D', '\u2018',
            '\u2019', '\u201A', '\u2122', '\uFB01', '\uFB02', '\u0141', '\u0152', '\u0160',
            '\u0178', '\u017D', '\u0131', '\u0142', '\u0153', '\u0161', '\u017E', Undefined,
            '\u20AC',
        ];
        high.CopyTo(table.AsSpan(0x80));

        return table;
    }

    private static FrozenDictionary<char, byte> BuildReverseTable()
    {
        var map = new Dictionary<char, byte>(256);
        for (int i = 0; i < 256; i++)
        {
            char c = PdfDocToUnicode[i];
            if (c != Undefined)
                map.TryAdd(c, (byte)i);
        }

        return map.ToFrozenDictionary();
    }
}
