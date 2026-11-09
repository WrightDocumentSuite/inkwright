using Inkwright.Color;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Filters;

namespace Inkwright.Images;

/// <summary>
/// Turns an image XObject back into pixels. The samples come out of the stream at whatever width
/// the image declares, are mapped through its decode array and colour space, and are then combined
/// with whichever of the three kinds of mask the image carries: a stencil, an explicit mask image,
/// or a range of colours to drop.
/// </summary>
public static class PdfImageReader
{
    /// <summary>The most pixels one image may decode to, which stops a hostile file exhausting memory.</summary>
    private const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>
    /// How far masks may chain. A conforming mask has no mask of its own, so this only has to leave
    /// room for sloppy producers while keeping recursion shallow.
    /// </summary>
    private const int MaxMaskDepth = 4;

    /// <summary>The most pixels one decode may touch: an image, a soft mask and an explicit mask.</summary>
    private const long MaxChainPixels = 3 * MaxPixels;

    /// <summary>What one decode has done so far, shared with the masks it decodes along the way.</summary>
    private sealed class DecodeContext
    {
        internal HashSet<PdfStream> ActivePath { get; } = new(ReferenceEqualityComparer.Instance);

        internal long Pixels { get; set; }
    }

    /// <summary>Decodes an image XObject.</summary>
    /// <param name="stream">The image stream.</param>
    /// <param name="resources">The resources the image's colour space may be named in.</param>
    /// <returns>The pixels.</returns>
    /// <exception cref="NotSupportedException">The image uses a codec this library does not decode.</exception>
    /// <exception cref="PdfFormatException">The image's masks refer back to one another.</exception>
    /// <exception cref="PdfResourceLimitException">The image's masks chain too deeply or are too large in total.</exception>
    public static PdfImageData Decode(PdfStream stream, PdfDictionary? resources = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Decode(stream, resources, new DecodeContext());
    }

    private static PdfImageData Decode(PdfStream stream, PdfDictionary? resources, DecodeContext context)
    {
        if (!context.ActivePath.Add(stream))
            throw new PdfFormatException("An image mask refers back to an image it is masking.");

        try
        {
            if (context.ActivePath.Count > MaxMaskDepth + 1)
                throw new PdfResourceLimitException("PdfImageReader.MaxMaskDepth", MaxMaskDepth, context.ActivePath.Count - 1);

            PdfDictionary dictionary = stream.Dictionary;
            int width = (int)dictionary.GetFirst(PdfNames.Width, PdfName.Get("W")).AsInteger;
            int height = (int)dictionary.GetFirst(PdfNames.Height, PdfName.Get("H")).AsInteger;

            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
                throw new NotSupportedException($"The image is {width} by {height}, which is not a size that can be decoded.");

            context.Pixels += (long)width * height;
            if (context.Pixels > MaxChainPixels)
                throw new PdfResourceLimitException("PdfImageReader.MaxChainPixels", MaxChainPixels, context.Pixels);

            Samples samples = ReadSamples(stream, dictionary, width, height, resources);
            SoftMask? softMask = ReadSoftMask(dictionary, samples.Components, resources, context);
            if (softMask is { Matte: not null } matted)
                RemoveMatte(samples, matted, width, height);

            byte[] pixels = ToRgba(samples, width, height);

            if (softMask is { } alpha)
                ApplySoftMask(alpha.Image, pixels, width, height);
            ApplyMask(dictionary, samples, pixels, width, height, resources, context);

            return new PdfImageData(width, height, pixels);
        }
        finally
        {
            context.ActivePath.Remove(stream);
        }
    }

    /// <summary>Whether an image is one this library can decode.</summary>
    /// <param name="stream">The image stream.</param>
    public static bool CanDecode(PdfStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        PdfName? codec = stream.UndecodedFilter;
        return codec is null || codec.Value is "DCTDecode" or "DCT" or "CCITTFaxDecode" or "CCF" or "JBIG2Decode";
    }

    /// <summary>
    /// The samples of an image, widened to one value per component per pixel and mapped through the
    /// decode array, alongside the untouched bytes a colour key mask has to be tested against.
    /// </summary>
    private readonly record struct Samples(
        double[] Values, PdfColorSpace Space, int Components, bool IsStencil, ReadOnlyMemory<byte> Data, int Bits);

    private readonly record struct SoftMask(PdfImageData Image, double[]? Matte);

