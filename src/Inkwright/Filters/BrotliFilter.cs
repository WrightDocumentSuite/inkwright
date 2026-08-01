using System.Buffers;
using System.IO.Compression;

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
    {
        if (data.Length == 0)
            return [];

        int cap = PdfFilterLimits.Cap;
        var writer = new ArrayBufferWriter<byte>(Math.Min(cap, Math.Max(256, data.Length * 4)));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
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

                if (writer.WrittenCount + read > cap)
                {
                    writer.Write(buffer.AsSpan(0, cap - writer.WrittenCount));
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
}
