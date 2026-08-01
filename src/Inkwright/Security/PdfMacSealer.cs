using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Inkwright.Cos;

namespace Inkwright.Security;

/// <summary>
/// Writes the integrity code into a finished file. The code covers the bytes of the file, so it
/// cannot exist until the file does; the writer therefore leaves a gap of a known size and this
/// fills it afterwards, the same way a signature is applied (ISO/TS 32004 §6.5.1).
/// </summary>
internal static class PdfMacSealer
{
    /// <summary>The byte range placeholder, wide enough for any offset a file can have.</summary>
    private const string RangePlaceholder = "[0 9999999999 9999999999 9999999999]";

    /// <summary>How many bytes to reserve for the token, which runs to a few hundred.</summary>
    private const int ReservedBytes = 2048;

    /// <summary>Builds the authentication dictionary with room for a code that does not exist yet.</summary>
    internal static PdfDictionary CreatePlaceholder()
    {
        var authentication = new PdfDictionary(3);
        authentication.Set(PdfName.Get("MACLocation"), PdfValue.Name(PdfName.Get("Standalone")));

        // Both entries are placeholders of an exact width, patched once the file is laid out.
        authentication.Set(
            PdfName.Get("ByteRange"), PdfValue.Array(PdfArray.OfNumbers(0, 9999999999, 9999999999, 9999999999)));

        authentication.Set(PdfName.Get("MAC"), PdfValue.String(new byte[ReservedBytes], hex: true));

        return authentication;
    }

    /// <summary>
    /// Computes the code over the finished file and writes it into the reserved space, together
    /// with the byte range that says exactly what it covers.
    /// </summary>
    /// <param name="file">The written file, modified in place.</param>
    /// <param name="security">The handler holding the file encryption key.</param>
    /// <exception cref="InvalidOperationException">The placeholders are missing or too small.</exception>
    internal static void Seal(byte[] file, StandardSecurityHandler security)
    {
        int rangeStart = file.AsSpan().LastIndexOf(Encoding.ASCII.GetBytes(RangePlaceholder));
        if (rangeStart < 0)
            throw new InvalidOperationException("The integrity placeholder was not written as expected.");

        int hexStart = file.AsSpan(rangeStart).IndexOf("/MAC"u8);
        if (hexStart < 0)
            throw new InvalidOperationException("The authentication dictionary has no MAC entry.");

        hexStart += rangeStart;
        while (hexStart < file.Length && file[hexStart] != (byte)'<')
            hexStart++;

        int hexEnd = hexStart;
        while (hexEnd < file.Length && file[hexEnd] != (byte)'>')
            hexEnd++;

        if (hexEnd >= file.Length)
            throw new InvalidOperationException("The integrity placeholder is not terminated.");

        int afterHex = hexEnd + 1;
        int tailLength = file.Length - afterHex;

        // The range covers everything except the value itself, which cannot vouch for its own bytes.
        string range = string.Create(
                CultureInfo.InvariantCulture, $"[0 {hexStart} {afterHex} {tailLength}]")
            .PadRight(RangePlaceholder.Length);

        if (range.Length > RangePlaceholder.Length)
            throw new InvalidOperationException("The byte range does not fit in the space reserved for it.");

        Encoding.ASCII.GetBytes(range, file.AsSpan(rangeStart));

        byte[] covered = new byte[hexStart + tailLength];
        file.AsSpan(0, hexStart).CopyTo(covered);
        file.AsSpan(afterHex, tailLength).CopyTo(covered.AsSpan(hexStart));

        byte[] salt = FindSalt(file)
            ?? throw new InvalidOperationException("The encryption dictionary carries no salt to derive a key from.");

        byte[] token = PdfMacWriter.Build(security.FileKey, salt, SHA256.HashData(covered), []);

        int available = (hexEnd - hexStart - 1) / 2;
        if (token.Length > available)
            throw new InvalidOperationException($"The integrity token needs {token.Length} bytes of the {available} reserved.");

        // The standard forbids trailing data inside the string, so the token is padded with zeros
        // that are themselves part of a longer, still valid, hexadecimal value.
        Span<byte> destination = file.AsSpan(hexStart + 1, hexEnd - hexStart - 1);
        destination.Fill((byte)'0');

        for (int i = 0; i < token.Length; i++)
        {
            destination[i * 2] = HexDigit(token[i] >> 4);
            destination[(i * 2) + 1] = HexDigit(token[i] & 0x0F);
        }
    }

    /// <summary>
    /// Reads the salt back out of the written file. It was placed in the encryption dictionary,
    /// which is written in the clear before the key takes effect.
    /// </summary>
    private static byte[]? FindSalt(byte[] file)
    {
        int at = file.AsSpan().IndexOf("/KDFSalt"u8);
        if (at < 0)
            return null;

        int start = at;
        while (start < file.Length && file[start] != (byte)'<')
            start++;

        int end = start;
        while (end < file.Length && file[end] != (byte)'>')
            end++;

        if (end >= file.Length || end - start - 1 < 2)
            return null;

        try
        {
            return Convert.FromHexString(Encoding.ASCII.GetString(file, start + 1, end - start - 1));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte HexDigit(int value) => (byte)(value < 10 ? '0' + value : 'A' + value - 10);
}
