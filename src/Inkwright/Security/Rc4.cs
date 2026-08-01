namespace Inkwright.Security;

/// <summary>
/// The RC4 stream cipher, required to read files encrypted with revisions 2 to 4 of the standard
/// security handler. It is implemented here because the platform no longer ships it, and it is
/// never used for writing.
/// </summary>
internal static class Rc4
{
    /// <summary>Encrypts or decrypts in place; RC4 is its own inverse.</summary>
    /// <param name="key">The cipher key, 5 to 16 bytes in PDF's usage.</param>
    /// <param name="input">The bytes to transform.</param>
    /// <param name="output">The destination, which may be the same memory as the input.</param>
    internal static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (key.IsEmpty)
        {
            input.CopyTo(output);
            return;
        }

        Span<byte> state = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
            state[i] = (byte)i;

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        int x = 0;
        int y = 0;
        for (int i = 0; i < input.Length; i++)
        {
            x = (x + 1) & 0xFF;
            y = (y + state[x]) & 0xFF;
            (state[x], state[y]) = (state[y], state[x]);
            output[i] = (byte)(input[i] ^ state[(state[x] + state[y]) & 0xFF]);
        }
    }

    /// <summary>Encrypts or decrypts into a fresh array.</summary>
    /// <param name="key">The cipher key.</param>
    /// <param name="input">The bytes to transform.</param>
    internal static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input)
    {
        byte[] output = new byte[input.Length];
        Transform(key, input, output);
        return output;
    }
}
