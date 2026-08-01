using System.Buffers.Binary;
using System.IO.Compression;

namespace Inkwright.Tests;

/// <summary>
/// Builds image files byte by byte, so the image tests depend on nothing but the format itself.
/// </summary>
public static class TestImages
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Builds a PNG with a simple gradient, optionally carrying an alpha channel.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="withAlpha">Whether to write RGBA rather than RGB.</param>
    public static byte[] Png(int width, int height, bool withAlpha)
    {
        int components = withAlpha ? 4 : 3;
        byte[] raw = new byte[height * ((width * components) + 1)];

        int position = 0;
        for (int y = 0; y < height; y++)
        {
            raw[position++] = 0; // filter type: none
            for (int x = 0; x < width; x++)
            {
                raw[position++] = (byte)(x * 255 / Math.Max(1, width - 1));
                raw[position++] = (byte)(y * 255 / Math.Max(1, height - 1));
                raw[position++] = 0x40;
                if (withAlpha)
                    raw[position++] = (byte)((x + y) % 2 == 0 ? 255 : 64);
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);

        using var file = new MemoryStream();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = (byte)(withAlpha ? 6 : 2);
        WriteChunk(file, "IHDR"u8, header);
        WriteChunk(file, "IDAT"u8, compressed.ToArray());
        WriteChunk(file, "IEND"u8, []);

        return file.ToArray();
    }

    /// <summary>
    /// Builds a JPEG whose markers are laid out the way a real encoder writes them. The entropy
    /// coded data is a placeholder, which is all the library ever needs: it reads the frame header
    /// and copies everything else through untouched.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    public static byte[] Jpeg(int width, int height)
    {
        using var file = new MemoryStream();

        file.Write([0xFF, 0xD8]); // SOI

        byte[] jfif = [0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];
        WriteSegment(file, 0xE0, jfif);

        byte[] frame = new byte[6 + (3 * 3)];
        frame[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1, 2), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3, 2), (ushort)width);
        frame[5] = 3;
        for (int i = 0; i < 3; i++)
        {
            frame[6 + (i * 3)] = (byte)(i + 1);
            frame[7 + (i * 3)] = 0x11;
            frame[8 + (i * 3)] = (byte)(i == 0 ? 0 : 1);
        }

        WriteSegment(file, 0xC0, frame); // SOF0
        WriteSegment(file, 0xDA, [3, 1, 0, 2, 0x11, 3, 0x11, 0, 63, 0]); // SOS
        file.Write(new byte[width * height / 8]);
        file.Write([0xFF, 0xD9]); // EOI

        return file.ToArray();
    }

    private static void WriteSegment(Stream stream, byte marker, ReadOnlySpan<byte> payload)
    {
        stream.Write([0xFF, marker]);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
        stream.Write(length);
        stream.Write(payload);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(payload);

        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        foreach (byte b in payload)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc ^ 0xFFFFFFFF);
        stream.Write(checksum);
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;

            table[i] = value;
        }

        return table;
    }
}
