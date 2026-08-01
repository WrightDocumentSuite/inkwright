using System.Globalization;
using System.Text;

namespace Inkwright.Signing;

/// <summary>
/// The byte-level half of signing, shared between signatures and document timestamps: find the
/// placeholders that were written into the laid-out file, patch the real byte range in, hand the
/// covered bytes to whoever produces the binary object, and write that object into the reserved
/// hexadecimal string.
/// </summary>
internal static class SignatureEmbedder
{
    internal const string ByteRangePlaceholder = "[0 9999999999 9999999999 9999999999]";

    /// <summary>Fills in the byte range and the signature value of the last placeholder written.</summary>
    /// <param name="file">The complete laid-out file, patched in place.</param>
    /// <param name="produce">Turns the covered bytes into the DER object to embed.</param>
    /// <param name="describeOverflow">Names the option to raise when the object does not fit.</param>
    internal static byte[] Embed(byte[] file, Func<byte[], byte[]> produce, string describeOverflow)
    {
        ReadOnlySpan<byte> span = file;

        int rangeStart = span.LastIndexOf(Encoding.ASCII.GetBytes(ByteRangePlaceholder));
        if (rangeStart < 0)
            throw new InvalidOperationException("The signature placeholder was not written as expected.");

        int contentsStart = span[rangeStart..].IndexOf("/Contents"u8);
        if (contentsStart < 0)
            throw new InvalidOperationException("The signature dictionary has no Contents entry.");

        int hexStart = rangeStart + contentsStart;
        while (hexStart < file.Length && file[hexStart] != (byte)'<')
            hexStart++;

        int hexEnd = hexStart;
        while (hexEnd < file.Length && file[hexEnd] != (byte)'>')
            hexEnd++;

        if (hexEnd >= file.Length)
            throw new InvalidOperationException("The signature placeholder is not terminated.");

        int afterHex = hexEnd + 1;
        int tailLength = file.Length - afterHex;

        string range = string.Create(
            CultureInfo.InvariantCulture, $"[0 {hexStart} {afterHex} {tailLength}]").PadRight(ByteRangePlaceholder.Length);

        if (range.Length > ByteRangePlaceholder.Length)
            throw new InvalidOperationException("The byte range does not fit in the space reserved for it.");

        Encoding.ASCII.GetBytes(range, file.AsSpan(rangeStart));

        // The value covers everything except its own hexadecimal string.
        byte[] covered = new byte[hexStart + tailLength];
        file.AsSpan(0, hexStart).CopyTo(covered);
        file.AsSpan(afterHex, tailLength).CopyTo(covered.AsSpan(hexStart));

        byte[] blob = produce(covered);

        int available = (hexEnd - hexStart - 1) / 2;
        if (blob.Length > available)
        {
            throw new InvalidOperationException(
                $"The signature needs {blob.Length} bytes but only {available} were reserved; raise {describeOverflow}.");
        }

        Span<byte> destination = file.AsSpan(hexStart + 1, hexEnd - hexStart - 1);
        destination.Fill((byte)'0');

        for (int i = 0; i < blob.Length; i++)
        {
            destination[i * 2] = HexDigit(blob[i] >> 4);
            destination[(i * 2) + 1] = HexDigit(blob[i] & 0x0F);
        }

        return file;
    }

    private static byte HexDigit(int value) => (byte)(value < 10 ? '0' + value : 'A' + value - 10);
}
