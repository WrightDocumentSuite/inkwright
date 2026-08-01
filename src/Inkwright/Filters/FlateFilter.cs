using System.Buffers;
using System.IO.Compression;

namespace Inkwright.Filters;

/// <summary>
/// The <c>FlateDecode</c> filter of ISO 32000-1 §7.4.4. Decoding is deliberately forgiving:
/// producers ship streams with stray leading bytes, missing zlib wrappers and truncated tails, and
/// every one of those still yields the bytes decoded so far rather than an exception.
/// </summary>
public static class FlateFilter
{
    /// <summary>Inflates a stream payload.</summary>
    /// <param name="data">The compressed bytes.</param>
    /// <returns>The inflated bytes, truncated at the first unrecoverable position.</returns>
    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        int start = 0;
        while (start < data.Length && IsWhitespace(data[start]))
            start++;

        if (start >= data.Length)
            return [];

        ReadOnlySpan<byte> body = data[start..];

        // A well-formed payload carries a zlib header; the check byte pair is a multiple of 31.
        bool looksLikeZlib = body.Length >= 2 && (body[0] & 0x0F) == 8 && ((body[0] << 8) + body[1]) % 31 == 0;

        if (looksLikeZlib && TryInflate(body, zlib: true, out byte[] inflated))
            return inflated;

        if (TryInflate(body, zlib: false, out byte[] raw) && raw.Length > 0)
            return raw;

        if (!looksLikeZlib && TryInflate(body, zlib: true, out byte[] zlibAnyway) && zlibAnyway.Length > 0)
            return zlibAnyway;

        // Some producers prepend a byte of noise before an otherwise valid stream.
        if (body.Length > 1 && TryInflate(body[1..], zlib: true, out byte[] shifted) && shifted.Length > 0)
            return shifted;

        return raw.Length > 0 ? raw : [];
    }

    /// <summary>Deflates bytes into a zlib-wrapped payload.</summary>
    /// <param name="data">The plaintext bytes.</param>
    /// <param name="level">How hard to work at shrinking the output.</param>
    public static byte[] Encode(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream(Math.Max(64, data.Length / 3));
        using (var deflate = new ZLibStream(output, level, leaveOpen: true))
            deflate.Write(data);

        return output.ToArray();
    }

    private static bool TryInflate(ReadOnlySpan<byte> body, bool zlib, out byte[] result)
    {
        int cap = PdfFilterLimits.Cap;
        var writer = new ArrayBufferWriter<byte>(Math.Min(cap, Math.Max(256, body.Length * 3)));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            using var input = new MemoryStream(body.ToArray(), writable: false);
            using Stream decompressor = zlib
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);

            while (true)
            {
                int read;
                try
                {
                    read = decompressor.Read(buffer, 0, buffer.Length);
                }
                catch (InvalidDataException)
                {
                    // Truncated or damaged tail: keep whatever decoded cleanly.
                    result = writer.WrittenSpan.ToArray();
                    return result.Length > 0;
                }

                if (read == 0)
                    break;

                if (writer.WrittenCount + read > cap)
                {
                    // A decompression bomb: keep what fits and stop rather than exhaust memory.
                    writer.Write(buffer.AsSpan(0, cap - writer.WrittenCount));
                    break;
                }

                writer.Write(buffer.AsSpan(0, read));
            }

            result = writer.WrittenSpan.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            result = writer.WrittenSpan.ToArray();
            return result.Length > 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsWhitespace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
}
