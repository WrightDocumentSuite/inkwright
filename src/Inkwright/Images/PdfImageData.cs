using System.Buffers.Binary;
using System.IO.Compression;

namespace Inkwright.Images;

/// <summary>
/// A decoded image: one byte each of red, green, blue and alpha per pixel, top row first. This is
/// the form every consumer of image data wants, and the form the codecs and colour spaces all
/// converge on.
/// </summary>
public sealed class PdfImageData
{
    internal PdfImageData(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixels, four bytes each in red, green, blue, alpha order.</summary>
    public byte[] Pixels { get; }

    /// <summary>Whether any pixel is less than fully opaque.</summary>
    public bool HasTransparency
    {
        get
        {
            for (int i = 3; i < Pixels.Length; i += 4)
            {
                if (Pixels[i] != 255)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Encodes the image as a PNG file.</summary>
    /// <returns>The whole file, ready to be written to disk.</returns>
    public byte[] ToPng()
    {
        using var output = new MemoryStream(Pixels.Length / 2);
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], Width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), Height);
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;

        Chunk(output, "IHDR"u8, header);
        Chunk(output, "IDAT"u8, Compress());
        Chunk(output, "IEND"u8, []);

        return output.ToArray();
    }

    /// <summary>Writes the image to a PNG file.</summary>
    /// <param name="path">Where to write it.</param>
    public void SavePng(string path) => File.WriteAllBytes(path, ToPng());

    /// <summary>
    /// Deflates the rows, each prefixed with the filter byte PNG requires. No filtering is applied:
    /// the deflate stream does the work, and a filter would cost a pass over the data for a saving
    /// that only matters when the file is being kept rather than handed straight on.
    /// </summary>
    private byte[] Compress()
    {
        int stride = Width * 4;
        using var raw = new MemoryStream((stride + 1) * Height);

        for (int y = 0; y < Height; y++)
        {
            raw.WriteByte(0);
            raw.Write(Pixels, y * stride, stride);
        }

        using var compressed = new MemoryStream(raw.Capacity / 4);
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw.GetBuffer(), 0, (int)raw.Length);

        return compressed.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        uint crc = Checksum(Checksum(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    /// <summary>The cyclic redundancy check PNG puts after every chunk, over the standard polynomial.</summary>
    private static uint Checksum(uint seed, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            seed = ChecksumTable[(seed ^ b) & 0xFF] ^ (seed >> 8);

        return seed;
    }

    private static readonly uint[] ChecksumTable = BuildChecksumTable();

    private static uint[] BuildChecksumTable()
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
