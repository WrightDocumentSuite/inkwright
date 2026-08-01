using System.Buffers.Binary;
using System.Text;

namespace Inkwright.Metadata;

/// <summary>
/// Builds the ICC profile a PDF/A output intent has to carry. The profile is generated rather than
/// shipped as an opaque blob: an sRGB matrix-shaper profile is a few hundred bytes of well-specified
/// structure, and writing it out keeps the package free of binary resources nobody can review.
/// </summary>
public static class IccProfile
{
    /// <summary>Builds an ICC v2 display profile describing the sRGB colour space.</summary>
    public static byte[] CreateSRgb()
    {
        List<(string Tag, byte[] Data)> tags =
        [
            ("desc", TextDescription("sRGB IEC61966-2.1")),
            ("cprt", Text("Public domain")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),

            // Primaries chromatically adapted to the D50 profile connection space.
            ("rXYZ", Xyz(0.4360, 0.2225, 0.0139)),
            ("gXYZ", Xyz(0.3851, 0.7169, 0.0971)),
            ("bXYZ", Xyz(0.1431, 0.0606, 0.7139)),
            ("rTRC", Gamma(2.2)),
            ("gTRC", Gamma(2.2)),
            ("bTRC", Gamma(2.2)),
            ("chad", ChromaticAdaptation()),
        ];

        int tableSize = 4 + (tags.Count * 12);
        int offset = 128 + tableSize;
        var payload = new MemoryStream();
        var table = new List<(string Tag, int Offset, int Size)>(tags.Count);

        foreach ((string tag, byte[] data) in tags)
        {
            table.Add((tag, offset + (int)payload.Length, data.Length));
            payload.Write(data);

            while (payload.Length % 4 != 0)
                payload.WriteByte(0);
        }

        int total = offset + (int)payload.Length;
        byte[] profile = new byte[total];

        BinaryPrimitives.WriteInt32BigEndian(profile, total);
        Encoding.ASCII.GetBytes("ADBE", profile.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x02100000);
        Encoding.ASCII.GetBytes("mntr", profile.AsSpan(12));
        Encoding.ASCII.GetBytes("RGB ", profile.AsSpan(16));
        Encoding.ASCII.GetBytes("XYZ ", profile.AsSpan(20));
        WriteDate(profile.AsSpan(24));
        Encoding.ASCII.GetBytes("acsp", profile.AsSpan(36));
        Encoding.ASCII.GetBytes("APPL", profile.AsSpan(40));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(64), 0);
        WriteXyz(profile.AsSpan(68), 0.9642, 1.0, 0.8249);
        Encoding.ASCII.GetBytes("none", profile.AsSpan(80));

        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(128), tags.Count);
        int record = 132;
        foreach ((string tag, int tagOffset, int size) in table)
        {
            Encoding.ASCII.GetBytes(tag, profile.AsSpan(record));
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(record + 4), tagOffset);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(record + 8), size);
            record += 12;
        }

        payload.ToArray().CopyTo(profile.AsSpan(offset));
        return profile;
    }

    private static void WriteDate(Span<byte> destination)
    {
        DateTime now = DateTime.UtcNow;
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)now.Year);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)now.Month);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], (ushort)now.Day);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], (ushort)now.Hour);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], (ushort)now.Minute);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], (ushort)now.Second);
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        byte[] data = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ", data);
        WriteXyz(data.AsSpan(8), x, y, z);
        return data;
    }

    private static void WriteXyz(Span<byte> destination, double x, double y, double z)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, Fixed16(x));
        BinaryPrimitives.WriteInt32BigEndian(destination[4..], Fixed16(y));
        BinaryPrimitives.WriteInt32BigEndian(destination[8..], Fixed16(z));
    }

    private static byte[] Gamma(double gamma)
    {
        byte[] data = new byte[14];
        Encoding.ASCII.GetBytes("curv", data);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return data;
    }

    private static byte[] ChromaticAdaptation()
    {
        // The Bradford matrix taking D65 to the D50 connection space.
        double[] matrix = [1.0479, 0.0229, -0.0502, 0.0296, 0.9905, -0.0171, -0.0092, 0.0151, 0.7519];

        byte[] data = new byte[8 + (9 * 4)];
        Encoding.ASCII.GetBytes("sf32", data);
        for (int i = 0; i < matrix.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8 + (i * 4)), Fixed16(matrix[i]));

        return data;
    }

    private static byte[] TextDescription(string text)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(text);
        byte[] data = new byte[12 + ascii.Length + 1 + 12 + 67];
        Encoding.ASCII.GetBytes("desc", data);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), ascii.Length + 1);
        ascii.CopyTo(data.AsSpan(12));
        return data;
    }

    private static byte[] Text(string text)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(text);
        byte[] data = new byte[8 + ascii.Length + 1];
        Encoding.ASCII.GetBytes("text", data);
        ascii.CopyTo(data.AsSpan(8));
        return data;
    }

    private static int Fixed16(double value) => (int)Math.Round(value * 65536);
}
