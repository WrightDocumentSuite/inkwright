namespace Inkwright.Images;

/// <summary>What a JPEG's frame header says about the image.</summary>
/// <param name="Width">The width in samples.</param>
/// <param name="Height">The height in samples.</param>
/// <param name="Components">One for grey, three for YCbCr, four for CMYK or YCCK.</param>
/// <param name="Precision">Bits per component, eight in practice.</param>
/// <param name="AdobeInverted">Whether an Adobe marker says the CMYK values are stored inverted.</param>
internal readonly record struct JpegInfo(int Width, int Height, int Components, int Precision, bool AdobeInverted);

/// <summary>
/// Reads only what embedding a JPEG requires: the frame header and the Adobe marker. The entropy
/// coded data is never touched, so the bytes that go into the PDF are the bytes that came in.
/// </summary>
internal static class JpegReader
{
    internal static JpegInfo Read(ReadOnlySpan<byte> data)
    {
        bool adobe = false;
        int adobeTransform = -1;
        int position = 2;

        while (position + 3 < data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            byte marker = data[position + 1];
            position += 2;

            // Standalone markers carry no payload.
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue;

            if (marker == 0xD9 || position + 1 >= data.Length)
                break;

            int length = (data[position] << 8) | data[position + 1];
            if (length < 2 || position + length > data.Length)
                break;

            ReadOnlySpan<byte> segment = data.Slice(position + 2, length - 2);

            if (marker == 0xEE && segment.Length >= 12 && segment[..5].SequenceEqual("Adobe"u8))
            {
                adobe = true;
                adobeTransform = segment[^1];
            }
            else if (IsFrameHeader(marker) && segment.Length >= 6)
            {
                int precision = segment[0];
                int height = (segment[1] << 8) | segment[2];
                int width = (segment[3] << 8) | segment[4];
                int components = segment[5];

                return new JpegInfo(width, height, components, precision, adobe && adobeTransform != 0);
            }

            position += length;
        }

        throw new NotSupportedException("The JPEG data has no frame header.");
    }

    private static bool IsFrameHeader(byte marker) =>
        marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
}