    private static Samples ReadSamples(
        PdfStream stream, PdfDictionary dictionary, int width, int height, PdfDictionary? resources)
    {
        ReadOnlyMemory<byte> data = stream.Decode();
        PdfName? codec = stream.UndecodedFilter;
        bool stencil = dictionary.GetFirst(PdfNames.ImageMask, PdfName.Get("IM")).AsBoolean;
        int bits = stencil ? 1 : (int)dictionary.GetFirst(PdfNames.BitsPerComponent, PdfName.Get("BPC")).AsInteger;

        PdfColorSpace space = stencil
            ? PdfColorSpace.DeviceGray
            : PdfColorSpace.Read(dictionary.GetFirst(PdfNames.ColorSpace, PdfName.Get("CS")), resources)
              ?? PdfColorSpace.DeviceGray;

        if (codec is not null)
        {
            (data, bits, space) = Uncompress(codec, data, dictionary, width, height, space);
        }

        if (bits is not (1 or 2 or 4 or 8 or 16))
            bits = 8;

        int components = stencil ? 1 : space.ComponentCount;
        double[] decode = Numbers(dictionary.GetFirst(PdfNames.Decode, PdfName.Get("D"))) ?? space.DefaultDecode(bits);

        // A stencil paints where its samples are zero unless the decode array says otherwise.
        if (stencil && decode.Length < 2)
            decode = [0, 1];

        return new Samples(
            Unpack(data.Span, width, height, components, bits, decode),
            space,
            components,
            stencil,
            data,
            bits);
    }

    /// <summary>Hands the payload to whichever image codec the filter chain stopped at.</summary>
    private static (ReadOnlyMemory<byte> Data, int Bits, PdfColorSpace Space) Uncompress(
        PdfName codec,
        ReadOnlyMemory<byte> data,
        PdfDictionary dictionary,
        int width,
        int height,
        PdfColorSpace space)
    {
        switch (codec.Value)
        {
            case "DCTDecode" or "DCT":
                {
                    PdfDictionary? parms = Parms(dictionary);
                    int? colorTransform = parms?.ContainsKey(PdfName.Get("ColorTransform")) == true
                        ? (int)parms.GetInteger(PdfName.Get("ColorTransform"))
                        : null;
                    JpegImage jpeg = JpegDecoder.Decode(data.Span, colorTransform);
                    return (jpeg.Samples, 8, JpegSpace(jpeg, space));
                }

            case "CCITTFaxDecode" or "CCF":
                {
                    PdfDictionary parms = Parms(dictionary) ?? new PdfDictionary(0);

                    try
                    {
                        return (CcittFaxDecoder.Decode(data.Span, CcittFaxDecoder.Read(parms, width, height)), 1, space);
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new NotSupportedException(
                            $"The CCITT fax data could not be decoded: {exception.Message}", exception);
                    }
                }

            case "JBIG2Decode":
                {
                    PdfDictionary? parms = Parms(dictionary);
                    ReadOnlyMemory<byte> globals = parms?.GetStream(PdfName.Get("JBIG2Globals")) is { } shared
                        ? shared.Decode()
                        : ReadOnlyMemory<byte>.Empty;

                    try
                    {
                        // JBIG2 sets a bit where the pixel is black, which is the reverse of how a
                        // one-bit image is otherwise read, so the rows come back inverted.
                        byte[] rows = Jbig2.Jbig2Decoder.Decode(data, globals, width, height);
                        for (int i = 0; i < rows.Length; i++)
                            rows[i] = (byte)~rows[i];

                        return (rows, 1, space);
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new NotSupportedException(
                            $"The JBIG2 data could not be decoded: {exception.Message}", exception);
                    }
                }

            default:
                throw new NotSupportedException(
                    $"Images compressed with {codec.Value} are not decoded; read the stream's raw data instead.");
        }
    }

    /// <summary>
    /// The colour space a decoded JPEG is really in. The stream dictionary usually agrees, but a
    /// JPEG that turned out to hold a different number of components has the last word, because
    /// that is what the samples actually are.
    /// </summary>
    private static PdfColorSpace JpegSpace(JpegImage jpeg, PdfColorSpace declared)
    {
        if (declared.ComponentCount == jpeg.Components && !declared.IsIndexed)
            return declared;

        return jpeg.Components switch
        {
            1 => PdfColorSpace.DeviceGray,
            4 => PdfColorSpace.DeviceCmyk,
            _ => PdfColorSpace.DeviceRgb,
        };
    }

