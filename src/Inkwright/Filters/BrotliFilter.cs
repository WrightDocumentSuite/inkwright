using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Inkwright.Filters;

/// <summary>
/// The <c>BrotliDecode</c> filter. No part of ISO 32000 lists it: it is an extension Mozilla added
/// to pdf.js, and files that use it are not conforming PDFs. Reading it costs almost nothing on
/// .NET and means such a file opens rather than showing empty pages, but nothing in this library
/// ever writes it.
/// </summary>
public static class BrotliFilter
{
    /// <summary>Decompresses a Brotli-compressed stream payload.</summary>
    /// <param name="data">The compressed bytes.</param>
    /// <returns>The decompressed bytes, truncated at the first unrecoverable position.</returns>
    public static byte[] Decode(ReadOnlySpan<byte> data)
        => Decode(data.ToArray(), PdfFilterLimits.Cap, out _);

    /// <summary>Decompresses no more than <paramref name="maxDecodedBytes"/> bytes.</summary>
    internal static byte[] Decode(
        ReadOnlyMemory<byte> data,
        int maxDecodedBytes,
        out bool limitExceeded)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        limitExceeded = false;

        if (data.Length == 0)
            return [];

        int cap = maxDecodedBytes;
        int initialCapacity = Math.Min(cap, Math.Max(1, Math.Min(data.Length, 64 * 1024)));
        var writer = new ArrayBufferWriter<byte>(initialCapacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            using MemoryStream input = Input(data);
            using var decompressor = new BrotliStream(input, CompressionMode.Decompress);

            while (true)
            {
                int read;
                try
                {
                    read = decompressor.Read(buffer, 0, buffer.Length);
                }
                catch (InvalidDataException)
                {
                    // Producers append stray bytes past the end of the compressed data; whatever
                    // decoded cleanly before that is still worth having.
                    break;
                }

                if (read == 0)
                    break;

                if (read > cap - writer.WrittenCount)
                {
                    writer.Write(buffer.AsSpan(0, cap - writer.WrittenCount));
                    limitExceeded = true;
                    break;
                }

                writer.Write(buffer.AsSpan(0, read));
            }

            return writer.WrittenSpan.ToArray();
        }
        catch (InvalidDataException)
        {
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static MemoryStream Input(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment))
        {
            return new MemoryStream(
                segment.Array!,
                segment.Offset,
                segment.Count,
                writable: false,
                publiclyVisible: true);
        }

        return new MemoryStream(data.ToArray(), writable: false);
    }
}
