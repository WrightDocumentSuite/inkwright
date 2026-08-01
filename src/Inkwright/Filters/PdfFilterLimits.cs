namespace Inkwright.Filters;

/// <summary>
/// Caps how much a single stream may expand while being decoded. A few kilobytes of deflate data
/// can name a terabyte of output, and a library that reads untrusted files has to say no somewhere;
/// decoding stops at the limit and hands back what it produced instead of exhausting memory.
/// </summary>
public static class PdfFilterLimits
{
    private static long _maxDecodedBytes = 128L * 1024 * 1024;

    /// <summary>
    /// The most bytes one stream may decode to. Defaults to 128 MB, which is far above any honest
    /// content stream or Flate-compressed image and far below what a decompression bomb wants.
    /// </summary>
    public static long MaxDecodedBytes
    {
        get => _maxDecodedBytes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDecodedBytes = value;
        }
    }

    internal static int Cap => (int)Math.Min(_maxDecodedBytes, int.MaxValue - 64);
}
