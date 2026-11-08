namespace Inkwright.Filters;

/// <summary>
/// Caps how much a single stream may expand while being decoded. A few kilobytes of deflate data
/// can name a terabyte of output, and a library that reads untrusted files has to say no somewhere;
/// standalone compatibility decoding stops at the limit instead of exhausting memory. Document
/// reads additionally use <see cref="IO.PdfResourceLimits"/> and report an explicit
/// <see cref="Diagnostics.PdfResourceLimitException"/> rather than caching a truncated prefix.
/// </summary>
public static class PdfFilterLimits
{
    private static long _maxDecodedBytes = 128L * 1024 * 1024;

    /// <summary>
    /// The most bytes one stream may decode to. Defaults to 128 MB, which is far above any honest
    /// content stream or Flate-compressed image and far below what a decompression bomb wants. It
    /// remains a process-wide upper ceiling for source compatibility; prefer per-read limits in
    /// concurrent applications.
    /// </summary>
    public static long MaxDecodedBytes
    {
        get => Interlocked.Read(ref _maxDecodedBytes);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            Interlocked.Exchange(ref _maxDecodedBytes, value);
        }
    }

    internal static int Cap => (int)Math.Min(MaxDecodedBytes, int.MaxValue - 64);
}
