using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Inkwright.Security;

/// <summary>
/// The AES key wrap of IETF RFC 3394, which .NET does not carry. It is used to protect one key with
/// another, and unlike ordinary encryption it authenticates as well: the wrapped form embeds a
/// known constant, so unwrapping with the wrong key is detected rather than yielding a plausible
/// key that then fails mysteriously somewhere else.
/// </summary>
internal static class AesKeyWrap
{
    /// <summary>The initial value RFC 3394 §2.2.3.1 specifies, which is what makes unwrapping checkable.</summary>
    private static ReadOnlySpan<byte> DefaultIv => [0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6];

    /// <summary>Wraps a key.</summary>
    /// <param name="kek">The key encryption key.</param>
    /// <param name="key">The key to wrap; its length must be a multiple of eight bytes.</param>
    /// <returns>The wrapped key, eight bytes longer than the input.</returns>
    internal static byte[] Wrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> key)
    {
        if (key.Length < 16 || key.Length % 8 != 0)
            throw new ArgumentException("A wrapped key must be at least sixteen bytes and a multiple of eight.", nameof(key));

        int blocks = key.Length / 8;
        byte[] output = new byte[key.Length + 8];
        DefaultIv.CopyTo(output);
        key.CopyTo(output.AsSpan(8));

        using Aes aes = Aes.Create();
        aes.Key = kek.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        Span<byte> buffer = stackalloc byte[16];

        for (int round = 0; round < 6; round++)
        {
            for (int i = 1; i <= blocks; i++)
            {
                output.AsSpan(0, 8).CopyTo(buffer);
                output.AsSpan(i * 8, 8).CopyTo(buffer[8..]);
                aes.EncryptEcb(buffer, buffer, PaddingMode.None);

                // The round counter is mixed into the low end of the first half.
                long counter = (blocks * round) + i;
                for (int b = 0; b < 8; b++)
                    buffer[7 - b] ^= (byte)(counter >> (8 * b));

                buffer[..8].CopyTo(output);
                buffer[8..].CopyTo(output.AsSpan(i * 8));
            }
        }

        return output;
    }

    /// <summary>Unwraps a key.</summary>
    /// <param name="kek">The key encryption key.</param>
    /// <param name="wrapped">The wrapped key.</param>
    /// <param name="key">Receives the unwrapped key when the check value matches.</param>
    /// <returns>Whether the unwrapping produced the expected check value.</returns>
    internal static bool TryUnwrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapped, out byte[] key)
    {
        key = [];

        if (wrapped.Length < 24 || wrapped.Length % 8 != 0)
            return false;

        int blocks = (wrapped.Length / 8) - 1;
        byte[] state = wrapped.ToArray();

        using Aes aes = Aes.Create();
        aes.Key = kek.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        Span<byte> buffer = stackalloc byte[16];

        for (int round = 5; round >= 0; round--)
        {
            for (int i = blocks; i >= 1; i--)
            {
                state.AsSpan(0, 8).CopyTo(buffer);

                long counter = (blocks * round) + i;
                for (int b = 0; b < 8; b++)
                    buffer[7 - b] ^= (byte)(counter >> (8 * b));

                state.AsSpan(i * 8, 8).CopyTo(buffer[8..]);
                aes.DecryptEcb(buffer, buffer, PaddingMode.None);

                buffer[..8].CopyTo(state);
                buffer[8..].CopyTo(state.AsSpan(i * 8));
            }
        }

        if (!CryptographicOperations.FixedTimeEquals(state.AsSpan(0, 8), DefaultIv))
            return false;

        key = state[8..];
        return true;
    }

    /// <summary>Derives the key that wraps the integrity key (ISO/TS 32004 §6.4).</summary>
    /// <param name="fileKey">The document's file encryption key.</param>
    /// <param name="salt">The thirty-two byte salt from the encryption dictionary.</param>
    /// <param name="length">How long a key to produce.</param>
    internal static byte[] DeriveWrappingKey(ReadOnlySpan<byte> fileKey, ReadOnlySpan<byte> salt, int length)
    {
        // The specification pins every parameter: SHA-256, the salt from the file, and a fixed
        // label, so two implementations that read the same file arrive at the same key.
        byte[] info = "PDFMAC"u8.ToArray();
        byte[] derived = new byte[length];

        HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, derived, salt, info);
        return derived;
    }

    /// <summary>Writes an unsigned big-endian counter, for callers assembling wrap input by hand.</summary>
    /// <param name="destination">Where to write the eight bytes.</param>
    /// <param name="value">The counter value.</param>
    internal static void WriteCounter(Span<byte> destination, long value) =>
        BinaryPrimitives.WriteInt64BigEndian(destination, value);
}
