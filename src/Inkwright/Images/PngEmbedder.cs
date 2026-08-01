using System.Buffers.Binary;
using Inkwright.Cos;
using Inkwright.Filters;

namespace Inkwright.Images;

/// <summary>
/// Puts a PNG into a PDF. Grey, truecolour and palette images go in without being decoded at all:
/// their IDAT stream is a deflate stream with PNG line filters, which is exactly what
/// <c>FlateDecode</c> with <c>Predictor 15</c> means in PDF. Only an alpha channel forces a decode,
/// and then only to split it out into a soft mask.
/// </summary>
internal static class PngEmbedder
{
    internal static PdfImage Embed(PdfDocument document, ReadOnlySpan<byte> data)
    {
        PngFile png = PngFile.Parse(data);

        if (png.Interlaced)
            throw new NotSupportedException("Interlaced PNG images are not supported; save the file without Adam7.");

        return png.ColorType switch
        {
            0 or 2 => EmbedDirect(document, png, png.ColorType == 0 ? 1 : 3, PdfValue.Name(
                png.ColorType == 0 ? PdfNames.DeviceGray : PdfNames.DeviceRGB)),
            3 => EmbedPalette(document, png),
            4 or 6 => EmbedWithAlpha(document, png),
            _ => throw new NotSupportedException($"PNG colour type {png.ColorType} is not supported."),
        };
    }