    /// <summary>
    /// Widens packed samples into one number per component per pixel, mapping each through the
    /// decode array as it goes. Rows are padded to a byte boundary, which is why this cannot simply
    /// walk the bit stream from end to end.
    /// </summary>
    private static double[] Unpack(
        ReadOnlySpan<byte> data, int width, int height, int components, int bits, double[] decode)
    {
        double[] values = new double[(long)width * height * components <= int.MaxValue
            ? width * height * components
            : 0];

        if (values.Length == 0)
            return values;

        double maximum = (1 << bits) - 1;
        int rowBits = width * components * bits;
        int stride = (rowBits + 7) / 8;
        int index = 0;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            int bit = 0;

            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < components; c++)
                {
                    double raw = Read(data, rowStart, ref bit, bits);
                    double low = (2 * c) + 1 < decode.Length ? decode[2 * c] : 0;
                    double high = (2 * c) + 1 < decode.Length ? decode[(2 * c) + 1] : 1;

                    values[index++] = low + (raw * (high - low) / maximum);
                }
            }
        }

        return values;
    }

    private static double Read(ReadOnlySpan<byte> data, int rowStart, ref int bit, int bits)
    {
        int value = 0;

        for (int i = 0; i < bits; i++)
        {
            int position = rowStart + (bit >> 3);
            int shift = 7 - (bit & 7);
            int one = position < data.Length ? (data[position] >> shift) & 1 : 0;

            value = (value << 1) | one;
            bit++;
        }

        return value;
    }

    /// <summary>Runs the samples through the colour space, one pixel at a time.</summary>
    private static byte[] ToRgba(Samples samples, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        Span<double> colour = stackalloc double[Math.Max(1, Math.Min(samples.Components, 32))];

        int index = 0;
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            for (int c = 0; c < colour.Length; c++)
            {
                int position = (pixel * samples.Components) + c;
                colour[c] = position < samples.Values.Length ? samples.Values[position] : 0;
            }

            if (samples.IsStencil)
            {
                // A stencil is not a colour but a shape: it paints black where the sample is zero
                // and lets the page through everywhere else.
                bool paint = colour[0] < 0.5;
                pixels[index++] = 0;
                pixels[index++] = 0;
                pixels[index++] = 0;
                pixels[index++] = paint ? (byte)255 : (byte)0;
                continue;
            }

            // An indexed space addresses its palette by number, so the sample is scaled back up.
            if (samples.Space.IsIndexed)
                colour[0] = Math.Round(colour[0]);

            PdfRgb rgb = samples.Space.ToRgb(colour);
            pixels[index++] = rgb.R;
            pixels[index++] = rgb.G;
            pixels[index++] = rgb.B;
            pixels[index++] = 255;
        }

        return pixels;
    }

    /// <summary>
    /// Reads a soft mask (§11.6.5), including the optional colour that the source samples were
    /// precomposited against.
    /// </summary>
    private static SoftMask? ReadSoftMask(
        PdfDictionary dictionary, int components, PdfDictionary? resources, DecodeContext context)
    {
        if (dictionary.GetStream(PdfNames.SMask) is not { } mask)
            return null;

        PdfImageData alpha;
        try
        {
            alpha = Decode(mask, resources, context);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        double[]? matte = Numbers(mask.Dictionary.Get(PdfName.Get("Matte")));
        return new SoftMask(alpha, matte is { Length: var length } && length >= components ? matte : null);
    }

    /// <summary>
    /// Recovers unassociated source components from samples precomposited against <c>/Matte</c>:
    /// C = (C' - (1 - alpha) M) / alpha. This runs before colour-space conversion, because both C'
    /// and M are expressed in the parent image's colour space.
    /// </summary>
    private static void RemoveMatte(Samples samples, SoftMask mask, int width, int height)
    {
        double[] matte = mask.Matte!;
        double[] ranges = samples.Space.DefaultDecode(samples.Bits);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double alpha = Sample(mask.Image, x, y, width, height, channel: 0) / 255.0;
                int pixel = (y * width) + x;

                for (int component = 0; component < samples.Components; component++)
                {
                    int position = (pixel * samples.Components) + component;
                    if ((uint)position >= (uint)samples.Values.Length)
                        continue;

                    double value = alpha <= 0
                        ? matte[component]
                        : (samples.Values[position] - ((1 - alpha) * matte[component])) / alpha;
                    double lower = (2 * component) + 1 < ranges.Length ? ranges[2 * component] : 0;
                    double upper = (2 * component) + 1 < ranges.Length ? ranges[(2 * component) + 1] : 1;
                    samples.Values[position] = Math.Clamp(value, Math.Min(lower, upper), Math.Max(lower, upper));
                }
            }
        }
    }

    /// <summary>Copies the soft mask's greyscale value into the output alpha channel.</summary>
    private static void ApplySoftMask(PdfImageData alpha, byte[] pixels, int width, int height)
    {

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // The mask's own conversion put its grey value in all three channels.
                pixels[(((y * width) + x) * 4) + 3] = Sample(alpha, x, y, width, height, channel: 0);
            }
        }
    }

    /// <summary>
    /// Applies an explicit mask or a colour key one (§8.9.6.3 and §8.9.6.4). The first is a stencil
    /// saying which pixels to leave out; the second is a range of colours to drop wherever they occur.
    /// </summary>
    private static void ApplyMask(
        PdfDictionary dictionary,
        Samples samples,
        byte[] pixels,
        int width,
        int height,
        PdfDictionary? resources,
        DecodeContext context)
    {
        PdfValue mask = dictionary.Get(PdfNames.Mask);

        if (mask.AsStream is { } stencil)
        {
            PdfImageData shape;
            try
            {
                shape = Decode(stencil, resources, context);
            }
            catch (NotSupportedException)
            {
                return;
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // A stencil mask marks what to hide, so its painted areas become transparent.
                    if (Sample(shape, x, y, width, height, channel: 3) > 127)
                        pixels[(((y * width) + x) * 4) + 3] = 0;
                }
            }

            return;
        }

        if (mask.AsArray is not { Count: >= 2 } ranges)
            return;

        ColorKey(samples, pixels, width, height, ranges);
    }

    /// <summary>
    /// Drops every pixel whose original sample values all fall inside the given ranges. The test is
    /// on the samples as stored, not on the colour they became, which is what the specification says.
    /// </summary>
    private static void ColorKey(Samples samples, byte[] pixels, int width, int height, PdfArray ranges)
    {
        int components = samples.Components;
        if (ranges.Count < components * 2)
            return;

        // The ranges are given in the sample values themselves, so the data is unpacked again with
        // an identity decode rather than the one the image declares.
        double[] identity = new double[components * 2];
        for (int i = 0; i < components; i++)
            identity[(2 * i) + 1] = (1 << samples.Bits) - 1;

        double[] raw = Unpack(samples.Data.Span, width, height, components, samples.Bits, identity);

        for (int pixel = 0; pixel < width * height; pixel++)
        {
            bool inside = true;

            for (int c = 0; c < components && inside; c++)
            {
                int position = (pixel * components) + c;
                double value = position < raw.Length ? raw[position] : 0;
                inside = value >= ranges.GetNumber(2 * c) && value <= ranges.GetNumber((2 * c) + 1);
            }

            if (inside)
                pixels[(pixel * 4) + 3] = 0;
        }
    }

    /// <summary>Reads one channel of a mask at a position, scaled to the size of the image it masks.</summary>
    private static byte Sample(PdfImageData mask, int x, int y, int width, int height, int channel)
    {
        int mx = width == mask.Width ? x : (int)((long)x * mask.Width / width);
        int my = height == mask.Height ? y : (int)((long)y * mask.Height / height);

        mx = Math.Clamp(mx, 0, mask.Width - 1);
        my = Math.Clamp(my, 0, mask.Height - 1);

        return mask.Pixels[(((my * mask.Width) + mx) * 4) + channel];
    }

    private static PdfDictionary? Parms(PdfDictionary dictionary)
    {
        PdfValue parms = dictionary.GetFirst(PdfNames.DecodeParms, PdfName.Get("DP"));

        if (parms.AsArray is { } array)
        {
            for (int i = array.Count - 1; i >= 0; i--)
            {
                if (array.GetDictionary(i) is { } last)
                    return last;
            }

            return null;
        }

        return parms.AsDictionary;
    }

    private static double[]? Numbers(PdfValue value)
    {
        if (value.AsArray is not { Count: > 0 } array)
            return null;

        double[] numbers = new double[array.Count];
        for (int i = 0; i < array.Count; i++)
            numbers[i] = array.GetNumber(i);

        return numbers;
    }
}
