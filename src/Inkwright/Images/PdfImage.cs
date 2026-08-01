using Inkwright.Cos;
using Inkwright.Filters;

namespace Inkwright.Images;

/// <summary>
/// An image XObject inside a document. JPEG data is embedded exactly as it arrived and PNG data is
/// embedded without ever being re-encoded, because the deflate stream a PNG already carries is the
/// same deflate stream PDF wants; only images with an alpha channel are decoded, and only far enough
/// to separate that channel into a soft mask.
/// </summary>
public sealed class PdfImage
{
    private PdfImage(PdfObjectId id, PdfStream stream, int width, int height)
    {
        Id = id;
        Stream = stream;
        Width = width;
        Height = height;
    }

    /// <summary>The identity of the image object.</summary>
    public PdfObjectId Id { get; }

    /// <summary>The image XObject.</summary>
    public PdfStream Stream { get; }

    /// <summary>The width in samples.</summary>
    public int Width { get; }

    /// <summary>The height in samples.</summary>
    public int Height { get; }

    /// <summary>A reference to the image, ready to be placed in a resource dictionary.</summary>
    public PdfValue Reference => PdfValue.Reference(Id);

    /// <summary>The size the image takes at a given resolution.</summary>
    /// <param name="dpi">The intended output resolution.</param>
    public PageSize SizeAt(double dpi = 72) => new(Width * 72 / dpi, Height * 72 / dpi);

    /// <summary>Decodes the image back into pixels.</summary>
    /// <exception cref="NotSupportedException">The image uses a codec this library does not decode.</exception>
    public PdfImageData Decode() => PdfImageReader.Decode(Stream);

    /// <summary>Loads an image from a file, choosing the decoder by its content.</summary>
    /// <param name="document">The document to add the image to.</param>
    /// <param name="path">The image file.</param>
    public static PdfImage Load(PdfDocument document, string path) =>
        Load(document, File.ReadAllBytes(path));

    /// <summary>Loads an image from memory, choosing the decoder by its content.</summary>
    /// <param name="document">The document to add the image to.</param>
    /// <param name="data">The encoded image.</param>
    /// <exception cref="NotSupportedException">The bytes are neither JPEG nor a PNG shape the library embeds.</exception>
    public static PdfImage Load(PdfDocument document, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return FromJpeg(document, data);

        if (data.Length > 8 && data[0] == 0x89 && data.Slice(1, 3).SequenceEqual("PNG"u8) &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return FromPng(document, data);
        }

        throw new NotSupportedException("Only JPEG and PNG data can be embedded directly.");
    }

    /// <summary>Embeds JPEG data without recompressing it.</summary>
    /// <param name="document">The document to add the image to.</param>
    /// <param name="data">The JPEG file.</param>
    public static PdfImage FromJpeg(PdfDocument document, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(document);

        JpegInfo info = JpegReader.Read(data);

        var stream = new PdfStream(new PdfDictionary(8), data.ToArray());
        PdfDictionary dictionary = stream.Dictionary;
        dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Image));
        dictionary.Set(PdfNames.Width, PdfValue.Integer(info.Width));
        dictionary.Set(PdfNames.Height, PdfValue.Integer(info.Height));
        dictionary.Set(PdfNames.BitsPerComponent, PdfValue.Integer(info.Precision));
        dictionary.Set(PdfNames.ColorSpace, PdfValue.Name(info.Components switch
        {
            1 => PdfNames.DeviceGray,
            4 => PdfNames.DeviceCMYK,
            _ => PdfNames.DeviceRGB,
        }));
        dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfNames.DCTDecode));
        dictionary.Set(PdfNames.Length, PdfValue.Integer(data.Length));

        // Adobe writes CMYK JPEGs inverted; the Decode array puts them right without touching pixels.
        if (info.Components == 4 && info.AdobeInverted)
            dictionary.Set(PdfNames.Decode, PdfValue.Array(PdfArray.OfNumbers(1, 0, 1, 0, 1, 0, 1, 0)));

        return Attach(document, stream, info.Width, info.Height);
    }

    /// <summary>Embeds PNG data, reusing its deflate stream whenever the format allows.</summary>
    /// <param name="document">The document to add the image to.</param>
    /// <param name="data">The PNG file.</param>
    public static PdfImage FromPng(PdfDocument document, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PngEmbedder.Embed(document, data);
    }

    /// <summary>Builds an image from raw samples.</summary>
    /// <param name="document">The document to add the image to.</param>
    /// <param name="width">The width in samples.</param>
    /// <param name="height">The height in samples.</param>
    /// <param name="samples">Interleaved samples, eight bits each.</param>
    /// <param name="components">One for grey, three for RGB, four for CMYK.</param>
    /// <param name="alpha">One alpha byte per sample, or empty for an opaque image.</param>
    public static PdfImage FromPixels(
        PdfDocument document,
        int width,
        int height,
        ReadOnlySpan<byte> samples,
        int components = 3,
        ReadOnlySpan<byte> alpha = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (components is not (1 or 3 or 4))
            throw new ArgumentOutOfRangeException(nameof(components), components, "Expected 1, 3 or 4 components.");

        int expected = width * height * components;
        if (samples.Length < expected)
            throw new ArgumentException($"Expected at least {expected} bytes of samples.", nameof(samples));

        PdfStream stream = PdfStream.FromData(samples[..expected].ToArray());
        PdfDictionary dictionary = stream.Dictionary;
        dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Image));
        dictionary.Set(PdfNames.Width, PdfValue.Integer(width));
        dictionary.Set(PdfNames.Height, PdfValue.Integer(height));
        dictionary.Set(PdfNames.BitsPerComponent, PdfValue.Integer(8));
        dictionary.Set(PdfNames.ColorSpace, PdfValue.Name(components switch
        {
            1 => PdfNames.DeviceGray,
            4 => PdfNames.DeviceCMYK,
            _ => PdfNames.DeviceRGB,
        }));

        if (!alpha.IsEmpty)
            AttachSoftMask(document, dictionary, width, height, alpha, 8);

        return Attach(document, stream, width, height);
    }

    internal static PdfImage Attach(PdfDocument document, PdfStream stream, int width, int height)
    {
        PdfObjectId id = document.AddObject(PdfValue.Stream(stream));
        return new PdfImage(id, stream, width, height);
    }

    internal static void AttachSoftMask(
        PdfDocument document, PdfDictionary dictionary, int width, int height, ReadOnlySpan<byte> alpha, int bits)
    {
        PdfStream mask = PdfStream.FromData(alpha.ToArray());
        PdfDictionary maskDictionary = mask.Dictionary;
        maskDictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        maskDictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Image));
        maskDictionary.Set(PdfNames.Width, PdfValue.Integer(width));
        maskDictionary.Set(PdfNames.Height, PdfValue.Integer(height));
        maskDictionary.Set(PdfNames.BitsPerComponent, PdfValue.Integer(bits));
        maskDictionary.Set(PdfNames.ColorSpace, PdfValue.Name(PdfNames.DeviceGray));

        PdfObjectId maskId = document.AddObject(PdfValue.Stream(mask));
        dictionary.Set(PdfNames.SMask, PdfValue.Reference(maskId));
    }

    internal static byte[] Deflate(ReadOnlySpan<byte> data) => FlateFilter.Encode(data);
}
