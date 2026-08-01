using System.Buffers.Binary;
using System.Text;
using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>
/// The first bytes of an ICC profile, which are all the archival rules ever ask about: how big the
/// profile says it is, what kind of device it describes, and which colour space it speaks.
/// </summary>
/// <param name="DeviceClass">Four characters such as <c>mntr</c> or <c>prtr</c>.</param>
/// <param name="ColourSpace">The data colour space, trimmed: <c>GRAY</c>, <c>RGB</c>, <c>CMYK</c>.</param>
/// <param name="Version">The major version of the profile format.</param>
internal readonly record struct IccHeader(string DeviceClass, string ColourSpace, int Version)
{
    private const int HeaderLength = 128;

    /// <summary>Reads the header of a profile stream, or nothing when it is not a profile at all.</summary>
    /// <param name="stream">The stream that should hold an ICC profile.</param>
    internal static IccHeader? Read(PdfStream stream)
    {
        ReadOnlySpan<byte> bytes;

        try
        {
            bytes = stream.DecodedSpan();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
        }

        if (bytes.Length < HeaderLength)
            return null;

        // Every profile carries 'acsp' at offset 36; without it the bytes are something else.
        if (!bytes.Slice(36, 4).SequenceEqual("acsp"u8))
            return null;

        int declared = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (declared > bytes.Length)
            return null;

        return new IccHeader(
            Encoding.ASCII.GetString(bytes.Slice(12, 4)).Trim(),
            Encoding.ASCII.GetString(bytes.Slice(16, 4)).Trim(),
            bytes[8]);
    }
}
