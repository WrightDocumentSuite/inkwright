using Inkwright.Cos;
using Inkwright.Images.Ccitt;

namespace Inkwright.Images;

/// <summary>
/// The <c>CCITTFaxDecode</c> filter of ISO 32000-2 §7.4.6: the fax codings of ITU-T T.4 and T.6,
/// which scanners still produce and which a PDF stores one bit per pixel.
/// </summary>
internal static class CcittFaxDecoder
{
    /// <summary>Reads the decode parameters of Table 11.</summary>
    /// <param name="parms">The decode parameters dictionary.</param>
    /// <param name="width">The image width, used when the parameters name no column count.</param>
    /// <param name="height">The image height, used when the parameters name no row count.</param>
    internal static CcittOptions Read(PdfDictionary parms, int width, int height)
    {
        int columns = (int)parms.GetInteger(PdfName.Get("Columns"), 1728);
        int rows = (int)parms.GetInteger(PdfName.Get("Rows"), height);

        return new CcittOptions(
            Columns: columns > 0 ? columns : width,
            Rows: rows > 0 ? rows : height,
            K: (int)parms.GetInteger(PdfName.Get("K")),
            EndOfLine: parms.Get(PdfName.Get("EndOfLine")).AsBoolean,
            ByteAligned: parms.Get(PdfName.Get("EncodedByteAlign")).AsBoolean,
            BlackIs1: parms.Get(PdfName.Get("BlackIs1")).AsBoolean,
            DamagedRowsBeforeError: (int)parms.GetInteger(PdfName.Get("DamagedRowsBeforeError")));
    }

    /// <summary>Decodes fax data into one bit per pixel, rows padded to a byte boundary.</summary>
    /// <param name="data">The coded data.</param>
    /// <param name="options">The parameters of Table 11.</param>
    /// <exception cref="InvalidDataException">The stream is damaged beyond what the parameters tolerate.</exception>
    internal static byte[] Decode(ReadOnlySpan<byte> data, CcittOptions options)
    {
        byte[] output = CcittDecoder.Decode(data, options);

        // The decoder sets a bit where the pixel is black. PDF reads a zero as black unless the
        // parameters say otherwise, so the usual case is the inverted one.
        if (!options.BlackIs1)
        {
            for (int i = 0; i < output.Length; i++)
                output[i] = (byte)~output[i];
        }

        return output;
    }
}