    private static PdfImage EmbedDirect(PdfDocument document, PngFile png, int components, PdfValue colorSpace)
    {
        var stream = new PdfStream(new PdfDictionary(10), png.ImageData);
        PdfDictionary dictionary = stream.Dictionary;
        Describe(dictionary, png.Width, png.Height, png.BitDepth, colorSpace);

        dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfNames.FlateDecode));
        dictionary.Set(PdfNames.DecodeParms, PdfValue.Dictionary(DecodeParms(components, png.BitDepth, png.Width)));
        dictionary.Set(PdfNames.Length, PdfValue.Integer(png.ImageData.Length));

        return PdfImage.Attach(document, stream, png.Width, png.Height);
    }

    private static PdfImage EmbedPalette(PdfDocument document, PngFile png)
    {
        if (png.Palette.Length == 0)
            throw new NotSupportedException("The PNG declares a palette colour type but carries no PLTE chunk.");

        int entries = png.Palette.Length / 3;
        var indexed = new PdfArray(4);
        indexed.Add(PdfValue.Name(PdfNames.Indexed));
        indexed.Add(PdfValue.Name(PdfNames.DeviceRGB));
        indexed.Add(PdfValue.Integer(entries - 1));
        indexed.Add(PdfValue.String(png.Palette, hex: true));

        PdfImage image = EmbedDirect(document, png, 1, PdfValue.Array(indexed));

        if (png.Transparency.Length == 0)
            return image;

        // Palette transparency lives in tRNS, one alpha byte per palette entry, so the indices have
        // to be unpacked before they can be turned into a mask.
        byte[] indices = Unfilter(png, 1);
        byte[] alpha = new byte[png.Width * png.Height];
        for (int i = 0; i < alpha.Length && i < indices.Length; i++)
        {
            int index = indices[i];
            alpha[i] = index < png.Transparency.Length ? png.Transparency[index] : (byte)255;
        }

        PdfImage.AttachSoftMask(document, image.Stream.Dictionary, png.Width, png.Height, alpha, 8);
        return image;
    }

    private static PdfImage EmbedWithAlpha(PdfDocument document, PngFile png)
    {
        int colorComponents = png.ColorType == 4 ? 1 : 3;
        int total = colorComponents + 1;
        int bytesPerSample = png.BitDepth / 8;

        if (bytesPerSample is not (1 or 2))
            throw new NotSupportedException($"PNG bit depth {png.BitDepth} with an alpha channel is not supported.");

        byte[] samples = Unfilter(png, total);
        int pixels = png.Width * png.Height;

        byte[] color = new byte[pixels * colorComponents * bytesPerSample];
        byte[] alpha = new byte[pixels * bytesPerSample];

        for (int i = 0; i < pixels; i++)
        {
            int source = i * total * bytesPerSample;
            int colorTarget = i * colorComponents * bytesPerSample;
            int alphaTarget = i * bytesPerSample;

            if (source + (total * bytesPerSample) > samples.Length)
                break;

            samples.AsSpan(source, colorComponents * bytesPerSample).CopyTo(color.AsSpan(colorTarget));
            samples.AsSpan(source + (colorComponents * bytesPerSample), bytesPerSample).CopyTo(alpha.AsSpan(alphaTarget));
        }

        PdfStream stream = PdfStream.FromData(color);
        Describe(stream.Dictionary, png.Width, png.Height, png.BitDepth,
            PdfValue.Name(colorComponents == 1 ? PdfNames.DeviceGray : PdfNames.DeviceRGB));

        PdfImage.AttachSoftMask(document, stream.Dictionary, png.Width, png.Height, alpha, png.BitDepth);
        return PdfImage.Attach(document, stream, png.Width, png.Height);
    }

    private static byte[] Unfilter(PngFile png, int components)
    {
        byte[] inflated = FlateFilter.Decode(png.ImageData);
        return Predictor.Undo(inflated, 15, components, png.BitDepth, png.Width);
    }

    private static void Describe(PdfDictionary dictionary, int width, int height, int bitDepth, PdfValue colorSpace)
    {
        dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Image));
        dictionary.Set(PdfNames.Width, PdfValue.Integer(width));
        dictionary.Set(PdfNames.Height, PdfValue.Integer(height));
        dictionary.Set(PdfNames.BitsPerComponent, PdfValue.Integer(bitDepth));
        dictionary.Set(PdfNames.ColorSpace, colorSpace);
    }

    private static PdfDictionary DecodeParms(int components, int bitDepth, int width)
    {
        var parms = new PdfDictionary(4);
        parms.Set(PdfNames.Predictor, PdfValue.Integer(15));
        parms.Set(PdfNames.Colors, PdfValue.Integer(components));
        parms.Set(PdfNames.BitsPerComponent, PdfValue.Integer(bitDepth));
        parms.Set(PdfNames.Columns, PdfValue.Integer(width));
        return parms;
    }

    private readonly record struct PngFile(
        int Width,
        int Height,
        int BitDepth,
        int ColorType,
        bool Interlaced,
        byte[] ImageData,
        byte[] Palette,
        byte[] Transparency)
    {
        internal static PngFile Parse(ReadOnlySpan<byte> data)
        {
            int width = 0, height = 0, bitDepth = 8, colorType = 0;
            bool interlaced = false;
            byte[] palette = [];
            byte[] transparency = [];
            var image = new MemoryStream(data.Length);

            int position = 8;
            while (position + 8 <= data.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(data.Slice(position, 4));
                if (length < 0 || position + 12 + length > data.Length)
                    break;

                ReadOnlySpan<byte> type = data.Slice(position + 4, 4);
                ReadOnlySpan<byte> payload = data.Slice(position + 8, length);
                position += 12 + length;

                if (type.SequenceEqual("IHDR"u8) && payload.Length >= 13)
                {
                    width = BinaryPrimitives.ReadInt32BigEndian(payload[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
                    bitDepth = payload[8];
                    colorType = payload[9];
                    interlaced = payload[12] != 0;
                }
                else if (type.SequenceEqual("PLTE"u8))
                {
                    palette = payload.ToArray();
                }
                else if (type.SequenceEqual("tRNS"u8))
                {
                    transparency = payload.ToArray();
                }
                else if (type.SequenceEqual("IDAT"u8))
                {
                    image.Write(payload);
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    break;
                }
            }

            if (width <= 0 || height <= 0)
                throw new NotSupportedException("The PNG has no usable header.");

            return new PngFile(width, height, bitDepth, colorType, interlaced, image.ToArray(), palette, transparency);
        }
    }
}
